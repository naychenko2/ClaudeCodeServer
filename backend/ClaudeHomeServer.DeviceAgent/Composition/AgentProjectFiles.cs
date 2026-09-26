using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Шов <see cref="IProjectFiles"/> на машине проекта (ADR-016, задача 4.2): та же вертикаль
/// Files, что на сервере, только корень берётся не guard'ом «файлы на сервере», а политикой
/// агента (<see cref="AgentPathPolicy"/>): разрешённые корни машины, реальный путь, потолки.
/// Здесь ни одной собственной файловой операции — только проверка и делегирование
/// <see cref="FileService"/>; это сторожит тест «только композиция».
///
/// Проверка пути (<see cref="Check"/>) и открытие — два шага, между ними ссылку можно
/// подменить (TOCTOU). Поэтому <see cref="FileService"/> здесь всегда со сверщиком
/// <see cref="AgentOpenedPathGuard"/>: судит то, что реально открылось, по дескриптору.
/// </summary>
internal sealed class AgentProjectFiles : IProjectFiles
{
    private readonly FileService _files;
    private readonly AgentPathPolicy _policy;

    public AgentProjectFiles(FileService files, AgentPathPolicy policy)
    {
        _files = files.WithOpenedPathGuard(new AgentOpenedPathGuard());
        _policy = policy;
        _files.OnMutated += (root, rel, kind, newRel) => OnMutated?.Invoke(root, rel, kind, newRel);
    }

    public event Action<string, string, FileMutationKind, string?>? OnMutated;

    public void NotifyMutated(string root, string relativePath, FileMutationKind kind) =>
        _files.NotifyMutated(root, relativePath, kind);

    /// <summary>Тестовый шов: между проверкой пути и операцией — окно TOCTOU.</summary>
    internal Action<string>? AfterCheck { get; set; }

    public Task<IReadOnlyList<FileEntry>> ListAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) => _policy.Filter(root, _files.List(root, relativePath, showHidden)));

    public Task<IReadOnlyList<FileEntry>> TreeAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) => _policy.Filter(root, _files.Tree(root, relativePath, showHidden)));

    public Task<IReadOnlyList<FileEntry>> SearchAsync(Project project, string query, CancellationToken ct = default) =>
        Run(project, "", (root, _) => _policy.Filter(root, _files.Search(root, query), checkEach: true));

    public Task<string> ReadFileAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, full) =>
        {
            _policy.EnsureReadable(full);
            return _files.ReadFile(root, relativePath);
        });

    public Task<byte[]> ReadFileBytesAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, full) =>
        {
            _policy.EnsureReadable(full);
            return _files.ReadFileBytes(root, relativePath);
        });

    public Task<FileContentView> GetContentAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, full) =>
        {
            _policy.EnsureReadable(full);
            return FileContentReader.Read(_files, root, relativePath);
        });

    public Task<ProjectFileStream> OpenReadAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, full) =>
        {
            _policy.EnsureStreamable(full);
            var stream = _files.OpenRead(root, relativePath);
            return new ProjectFileStream(stream, stream.Length);
        });

    public Task WriteFileAsync(Project project, string relativePath, string content, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) =>
        {
            _policy.EnsureWritable(Encoding.UTF8.GetByteCount(content));
            _files.WriteFile(root, relativePath, content);
        });

    public Task WriteFileBytesAsync(Project project, string relativePath, byte[] content, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) =>
        {
            _policy.EnsureWritable(content.LongLength);
            _files.WriteFileBytes(root, relativePath, content);
        });

    /// <summary>Запись такого объёма допустима — проверка до того, как тело прочитано в память.</summary>
    public void EnsureWritable(long bytes) => _policy.EnsureWritable(bytes);

    public Task CreateFileAsync(Project project, string relativePath, string content = "", CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) =>
        {
            _policy.EnsureWritable(Encoding.UTF8.GetByteCount(content));
            _files.CreateFile(root, relativePath, content);
        });

    public Task CreateDirectoryAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) => _files.CreateDirectory(root, relativePath));

    public Task DeleteAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) => _files.Delete(root, relativePath));

    public Task RenameAsync(Project project, string oldRelative, string newRelative, CancellationToken ct = default) =>
        Run(project, oldRelative, (root, _) => _files.Rename(root, oldRelative, newRelative), alsoCheck: newRelative);

    public async Task<string?> GetDiffAsync(Project project, string relativePath, CancellationToken ct = default)
    {
        var (root, _) = Check(project, relativePath);
        return await _files.GetDiffAsync(root, relativePath, ct);
    }

    public Task<bool> RevertFileAsync(Project project, string relativePath, CancellationToken ct = default) =>
        Run(project, relativePath, (root, _) => _files.RevertFile(root, relativePath));

    /// <summary>Реальный корень проекта и полный реальный путь — или отказ политики.</summary>
    public (string Root, string Full) Check(Project project, string relativePath)
    {
        var root = _policy.ProjectRoot(project.RootPath);
        return (root, _policy.Resolve(root, relativePath));
    }

    // Как у серверной реализации: исключение (включая отказ политики) уходит в задачу
    private Task<T> Run<T>(Project project, string relativePath, Func<string, string, T> op, string? alsoCheck = null)
    {
        try
        {
            var (root, full) = Check(project, relativePath);
            if (alsoCheck is not null) _policy.Resolve(root, alsoCheck);
            AfterCheck?.Invoke(full);
            return Task.FromResult(op(root, full));
        }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }

    private Task Run(Project project, string relativePath, Action<string, string> op, string? alsoCheck = null) =>
        Run<object?>(project, relativePath, (root, full) => { op(root, full); return null; }, alsoCheck);
}

/// <summary>Открытый файл или каталог обязан лежать под реальным корнем проекта.</summary>
internal sealed class AgentOpenedPathGuard : IOpenedPathGuard
{
    public void Verify(string rootPath, string openedPath)
    {
        if (!AgentPathPolicy.IsUnder(openedPath, rootPath))
            throw new AgentPathRefusedException("Открылся файл за пределами проекта: ссылку подменили после проверки");
    }
}
