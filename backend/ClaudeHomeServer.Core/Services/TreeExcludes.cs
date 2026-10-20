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

    // Список работает ДВАЖДЫ: по нему обрезается обход дерева и по нему же не подписывается
    // наблюдатель (RecursiveDirectoryWatcher). Поэтому чужие экосистемы тут не роскошь:
    // под потолком слежек (4000) непрописанный `.venv` python-проекта на 6–10 тыс. каталогов
    // съедал весь бюджет, и рабочие каталоги оставались без наблюдения молча.
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "dev-dist",
        ".vs", ".idea", "publish", ".next", "target", ".cache",
        // Python
        ".venv", "venv", "site-packages", "__pycache__", ".tox",
        // JVM/Go/PHP и общие каталоги сборки и отчётов
        "build", "vendor", ".gradle", "coverage",
        AttachmentsDir,
    };

    public static bool Contains(string name) => Names.Contains(name);

    // Что наблюдатели дерева файлов (FileWatcherService на сервере, AgentFileWatchers на
    // устройстве) не подписывают и чьи события глушат — одним списком для подписки и фильтра.
    // Сверх Names — `.omc`: состояние плагина oh-my-claudecode копит каталог на каждую сессию
    // (`.omc/state/sessions`, `session-end-jobs/runs`) и на этом репозитории к 2026-09-27
    // съело 426 из 1013 слежек. В дереве файлов `.omc` по умолчанию и так скрыт, поэтому в
    // Names его нет, а правки на каждый вызов инструмента гнали бы filesChanged впустую.
    public static readonly HashSet<string> WatchNames =
        new([.. Names, ".omc"], StringComparer.OrdinalIgnoreCase);

    public static bool WatchContains(string name) => WatchNames.Contains(name);

    // Каталог с СОБСТВЕННЫМ .git: файл — у git worktree (gitdir: …), каталог — у вложенного
    // репозитория. Это полная копия чужого дерева: наблюдатель ставит слежку на его корень
    // (удаление и правки в нём видны), но вглубь не заходит — иначе бюджет inotify ушёл бы
    // на чужую копию. Корень проекта под правило не попадает: его собственный .git (репо)
    // не мешает наблюдать сам корень, а вот подкаталог с .git — уже чужое дерево.
    //
    // Правило по признаку, а не по имени: имя worktree произвольное (feature-x, minimax-m31…),
    // а .git — единственный надёжный маркер «здесь начинается отдельное git-дерево».
    public static bool HasOwnGit(string dir) =>
        File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"));
}
