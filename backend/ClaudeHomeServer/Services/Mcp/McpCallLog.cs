using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Учёт вызовов продуктовых MCP-серверов (mcp/*) к бэкенду: счётчики по инструментам плюс
/// кольцевой буфер последних сбоев.
///
/// Зачем: наблюдаемости у MCP не было вовсе. Разбор жалобы «инструменты отваливаются» пришлось
/// вести вручную по data/sessions/*/history.json за 288 сессий — единственному месту, где
/// оставался след вызова. Со стороны бэкенда не было видно ни частоты вызовов, ни доли отказов,
/// ни того, какой инструмент отказывает. Здесь этот след появляется на стороне сервера.
///
/// Хранение — только в памяти: данные диагностические, переживать рестарт им незачем, а писать
/// их в data/ нельзя без оглядки на бэкапы (см. «Новое хранилище → сверься с бэкапом» в CLAUDE.md).
/// </summary>
public sealed class McpCallLog
{
    // Хватает, чтобы увидеть картину сбоя, и не растёт бесконечно на долгоживущем процессе
    private const int MaxFailures = 200;

    private readonly ConcurrentDictionary<string, ToolCounters> _byTool = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<McpCallFailure> _failures = new();

    private sealed class ToolCounters
    {
        public long Calls;
        public long Failures;
        public long TotalMs;
    }

    public void Record(string tool, string? sessionId, string path, int statusCode, long elapsedMs)
    {
        var counters = _byTool.GetOrAdd(tool, _ => new ToolCounters());
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.TotalMs, elapsedMs);
        if (statusCode < 400) return;

        Interlocked.Increment(ref counters.Failures);
        _failures.Enqueue(new McpCallFailure(DateTime.UtcNow, tool, sessionId, path, statusCode, elapsedMs));
        // Кольцо: держим только хвост
        while (_failures.Count > MaxFailures && _failures.TryDequeue(out _)) { }
    }

    /// <summary>Сводка по инструментам, самые проблемные — первыми.</summary>
    public IReadOnlyList<McpToolStat> Stats() =>
        [.. _byTool
            .Select(kv => new McpToolStat(
                kv.Key,
                Interlocked.Read(ref kv.Value.Calls),
                Interlocked.Read(ref kv.Value.Failures),
                Interlocked.Read(ref kv.Value.Calls) is var c && c > 0
                    ? (int)(Interlocked.Read(ref kv.Value.TotalMs) / c) : 0))
            .OrderByDescending(s => s.Failures)
            .ThenByDescending(s => s.Calls)];

    /// <summary>Последние сбои, свежие — первыми.</summary>
    public IReadOnlyList<McpCallFailure> RecentFailures(int limit = 50) =>
        [.. _failures.Reverse().Take(Math.Clamp(limit, 1, MaxFailures))];
}

public record McpToolStat(string Tool, long Calls, long Failures, int AvgMs);

public record McpCallFailure(
    DateTime At, string Tool, string? SessionId, string Path, int StatusCode, long ElapsedMs);
