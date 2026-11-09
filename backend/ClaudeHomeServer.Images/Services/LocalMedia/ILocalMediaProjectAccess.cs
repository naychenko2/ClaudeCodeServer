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

    // Состояние проекта для истории (ADR-030 §3): различает причины 410. Gone — проекта нет
    // или он чужой; Local — проект на устройстве (файлы не на сервере); OnServer — на сервере
    // (RootPath — корень). null — projectId пуст (запись без проекта: старые прогоны/тесты)
    MediaHistoryProjectProbe? ProbeProject(string ownerId, string projectId);
}

// Состояние проекта для истории: где лежат его файлы и есть ли корень на сервере
public sealed record MediaHistoryProjectProbe(MediaHistoryProjectState State, string? RootPath);

public enum MediaHistoryProjectState
{
    // Gone — проекта нет (удалён) или он чужой
    Gone,
    // Local — проект переехал на устройство, его файлы не на сервере
    Local,
    // OnServer — файлы на сервере, RootPath задан
    OnServer,
}
