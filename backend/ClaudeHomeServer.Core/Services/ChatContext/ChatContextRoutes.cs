namespace ClaudeHomeServer.Services.ChatContext;

// Маршруты контекста чата (ADR-023 §2.1, ChatContextController в Main). Владелец — из sub, сессия — GetOwned.
public static class ChatContextRoutes
{
    public const string Base = "api/chats/{sessionId}/context";
    public const string Primary = Base + "/primary";
    public const string Refs = Base + "/refs";
    public const string Ref = Base + "/refs/{itemId}";
    // [{path, threadKind, savedAt}] — сохранённые из нитей чата файлы, для commitViaChat (ADR §3.3)
    public const string SavedFiles = Base + "/saved-files";
}
