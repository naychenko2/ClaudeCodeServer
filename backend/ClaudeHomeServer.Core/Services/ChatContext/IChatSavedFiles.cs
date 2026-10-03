namespace ClaudeHomeServer.Services.ChatContext;

// Файлы, которые человек сохранил из нитей этого чата (ADR-023 §3.3): их список дополняет
// промпт поручения «Зафиксировать только этот чат». Каждая вертикаль отдаёт свои, спина склеивает
// IEnumerable<IChatSavedFiles>. ThreadKind — вид нити ("image", "audio").
public interface IChatSavedFiles
{
    IReadOnlyList<ChatSavedFile> List(ContextScope scope);
}

public sealed record ChatSavedFile(string Path, string ThreadKind, DateTime SavedAt);

// След сохранения в нити: хранится в самой нити (журнал событий обрезается), отсюда вертикаль читает IChatSavedFiles
public sealed record ThreadSavedFile(string Path, DateTime SavedAt);
