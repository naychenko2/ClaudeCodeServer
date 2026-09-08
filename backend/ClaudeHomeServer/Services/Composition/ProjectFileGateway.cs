namespace ClaudeHomeServer.Services.Composition;

// Реализация IProjectFileGateway: тонкая обёртка над FileService, достаёт ровно
// файловые операции, нужные вертикали Docs (см. контракт IProjectFileGateway).
//
// Адаптер идёт ЧЕРЕЗ FileService, а не пишет файлы сам: FileService дёргает
// событие OnMutated, на котором висит синк базы знаний (ProjectKnowledgeSyncService).
// Прямой File.WriteAllText/Directory.Move здесь обходил бы синк молча — правки
// документов перестали бы доходить до Dify.
public sealed class ProjectFileGateway(FileService files) : IProjectFileGateway
{
    public void CreateDirectory(string root, string relativePath) =>
        files.CreateDirectory(root, relativePath);

    public void CreateFile(string root, string relativePath) =>
        files.CreateFile(root, relativePath);

    public void CreateFile(string root, string relativePath, string content) =>
        files.CreateFile(root, relativePath, content);

    public void WriteFile(string root, string relativePath, string content) =>
        files.WriteFile(root, relativePath, content);

    public void WriteFileBytes(string root, string relativePath, byte[] content) =>
        files.WriteFileBytes(root, relativePath, content);

    public byte[] ReadFileBytes(string root, string relativePath) =>
        files.ReadFileBytes(root, relativePath);

    public void Delete(string root, string relativePath) =>
        files.Delete(root, relativePath);

    public void Rename(string root, string oldRelative, string newRelative) =>
        files.Rename(root, oldRelative, newRelative);
}
