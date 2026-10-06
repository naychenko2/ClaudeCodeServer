namespace ClaudeHomeServer.Services.ChatContext;

// Отказ записи контекста: Code — одна из констант ChatContextErrors, контроллер делает из него 400
public class ChatContextException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Ревизия клиента устарела: Current — свежее состояние, контроллер делает из него 409 context_changed
public sealed class ChatContextConflictException(ChatContextState current)
    : ChatContextException(ChatContextErrors.ContextChanged, "Контекст чата изменился")
{
    public ChatContextState Current { get; } = current;
}
