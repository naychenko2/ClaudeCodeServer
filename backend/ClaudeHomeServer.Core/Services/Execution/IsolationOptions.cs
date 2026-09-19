using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Execution;

// Изоляция процессов local-среды по памяти (секция Execution:Isolation). Смысл и механика —
// в LocalProcessRunner.BuildStartInfo. LocalProcessRunner — статический синглтон без DI,
// поэтому опции тоже статические: выставляются один раз в Program.cs. Дефолт — выключено.
public sealed class IsolationOptions
{
    public static IsolationOptions Instance { get; set; } = new();

    public bool Enabled { get; init; }
    // Дефис в имени slice systemd читает как иерархию: ccs-agents.slice живёт внутри ccs.slice
    public string Slice { get; init; } = "ccs-agents.slice";
    // Пределы памяти scope (MemoryHigh — мягкий, с троттлингом; MemoryMax — OOM внутри scope).
    // Пусто — свойство не ставится.
    public string? MemoryHigh { get; init; }
    public string? MemoryMax { get; init; }
    // Явный путь к systemd-run; пусто — поиск по PATH
    public string? SystemdRunPath { get; init; }

    public static IsolationOptions FromConfig(IConfiguration config) => new()
    {
        Enabled = config.GetValue("Execution:Isolation:Enabled", false),
        Slice = config["Execution:Isolation:Slice"] is { Length: > 0 } s ? s : "ccs-agents.slice",
        MemoryHigh = config["Execution:Isolation:MemoryHigh"],
        MemoryMax = config["Execution:Isolation:MemoryMax"],
        SystemdRunPath = config["Execution:Isolation:SystemdRunPath"],
    };
}
