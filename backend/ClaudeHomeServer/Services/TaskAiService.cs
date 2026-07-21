using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Генерация контента задач одноразовым вызовом (без сессии). Идёт через «дешёвый» раннер:
// локальная модель Ollama (если действие task-ai на неё заведено) или claude (Tasks:AiModel).
// Контекст проекта передаётся в промпте (имя + выдержка из CLAUDE.md).
public class TaskAiService(ProjectManager projects, IConfiguration config,
    Llm.ICheapTextRunner cheap)
{

    public async Task<string> GenerateDescriptionAsync(string title, string? projectId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Составь краткое описание задачи в Markdown: заголовок «### Цель», 1-2 предложения сути, " +
                      "маркированный список ключевых пунктов (3-5), при необходимости — критерии готовности. " +
                      "Не больше 120 слов. Ответь ТОЛЬКО markdown-описанием, без вступлений и пояснений.");
        sb.AppendLine();
        sb.AppendLine($"Задача: «{title}»");
        AppendProjectContext(sb, projectId);
        return CleanupText(await RunAsync(sb.ToString(), ct));
    }

    public async Task<IReadOnlyList<string>> GenerateSubtasksAsync(
        string title, string description, string? projectId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Разбей задачу на 3-7 конкретных выполнимых подзадач (коротких, в повелительном наклонении). " +
                      "Ответь ТОЛЬКО JSON-массивом строк без пояснений и без markdown-обёртки. " +
                      "Пример формата: [\"Первая подзадача\", \"Вторая подзадача\"]");
        sb.AppendLine();
        sb.AppendLine($"Задача: «{title}»");
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine("Описание задачи:");
            sb.AppendLine(description);
        }
        AppendProjectContext(sb, projectId);

        var raw = await RunAsync(sb.ToString(), ct);
        return ParseSubtasks(raw);
    }

    // --- Классификация: приоритет + метки (локальная модель, action task-classify) ---

    public record TaskClassification(string? Priority, IReadOnlyList<string> Labels);

    // Предложить приоритет (low|medium|high|urgent) и до 3 меток по названию+описанию.
    // existingLabels — метки владельца (приоритетны, чтобы не плодить синонимы).
    public async Task<TaskClassification> ClassifyAsync(string title, string? description,
        IReadOnlyList<string> existingLabels, string? projectId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Оцени задачу и верни ТОЛЬКО JSON-объект без пояснений: " +
                      "{\"priority\":\"low|medium|high|urgent\",\"labels\":[\"метка\",…]}.");
        sb.AppendLine("priority — по срочности/важности из текста (по умолчанию medium). " +
                      "labels — до 3 коротких меток (одно-два слова, по-русски, без #). " +
                      "Если подходят метки из списка существующих — используй их, не выдумывай синонимы.");
        sb.AppendLine();
        sb.AppendLine($"Задача: «{title}»");
        if (!string.IsNullOrWhiteSpace(description)) { sb.AppendLine("Описание:"); sb.AppendLine(Truncate(description, 1500)); }
        if (existingLabels.Count > 0) sb.AppendLine("Существующие метки владельца: " + string.Join(", ", existingLabels.Take(60)));
        AppendProjectContext(sb, projectId);

        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.TaskClassify, sb.ToString(),
            config["Tasks:AiModel"] ?? "haiku", ct: ct);
        return ParseClassification(raw, existingLabels);
    }

    internal static TaskClassification ParseClassification(string raw, IReadOnlyList<string> existingLabels)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return new TaskClassification(null, []);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var priority = root.TryGetProperty("priority", out var p) ? NormalizePriority(p.GetString()) : null;
            var labels = new List<string>();
            if (root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array)
                foreach (var e in l.EnumerateArray())
                {
                    var s = e.GetString()?.Trim().TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(s) && s.Length <= 30 && !labels.Contains(s, StringComparer.OrdinalIgnoreCase))
                        labels.Add(s);
                    if (labels.Count >= 3) break;
                }
            return new TaskClassification(priority, labels);
        }
        catch (JsonException) { return new TaskClassification(null, []); }
    }

    private static string? NormalizePriority(string? p)
    {
        var v = p?.Trim().ToLowerInvariant();
        return v is "low" or "medium" or "high" or "urgent" ? v : null;
    }

    // --- Нормализация заголовка (чистка голосового ввода, action task-normalize-title) ---

    public record TaskTitleNormalization(string Title, string? DueHint);

    // «сделаю отчёт завтра» → {title:"Сделать отчёт", dueHint:"завтра"}. Повелительное наклонение,
    // без мусора транскрибатора; упомянутый срок выносится в dueHint (парсинг даты — на вызывающем).
    public async Task<TaskTitleNormalization> NormalizeTitleAsync(string rawTitle, CancellationToken ct)
    {
        var prompt =
            "Приведи заголовок задачи к аккуратному виду: повелительное наклонение («Сделать», «Позвонить»), " +
            "с заглавной буквы, убери слова-паразиты и артефакты голосового ввода, без точки в конце. " +
            "Если в тексте назван срок (сегодня/завтра/дата/день недели) — вынеси его в dueHint, из title убери. " +
            "Ответь ТОЛЬКО JSON: {\"title\":\"…\",\"dueHint\":\"…|null\"}. Смысл не меняй.\n\n" +
            $"Заголовок: {rawTitle.Trim()}";
        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.TaskNormalizeTitle, prompt,
            config["Tasks:AiModel"] ?? "haiku", ct: ct);
        var json = ExtractJsonObject(raw);
        if (json is null) return new TaskTitleNormalization(rawTitle.Trim(), null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString()?.Trim() : null;
            var due = root.TryGetProperty("dueHint", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title)) title = rawTitle.Trim();
            return new TaskTitleNormalization(title!, string.IsNullOrWhiteSpace(due) ? null : due);
        }
        catch (JsonException) { return new TaskTitleNormalization(rawTitle.Trim(), null); }
    }

    // --- Дедуп: похожа ли новая задача на существующую (action task-dedup) ---

    public record TaskDuplicate(string? Id, string? Reason);

    // candidates — предотобранные (по ключевым словам) существующие задачи владельца.
    // Модель решает, дублирует ли новая одну из них. Пустой список / нет дубля → Id=null.
    public async Task<TaskDuplicate> FindDuplicateAsync(string title, string? description,
        IReadOnlyList<(string Id, string Title)> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return new TaskDuplicate(null, null);
        var sb = new StringBuilder();
        sb.AppendLine("Определи, дублирует ли НОВАЯ задача одну из существующих (то же дело по сути, не просто похожая тема).");
        sb.AppendLine($"Новая: «{title}»");
        if (!string.IsNullOrWhiteSpace(description)) sb.AppendLine("Описание: " + Truncate(description, 500));
        sb.AppendLine("Существующие (id | заголовок):");
        foreach (var (id, t) in candidates.Take(20)) sb.AppendLine($"{id} | {t}");
        sb.AppendLine();
        sb.AppendLine("Ответь ТОЛЬКО JSON: {\"duplicateId\":\"<id из списка или null>\",\"reason\":\"кратко почему\"}. " +
                      "Если явного дубля нет — duplicateId: null. Не выдумывай id.");

        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.TaskDedup, sb.ToString(),
            config["Tasks:AiModel"] ?? "haiku", ct: ct);
        var json = ExtractJsonObject(raw);
        if (json is null) return new TaskDuplicate(null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.TryGetProperty("duplicateId", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Trim() : null;
            // Страховка от галлюцинаций: id обязан быть из переданного списка
            if (string.IsNullOrWhiteSpace(id) || candidates.All(c => c.Id != id)) return new TaskDuplicate(null, null);
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString()?.Trim() : null;
            return new TaskDuplicate(id, reason);
        }
        catch (JsonException) { return new TaskDuplicate(null, null); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Первый сбалансированный JSON-объект из ответа модели (терпимо к преамбуле/fence)
    internal static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    private void AppendProjectContext(StringBuilder sb, string? projectId)
    {
        if (projectId is null) return;
        var project = projects.GetById(projectId);
        if (project is null) return;

        sb.AppendLine();
        sb.AppendLine($"Контекст: задача относится к проекту «{project.Name}».");
        try
        {
            var claudeMd = Path.Combine(project.RootPath, "CLAUDE.md");
            if (File.Exists(claudeMd))
            {
                var text = File.ReadAllText(claudeMd);
                if (text.Length > 4000) text = text[..4000] + "\n…";
                sb.AppendLine("Описание проекта (CLAUDE.md):");
                sb.AppendLine(text);
            }
        }
        catch { /* контекст опционален */ }
    }

    private Task<string> RunAsync(string prompt, CancellationToken ct) =>
        cheap.RunAsync(Llm.LocalActionCatalog.TaskAi, prompt, config["Tasks:AiModel"], ct: ct);

    // Снимаем возможную ```-обёртку вокруг ответа
    private static string CleanupText(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }
        return text;
    }

    private static IReadOnlyList<string> ParseSubtasks(string raw)
    {
        var text = CleanupText(raw);
        // Ищем JSON-массив в ответе (модель могла добавить текст вокруг)
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)]);
                if (list is { Count: > 0 })
                    return list.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch (JsonException) { /* фолбэк ниже */ }
        }
        // Фолбэк: строки-список («- …» / «1. …»)
        return text.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d+[.)]\s*", ""))
            .Where(l => l.Length > 2)
            .Take(10)
            .ToList();
    }
}
