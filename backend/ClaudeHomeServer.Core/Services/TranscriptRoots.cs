namespace ClaudeHomeServer.Services;

// Реестр корней транскриптов workflow-агентов и профилей CLI.
//
// Реестр наполняют ТРИ источника из разных слоёв:
//   * `DefaultRoot` — константа `~/.claude/projects/` (сессии родного Claude);
//   * `AddAllowedRoot` — вызовы из `Program.cs` (`LlmProviderRegistry.ProfilesDir`,
//     корень провайдера транскриптов) и из `DockerProcessRunner.EnsureProfile`
//     (хостовая папка профиля песочницы);
//   * `ProfilesRoot` — общий корень изолированных профилей CLI (data/claude-profiles).
//
// `IsPathAllowed` — защита от обхода каталога: используется REST-эндпоинтом
// `WorkflowController.GetAgents` для гейта `transcriptDir`.
//
// Парсинг самих `agent-*.jsonl` живёт отдельно в `Services.Llm.WorkflowAgentParser`
// и не зависит от реестра.
//
// Раньше всё это было в одном классе `WorkflowAgentParser` (472 строки), из-за
// чего реестр жил внутри вертикали Llm и возникал цикл `Execution → Llm`
// (DockerProcessRunner/SandboxManager звали `WorkflowAgentParser.AddAllowedRoot`
// из тел методов). После переезда реестра в спину `Services.TranscriptRoots`
// осталось только прямое использование парсера Llm-вертикалью.
public static class TranscriptRoots
{
    // Дефолтный корень — ~/.claude/projects/ (сессии родного Claude)
    public static readonly string DefaultRoot = Path.GetFullPath(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));
    // Дополнительные корни — пути транскриптов профилей сторонних провайдеров
    // (GLM/DeepSeek: /data/claude-profiles/{key}/projects/). Регистрируются из Program.cs.
    private static readonly List<string> _extraRoots = new();

    // Корень всех изолированных профилей CLI (data/claude-profiles). Любой
    // {ProfilesRoot}/{key}/projects/** разрешён — покрывает и подписки (sub-*), и профили,
    // созданные после старта сервера, которые в _extraRoots не попали.
    public static string? ProfilesRoot { get; set; }

    public static void AddAllowedRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (Directory.Exists(full) && !_extraRoots.Contains(full, StringComparer.OrdinalIgnoreCase))
            _extraRoots.Add(full);
    }

    public static bool IsPathAllowed(string path) =>
        IsPathAllowed(path, ProfilesRoot);

    // Перегрузка для тестов: profilesRoot — параметром, а не из статического поля.
    // Параллельные интеграционные тесты (WebApplicationFactory) при старте хоста
    // перезаписывают ProfilesRoot (Program.cs) — если тест тоже ставит статическое
    // поле и тут же читает его, возникает гонка и flaky-провал на CI.
    internal static bool IsPathAllowed(string path, string? profilesRoot) =>
        IsUnderRoot(path, DefaultRoot) ||
        _extraRoots.Any(r => IsUnderRoot(path, r)) ||
        IsUnderProfilesProjects(path, profilesRoot);

    // Префикс строго по границе сегмента: ~/.claude/projectsEvil не должен проходить
    // как ~/.claude/projects (как в ветке ProfilesRoot ниже)
    private static bool IsUnderRoot(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Путь вида {profilesRoot}/{key}/projects/… — ровно один сегмент профиля,
    // затем обязательный projects (не даём читать credentials и прочее из профиля)
    private static bool IsUnderProfilesProjects(string path, string? profilesRoot)
    {
        if (string.IsNullOrEmpty(profilesRoot)) return false;
        string full, root;
        try { full = Path.GetFullPath(path); root = Path.GetFullPath(profilesRoot); }
        catch { return false; }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = Path.GetRelativePath(root, full)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 2 && parts[1].Equals("projects", StringComparison.OrdinalIgnoreCase);
    }

    // Все корни транскриптов (родной + профили провайдеров) — для поиска папки
    // сабагентов сессии (SubagentStreamWatcher)
    public static IReadOnlyList<string> AllowedRoots => [DefaultRoot, .. _extraRoots];
}
