namespace ClaudeHomeServer.Services.TestRuns;

// Вид прогона: dotnet test, юнит-тесты фронта vitest или e2e Playwright
public enum TestRunKind { Dotnet, Vitest, Playwright }

// Запрос прогона: проект (по нему выбирается среда — ForProject), рабочее дерево чата
// (worktree или корень проекта, ХОСТОВЫЙ путь), чат-вызыватель, необязательные цель
// (путь ОТНОСИТЕЛЬНО дерева, уже проверенный вызывающим) и фильтр.
// dotnet: Target — проект/решение, Filter — выражение --filter, NoBuild — пропустить сборку.
// vitest/Playwright: Target — каталог с конфигом (cwd процесса), Filter — шаблон имени теста
// (-t / --grep), Files — пути тестов ОТНОСИТЕЛЬНО цели, Env — переменные из белого списка.
public sealed record TestRunRequest(ClaudeHomeServer.Models.Project Project, string WorkingDirectory,
    string SessionId, string? Target = null, string? Filter = null, bool NoBuild = false)
{
    public TestRunKind Kind { get; init; } = TestRunKind.Dotnet;
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    // vitest/Playwright: скрипт пакета ОТНОСИТЕЛЬНО цели, найденный TestRunService.FindNodeBin
    // (в монорепе пакет поднят в node_modules выше цели — «../../node_modules/…»). Форма
    // строгая: только «../» и константный путь пакета (Validate). null — node_modules цели
    public string? NodeBin { get; init; }
}

// Фаза конвейера: сборка (только dotnet) → подсчёт тестов (или файлов у vitest) → прогон
public enum TestRunPhase { Build, List, Test }

// Живой прогресс для карточки инструмента. Stage — queued | build | list | stand | running
// (stand — Playwright поднимает стенд из webServer конфига, до строки «Running N tests»);
// Percent при Exact — по настоящему общему числу, потолок 99 («готово» скажет результат)
public sealed record TestRunProgress(string Stage, string? Label, int? Percent = null, bool? Exact = null);

// Счётчик исходов тестов по строкам консоли — живой, до итогового отчёта
public readonly record struct TestCounts(int Passed, int Failed, int Skipped)
{
    public int Done => Passed + Failed + Skipped;
}

// Упавший тест: полное имя, сообщение (до MessageLines строк) и первые кадры стека
public sealed record TestFailure(string Name, string Message, string Stack);

// Сводка одного отчёта: TRX одной тестовой сборки dotnet или JSON-отчёт vitest/Playwright
public sealed record TestReport(string Assembly, int Total, int Passed, int Failed, int Skipped,
    TimeSpan? Duration, IReadOnlyList<TestFailure> Failures);

// Исход прогона. Refusal — прогон не начинался (дерево занято, цель-опция, стенд лежит).
// Cancelled — оборвал человек («Стоп» или обрыв вызова CLI), TimedOut — серверный потолок.
// NeverStarted — оборвано, пока прогон ждал слот очереди сборок: процесс не запускался вовсе.
public sealed record TestRunResult(string? Refusal, int? ExitCode, bool Cancelled, bool TimedOut,
    TimeSpan Elapsed, IReadOnlyList<string> Tail, string? TurnId = null)
{
    public TestRunKind Kind { get; init; } = TestRunKind.Dotnet;

    public bool NeverStarted { get; init; }

    // Фаза, на которой прогон кончился (или оборвался)
    public TestRunPhase Phase { get; init; } = TestRunPhase.Test;

    // Первые ошибки сборки (фаза Build с ненулевым кодом)
    public IReadOnlyList<string> BuildErrors { get; init; } = [];

    // Общее число из подсчёта (тесты dotnet/Playwright, файлы vitest); null — не удалось
    public int? Total { get; init; }

    public TestCounts Counts { get; init; }

    // Подпись последнего прогресса («412 из 7951 · упало 2») — для итога на обрыве
    public string? ProgressLabel { get; init; }

    // Имена упавших по строкам консоли (с потолком): на обрыве отчёта нет, а имена нужны
    public IReadOnlyList<string> FailedFromConsole { get; init; } = [];

    // Сводки отчётов (TRX по сборкам, JSON vitest/Playwright); пусто — отчёт не появился
    public IReadOnlyList<TestReport> Reports { get; init; } = [];

    // Отчёты, пропущенные из-за размера: читать их целиком в память сервер не стал
    public IReadOnlyList<string> OversizedReports { get; init; } = [];

    // Папка артефактов прогона ОТНОСИТЕЛЬНО рабочего дерева (console.log, *.trx, report.json)
    public string? ArtifactsPath { get; init; }

    public static TestRunResult Refused(string reason) => new(reason, null, false, false, TimeSpan.Zero, []);
}
