namespace ClaudeHomeServer.Services.Composition;

// Шов шлюза MCP (ADR-016) к MCP-over-HTTP бэкенда: куда звонить и чем подписаться.
//
// Шлюз живёт в вертикали Llm, а адрес Kestrel и выпуск сервисного JWT — зона Main
// (SessionManager.ResolveTasksApiUrl / GetServiceToken). Токен тот же, что уезжает в конфиг
// серверного хода: один owner-scoped JWT на владельца, наружу шлюз его не отдаёт никогда.
public interface IMcpBackendAccess
{
    // Базовый http-адрес бэкенда без завершающего «/» — к нему шлюз дописывает /mcp/{name}.
    string ApiUrl { get; }

    string ServiceTokenFor(string ownerId);
}
