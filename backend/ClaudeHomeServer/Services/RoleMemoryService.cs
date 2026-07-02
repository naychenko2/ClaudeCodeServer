using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Память роли-собеседника: устойчивые факты и договорённости, переживающие отдельные чаты.
// Роль глобальная, память — КОНТЕКСТНАЯ: один markdown-файл на пару (роль, контекст),
// data/role-memory/<roleId>/<context>.md. Контекст: projectId для проектных чатов,
// "chats-<ownerId>" для чатов вне проекта — факты проектов и болтовня в «Чатах» не смешиваются.
//
// Наполнение — два канала (вызывается из ClaudeSession по завершении хода):
//  1) маркеры [MEMORY] в ответе роли (реалтайм, с дедупликацией);
//  2) компакт-summary лёгким claude, когда память выросла на SummaryGrowthLines строк
//     с последнего summary (метка — sidecar <context>.meta.json; триггер переживает
//     рестарты и не зависит от числа параллельных чатов).
//
// Гонки: все операции пары (роль, контекст) — через последовательную очередь (SemaphoreSlim
// per ключ). Summary — оптимистичная блокировка: генерация идёт вне лока, перед записью
// содержимое сравнивается с прочитанным; если память изменилась — результат дропается.
public class RoleMemoryService
{
    // Порог авто-summary: на сколько строк память должна вырасти с последнего компакта
    private const int SummaryGrowthLines = 15;

    private readonly string _memDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public RoleMemoryService(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _memDir = Path.Combine(dataDir, "role-memory");
    }

    // Контекст памяти для сессии: проектная → projectId, чат вне проекта → chats-<ownerId>
    public static string ContextFor(Session session) =>
        session.ProjectId ?? $"chats-{session.OwnerId}";

    private string DirFor(string roleId) => Path.Combine(_memDir, Sanitize(roleId));
    private string PathFor(string roleId, string context) =>
        Path.Combine(DirFor(roleId), Sanitize(context) + ".md");
    private string MetaPathFor(string roleId, string context) =>
        Path.Combine(DirFor(roleId), Sanitize(context) + ".meta.json");

    // Id ролей/проектов у нас — GUID, но на всякий случай режем всё, что не годится в имя файла
    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private SemaphoreSlim LockFor(string roleId, string context) =>
        _locks.GetOrAdd(Sanitize(roleId) + "/" + Sanitize(context), _ => new SemaphoreSlim(1, 1));

    // --- Базовые операции (каждая — атомарна в своей очереди) ---

    public string Read(string roleId, string context)
    {
        var sem = LockFor(roleId, context);
        sem.Wait();
        try { return ReadUnsafe(roleId, context); }
        finally { sem.Release(); }
    }

