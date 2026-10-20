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
    // Юнит сервера, ПОСЛЕ которого systemd обязан гасить scope хода (Before=, инцидент
    // 20.10 15:33): при выключении МАШИНЫ менеджер пользователя systemd останавливает
    // ccs.service и scope-юниты CLI одной транзакцией БЕЗ порядка — CLI раньше сервера
    // получал SIGTERM, присылал result{subtype:error} и чат уходил в Error, а не оставался
    // Working для авто-продолжения. Before=<юнит> ставит scope ПОСЛЕ сервера, и путь
    // сводится к уже проверенной штатной остановке (KillAllProcesses → _stopping).
    // Автоопределяется по /proc/self/cgroup (последний сегмент, если он валидное имя
    // *.service) и переопределяется в конфиге; null — свойство Before= не ставится
    // (dev-стенд из dotnet run, Windows, нет systemd). Имя ccs.service в код НЕ зашито.
    // Сомнительное значение (не похоже на имя юнита) — null: лучше не ставить Before=,
    // чем провалить пробу systemd-run и, с ней, всю обёртку scope (fail-open, см.
    // LooksLikeServiceUnit).
    public string? StopAfterUnit { get; init; }
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
            // Явный конфиг сильнее автоопределения; пустой ключ — детект по /proc/self/cgroup.
            // Сомнительное значение (не похоже на имя юнита) — null: автоопределение при
            // ЯВНО заданном ключе не вызываем — оператор тронул ключ, и молчаливый фолбэк
            // на другое имя удивил бы.
            StopAfterUnit = config["Execution:Isolation:StopAfterUnit"] is { Length: > 0 } unit
                ? LooksLikeServiceUnit(unit) ? unit : null
                : DetectServerUnit(),
        };
    }

    // Юнит сервера из /proc/self/cgroup: последний сегмент пути, если он валидное имя
    // *.service (LooksLikeServiceUnit). cgroup v2 — одна строка «0::/path»; cgroup v1 —
    // несколько строк «N:subsys:/path», релевантна иерархия name=systemd (там и живёт
    // юнит). Чистая функция от содержимого файла: тестам настоящий /proc не нужен.
    // Не нашёлся (scope, пусто, dev-стенд) или сомнительный (не похоже на имя юнита) —
    // null: лучше не ставить Before=, чем провалить пробу systemd-run и, с ней, всю
    // обёртку scope (fail-open, см. LooksLikeServiceUnit).
    internal static string? ParseServerUnit(string cgroupContent)
    {
        foreach (var line in cgroupContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            // Путь — после ВТОРОГО ':': v1 «N:subsys:/path», v2 «0::/path» (пустой subsys).
            var path = trimmed;
            var firstColon = trimmed.IndexOf(':');
            if (firstColon >= 0)
            {
                var secondColon = trimmed.IndexOf(':', firstColon + 1);
                if (secondColon >= 0) path = trimmed[(secondColon + 1)..];
            }
            var last = LastSegment(path);
            if (last is not null && LooksLikeServiceUnit(last)) return last;
        }
        return null;
    }

    // Имя, похожее на systemd-юнит сервиса: суффикс `.service` и только символы
    // [A-Za-z0-9:_.@-] (пробел, «/», «*» и прочие — сомнительно). Цена ошибки: невалидное
    // имя в `--property=Before=…` роняет пробу systemd-run (`Invalid unit name`), и
    // BuildStartInfo по fail-open снимает обёртку scope у ВСЕХ local-процессов — прямой
    // путь к OOM-инциденту, ради которого scope и придуман. Лучше не ставить Before=
    // (порядок при выключении машины не задан, как до фикса), чем потерять изоляцию.
    internal static bool LooksLikeServiceUnit(string name) =>
        name.EndsWith(".service")
        && name.All(c => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or ':' or '_' or '.' or '@' or '-');

    // Последний сегмент пути: «/a/b/c» → «c»; «/» или «» → null.
    private static string? LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return null;
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    // Один раз при старте: читает /proc/self/cgroup (v1/v2) и возвращает юнит сервера.
    // Не нашёлся (dev-стенд из dotnet run, Windows, нет systemd) — null, Before= не ставим.
    // Fail-open: любой сбой чтения — null, обёртка работает как раньше.
    public static string? DetectServerUnit()
    {
        try
        {
            if (!File.Exists("/proc/self/cgroup")) return null;
            return ParseServerUnit(File.ReadAllText("/proc/self/cgroup"));
        }
        catch
        {
            return null;
        }
    }
}
