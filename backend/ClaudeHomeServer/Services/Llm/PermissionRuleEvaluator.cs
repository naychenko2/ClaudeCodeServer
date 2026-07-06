using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Применение permission-правил проекта к вызову инструмента. Общий для всех адаптеров:
// Claude получает запросы через sdk_control_request, DeepSeek — в собственном tool-цикле.
public static class PermissionRuleEvaluator
{
    // Возвращает "deny" (запрет приоритетен), "allow" или null (решает пользователь)
    public static string? Evaluate(IReadOnlyList<PermissionRule>? rules, string toolName, JsonElement input)
    {
        if (rules is null || rules.Count == 0) return null;
        string? allow = null;
        foreach (var r in rules)
        {
            if (!RuleMatches(r, toolName, input)) continue;
            if (string.Equals(r.Action, "deny", StringComparison.OrdinalIgnoreCase)) return "deny";
            if (string.Equals(r.Action, "allow", StringComparison.OrdinalIgnoreCase)) allow = "allow";
        }
        return allow;
    }

    private static bool RuleMatches(PermissionRule rule, string toolName, JsonElement input)
    {
        var (tool, spec) = ParseRule(rule.Pattern);
        if (tool != "*" && !string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(spec)) return true; // правило по имени инструмента — матчит любой вызов
        return GlobMatch(spec, ExtractRuleArg(toolName, input));
    }

    // "Tool(specifier)" → (Tool, specifier); "Tool" → (Tool, null)
    private static (string tool, string? spec) ParseRule(string pattern)
    {
        pattern = pattern.Trim();
        var i = pattern.IndexOf('(');
        if (i < 0 || !pattern.EndsWith(")")) return (pattern, null);
        return (pattern[..i].Trim(), pattern[(i + 1)..^1]);
    }

    // Основной аргумент инструмента, по которому матчим specifier
    private static string? ExtractRuleArg(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var keys = toolName.ToLowerInvariant() switch
        {
            "bash" => new[] { "command" },
            "edit" or "write" or "read" or "notebookedit" => new[] { "file_path", "path" },
            "glob" or "grep" => new[] { "pattern", "path" },
            "webfetch" => new[] { "url" },
            "websearch" => new[] { "query" },
            _ => [],
        };
        foreach (var k in keys)
            if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    // Glob: '*' — любая подстрока; остальное — буквально, без учёта регистра
    private static bool GlobMatch(string pattern, string? value)
    {
        if (value is null) return false;
        var rx = "^" + string.Join(".*",
            pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
