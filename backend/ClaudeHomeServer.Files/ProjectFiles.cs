using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Files;

// Серверная реализация шва IProjectFiles поверх FileService. Корень берётся только через
// guard файловой группы (ADR-016, сторож G1): у локального проекта файлы на устройстве,
// и отказ LocalProjectException случается до обращения к диску. Событие OnMutated —
// ретрансляция FileService.OnMutated, так что подписчики (синк знаний) видят и мутации
// через этот шов.
public sealed class ProjectFiles : IProjectFiles
{
    private readonly FileService _files;

    public ProjectFiles(FileService files)
    {
        _files = files;
        _files.OnMutated += (root, rel, kind, newRel) => OnMutated?.Invoke(root, rel, kind, newRel);
    }

    public event Action<string, string, FileMutationKind, string?>? OnMutated;

    public Task<IReadOnlyList<FileEntry>> ListAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default) =>
        Run<IReadOnlyList<FileEntry>>(project, root => _files.List(root, relativePath, showHidden).ToList());

    public Task<IReadOnlyList<FileEntry>> TreeAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default) =>
        Run<IReadOnlyList<FileEntry>>(project, root => _files.Tree(root, relativePath, showHidden).ToList());

    public Task<IReadOnlyList<FileEntry>> SearchAsync(Project project, string query, CancellationToken ct = default) =>
        Run<IReadOnlyList<FileEntry>>(project, root => _files.Search(root, query).ToList());

    public Task<string> ReadFileAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, root => _files.ReadFile(root, relativePath));

    public Task<byte[]> ReadFileBytesAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, root => _files.ReadFileBytes(root, relativePath));

    public Task WriteFileAsync(Project project, string relativePath, string content, CancellationToken ct = default) =>
        Run(project, root => _files.WriteFile(root, relativePath, content));

    public Task WriteFileBytesAsync(Project project, string relativePath, byte[] content, CancellationToken ct = default) =>
        Run(project, root => _files.WriteFileBytes(root, relativePath, content));

    public Task CreateFileAsync(Project project, string relativePath, string content = "", CancellationToken ct = default) =>
        Run(project, root => _files.CreateFile(root, relativePath, content));

    public Task CreateDirectoryAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, root => _files.CreateDirectory(root, relativePath));

    public Task DeleteAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, root => _files.Delete(root, relativePath));

    public Task RenameAsync(Project project, string oldRelative, string newRelative, CancellationToken ct = default) =>
        Run(project, root => _files.Rename(root, oldRelative, newRelative));

    public async Task<string?> GetDiffAsync(Project project, string relativePath, CancellationToken ct = default) =>
        await _files.GetDiffAsync(ProjectCapabilityGuard.ServerRoot(project), relativePath, ct);

    public Task<bool> RevertFileAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, root => _files.RevertFile(root, relativePath));

    // Синхронные операции FileService под асинхронным контрактом: исключение (в том числе
    // отказ guard'а) уходит в задачу, а не бросается из вызова метода.
    private static Task<T> Run<T>(Project project, Func<string, T> op)
    {
        try { return Task.FromResult(op(ProjectCapabilityGuard.ServerRoot(project))); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }

    private static Task Run(Project project, Action<string> op) =>
        Run<object?>(project, root => { op(root); return null; });
}
