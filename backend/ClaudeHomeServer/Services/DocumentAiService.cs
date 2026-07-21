using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// ИИ-помощь по документам: конвертирует бинарный документ в Markdown (MarkitdownService),
// затем локальной моделью (ICheapTextRunner, с фолбэком на claude) строит краткое содержание,
// выжимку (решения/даты/участники/действия) или теги. null от markitdown → null результат
// (документ не распознан / нет markitdown). Модель — Notes:AiModel/Tasks:AiModel (дефолт haiku).
public sealed class DocumentAiService(
    MarkitdownService markitdown, Llm.ICheapTextRunner cheap, IConfiguration config)
{
    private const int MdBudget = 12_000;   // символов Markdown в промпт (усечение хвоста)
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private string Model => config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku";

    public record DocExtractResult(
        IReadOnlyList<string> Decisions, IReadOnlyList<string> Dates,
        IReadOnlyList<string> People, IReadOnlyList<string> ActionItems);

    // Конвертация в Markdown без модели (детерминированно). null — не распознан / нет markitdown.
    public Task<string?> ConvertAsync(string absolutePath, CancellationToken ct) =>
        markitdown.ConvertAsync(absolutePath, ct);

    // Восстановление Markdown-разметки локальной моделью: markitdown из pdf даёт плоский текст
    // без заголовков/списков. Модель расставляет #/##, списки, выделения — НЕ меняя и не сокращая
    // текст. Пустой ввод / ошибка → исходный текст (безопасная деградация).
    public async Task<string> EnhanceMarkdownAsync(string markdown, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;
        var prompt =
            "Ниже — текст документа, извлечённый из PDF (без разметки). Оформи его как аккуратный Markdown: " +
            "расставь заголовки (#/##/###), маркированные и нумерованные списки, выдели важное **жирным**, " +
            "оформи таблицы если они есть. СТРОГО сохрани весь текст дословно — ничего не добавляй, не сокращай " +
            "и не перефразируй, только разметка и структура. Ответь ТОЛЬКО готовым Markdown, без пояснений.\n\n" +
            Truncate(markdown, MdBudget);
        var enhanced = (await cheap.RunAsync(Llm.LocalActionCatalog.DocFormat, prompt, Model, ct: ct)).Trim();
        // Снимаем возможную ```markdown-обёртку
        if (enhanced.StartsWith("```"))
        {
            var nl = enhanced.IndexOf('\n');
            var lastFence = enhanced.LastIndexOf("```", StringComparison.Ordinal);
            if (nl > 0 && lastFence > nl) enhanced = enhanced[(nl + 1)..lastFence].Trim();
        }
        return string.IsNullOrWhiteSpace(enhanced) ? markdown : enhanced;
    }

    // Краткое содержание: 5-8 пунктов сути. text — готовое содержимое (markdown документа или
    // текст .md/.txt): добывание текста (markitdown для бинарных / чтение для текстовых) — на вызывающем.
    public async Task<string?> SummaryAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var prompt =
            "Ниже — содержимое документа. Составь краткое содержание: 5-8 пунктов маркированного списка " +
            "по сути, по-русски. Ответь ТОЛЬКО markdown-списком, без вступлений.\n\n" + Truncate(text, MdBudget);
        return await cheap.RunAsync(Llm.LocalActionCatalog.DocSummary, prompt, Model, ct: ct);
    }

    // Структурная выжимка: решения, даты/сроки, участники, action items.
    public async Task<DocExtractResult?> ExtractAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var prompt =
            "Ниже — содержимое документа. Извлеки структурированную выжимку. Ответь ТОЛЬКО JSON-объектом " +
            "без пояснений: {\"decisions\":[],\"dates\":[],\"people\":[],\"actionItems\":[]}. " +
            "Каждое поле — массив СТРОК (НЕ объектов). decisions — принятые решения; dates — важные " +
            "даты/сроки строкой вида «дата — контекст»; people — упомянутые участники/ответственные; " +
            "actionItems — задачи/следующие шаги. Пусто → []. По-русски.\n\n" +
            Truncate(text, MdBudget);
        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.DocExtract, prompt, Model, ct: ct);
        return ParseExtract(raw);
    }

    // До 6 тегов по содержимому.
    public async Task<IReadOnlyList<string>?> TagsAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var prompt =
            "Ниже — содержимое документа. Предложи до 6 коротких тегов (одно-два слова, по-русски, без #) " +
            "по теме. Ответь ТОЛЬКО JSON-массивом строк.\n\n" + Truncate(text, MdBudget);
        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.DocTags, prompt, Model, ct: ct);
        return ParseStringArray(raw).Select(t => t.Trim().TrimStart('#').Trim())
            .Where(t => t.Length is > 1 and <= 30).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private static DocExtractResult ParseExtract(string raw)
    {
        var empty = new DocExtractResult([], [], [], []);
        var json = ExtractJsonObject(raw);
        if (json is null) return empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new DocExtractResult(
                ReadStrings(root, "decisions"), ReadStrings(root, "dates"),
                ReadStrings(root, "people"), ReadStrings(root, "actionItems"));
        }
        catch (JsonException) { return empty; }
    }

    // Массив строк из поля — устойчиво к тому, что модель вместо строки кладёт объект
    // (напр. dates: [{date, context}]): такой объект склеиваем в строку через « — ».
    private static IReadOnlyList<string> ReadStrings(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object => FlattenObject(e),
                JsonValueKind.Number => e.ToString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            if (list.Count >= 20) break;
        }
        return list;
    }

    private static string FlattenObject(JsonElement obj)
    {
        var parts = new List<string>();
        foreach (var p in obj.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                parts.Add(p.Value.GetString()!.Trim());
        return string.Join(" — ", parts);
    }

    private static IReadOnlyList<string> ParseStringArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try { return JsonSerializer.Deserialize<List<string>>(raw[start..(end + 1)], JsonOpts) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? ExtractJsonObject(string raw)
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
}
