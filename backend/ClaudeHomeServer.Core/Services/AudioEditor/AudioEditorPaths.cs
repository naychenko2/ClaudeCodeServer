namespace ClaudeHomeServer.Services.AudioEditor;

// Имена на диске, которые нужны спине, а не только модулю «Звук» (ADR-021 §2, как ImageEditorPaths):
// бэкап в Main исключает рабочую папку модуля, а типов динамического модуля Main не видит.
public static class AudioEditorPaths
{
    // Рабочая папка задач рядом с data: data/audio-editor/{ownerId}/{jobId} — кеш на 7 дней,
    // в бэкап не едет (BackupPaths, тест в BackupPathsTests). Нити (audio-threads) и префы
    // (audio-editor-prefs) — отдельные корни, они в архив едут
    public const string WorkspaceDirName = "audio-editor";
}
