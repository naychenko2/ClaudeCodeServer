namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Реестр тулсетов MCP-over-HTTP: имя сервера → реализация. Заполняется из DI, поэтому
/// новый сервер подключается одной регистрацией <c>AddSingleton&lt;IMcpToolset, XToolset&gt;()</c>,
/// без правки контроллера. Дубль имени — ошибка старта: два тулсета на одном URL молча
/// перекрыли бы друг друга, а инструмент пропал бы у модели без единого сообщения.
/// </summary>
public sealed class McpToolsetRegistry
{
    private readonly Dictionary<string, IMcpToolset> _byName;

    public McpToolsetRegistry(IEnumerable<IMcpToolset> toolsets)
    {
        _byName = new Dictionary<string, IMcpToolset>(StringComparer.Ordinal);
        foreach (var toolset in toolsets)
        {
            if (!_byName.TryAdd(toolset.Name, toolset))
                throw new InvalidOperationException(
                    $"MCP-тулсет с именем «{toolset.Name}» зарегистрирован дважды");
        }
    }

    public IMcpToolset? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyCollection<string> Names => _byName.Keys;
}
