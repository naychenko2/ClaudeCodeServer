namespace ClaudeHomeServer.Services.ChatContext;

// Читатель и писатель контекста чата. Проверку Kind/Role/Ref делает IContextKindProvider владельца,
// стор пускает только зарегистрированные виды. revision == null — без сверки (так пишет агент).
public interface IChatContextStore
{
    ChatContextState Get(string ownerId, string sessionId);
    ChatContextState SetPrimary(string ownerId, string sessionId, ContextItem? item, long? revision);
    ChatContextState AddRef(string ownerId, string sessionId, ContextItem item, long? revision);
    ChatContextState RemoveRef(string ownerId, string sessionId, string itemId, long? revision);
    ChatContextState Clear(string ownerId, string sessionId, long? revision);
    // Объект исчез (нить удалена, файл удалён) — убрать его отовсюду без ревизии
    void Forget(string ownerId, string sessionId, Func<ContextItem, bool> match);
}
