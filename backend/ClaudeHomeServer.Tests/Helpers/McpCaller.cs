namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Заголовок чата-вызывателя, который в бою кладёт конфиг хода в каждый http-MCP. Узкая рамка
/// (S2a-3a) читает вердикт по этому заголовку, а вызов без него считает узким (fail-closed):
/// тестам, зовущим инструменты «от лица чата», заголовок нужен так же, как настоящему ходу.
/// </summary>
public static class McpCaller
{
    /// <summary>Выставляет X-Caller-Session-Id клиента и возвращает его же.</summary>
    public static HttpClient AsCaller(this HttpClient client, string sessionId)
    {
        client.DefaultRequestHeaders.Remove("X-Caller-Session-Id");
        client.DefaultRequestHeaders.Add("X-Caller-Session-Id", sessionId);
        return client;
    }
}
