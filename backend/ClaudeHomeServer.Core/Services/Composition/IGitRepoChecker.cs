using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов GitService (вертикаль Git) для выноса Deploy (Этап 5, волна 2).
// DeployHost.GitSnapshotAsync проверяет, жив ли git-репозиторий: HEAD + dirty-дерево.
// Контракт в Core, реализация — сам GitService (вертикаль Git).
public interface IGitRepoChecker
{
    bool IsGitRepo(string path);
    Task<GitRepoSnapshot> RepoSnapshotAsync(string? ownerId, string path, CancellationToken ct = default);
}
