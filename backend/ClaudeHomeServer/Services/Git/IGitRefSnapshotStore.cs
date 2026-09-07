namespace ClaudeHomeServer.Services.Git;

// Идентичность коммита для write-операций над веткой-паспортом (автор + коммиттер).
// Отдельный тип: env-переменные GIT_AUTHOR_* / GIT_COMMITTER_* подменяются на вызов
// git (commit-tree), иначе серверный user.name/user.email уйдут в коммит как «root@…».
public sealed record GitRefIdentity(
    string AuthorName, string AuthorEmail,
    string CommitterName, string CommitterEmail);

// Узкий интерфейс работы с произвольной веткой-паспортом (commit-on-plumbing): запись
// полного дерева, резолв рефа локально или в origin, чтение списка файлов и содержимого,
// tip с автором, push. Реализация — GitService: там же `RunAsync`/`RunOkAsync`, per-repo
// `SemaphoreSlim`, `HostGitPath` и CAS-логика через `update-ref old new`.
//
// Идентичность коммита (`GitRefIdentity`), файл снапшота (`GitSnapshotFile`), итог записи
// (`GitRefSnapshotResult`) и tip ветки (`GitRefTip`) — публичные record-типы самого
// GitService (рядом с `GitCredentials`); контракт держится вместе, чтобы вертикаль-потребитель
// видела аргументы и возвращаемые значения единым блоком.
//
// Вертикали, ведущие свою ветку-паспорт, объявляют имя рефа и идентичность коммита
// как собственные константы (см. DossierBranch в Dossiers) и зовут методы с ними в
// качестве аргументов. Ветка не принадлежит GitService — это generic plumbing-приём.
public interface IGitRefSnapshotStore
{
    // Записать ПОЛНЫЙ снапшот файлов в ветку строго через плюминг (hash-object → временный
    // индекс → write-tree → commit-tree → update-ref old new). Рабочее дерево, индекс и
    // HEAD пользователя не затрагиваются. Первый коммит ветки — без родителя. Created=false
    // если дерево совпало с tip ветки и новый коммит не создавался.
    Task<GitRefSnapshotResult> WriteSnapshotAsync(string? ownerId, string root, string fullRef,
        IReadOnlyList<GitSnapshotFile> files, string message, GitRefIdentity identity,
        CancellationToken ct = default);

    // Резолв рефа из списка: возвращает имя первого существующего рефа либо null. Типовая
    // схема «локальная ветка → remote-tracking той же ветки»: caller передаёт обе и
    // получает имя того, что реально живёт в репозитории.
    Task<string?> ResolveRefAsync(string? ownerId, string root, IReadOnlyList<string> candidates,
        CancellationToken ct = default);

    // Существует ли реф (rev-parse --verify --quiet + валидный sha в stdout): локальная
    // ветка, remote-tracking или произвольный полный реф — generic.
    Task<bool> RefExistsAsync(string? ownerId, string root, string fullRef,
        CancellationToken ct = default);

    // Локальный tip ветки: rev-parse --verify --quiet на полный локальный реф (НЕ
    // remote-tracking). null — локальной ветки нет; remote-tracking мог остаться.
    Task<string?> LocalTipAsync(string? ownerId, string root, string fullRef,
        CancellationToken ct = default);

    // Список blob-файлов дерева ветки (рекурсивно, пути от корня ветки). Ветка отсутствует
    // либо дерево пустое — пустой список, без ошибки.
    Task<IReadOnlyList<string>> ListFilesAsync(string? ownerId, string root, string resolvedRef,
        CancellationToken ct = default);

    // Содержимое файла ветки по относительному пути. null — ветки либо файла нет, или
    // содержимое бинарное. Чтение по полному рефу — рабочее дерево, индекс и HEAD
    // пользователя не участвуют.
    Task<string?> ReadFileAsync(string? ownerId, string root, string resolvedRef, string relPath,
        CancellationToken ct = default);

    // Tip ветки: автор и дата последнего коммита (для пометки происхождения при импорте).
    // null — ветки нет. ref должен быть уже резолвленным (ResolveRefAsync).
    Task<GitRefTip?> TipAsync(string? ownerId, string root, string resolvedRef,
        CancellationToken ct = default);

    // Ручная публикация ветки в origin по полному рефу: уезжает ровно эта ветка, upstream
    // текущей ветки рабочего дерева не трогаем. Только по явной команде пользователя.
    Task PushRefAsync(string? ownerId, string root, string fullRef, GitCredentials? creds = null,
        CancellationToken ct = default);
}