    private string ReadUnsafe(string roleId, string context)
    {
        var p = PathFor(roleId, context);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    // Дозапись новых фактов с дедупликацией по содержимому строки (регистронезависимо)
    public void Append(string roleId, string context, IEnumerable<string> facts)
    {
        var sem = LockFor(roleId, context);
        sem.Wait();
        try
        {
            var existing = ReadUnsafe(roleId, context);
            var existingLines = existing
                .Split('\n')
                .Select(l => l.TrimStart('-', ' ', '\t').Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = facts
                .Select(f => f.Trim())
                .Where(f => f.Length > 0 && !existingLines.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toAdd.Count == 0) return;

            Directory.CreateDirectory(DirFor(roleId));
            var sb = new System.Text.StringBuilder(existing);
            if (existing.Length > 0 && !existing.EndsWith('\n')) sb.Append('\n');
            foreach (var f in toAdd) sb.Append("- ").Append(f).Append('\n');
            File.WriteAllText(PathFor(roleId, context), sb.ToString());
        }
        finally { sem.Release(); }
    }

    // Полная перезапись (ручная правка из UI). Сбрасывает счётчик роста —
    // свежеперезаписанная память считается «только что компактнутой».
    public void Overwrite(string roleId, string context, string content)
    {
        var sem = LockFor(roleId, context);
        sem.Wait();
        try { OverwriteUnsafe(roleId, context, content ?? ""); }
        finally { sem.Release(); }
    }

    private void OverwriteUnsafe(string roleId, string context, string content)
    {
        Directory.CreateDirectory(DirFor(roleId));
        File.WriteAllText(PathFor(roleId, context), content);
        WriteMeta(roleId, context, CountLines(content));
    }

    // Полное удаление роли из пула — вся память по всем контекстам
    public void DeleteRole(string roleId)
    {
        var dir = DirFor(roleId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        // Легаси-файл старого формата (мог остаться, если миграция не нашла проект)
        var legacy = Path.Combine(_memDir, Sanitize(roleId) + ".md");
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    // --- Обработка завершённого хода (вызывается из ClaudeSession, в фоне) ---

    // runOneShotClaude(prompt, model) — разовый текстовый прогон claude (даёт ClaudeSession)
    public async Task ProcessTurnAsync(Role role, string context, string turnText,
        Func<string, string, Task<string?>> runOneShotClaude)
    {
        // Канал 1: явные факты из маркеров [MEMORY] в ответе роли
        var facts = ExtractMemoryMarkers(turnText);
        if (facts.Count > 0) Append(role.Id, context, facts);

        // Канал 2: компакт-summary, когда память заметно выросла с последнего раза
        if (ShouldSummarize(role.Id, context))
            await SummarizeAsync(role, context, turnText, runOneShotClaude);
    }

    // Извлекает факты из строк вида "[MEMORY] текст" (маркер может быть в любом месте строки)
    public static List<string> ExtractMemoryMarkers(string text)
    {
        var facts = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf("[MEMORY]", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var fact = line[(idx + "[MEMORY]".Length)..].Trim().TrimStart(':', '-', ' ', '\t').Trim();
            if (fact.Length > 0) facts.Add(fact);
        }
        return facts;
    }

    // Память выросла на SummaryGrowthLines строк с последнего summary?
    private bool ShouldSummarize(string roleId, string context)
    {
        var sem = LockFor(roleId, context);
        sem.Wait();
        try
        {
            var lines = CountLines(ReadUnsafe(roleId, context));
            return lines - ReadMeta(roleId, context) >= SummaryGrowthLines;
        }
        finally { sem.Release(); }
    }

    // Компакт-summary с оптимистичной блокировкой: читаем → генерим (долго, вне лока) →
    // записываем, только если память за это время не изменилась.
    private async Task SummarizeAsync(Role role, string context, string turnText,
        Func<string, string, Task<string?>> runOneShotClaude)
    {
        var snapshot = Read(role.Id, context);
        if (string.IsNullOrWhiteSpace(snapshot)) return;   // нечего компактить

        var prompt =
            $"Ты ведёшь долговременную память роли «{role.Name}» (факты о проекте и договорённости с пользователем). Вот её текущая память:\n\n{snapshot}\n\n" +
            "Вот последний фрагмент диалога роли с пользователем:\n\n" +
            Truncate(turnText, 4000) + "\n\n" +
            "Перепиши память компактно: объедини дубли, убери устаревшее и сиюминутное, добавь важные " +
            "устойчивые факты и договорённости из диалога. Верни ТОЛЬКО обновлённый markdown-список " +
            "фактов (строки вида «- …»), без преамбул, заголовков и комментариев.";

        var summary = await runOneShotClaude(prompt, "claude-haiku-4-5-20251001");
        if (string.IsNullOrWhiteSpace(summary)) return;

        var sem = LockFor(role.Id, context);
        await sem.WaitAsync();
        try
        {
            // Память изменилась, пока думал claude (параллельный чат дописал факты) —
            // дропаем результат, чтобы не затереть чужие строки. Следующий summary догонит.
            if (ReadUnsafe(role.Id, context) != snapshot) return;
            OverwriteUnsafe(role.Id, context, summary.Trim() + "\n");
        }
        finally { sem.Release(); }
    }

    // --- Sidecar-метаданные (число строк на момент последнего summary) ---

    private int ReadMeta(string roleId, string context)
    {
        try
        {
            var p = MetaPathFor(roleId, context);
            if (!File.Exists(p)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("linesAtLastSummary", out var v) ? v.GetInt32() : 0;
        }
        catch { return 0; }
    }

    private void WriteMeta(string roleId, string context, int lines)
    {
        try
        {
            Directory.CreateDirectory(DirFor(roleId));
            File.WriteAllText(MetaPathFor(roleId, context),
                JsonSerializer.Serialize(new { linesAtLastSummary = lines }));
        }
        catch { /* мета — не критичный путь */ }
    }

    private static int CountLines(string content) =>
        content.Split('\n').Count(l => l.Trim().Length > 0);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // --- Обзор памяти по контекстам (для списка сотрудников) ---

    public record MemoryContextInfo(string Context, IReadOnlyList<string> Facts);

    // Все контексты памяти роли: проекты + внепроектные чаты ТОЛЬКО указанного владельца
    // (chats-контексты других пользователей — их приватные разговоры, не показываем).
    public IReadOnlyList<MemoryContextInfo> ListContexts(string roleId, string ownerId)
    {
        var dir = DirFor(roleId);
        if (!Directory.Exists(dir)) return [];

        var ownChats = "chats-" + Sanitize(ownerId);
        var result = new List<MemoryContextInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            var context = Path.GetFileNameWithoutExtension(file);
            if (context.StartsWith("chats-") && context != ownChats) continue;

            var facts = Read(roleId, context)
                .Split('\n')
                .Select(l => l.TrimStart('-', ' ', '\t').Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (facts.Count > 0) result.Add(new MemoryContextInfo(context, facts));
        }
        return result;
    }

    // --- Миграция старого формата (<roleId>.md → <roleId>/<projectId>.md) ---

    // roleToProject: roleId → прежний единственный ProjectId (из миграции roles.json)
    public void MigrateLegacy(IReadOnlyDictionary<string, string> roleToProject)
    {
        if (!Directory.Exists(_memDir)) return;
        foreach (var (roleId, projectId) in roleToProject)
        {
            try
            {
                var legacy = Path.Combine(_memDir, Sanitize(roleId) + ".md");
                if (!File.Exists(legacy)) continue;
                var target = PathFor(roleId, projectId);
                if (File.Exists(target)) continue;   // уже мигрировано
                Directory.CreateDirectory(DirFor(roleId));
                File.Move(legacy, target);
            }
            catch { /* память — не критичный путь, не валим старт */ }
        }
    }
}
