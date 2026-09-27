namespace ClaudeHomeServer.Services.ImageEditor;

// Имена на диске, которые нужны спине, а не только модулю редактора (ADR-018 §10.1):
// бэкап в Main исключает рабочую папку редактора, а типов модуля Main не видит.
public static class ImageEditorPaths
{
    // Рабочая папка редактора рядом с data: варианты задач, маски и шаги истории — кеш
    public const string WorkspaceDirName = "image-editor";

    // Состояние редактора чатов картинки: {WorkspaceDirName}/{ownerId}/chats/{sessionId}.json.
    // Тот же TTL-кеш рабочей папки, в бэкап не едет (BackupPaths, тест в BackupPathsTests)
    public const string ChatsDirName = "chats";
}
