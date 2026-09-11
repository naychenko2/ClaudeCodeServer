namespace ClaudeHomeServer.Services.Git;

// Срез git-репозитория: HEAD + dirty-файлы + опциональный текст ошибки.
// Контракт в Core (шиной для вертикали Deploy через `IGitRepoChecker`);
// единственная реализация — `GitService.RepoSnapshotAsync` в вертикали Git.
public sealed record GitRepoSnapshot(string? ShortHeadSha, IReadOnlyList<string> DirtyPaths, string? Error = null);
