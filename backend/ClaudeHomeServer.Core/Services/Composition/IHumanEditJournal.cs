namespace ClaudeHomeServer.Services.Composition;

// Кто и когда последним записал путь, ещё не закоммиченный (S3-3, «Общие проекты»).
public sealed record HumanEditEntry(string UserId, DateTimeOffset At);

// Журнал правок человека в общем проекте: вызывающая сторона (файловый менеджер, заметки)
// сообщает «пользователь U записал путь P», а журнал сам коммитит ТОЛЬКО записанные пути
// с автором-человеком после паузы без новых записей (дебаунс). Контракт в Core, реализация —
// HumanEditCommitter (корень Services).
// userId обязан приходить из JWT вызывающей стороны, а не из тела запроса: журнал ему
// доверяет и подставляет автором коммита.
// Личный проект и каталог без git-репозитория — молчаливый no-op: ни коммитов, ни ошибок.
public interface IHumanEditJournal
{
    void Record(string projectId, string root, string relPath, string userId);

    // Последний записавший путь человек, пока правка не попала в коммит; иначе null.
    HumanEditEntry? PendingAuthor(string root, string relPath);
}
