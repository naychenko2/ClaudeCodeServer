namespace ClaudeHomeServer.Services.Files;

// Запись листинга файлов проекта. Живёт в Core, а не в вертикали Files: её отдаёт шов
// IProjectFiles, контракт которого объявлен в спине.
public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified, string? Synced = null, bool IsNew = false);
