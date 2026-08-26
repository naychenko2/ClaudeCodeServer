using System.Collections.Concurrent;
using ClaudeHomeServer.Telemetry;

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

    // Потолок различных строк-инструментов в таблице. Ключ приходит снаружи (заголовок
    // MCP-сервера, а без него — путь запроса с GUID), поэтому без потолка словарь на
    // долгоживущем процессе растёт неограниченно. Всё сверх — в общую строку Overflow.
    private const int MaxTools = 512;
    private const string Overflow = "(прочее)";

    /// <summary>
    /// Ключ в <c>HttpContext.Items</c>: запрос учитывать не надо. Ставится там, где отказ —
    /// часть штатного протокола, а не сбой инструмента: у MCP-over-HTTP это GET на транспорт
    /// (клиент пробует SSE-канал, получает 405 и спокойно живёт дальше). Без пропуска каждый
    /// ход давал бы «отказ MCP» в GET /api/mcp/calls и в алерте 04-mcp-errors.
    /// </summary>
    public const string SkipItemKey = "mcp.call.skip";

    private readonly ConcurrentDictionary<string, ToolCounters> _byTool = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<McpCallFailure> _failures = new();

    private sealed class ToolCounters
    {
        public long Calls;
        public long Failures;
        public long TotalMs;
    }

    /// <summary>
    /// Учесть вызов. <paramref name="tool"/> — значение заголовка <c>X-Mcp-Tool</c>;
    /// null/пусто означает, что инструмент не назвался (старая версия сервера в песочнице,
    /// чужой клиент с тем же заголовком).
    /// </summary>
    public void Record(string? tool, string? sessionId, string path, int statusCode, long elapsedMs)
    {
        // Имя для таблицы диагностики: безымянный вызов показываем вместе с путём —
        // иначе непонятно, какой эндпоинт дёргают без имени инструмента.
        var display = string.IsNullOrEmpty(tool) ? $"(без имени) {path}" : tool;
        if (!_byTool.ContainsKey(display) && _byTool.Count >= MaxTools) display = Overflow;

        var counters = _byTool.GetOrAdd(display, _ => new ToolCounters());
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.TotalMs, elapsedMs);

        // OTel-метрики (ccs.mcp.calls / ccs.mcp.errors). Раньше RecordMcp* были
        // определены в ServerMetrics, но никто их не вызывал — мёртвые счётчики.
        // Единая точка записи — здесь, рядом с in-memory агрегацией, без дублей.
        //
        // В метрику идёт СЫРОЕ значение заголовка, а не display: путь с GUID в теге
        // tool_name — это и взрыв кардинальности, и PII в сторе, который живёт до конца
        // retention. Ограничитель значений — MetricTagGuard внутри ServerMetrics
        // (безымянный вызов схлопывается в "unnamed", мусор — в "other").
        var metricTool = tool ?? "";
        var outcome = statusCode < 400 ? "success" : "error";
        ServerMetrics.RecordMcpCall(metricTool, outcome);
        if (statusCode >= 400)
            ServerMetrics.RecordMcpError(metricTool, "http_" + statusCode);

        if (statusCode < 400) return;

        Interlocked.Increment(ref counters.Failures);
        _failures.Enqueue(new McpCallFailure(DateTime.UtcNow, display, sessionId, path, statusCode, elapsedMs));
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
