namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>
/// Каталоги агента. Конфиг (сопряжение, файл токена) и данные (копии CLI, профиль CLI,
/// журнал ходов, каталоги ходов) разведены по соглашениям ОС. Каталоги ходов — в данных
/// агента, а не в общем /tmp: общий temp даёт чужому пользователю машины подложить каталог.
/// </summary>
internal sealed record AgentPaths(string ConfigDirectory, string DataDirectory)
{
    public string RegistrationFile => Path.Combine(ConfigDirectory, "agent.json");
    /// <summary>Корни, под которыми агент открывает файлы проектов (ADR-016 §5).</summary>
    public string RootsFile => Path.Combine(ConfigDirectory, "roots.json");
    public string CliRoot => Path.Combine(DataDirectory, "cli");
    /// <summary><c>CLAUDE_CONFIG_DIR</c> ходов: транскрипты для --resume живут только здесь.</summary>
    public string CliProfile => Path.Combine(DataDirectory, "claude-profile");
    public string TurnsRoot => Path.Combine(DataDirectory, "turns");
    public string JournalDirectory => Path.Combine(DataDirectory, "journal");

    public static AgentPaths ForCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return new AgentPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiHomeAgent"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiHomeAgent"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support", "AiHomeAgent");
            return new AgentPaths(support, Path.Combine(support, "data"));
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xc ? xc : Path.Combine(home, ".config");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xd ? xd : Path.Combine(home, ".local", "share");
        return new AgentPaths(Path.Combine(config, "ai-home-agent"), Path.Combine(data, "ai-home-agent"));
    }

    /// <summary>Создаёт каталоги; на Unix — только для владельца.</summary>
    public void Ensure()
    {
        foreach (var dir in new[] { ConfigDirectory, DataDirectory, CliProfile, TurnsRoot, JournalDirectory })
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
