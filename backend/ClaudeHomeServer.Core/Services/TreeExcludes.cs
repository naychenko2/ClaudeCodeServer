namespace ClaudeHomeServer.Services;

// Имена каталогов, которые не обходятся при рекурсивном Tree (тяжёлые/нерелевантные
// для офлайна). Прежний примитив лежал в `FileService.TreeExcludes` (Main/Services)
// и был статическим HashSet. По соглашению проекта из вертикалей нельзя звать
// `FileService` напрямую — это ссылка на чужую вертикаль, сторож границ её ловит.
// Вынесен в Core отдельным файлом с сохранением имени, чтобы потребители
// (FileService внутри Main, FileWatcherService, FileTriggerSource, ProjectServiceDiscovery)
// переехали на новый namespace одним импортом.
public static class TreeExcludes
{
    // Папка вложений чата в рабочей папке (файлы, загруженные в сообщение с компьютера).
    // Служебная: исключена из дерева, ватчеров, дефолтного .gitignore и синка базы знаний.
    public const string AttachmentsDir = ".cc-attachments";

    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "dev-dist",
        ".vs", ".idea", "publish", ".next", "target", ".cache",
        AttachmentsDir,
    };

    public static bool Contains(string name) => Names.Contains(name);
}
