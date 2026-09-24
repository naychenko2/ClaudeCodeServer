using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IProjectFileGateway: тонкая обёртка над FileService, достаёт ровно
// файловые операции, нужные вертикали Docs (см. контракт IProjectFileGateway).
//
// Адаптер идёт ЧЕРЕЗ FileService, а не пишет файлы сам: FileService дёргает
// событие OnMutated, на котором висит синк базы знаний (ProjectKnowledgeSyncService).
// Прямой File.WriteAllText/Directory.Move здесь обходил бы синк молча — правки
// документов перестали бы доходить до Dify.
public sealed class ProjectFileGateway : IProjectFileGateway
{
    private readonly FileService _files;

    public ProjectFileGateway(FileService files)
    {
        _files = files;
        _files.OnMutated += (root, rel, kind, newRel) => OnMutated?.Invoke(root, rel, kind, newRel);
    }

    public void CreateDirectory(string root, string relativePath) =>
        _files.CreateDirectory(root, relativePath);

    public void CreateFile(string root, string relativePath) =>
        _files.CreateFile(root, relativePath);

    public void CreateFile(string root, string relativePath, string content) =>
        _files.CreateFile(root, relativePath, content);

    public void WriteFile(string root, string relativePath, string content) =>
        _files.WriteFile(root, relativePath, content);

    public void WriteFileBytes(string root, string relativePath, byte[] content) =>
        _files.WriteFileBytes(root, relativePath, content);

    public string ReadFile(string root, string relativePath) =>
        _files.ReadFile(root, relativePath);

    public byte[] ReadFileBytes(string root, string relativePath) =>
        _files.ReadFileBytes(root, relativePath);

    public void Delete(string root, string relativePath) =>
        _files.Delete(root, relativePath);

    public void Rename(string root, string oldRelative, string newRelative) =>
        _files.Rename(root, oldRelative, newRelative);

    public event Action<string, string, FileMutationKind, string?>? OnMutated;
}
