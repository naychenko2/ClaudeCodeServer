namespace ClaudeHomeServer.Services.Git;

// Идентичность коммита для write-операций над веткой-паспортом (автор + коммиттер).
// Отдельный тип: env-переменные GIT_AUTHOR_* / GIT_COMMITTER_* подменяются на вызов
// git (commit-tree), иначе серверный user.name/user.email уйдут в коммит как «root@…».
public sealed record GitRefIdentity(
    string AuthorName, string AuthorEmail,
    string CommitterName, string CommitterEmail);

// Контракт ветки-паспорта. Объявлен в Core (а не в ClaudeHomeServer.Git/Services/Git/)
// по той же причине, что и шина событий хода TurnEventContracts (ADR-013): контракт
// ДОЛЖЕН жить там, где его читают потребители. Вертикали, ведущие свою ветку-паспорт
// (Dossiers — `ccs/dossiers/v1`), объявляют имя рефа и идентичность коммита как
// собственные константы (см. DossierBranch) и зовут методы с ними в качестве аргументов.
// Ветка не принадлежит GitService — это generic plumbing-приём.
//
// Реализация — `GitService : IGitRefSnapshotStore` в ClaudeHomeServer.Git (вертикаль).
// Без переноса контракта в Core любая вертикаль, которой нужен generic plumbing
// (Dossiers, и в будущем — другие «ветки-паспорта»), получала бы запрещённую сторожем
// границ связь «вертикаль → вертикаль» через ссылку на тип из Services.Git. Здесь же
// контракт лежит в спине (Core) и доступен всем, кто зависит только от Core.
//
// Сопутствующие record-типы (`GitRefIdentity`, `GitRefTip`, `GitRefSnapshotResult`,
// `GitCredentials`, `GitSnapshotFile`) — публичные типы, упомянутые в контракте.
// Держатся вместе с интерфейсом, чтобы вертикаль-потребитель видела аргументы и
// возвращаемые значения единым блоком.

// Креды HTTP-remote (Forgejo): логин + персональный токен пользователя
public sealed record GitCredentials(string Username, string Token);

// Файл снапшота ветки-паспорта: путь внутри ветки + текстовое содержимое. При дубле пути
// в наборее побеждает последняя запись (update-index перезапишет запись индекса).
public sealed record GitSnapshotFile(string Path, string Content);

// Итог записи ветки-паспорта: Created=false — дерево снапшота совпало с последним
// коммитом ветки и новый коммит не создавался; CommitSha — tip ветки в обоих случаях.
public sealed record GitRefSnapshotResult(bool Created, string CommitSha);

// Tip ветки-паспорта: реф, коммит, автор и дата последнего коммита — происхождение данных
// при обратном чтении ветки (импорт «Историй решений»).
public sealed record GitRefTip(string Ref, string CommitSha, string Author, DateTimeOffset Date);

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