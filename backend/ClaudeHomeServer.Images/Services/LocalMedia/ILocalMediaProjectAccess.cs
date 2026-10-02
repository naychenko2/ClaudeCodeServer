namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Шов «папка проекта» для локальной генерации: проекты и уведомления о файлах живут в
// Main и вертикали Files, а вертикаль Images на них не ссылается. Реализация — в Main.
// Нужен и тулсету, и фоновому коллектору: результат дописывается в проект, даже когда
// агент уже не спрашивает статус.
public interface ILocalMediaProjectAccess
{
    // Корень проекта владельца на диске сервера. null — проекта нет, он чужой или его
    // файлы лежат не на сервере (локальный проект, ADR-016)
    string? ResolveRoot(string ownerId, string projectId);

    // Файл записан в проект: дерево файлов и синк знаний узнают о нём сразу
    void NotifyWritten(string root, string relativePath);
}
