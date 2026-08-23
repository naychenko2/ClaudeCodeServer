using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Локальный контекст затронутых чатов — то, чего в телеметрии нет и быть не должно:
/// имя чата и проект, расход за окно, отказы MCP этого чата.
///
/// Отдельный слой, а не код внутри <see cref="IncidentDossierService"/>: сборка досье —
/// чистая работа с ответами SigNoz, и держать её проверяемой без запуска SessionManager,
/// SpendStore и McpCallLog важнее, чем сэкономить один интерфейс.
/// </summary>
public interface IIncidentLocalContext
{
    /// <param name="alertChatId">
    /// Чат, названный самим алертом (метка <c>chat_id</c> у правил с разрезом по чату).
    /// Попадает в список даже без упавших ходов — иначе у «Ходы массово встали» раздел
    /// оказывался пустым при заведомо известном виновнике.
    /// </param>
    IReadOnlyList<IncidentChat> Describe(
        IReadOnlyList<IncidentTurn> turns, DateTimeOffset from, DateTimeOffset to,
        string? alertChatId);
}

public sealed class IncidentLocalContext(
    SessionManager sessions,
    SpendStore spend,
    McpCallLog mcpCalls,
    ILogger<IncidentLocalContext> log) : IIncidentLocalContext
{
    public IReadOnlyList<IncidentChat> Describe(
        IReadOnlyList<IncidentTurn> turns, DateTimeOffset from, DateTimeOffset to,
        string? alertChatId)
    {
        // Число упавших ходов на чат. Чат из меток алерта добавляется сюда с нулём: он
        // виновник по мнению самого правила, а падений у него может не быть вовсе.
        var byChat = turns
            .Where(t => !string.IsNullOrWhiteSpace(t.ChatId))
            .GroupBy(t => t.ChatId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(alertChatId)) byChat.TryAdd(alertChatId, 0);
        if (byChat.Count == 0) return [];

        var spendRows = SafeSpend(from, to);
        var failures = mcpCalls.RecentFailures();

        var result = new List<IncidentChat>();
        foreach (var (chatId, turnFailures) in byChat)
        {
            Session? session = null;
            try { session = sessions.GetById(chatId); }
            catch (Exception ex) { log.LogDebug(ex, "Чат {ChatId} не найден при сборе досье", chatId); }

            var tokens = spendRows
                .Where(r => string.Equals(r.SessionId, chatId, StringComparison.Ordinal))
                .Sum(r => r.TotalTokens);

            var mcp = failures
                .Where(f => string.Equals(f.SessionId, chatId, StringComparison.Ordinal))
                .Select(f => f.Tool)
                .Distinct(StringComparer.Ordinal)
                .Take(IncidentQueries.RowLimit)
                .ToList();

            result.Add(new IncidentChat(
                ChatId: chatId,
                ProjectId: session?.ProjectId,
                Title: session?.Name,
                Failures: turnFailures,
                TotalTokens: tokens,
                McpFailures: mcp,
                FromAlert: string.Equals(chatId, alertChatId, StringComparison.Ordinal)));
        }
        // Названный алертом — первым: по падениям он может быть последним (их ноль),
        // а разбирать инцидент начинают именно с него.
        return [.. result.OrderByDescending(c => c.FromAlert).ThenByDescending(c => c.Failures)];
    }

    private IReadOnlyList<SpendRecord> SafeSpend(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            return spend.DetailsBetween(
                DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime));
        }
        catch (Exception ex)
        {
            // Расход — украшение досье: терять из-за него всю карточку не стоит
            log.LogDebug(ex, "Расход за окно инцидента прочитать не удалось");
            return [];
        }
    }
}
