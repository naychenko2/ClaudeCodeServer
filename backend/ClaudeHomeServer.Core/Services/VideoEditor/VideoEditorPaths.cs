namespace ClaudeHomeServer.Services.VideoEditor;

// Имена на диске, которые нужны спине, а не только модулю «Видео» (ADR-022 §2, как AudioEditorPaths):
// бэкап в Main исключает рабочую папку модуля, а типов динамического модуля Main не видит.
public static class VideoEditorPaths
{
    // Рабочая папка задач рядом с data: data/video-editor/{ownerId}/{jobId} — кеш на 7 дней,
    // в бэкап не едет (BackupPaths, тест в BackupPathsTests). Нити (video-threads), префы
    // (video-editor-prefs) и состояние фильмов (video-films) — отдельные корни, они в архив едут
    public const string WorkspaceDirName = "video-editor";
}
