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
    IReadOnlyList<IncidentChat> Describe(
        IReadOnlyList<IncidentTurn> turns, DateTimeOffset from, DateTimeOffset to);
}

public sealed class IncidentLocalContext(
    SessionManager sessions,
    SpendStore spend,
    McpCallLog mcpCalls,
    ILogger<IncidentLocalContext> log) : IIncidentLocalContext
{
    public IReadOnlyList<IncidentChat> Describe(
        IReadOnlyList<IncidentTurn> turns, DateTimeOffset from, DateTimeOffset to)
    {
        var byChat = turns
            .Where(t => !string.IsNullOrWhiteSpace(t.ChatId))
            .GroupBy(t => t.ChatId!, StringComparer.Ordinal)
            .ToList();
        if (byChat.Count == 0) return [];

        var spendRows = SafeSpend(from, to);
        var failures = mcpCalls.RecentFailures();

        var result = new List<IncidentChat>();
        foreach (var group in byChat)
        {
            var chatId = group.Key;
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
                Failures: group.Count(),
                TotalTokens: tokens,
                McpFailures: mcp));
        }
        return [.. result.OrderByDescending(c => c.Failures)];
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
