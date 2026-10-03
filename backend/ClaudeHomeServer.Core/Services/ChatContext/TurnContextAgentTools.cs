namespace ClaudeHomeServer.Services.ChatContext;

// Инструменты MCP-сервера turn-context, которые чат выполняет без карточки разрешения (ADR-023 §3.2, как
// AudioEditorAgentTools): правка контекста агентом видна человеку в строке контекста (чип ✦), а удалить
// чужое нельзя — context_detach убирает только референсы. Список едет в ход через TurnContextMcpContext и
// проверяется в ClaudeSession.DecidePermission. Имена обязаны совпадать со схемами тулсета — это держит тест.
public static class TurnContextAgentTools
{
    public static readonly IReadOnlyList<string> AutoAllowTools =
    [
        "mcp__turn-context__context_state",
        "mcp__turn-context__context_attach",
        "mcp__turn-context__context_detach",
    ];
}
