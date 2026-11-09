using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Есть ли у сессии-исполнителя инструмент с таким именем — по профилю облегчённого контекста
// её модели (чистая функция, тестируется без сессии). Та же ось, что у McpToolWhitelist и
// урезания серверов: целые серверы — TrimMcpServers/KeepMcpServers, инструменты внутри сервера —
// KeepMcpTools, встроенные инструменты CLI под --bare — BareTools. Всё, что профиль не ограничивает,
// считаем известным: ошибка «известен» дешевле (повтор формата), чем ошибка «неизвестен»
// (модель бросит реальный инструмент).
public static class ExecutorToolAvailability
{
    private const string McpPrefix = "mcp__";

    public static bool IsKnown(string tool, LightProfile profile)
    {
        if (string.IsNullOrWhiteSpace(tool)) return true;
        if (tool.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            var rest = tool[McpPrefix.Length..];
            var sep = rest.IndexOf("__", StringComparison.Ordinal);
            if (sep <= 0) return true;
            var server = rest[..sep];
            var name = rest[(sep + 2)..];
            if (profile.TrimMcpServers && profile.KeepMcpServers.Length > 0
                && !profile.KeepMcpServers.Contains(server, StringComparer.OrdinalIgnoreCase))
                return false;
            foreach (var (key, tools) in profile.KeepMcpTools)
                if (string.Equals(key, server, StringComparison.OrdinalIgnoreCase))
                    return tools is not { Length: > 0 } || tools.Contains(name, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        return !profile.BareMode || profile.BareTools.Length == 0
            || profile.BareTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
    }

    // Состав для директивы: встроенные инструменты и MCP как mcp__сервер__инструмент
    // (сервер без белого списка — mcp__сервер__*).
    public static IReadOnlyList<string> Describe(LightProfile profile)
    {
        var list = new List<string>();
        if (profile.BareMode) list.AddRange(profile.BareTools);
        var servers = profile.TrimMcpServers && profile.KeepMcpServers.Length > 0
            ? profile.KeepMcpServers
            : profile.KeepMcpTools.Keys.ToArray();
        foreach (var server in servers)
        {
            string[]? tools = null;
            foreach (var (key, value) in profile.KeepMcpTools)
                if (string.Equals(key, server, StringComparison.OrdinalIgnoreCase)) { tools = value; break; }
            if (tools is { Length: > 0 }) list.AddRange(tools.Select(t => $"{McpPrefix}{server}__{t}"));
            else list.Add($"{McpPrefix}{server}__*");
        }
        return list;
    }
}
