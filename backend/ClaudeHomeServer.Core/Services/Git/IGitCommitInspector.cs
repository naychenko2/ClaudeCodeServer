namespace ClaudeHomeServer.Services.Git;

// Узкий шов инспекции коммитов (Этап 5, волна 2, разрез Dossiers↔Git).
// Контракт ОДНОГО намерения — «прочитать данные конкретного коммита»: список измен
//, статистика, число родителей, достижимость, статистика диапазона. Все пять
// методов описывают одну операцию (что у коммита внутри), и единственный потребитель —
// Dossiers (DossierCaptureService, DossierRecallService). Контракт узкий
// по фактическим вызовам, не «грабмешок»: разведка 2026-09-07 отклонила общий
// IGitGuard на все нужды Deploy и Dossiers сразу как 7+ методов с 5 намерениями —
// здесь же все намерения сводятся к инспекции коммита.
//
// Реализация — `GitCommitInspector` (Main, тонкий форвардер на `GitService`).
// По той же причине, что и `IGitRefSnapshotStore`: контракт живёт в Core, а
// не в Services.Git, чтобы вертикаль-потребитель не получала запрещённую сторожем
// границ связь «вертикаль → вертикаль».
public interface IGitCommitInspector
{
    // Файлы, изменённые указанным коммитом (git show --name-only).
    // Используется DossierCaptureService для якорей паспорта.
    Task<IReadOnlyList<string>> ChangedFilePathsAsync(string? ownerId, string root, string sha);

    // Статистика коммита (numstat: добавлено/удалено по файлам).
    // Используется DossierCaptureService для промпта выжимки.
    Task<string> CommitStatAsync(string? ownerId, string root, string sha);

    // Достижим ли commit `ancestor` от `descendant` (`git merge-base --is-ancestor`).
    // Используется DossierCaptureService при переякорении squash-коммитов (§7):
    // старый sha паспорта недостижим от текущего HEAD — коммит был поглощён.
    Task<bool> IsAncestorAsync(string? ownerId, string root, string ancestor, string descendant);

    // Число родителей коммита (через %P — родительские sha через пробел).
    // 0 — корневой, 1 — обычный, 2+ — merge. Используется DossierCaptureService
    // как надёжный признак merge (conventional-тип не всегда разбирается).
    Task<int> ParentCountAsync(string? ownerId, string root, string sha);

    // `git log --format=%H --numstat <from>..HEAD -- <files>` для диапазона коммитов:
    // первый кусок stdout — sha коммитов между from и HEAD; второй — суммарная
    // статистика по указанным файлам. Используется DossierRecallService при
    // ленивом пересчёте статусов паспортов: «≤3 коммитов ИЛИ ≤30% строк — Active».
    Task<string?> LogNumstatRangeAsync(string? ownerId, string root, string fromSha,
        IReadOnlyList<string> files);
}

// Примитив спины для проверки «это git-репозиторий?» (Этап 5, волна 2).
// Раньше — статика `Git.GitService.IsGitRepo` (Main), использовалась в Dossiers
// и контроллерах. Префикс-шов `Services.Git` в Boundaries[Dossiers] держался
// ради этой единственной ссылки; сейчас примитив переехал в Core — префикс
// можно снимать. `Path.Exists(Path.Combine(root, ".git"))` точно соответствует
// прежней логике GitService.IsGitRepo: репозиторий — это наличие папки .git
// в корне (файл-ссылка у worktree тоже подхватывается через Path.Exists).
public static class GitRepo
{
    public static bool IsRepo(string root) => Path.Exists(Path.Combine(root, ".git"));
}