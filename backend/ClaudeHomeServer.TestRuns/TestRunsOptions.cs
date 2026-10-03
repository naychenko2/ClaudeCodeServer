namespace ClaudeHomeServer.Services.TestRuns;

// Настройки прогона (секция TestRuns). Потолок прогона обязан быть МЕНЬШЕ MCP_TOOL_TIMEOUT
// хода (600 с, ClaudeSession): ответ модели должен успеть уйти до того, как CLI оборвёт вызов.
// В потолок входит и ожидание очереди сборок.
public sealed class TestRunsOptions
{
    public const int DefaultCeilingSeconds = 540;
    public const int DefaultMaxResultBytes = 10240;

    public int CeilingSeconds { get; init; } = DefaultCeilingSeconds;

    // Потолок текста ответа модели (UTF-8 байты)
    public int MaxResultBytes { get; init; } = DefaultMaxResultBytes;

    // Сколько последних строк вывода отдать в черновом итоге
    public int TailLines { get; init; } = 40;

    // Каталог памяти прошлых сборок (BuildRunMemory) — в data сервера, вне рабочих деревьев
    // агентов; null — память не ведётся (сборка идёт с оценкой по XML). Из конфига не читается
    public string? MemoryDirectory { get; init; }

    public static TestRunsOptions From(IConfiguration config)
    {
        var options = config.GetSection("TestRuns").Get<TestRunsOptions>() ?? new TestRunsOptions();
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
        // Мусор из конфига не должен снять потолок: без него прогон пережил бы обрыв CLI
        return new TestRunsOptions
        {
            CeilingSeconds = options.CeilingSeconds is > 0 and <= DefaultCeilingSeconds
                ? options.CeilingSeconds : DefaultCeilingSeconds,
            MaxResultBytes = options.MaxResultBytes > 0 ? options.MaxResultBytes : DefaultMaxResultBytes,
            TailLines = options.TailLines > 0 ? options.TailLines : 40,
            MemoryDirectory = Path.Combine(dataDir, BuildRunMemory.DirName),
        };
    }
}
