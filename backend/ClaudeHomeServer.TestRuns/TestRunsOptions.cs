using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.TestRuns;

// Настройки прогона (секция TestRuns). Потолок прогона обязан быть МЕНЬШЕ MCP_TOOL_TIMEOUT
// хода (600 с, ClaudeSession): ответ модели должен успеть уйти до того, как CLI оборвёт вызов.
// В потолок входит и ожидание очереди сборок.
public sealed class TestRunsOptions
{
    // Потолок прогона — тот же, что у шва предгейта: число живёт в Core (RunBudget),
    // вертикаль берёт его оттуда, чтобы два потолка не разошлись молча.
    public const int DefaultCeilingSeconds = RunBudget.MaxSeconds;
    public const int DefaultMaxResultBytes = 10240;

    public int CeilingSeconds { get; init; } = DefaultCeilingSeconds;

    // Потолок текста ответа модели (UTF-8 байты)
    public int MaxResultBytes { get; init; } = DefaultMaxResultBytes;

    // Сколько последних строк вывода отдать в черновом итоге
    public int TailLines { get; init; } = 40;

    // Монолит без filter идёт столькими процессами параллельно (TestShards); 1 — одним.
    // Слот BuildConcurrencyGate у прогона всё равно один: рост N — это рост пика RSS
    public const int DefaultMonolithShards = 3;
    public int MonolithShards { get; init; } = DefaultMonolithShards;

    // Цель монолита относительно корня дерева (каталог проекта; его .csproj — тоже он)
    public const string DefaultMonolithTarget = "backend/ClaudeHomeServer.Tests";
    public string? MonolithTarget { get; init; } = DefaultMonolithTarget;

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
            MonolithShards = Math.Clamp(options.MonolithShards, 1, TestShards.MaxShards),
            MonolithTarget = options.MonolithTarget,
            MemoryDirectory = Path.Combine(dataDir, BuildRunMemory.DirName),
        };
    }
}
