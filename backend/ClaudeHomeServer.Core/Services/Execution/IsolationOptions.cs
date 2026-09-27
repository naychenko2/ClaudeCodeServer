using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Execution;

// Изоляция процессов local-среды по памяти (секция Execution:Isolation). Смысл и механика —
// в LocalProcessRunner.BuildStartInfo. LocalProcessRunner — статический синглтон без DI,
// поэтому опции тоже статические: выставляются один раз в Program.cs. Дефолт — выключено.
public sealed class IsolationOptions
{
    public static IsolationOptions Instance { get; set; } = new();

    public bool Enabled { get; init; }
    // Дефис в имени slice systemd читает как иерархию: ccs-agents.slice живёт внутри
    // ccs.slice. Прод ccs.service не «внутри» ccs.slice — у user-юнитов slice по умолчанию
    // app.slice, и ccs.slice с app.slice — сиблинги под user@<uid>.service. Итог тот же:
    // scope агента вне cgroup прода.
    public string Slice { get; init; } = "ccs-agents.slice";
    // Пределы памяти scope. MemoryMax — OOM внутри scope (ядро убивает самый большой процесс,
    // ход живёт). MemoryHigh — дроссель; под systemd-oomd его НЕ ставить: стойло в reclaim
    // считается PSI-давлением, суммируется вверх до user@<uid>.service и oomd убивает scope
    // целиком (разбор 2026-09-22, deploy/systemd/README.md). Пусто — свойство не ставится.
    public string? MemoryHigh { get; init; }
    public string? MemoryMax { get; init; }
    // Явный путь к systemd-run; пусто — поиск по PATH
    public string? SystemdRunPath { get; init; }
    // Реюз узлов MSBuild и общего компилятора ВНУТРИ scope (замер 2026-09-19: пересборка
    // тестов 7,1 с → 3,4–4,0 с на тёплых узлах). Хвосты гасятся остановкой scope по выходу
    // процесса, узлы приватны scope (соль рукопожатия = имя юнита). false — прежний режим:
    // MSBUILDDISABLENODEREUSE=1, DOTNET_CLI_USE_MSBUILD_SERVER=0, UseSharedCompilation=false.
    // Опция конфига, а не константа: откат — правкой appsettings без пересборки.
    public bool BuildNodeReuse { get; init; } = true;

    // Текст предупреждения о заполненном MemoryHigh (проверяется тестом — формулировка
    // обязана называть причину, иначе её прочитают как придирку и вернут значение обратно).
    public const string MemoryHighWarning =
        "[isolation] Execution:Isolation:MemoryHigh заполнен — под systemd-oomd дроссель " +
        "становится источником PSI-давления и убивает scope агента целиком (разбор 2026-09-22). " +
        "Ограничивать память нужно MemoryMax; значение лучше обнулить.";

    public static IsolationOptions FromConfig(IConfiguration config)
    {
        var memoryHigh = config["Execution:Isolation:MemoryHigh"];
        // Запрет держался только на комментарии в документации, а такой запрет не держится
        // вовсе: значение возвращается в конфиг при первом же подозрении на «зависает сборка».
        if (!string.IsNullOrWhiteSpace(memoryHigh)) Console.Error.WriteLine(MemoryHighWarning);
        return new IsolationOptions
        {
            Enabled = config.GetValue("Execution:Isolation:Enabled", false),
            Slice = config["Execution:Isolation:Slice"] is { Length: > 0 } s ? s : "ccs-agents.slice",
            MemoryHigh = memoryHigh,
            MemoryMax = config["Execution:Isolation:MemoryMax"],
            SystemdRunPath = config["Execution:Isolation:SystemdRunPath"],
            BuildNodeReuse = config.GetValue("Execution:Isolation:BuildNodeReuse", true),
        };
    }
}
