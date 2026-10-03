namespace ClaudeHomeServer.Services.ChatContext;

// Стор сообщает о каждой записи, поменявшей состояние; рассылку владельцу делает реализация
// (ChatContextBroadcaster). Вне цепочки записи: сбой рассылки не откатывает и не ломает запись
public interface IChatContextNotifier
{
    void Changed(string ownerId, string sessionId, ChatContextState state);
}
