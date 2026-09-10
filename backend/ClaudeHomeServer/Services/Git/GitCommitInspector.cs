namespace ClaudeHomeServer.Services.Git;

// Тонкий адаптер `IGitCommitInspector` → `GitService` (Этап 5, волна 2).
// Реализация в Core, реализация-форма — здесь, в Main: вертикаль Git реализует
// generic plumbing (GitService), а форвардер для узкого шова живёт в композиции
// рядом с прочими forwarder'ами. Это позволяет Dossiers зависеть от Core
// (IGitCommitInspector) без прямой ссылки на вертикаль Git.
//
// Каждый метод — однострочный форвардер с теми же аргументами; никакой логики
// здесь быть не должно (контракт Core — это поведение GitService для этих пяти
// методов; если поведение изменится — менять и форвардер, и контракт одновременно).
public sealed class GitCommitInspector(GitService git) : IGitCommitInspector
{
    public Task<IReadOnlyList<string>> ChangedFilePathsAsync(string? ownerId, string root, string sha) =>
        git.ChangedFilePathsAsync(ownerId, root, sha);

    public Task<string> CommitStatAsync(string? ownerId, string root, string sha) =>
        git.CommitStatAsync(ownerId, root, sha);

    public Task<bool> IsAncestorAsync(string? ownerId, string root, string ancestor, string descendant) =>
        git.IsAncestorAsync(ownerId, root, ancestor, descendant);

    public Task<int> ParentCountAsync(string? ownerId, string root, string sha) =>
        git.ParentCountAsync(ownerId, root, sha);

    public Task<string?> LogNumstatRangeAsync(string? ownerId, string root, string fromSha,
        IReadOnlyList<string> files) =>
        git.LogNumstatRangeAsync(ownerId, root, fromSha, files);
}