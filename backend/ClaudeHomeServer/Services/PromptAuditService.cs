using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Разбор промпта хода: «что тут лишнее и как оптимизировать». Один on-demand вызов
/// дешёвого исполнителя (место prompt-audit в «Поставщиках моделей»).
///
/// ПРИВАТНОСТЬ. По умолчанию наружу уходят только МЕТАДАННЫЕ: ключи и заголовки секций,
/// их размеры и доли, имена инструментов, веса из usage. Текст секций (а там auto-recall
/// личных заметок, долгая память персоны и привязки) не покидает машину, пока человек
/// сам не поставит галочку: место prompt-audit может быть назначено на OpenRouter или
/// локальную Ollama — то есть не туда, куда шёл сам чат.
/// </summary>
public sealed class PromptAuditService(ICheapTextRunner cheap)
{
    // С галочкой: сколько текста берём от секции и сколько суммарно. Лимит нужен не только
    // ради приватности: у Large-профиля локали NumCtx 16k, а Ollama режет хвост молча.
    public const int MaxSectionChars = 1024;
    public const int MaxTotalChars = 8 * 1024;

    // Идущие разборы: каждый клик платный, второй по той же сессии отклоняем
    private readonly ConcurrentDictionary<string, byte> _running = new();

    public bool IsRunning(string sessionId) => _running.ContainsKey(sessionId);

    /// <summary>
    /// Разобрать снимок. Возвращает текст разбора (markdown) либо null, если по этой
    /// сессии разбор уже идёт.
    /// </summary>
    public async Task<string?> AnalyzeAsync(string sessionId, PromptSnapshotDto snapshot,
        bool includeText, string? ownerId, CancellationToken ct = default)
    {
        if (!_running.TryAdd(sessionId, 0)) return null;
        try
        {
            return await cheap.RunAsync(LocalActionCatalog.PromptAudit,
                BuildPrompt(snapshot, includeText), ownerId: ownerId, ct: ct);
        }
        finally { _running.TryRemove(sessionId, out _); }
    }

    /// <summary>
    /// Промпт разбора. Сторож приватности: без <paramref name="includeText"/> в него не
    /// попадает ни один символ текста секций (проверяется тестом).
    /// </summary>
    public static string BuildPrompt(PromptSnapshotDto snapshot, bool includeText)
    {
        var system = snapshot.Sections.Where(s => s.Kind == "system").ToList();
        var total = Math.Max(1, system.Sum(s => s.Text.Length));

        var sb = new StringBuilder();
        sb.AppendLine("Ты разбираешь системный промпт, который приложение собрало и отправило модели на одном ходу чата.");
        sb.AppendLine("Задача: найти, что здесь лишнее, и предложить, как сократить.");
        sb.AppendLine();
        sb.AppendLine($"Всего в системном промпте {total} символов, секций {system.Count}.");
        sb.AppendLine("Секции (доля от общего):");
        foreach (var s in system)
            sb.AppendLine($"- {s.Key} «{s.Title}»: {s.Text.Length} симв. ({s.Text.Length * 100 / total}%)");

        if (snapshot.McpServers.Count > 0)
            sb.AppendLine($"\nПодняты MCP-серверы: {string.Join(", ", snapshot.McpServers)}.");

        if (snapshot.CliLayer?.Tools is { Count: > 0 } tools)
            sb.AppendLine($"Инструментов у модели на этом ходу: {tools.Count} ({string.Join(", ", tools.Take(40))}).");

        if (snapshot.CliLayer?.TranscriptBytes is { } bytes)
            sb.AppendLine($"История разговора, которую подтягивает --resume: {bytes / 1024} КБ.");

        if (includeText)
        {
            sb.AppendLine("\nФрагменты секций (начало каждой):");
            var budget = MaxTotalChars;
            foreach (var s in system)
            {
                if (budget <= 0) break;
                var take = Math.Min(Math.Min(MaxSectionChars, budget), s.Text.Length);
                sb.AppendLine($"\n### {s.Key}\n{s.Text[..take]}");
                budget -= take;
            }
        }

        sb.AppendLine();
        sb.AppendLine("Ответь коротким markdown-списком: что дублируется между секциями, что не пригодилось этому ходу,");
        sb.AppendLine("что раздуто, и какую экономию (примерно, в символах или процентах) даст каждая правка.");
        sb.AppendLine("Без вступлений и пересказа — только выводы и рекомендации. По-русски.");
        if (!includeText)
            sb.AppendLine("Учти: тексты секций тебе не показаны, только их размеры — не выдумывай их содержимое.");

        return sb.ToString();
    }
}
