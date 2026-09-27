using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Images.LocalMedia;

namespace ClaudeHomeServer.Services.Mcp;

// Шов ILocalMediaProjectAccess: вертикаль Images видит только корень проекта владельца и
// «файл записан», а не ProjectManager и FileService. Локальный проект (ADR-016) — отказ через
// ProjectCapabilities: его файлы на устройстве, серверного диска у него нет.
// FileService — из отключаемой вертикали Files: выключена — дерево узнает о файле при
// следующей пересинхронизации.
public sealed class LocalMediaProjectAccess(ProjectManager projects, FileService? files = null) : ILocalMediaProjectAccess
{
    public string? ResolveRoot(string ownerId, string projectId)
    {
        var project = projects.GetById(projectId);
        if (project is null || !string.Equals(project.OwnerId, ownerId, StringComparison.Ordinal)) return null;
        return ProjectCapabilities.FilesOnServer(project) ? project.RootPath : null;
    }

    public void NotifyWritten(string root, string relativePath) =>
        files?.NotifyMutated(root, relativePath, FileMutationKind.Write);
}
