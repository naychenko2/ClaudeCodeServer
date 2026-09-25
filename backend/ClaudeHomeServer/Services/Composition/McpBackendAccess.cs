namespace ClaudeHomeServer.Services.Composition;

// Реализация IMcpBackendAccess: адрес и токен берутся теми же функциями, что и для конфига
// серверного хода, — второго правила про адрес Kestrel и второго кэша токенов не заводим.
// Адрес — без владельца: шлюз звонит из процесса бэкенда, а не из песочницы.
public sealed class McpBackendAccess(SessionManager sessions) : IMcpBackendAccess
{
    public string ApiUrl => sessions.ResolveTasksApiUrl();

    public string ServiceTokenFor(string ownerId) => sessions.GetServiceToken(ownerId);
}
