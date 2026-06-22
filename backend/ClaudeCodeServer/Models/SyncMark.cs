namespace ClaudeCodeServer.Models;

// Метка синхронизации файла/папки для офлайн-доступа.
// Path — относительный путь от корня проекта (через '/'). Папка → каскад на всё содержимое.
public record SyncMark(string Path, bool IsDirectory);
