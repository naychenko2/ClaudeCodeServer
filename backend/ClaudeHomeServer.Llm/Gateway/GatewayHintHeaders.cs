using System.Text;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Gateway hint headers (CLI CLAUDE_CODE_GATEWAY_HINT_HEADERS=1, ADR-029): per-request
// attribution facts the LLM-шлюз читает для статистики локальных моделей, не разбирая тел.
// CLI шлёт их только на запусках, идущих через шлюз (мы сами включаем переменную, см.
// ClaudeSession). Значения — недоверенный ввод: валидируем по фиксированным словарям /
// printable ASCII и обрезаем; сырые в лог не пишем.
public sealed record GatewayHintHeaders
{
    // x-claude-code-request-class: main | subagent | workflow | compaction | auxiliary.
    // «Какой это запрос» — на каждом запросе хода.
    public string? RequestClass { get; init; }

    // x-claude-code-agent-type: встроенное имя агента (Explore, Plan, general-purpose, …)
    // или custom | teammate | fork. Только на собственных ходах сабагента.
    public string? AgentType { get; init; }

    // x-claude-code-compaction: auto | manual | reactive — только на запросе-суммаризации.
    public string? Compaction { get; init; }

    // x-claude-code-context-compacted: auto | manual | reactive — один раз, на первом
    // основном запросе после компакции (префикс до него больше не используется).
    public string? ContextCompacted { get; init; }

    // Потолок длины значения: обрезаем длиннее (docs: «all values are printable ASCII»).
    public const int MaxValueLength = 64;

    public const string RequestClassHeader = "x-claude-code-request-class";
    public const string AgentTypeHeader = "x-claude-code-agent-type";
    public const string CompactionHeader = "x-claude-code-compaction";
    public const string ContextCompactedHeader = "x-claude-code-context-compacted";

    // Фиксированные словари (docs: «fixed vocabularies … all values are printable ASCII»).
    // Все значения в нижнем регистре; сравнение без учёта регистра.
    public static readonly HashSet<string> RequestClasses = new(StringComparer.OrdinalIgnoreCase)
        { "main", "subagent", "workflow", "compaction", "auxiliary" };
    public static readonly HashSet<string> CompactionKinds = new(StringComparer.OrdinalIgnoreCase)
        { "auto", "manual", "reactive" };

    // Из заголовков запроса. null — подсказочных заголовков нет (переменная выключена,
    // старый CLI, или запрос без атрибуции).
    public static GatewayHintHeaders? From(IHeaderDictionary headers) =>
        FromValues(
            First(headers, RequestClassHeader),
            First(headers, AgentTypeHeader),
            First(headers, CompactionHeader),
            First(headers, ContextCompactedHeader));

    // Ядро разбора (тестируется без ASP.NET): сырые значения заголовков → валидированная
    // запись. null — ни одного заголовка не пришло.
    public static GatewayHintHeaders? FromValues(string? requestClass, string? agentType,
        string? compaction, string? contextCompacted)
    {
        var rc = CleanEnum(requestClass, RequestClasses);
        var at = CleanAscii(agentType);
        var c = CleanEnum(compaction, CompactionKinds);
        var cc = CleanEnum(contextCompacted, CompactionKinds);
        if (rc is null && at is null && c is null && cc is null) return null;
        return new GatewayHintHeaders
        {
            RequestClass = rc,
            AgentType = at,
            Compaction = c,
            ContextCompacted = cc,
        };
    }

    private static string? First(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    // Фиксированный словарь: trim + нижний регистр; не из словаря → null (недоверенный).
    internal static string? CleanEnum(string? raw, HashSet<string> allowed)
    {
        var v = CleanAscii(raw);
        if (v is null) return null;
        return allowed.Contains(v) ? v.ToLowerInvariant() : null;
    }

    // Printable ASCII (0x20..0x7E), trim, обрезка до MaxValueLength; пустое/отсутствующее → null.
    // Обрываем на первом не-ASCII: дальше значение уже не «чистое» (docs: все значения ASCII).
    internal static string? CleanAscii(string? raw)
    {
        if (raw is null) return null;
        var sb = new StringBuilder(Math.Min(raw.Length, MaxValueLength));
        foreach (var ch in raw)
        {
            if (ch < 0x20 || ch > 0x7E) break;
            sb.Append(ch);
            if (sb.Length == MaxValueLength) break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }
}
