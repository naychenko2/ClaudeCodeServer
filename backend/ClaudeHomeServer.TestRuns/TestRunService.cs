using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Движок прогона тестов (run_tests). Запуск только через среду проекта
// (ILauncherFactory.ForProject): local — scope в ccs-agents.slice, container — docker-песочница.
//
// Три вида прогона (TestRunKind):
//  • dotnet: `dotnet build` → `dotnet test --list-tests --no-build` (общее число тестов для
//    честного процента) → `dotnet test --no-build` с логгерами console;normal и TRX;
//  • vitest: `vitest list --filesOnly` (общее число файлов, ~1 с) → `vitest run` с репортерами
//    default (строка на файл — прогресс) и json (итог);
//  • Playwright: проверка, что стенд отвечает (только local и только без webServer в конфиге —
//    с ним стенд поднимает сам Playwright) → `playwright test` с репортерами list (число тестов
//    первой строкой — процент сразу) и json (итог).
// node-инструменты запускаются `node <bin пакета>` из ближайшего node_modules от каталога
// прогона вверх до корня дерева (FindNodeBin: монорепа поднимает пакеты в корень), а НЕ через
// npx: на Windows npx — это npx.cmd, то есть cmd.exe /c, и аргумент с «&» стал бы второй
// командой. Весь вывод всех фаз пишется в .cc-attachments/test-runs/{runId}/console.log, отчёты
// (TRX, report.json) — туда же; модели уходит сводка под потолком MaxResultBytes.
//
// Инварианты:
//  • dotnet: все spec Heavy, и прогон держит ОДИН слот BuildConcurrencyGate на весь конвейер:
//    отдать слот между фазами — значит пустить чужую сборку в середину прогона. vitest встаёт в
//    гейт только прогоном и только когда файлов много (VitestHeavyFiles): воркеров у него по
//    числу файлов, до ядер−1. Playwright вне гейта (workers: 1, один браузер);
//  • свой TurnId на каждый процесс из единственного генератора (ProcessTurnIds): без метки
//    kill docker-клиента не трогает процесс в контейнере;
//  • отмена (обрыв HTTP-вызова = «Стоп» или таймаут CLI) и серверный потолок гасят ДЕРЕВО
//    процесса текущей фазы через launcher.Kill; потолок общий на конвейер вместе с очередью;
//  • одно рабочее дерево — один прогон: два dotnet test подерутся за obj/bin;
//  • ни один аргумент, пришедший от модели, не начинается с «-» или «@»: позиционный аргумент
//    с «-» стал бы опцией, а «@файл» dotnet раскрывает в опции из файла (response-файл).
public sealed partial class TestRunService
{
    // Подкаталог артефактов внутри .cc-attachments рабочего дерева
    public const string ArtifactsSubdir = "test-runs";

    // Сколько имён упавших по консоли держать на случай обрыва (отчёта тогда нет)
    private const int MaxConsoleFailures = 50;

    // Потолок кандидатов в ошибки сборки: точный разбор берёт первые 20 без повторов, а лог
    // сборки с тысячами ошибок не должен копиться в памяти целиком (L-c)
    internal const int MaxBuildErrorCandidates = 2000;

    // Отчёт больше этого сервер в память не читает — модель дочитает файл сама (L-c)
    public const long MaxReportBytes = 32L * 1024 * 1024;

    // С какого числа файлов прогон vitest тяжёлый. Замер 2026-10-02 (vitest 4.1.9, 16 ядер):
    // два файла — пик 407 МБ на четыре процесса node (npx, главный, два воркера); воркер
    // поднимается на файл, до ядра−1, так что полный прогон — это уже гигабайты
    public const int VitestHeavyFiles = 4;

    // Исполняемые скрипты пакетов относительно каталога прогона
    public const string VitestBin = "node_modules/vitest/vitest.mjs";
    public const string PlaywrightBin = "node_modules/@playwright/test/cli.js";

    // Имя JSON-отчёта vitest/Playwright в папке артефактов
    public const string JsonReportName = "report.json";

    // Переменные окружения, которые модель может передать Playwright. Учётка e2e и адрес
    // стенда; адрес — только loopback (проверяет TestsToolset): сервер стучится по нему сам
    public static readonly IReadOnlySet<string> PlaywrightEnvKeys =
        new HashSet<string>(StringComparer.Ordinal) { "PLAYWRIGHT_BASE_URL", "E2E_USER", "E2E_PASS" };

    private readonly ILauncherFactory _launchers;
    private readonly TestRunsOptions _options;
    // Общий конвейер фаз: блокировка дерева, гейт, потолок, процесс фазы
    private readonly PhasePipeline _pipeline;
    private readonly TimeSpan _progressInterval;
    private readonly Func<Uri, CancellationToken, Task<bool>> _standProbe;

    public TestRunService(ILauncherFactory launchers, TestRunsOptions options)
        : this(launchers, options, new PhasePipeline()) { }

    // DI: конвейер — синглтон подсистемы, блокировка дерева общая с другими его движками
    public TestRunService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline)
        : this(launchers, options, pipeline, progressInterval: null, standProbe: null) { }

    // Для тестов: свой экземпляр гейта вместо процесс-глобального, короткая пауза на выход,
    // свой шаг прогресса (по умолчанию — не чаще раза в секунду) и своя проверка стенда
    internal TestRunService(ILauncherFactory launchers, TestRunsOptions options,
        BuildConcurrencyGate? gate, TimeSpan exitGrace, TimeSpan? progressInterval = null,
        Func<Uri, CancellationToken, Task<bool>>? standProbe = null)
        : this(launchers, options, new PhasePipeline(gate, exitGrace), progressInterval, standProbe) { }

    private TestRunService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline,
        TimeSpan? progressInterval, Func<Uri, CancellationToken, Task<bool>>? standProbe)
    {
        _launchers = launchers;
        _options = options;
        _pipeline = pipeline;
        _progressInterval = progressInterval ?? TimeSpan.FromSeconds(1);
        _standProbe = standProbe ?? ProbeStandAsync;
    }

    public TestRunsOptions Options => _options;

    // Конвейер этого движка — для проверки, что он общий со сборкой
    internal PhasePipeline Pipeline => _pipeline;

    // Потолок длины фильтра: он едет одним аргументом ArgumentList, без shell, но бесконечная
    // строка — это мусор, а не выражение фильтра
    public const int MaxFilterLength = 2048;

    // Аргумент от модели, который процесс прочёл бы как опцию или response-файл
    public static bool LooksLikeOption(string? value) =>
        value is not null && value.TrimStart() is { Length: > 0 } trimmed && (trimmed[0] == '-' || trimmed[0] == '@');

    public static string KindName(TestRunKind kind) => kind switch
    {
        TestRunKind.Vitest => "vitest",
        TestRunKind.Playwright => "Playwright",
        _ => "dotnet",
    };

    // Каталог процесса: корень дерева для dotnet (цель — аргументом), каталог-цель для node.
    // Буква диска — заглавная: vite/vitest на Windows с cwd «c:\…» грузят модуль vitest дважды
    // (c:/… и C:/… — разные id модуля), и КАЖДЫЙ файл падает «Cannot read properties of
    // undefined (reading 'config')» / «Vitest failed to find the runner» (бой 2026-10-02: корень
    // проекта записан как «c:\GIT\…»). Маппер путей песочницы префикс хоста сравнивает без
    // учёта регистра — для container это безразлично
    internal static string ProcessDirectory(TestRunRequest request) => UpperDriveLetter(
        request.Kind == TestRunKind.Dotnet || string.IsNullOrEmpty(request.Target)
            ? request.WorkingDirectory
            : Path.Combine(request.WorkingDirectory, request.Target));

    internal static string UpperDriveLetter(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetterLower(path[0])
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;

    // spec фазы конвейера. Аргументы — отдельными элементами Args (ArgumentList): ни shell, ни
    // склейки строки нет. Папка результатов — ОТНОСИТЕЛЬНО каталога процесса: путь одинаково
    // верен на хосте и в песочнице (без маппинга путей)
    internal static ProcessSpec BuildSpec(TestRunPhase phase, TestRunRequest request, string turnId,
        string resultsDirectory, bool heavy = true) => request.Kind switch
    {
        TestRunKind.Vitest => VitestSpec(phase, request, turnId, resultsDirectory, heavy),
        TestRunKind.Playwright => PlaywrightSpec(request, turnId, resultsDirectory),
        _ => DotnetSpec(phase, request, turnId, resultsDirectory),
    };

    private static ProcessSpec DotnetSpec(TestRunPhase phase, TestRunRequest request, string turnId,
        string resultsDirectory)
    {
        var args = new List<string> { phase == TestRunPhase.Build ? "build" : "test" };
        if (!string.IsNullOrWhiteSpace(request.Target)) args.Add(request.Target);
        if (phase != TestRunPhase.Build)
        {
            args.Add("--no-build");
            if (phase == TestRunPhase.List) args.Add("--list-tests");
            // --list-tests учитывает --filter (проверено на vstest .NET 10): процент честный
            if (!string.IsNullOrWhiteSpace(request.Filter))
            {
                args.Add("--filter");
                args.Add(request.Filter);
            }
        }
        if (phase == TestRunPhase.Test)
        {
            args.AddRange(["--logger", "console;verbosity=normal",
                // Префикс, а не LogFileName: у решения с несколькими тестовыми проектами одно
                // имя файла перезаписалось бы, префикс даёт по файлу на сборку
                "--logger", "trx;LogFilePrefix=results",
                "--results-directory", resultsDirectory]);
        }
        return Spec("dotnet", args, request, turnId, heavy: true, new Dictionary<string, string>
        {
            // Разборщикам вывода нужен английский: локализованный «Пройден» не разобрать
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["DOTNET_NOLOGO"] = "1",
        }) with
        {
            // Узлы MSBuild и компилятор — только свои (ProcessSpec.PrivateBuildNodes); фазы
            // list/test идут с --no-build, так что реюз между фазами ничего не давал
            PrivateBuildNodes = true,
        };
    }

    // vitest без TTY: CI=1 — строка на файл, без перерисовки; NO_COLOR — без ANSI
    private static ProcessSpec VitestSpec(TestRunPhase phase, TestRunRequest request, string turnId,
        string resultsDirectory, bool heavy)
    {
        var args = new List<string> { request.NodeBin ?? VitestBin };
        if (phase == TestRunPhase.List)
        {
            args.AddRange(["list", "--filesOnly"]);
            args.AddRange(request.Files);
        }
        else
        {
            args.Add("run");
            args.AddRange(request.Files);
            if (!string.IsNullOrWhiteSpace(request.Filter))
            {
                args.Add("-t");
                args.Add(request.Filter);
            }
            args.AddRange(["--reporter=default", "--reporter=json",
                $"--outputFile.json={resultsDirectory}/{JsonReportName}"]);
        }
        return Spec("node", args, request, turnId, heavy && phase == TestRunPhase.Test,
            new Dictionary<string, string> { ["CI"] = "1", ["NO_COLOR"] = "1" });
    }

    // Playwright: list — число тестов первой строкой и строка на тест; json — итог.
    // FORCE_COLOR=0 без NO_COLOR: вместе они дают предупреждение node на каждый процесс
    private static ProcessSpec PlaywrightSpec(TestRunRequest request, string turnId, string resultsDirectory)
    {
        var args = new List<string> { request.NodeBin ?? PlaywrightBin, "test" };
        args.AddRange(request.Files);
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            args.Add("--grep");
            args.Add(request.Filter);
        }
        args.Add("--reporter=list,json");
        var env = new Dictionary<string, string>
        {
            ["FORCE_COLOR"] = "0",
            ["PLAYWRIGHT_JSON_OUTPUT_NAME"] = $"{resultsDirectory}/{JsonReportName}",
        };
        foreach (var (key, value) in request.Env)
            if (PlaywrightEnvKeys.Contains(key)) env[key] = value;
        return Spec("node", args, request, turnId, heavy: false, env);
    }

    private static ProcessSpec Spec(string fileName, List<string> args, TestRunRequest request, string turnId,
        bool heavy, Dictionary<string, string> env) => new()
    {
        FileName = fileName,
        Args = args,
        WorkingDirectory = ProcessDirectory(request),
        Env = env,
        StdioEncoding = new UTF8Encoding(false),
        RedirectStdin = false,
        Heavy = heavy,
        Track = true,
        TurnId = turnId,
        SessionId = request.SessionId,
    };

    // Прогон dotnet test (совместимость: вид берётся из запроса)
    public Task<TestRunResult> RunDotnetAsync(TestRunRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct) => RunAsync(request with { Kind = TestRunKind.Dotnet }, progress, ct);

    public Task<TestRunResult> RunVitestAsync(TestRunRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct) => RunAsync(request with { Kind = TestRunKind.Vitest }, progress, ct);

    public Task<TestRunResult> RunPlaywrightAsync(TestRunRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct) => RunAsync(request with { Kind = TestRunKind.Playwright }, progress, ct);

    // Прогон конвейером. progress — живые фазы для карточки инструмента
    // (queued / build / list / running); исключения из колбэка не глотаются молча —
    // вызывающий обязан сделать его безопасным (тулсет шлёт fire-and-forget).
    public async Task<TestRunResult> RunAsync(TestRunRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct)
    {
        if (Validate(request) is { } refusal) return Refused(request, refusal);

        using var treeLock = _pipeline.TryLockTree(request.WorkingDirectory);
        if (treeLock is null)
            return Refused(request, "В этом дереве уже идёт прогон тестов или сборка — дождись его конца: "
                + "два процесса подерутся за obj/bin.");

        var launcher = _launchers.ForProject(request.Project);
        // Стенд из webServer конфига Playwright поднимает сам — проверять заранее нечего
        if (request.Kind == TestRunKind.Playwright && !launcher.IsSandboxed && !ConfigStartsWebServer(request)
            && await StandRefusalAsync(request, ct) is { } down)
            return Refused(request, down);

        // Папка за ссылкой (InTree=false): процесс пишет результаты своими правами, как и без
        // нас, а хост туда не пишет лог и не читает отчёты — сводка только по выводу
        var artifacts = PhasePipeline.CreateArtifacts(request.WorkingDirectory, ArtifactsSubdir);
        // Аргумент папки результатов — относительно каталога процесса (у node — каталог цели)
        var results = Path.GetRelativePath(ProcessDirectory(request), artifacts.Full).Replace('\\', '/');
        using var run = new RunState(this, request.Kind, PhasePipeline.OpenLog(request.WorkingDirectory, artifacts),
            progress, artifacts.InTree ? artifacts.Relative : null, results);
        return request.Kind switch
        {
            TestRunKind.Vitest => await RunVitestPipelineAsync(launcher, request, run, artifacts, ct),
            TestRunKind.Playwright => await RunPlaywrightPipelineAsync(launcher, request, run, artifacts, ct),
            _ => await RunDotnetPipelineAsync(launcher, request, run, artifacts, ct),
        };
    }

    private static TestRunResult Refused(TestRunRequest request, string reason) =>
        TestRunResult.Refused(reason) with { Kind = request.Kind };

    // Второй рубеж к проверкам тулсета: ни одного аргумента-опции и response-файла в процесс
    internal static string? Validate(TestRunRequest request)
    {
        var tool = KindName(request.Kind);
        if (LooksLikeOption(request.Target))
            return $"target не может начинаться с «-» или «@»: это была бы опция {tool} (или response-файл).";
        if (request.Filter is { Length: > MaxFilterLength })
            return $"Фильтр длиннее {MaxFilterLength} символов — сократи выражение.";
        if (LooksLikeOption(request.Filter))
            return $"filter не может начинаться с «-» или «@»: это была бы опция {tool} (или response-файл).";
        if (request.Kind == TestRunKind.Dotnet && request.Files.Count > 0)
            return "files — только для vitest и Playwright; у dotnet выбирай тесты аргументом filter.";
        if (request.Files.FirstOrDefault(f => string.IsNullOrWhiteSpace(f) || LooksLikeOption(f)) is { } bad)
            return $"Путь «{bad}» в files не может быть пустым или начинаться с «-» или «@».";
        if (request.Env.Keys.FirstOrDefault(k => request.Kind != TestRunKind.Playwright || !PlaywrightEnvKeys.Contains(k)) is { } env)
            return $"Переменная окружения {env} не из белого списка ({string.Join(", ", PlaywrightEnvKeys)}; только Playwright).";
        if (request.NodeBin is { } nodeBin && !IsNodeBinShape(nodeBin, request.Kind))
            return "Путь скрипта тестового раннера — только «../»… и путь пакета в node_modules.";
        return null;
    }

    // Скрипт пакета вида прогона относительно каталога пакета-владельца node_modules
    public static string? NodeBinOf(TestRunKind kind) => kind switch
    {
        TestRunKind.Vitest => VitestBin,
        TestRunKind.Playwright => PlaywrightBin,
        _ => null,
    };

    // Форма NodeBin: ноль и больше «../» и константа пакета — ни опции, ни абсолютного пути,
    // ни чужого скрипта: бинарь выбирает сервер, а не модель
    internal static bool IsNodeBinShape(string nodeBin, TestRunKind kind)
    {
        if (NodeBinOf(kind) is not { } bin) return false;
        var rest = nodeBin;
        while (rest.StartsWith("../", StringComparison.Ordinal)) rest = rest[3..];
        return rest == bin;
    }

    // Потолок переходов по ссылкам при проверке пути пакета: цикл ссылок не повесит вызов
    private const int MaxLinkHops = 40;

    // Ищет скрипт пакета по правилам разрешения Node: node_modules цели, затем родителей —
    // но НЕ выше корня рабочего дерева (монорепа npm/yarn/pnpm workspaces поднимает пакеты в
    // корень). Найденный путь обязан и ПОСЛЕ раскрытия ссылок лежать в дереве: pnpm кладёт пакеты
    // ссылками в node_modules/.pnpm — это легально, а ссылка наружу дерева — отказ (node исполнил
    // бы чужой код хоста). Первая найденная копия — ровно та, что взял бы сам node: дальше вверх
    // не идём и при отказе. Возвращает путь ОТНОСИТЕЛЬНО цели через «/» или null с error.
    public static string? FindNodeBin(string root, string target, TestRunKind kind, out string error)
    {
        error = "";
        if (NodeBinOf(kind) is not { } bin)
        {
            error = "Скрипт пакета есть только у vitest и Playwright.";
            return null;
        }
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string targetFull;
        try { targetFull = SafePath.Join(rootFull, target); }
        catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "target выходит за пределы проекта — вызов отклонён.";
            return null;
        }
        targetFull = targetFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (var dir = targetFull; ; dir = Path.GetDirectoryName(dir)!)
        {
            var candidate = Path.Combine(dir, bin);
            if (File.Exists(candidate))
            {
                if (!ResolvesInside(rootFull, candidate))
                {
                    error = $"{Relative(rootFull, candidate)} ведёт символической ссылкой за пределы проекта — "
                        + "такой пакет не запускаем.";
                    return null;
                }
                return Path.GetRelativePath(targetFull, candidate).Replace('\\', '/');
            }
            // Корень дерева — последний каталог поиска: выше лежит чужое
            if (PathComparer.Equals(dir, rootFull) || !IsInside(rootFull, dir)) break;
        }
        var shown = Relative(rootFull, targetFull);
        error = $"Ни в «{(shown.Length == 0 ? "." : shown)}», ни выше до корня проекта нет {bin} — зависимости "
            + "не установлены (npm ci / pnpm install) или это не тот каталог.";
        return null;
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Relative(string rootFull, string path) =>
        Path.GetRelativePath(rootFull, path).Replace('\\', '/') is var relative && relative == "." ? "" : relative;

    // Путь лежит в корне ЛЕКСИЧЕСКИ (корень сам себе — тоже)
    private static bool IsInside(string rootFull, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathComparer.Equals(full, rootFull)) return true;
        var prefix = rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Раскрывает ссылки (symlink, на Windows и junction pnpm) на каждом сегменте ниже корня и
    // проверяет, что каждая цель — в дереве. Сам корень не раскрывается: проект вправе лежать
    // под ссылкой (как в ProjectLinkGuard). Не смогли раскрыть — считаем, что наружу
    internal static bool ResolvesInside(string rootFull, string path)
    {
        var current = Path.GetFullPath(path);
        for (var hops = 0; hops <= MaxLinkHops; hops++)
        {
            if (!IsInside(rootFull, current)) return false;
            var rest = Path.GetRelativePath(rootFull, current);
            if (rest == ".") return true;
            var segments = rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            var walked = rootFull;
            string? relinked = null;
            for (var i = 0; i < segments.Length; i++)
            {
                walked = Path.Combine(walked, segments[i]);
                FileSystemInfo info = Directory.Exists(walked) ? new DirectoryInfo(walked) : new FileInfo(walked);
                if (info.LinkTarget is null) continue;
                FileSystemInfo? resolved;
                try { resolved = info.ResolveLinkTarget(returnFinalTarget: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
                if (resolved is null) return false;
                // Остаток пути — от цели ссылки; цель могла сама лежать под ссылкой — круг заново
                relinked = Path.Combine([resolved.FullName, .. segments[(i + 1)..]]);
                break;
            }
            if (relinked is null) return true;
            current = Path.GetFullPath(relinked);
        }
        return false;
    }

    // Захват слота гейта с подписью очереди; Aborted — оборвано в очереди, процесс не запускался
    private async Task<(IDisposable? Slot, TestRunResult? Aborted)> AcquireAsync(ProcessSpec spec,
        TestRunPhase phase, RunState run, CancellationToken limit, CancellationToken callerCt)
    {
        var slot = await _pipeline.AcquireAsync(spec,
            busy => run.Report(new TestRunProgress("queued", $"ждёт очереди сборок (занято {busy})")), limit);
        if (slot is not null) return (slot, null);
        // «Погашен» здесь было бы враньём: процесса не было
        return (null, run.Result(null, phase) with
        {
            Cancelled = callerCt.IsCancellationRequested,
            TimedOut = !callerCt.IsCancellationRequested,
            NeverStarted = true,
        });
    }

    private CancellationTokenSource Ceiling(CancellationToken ct) => PhasePipeline.Ceiling(_options.CeilingSeconds, ct);

    private async Task<TestRunResult> RunDotnetPipelineAsync(IProcessLauncher launcher, TestRunRequest request,
        RunState run, PhaseArtifacts artifacts, CancellationToken ct)
    {
        using var limit = Ceiling(ct);
        var firstPhase = request.NoBuild ? TestRunPhase.List : TestRunPhase.Build;
        var (slot, queued) = await AcquireAsync(BuildSpec(firstPhase, request, "", run.Results), firstPhase, run,
            limit.Token, ct);
        if (queued is not null) return queued;

        using (slot)
        {
            if (!request.NoBuild)
            {
                run.Report(new TestRunProgress("build", "сборка"));
                var build = await RunPhaseAsync(launcher, TestRunPhase.Build, request, run, limit.Token);
                if (build.Aborted) return Aborted(run, build, TestRunPhase.Build, ct);
                if (build.ExitCode != 0)
                    return run.Result(build.ExitCode, TestRunPhase.Build) with
                    {
                        BuildErrors = VsTestConsoleParser.BuildErrors(run.BuildErrorCandidates()),
                    };
            }

            run.Report(new TestRunProgress("list", "подсчёт тестов"));
            var list = await RunPhaseAsync(launcher, TestRunPhase.List, request, run, limit.Token);
            if (list.Aborted) return Aborted(run, list, TestRunPhase.List, ct);
            // Подсчёт не удался — прогон всё равно идёт, просто без процента
            run.Total = list.ExitCode == 0 ? run.ListedCount : null;

            run.StartTesting();
            var test = await RunPhaseAsync(launcher, TestRunPhase.Test, request, run, limit.Token);
            if (test.Aborted) return Aborted(run, test, TestRunPhase.Test, ct);
            var (reports, oversized) = ReadTrxReports(request.WorkingDirectory, artifacts);
            return run.Result(test.ExitCode, TestRunPhase.Test) with { Reports = reports, OversizedReports = oversized };
        }
    }

    private async Task<TestRunResult> RunVitestPipelineAsync(IProcessLauncher launcher, TestRunRequest request,
        RunState run, PhaseArtifacts artifacts, CancellationToken ct)
    {
        using var limit = Ceiling(ct);
        run.Report(new TestRunProgress("list", "подсчёт файлов"));
        var list = await RunPhaseAsync(launcher, TestRunPhase.List, request, run, limit.Token);
        if (list.Aborted) return Aborted(run, list, TestRunPhase.List, ct);
        run.Total = list.ExitCode == 0 ? run.ListedCount : null;

        // Тяжесть — по числу файлов; не удалось посчитать — считаем тяжёлым
        var heavy = run.Total is not { } files || files >= VitestHeavyFiles;
        var spec = BuildSpec(TestRunPhase.Test, request, "", run.Results, heavy);
        var (slot, queued) = await AcquireAsync(spec, TestRunPhase.Test, run, limit.Token, ct);
        if (queued is not null) return queued;
        using (slot)
        {
            run.StartTesting();
            var test = await RunPhaseAsync(launcher, TestRunPhase.Test, request, run, limit.Token, heavy);
            if (test.Aborted) return Aborted(run, test, TestRunPhase.Test, ct);
            var (reports, oversized) = ReadJsonReport(request.WorkingDirectory, artifacts,
                text => VitestJsonReader.Read(text, ProcessDirectory(request)));
            return run.Result(test.ExitCode, TestRunPhase.Test) with { Reports = reports, OversizedReports = oversized };
        }
    }

    private async Task<TestRunResult> RunPlaywrightPipelineAsync(IProcessLauncher launcher, TestRunRequest request,
        RunState run, PhaseArtifacts artifacts, CancellationToken ct)
    {
        // Потолок общий и для подъёма стенда из webServer: он идёт внутри того же процесса.
        // «Стоп» и потолок гасят ДЕРЕВО процесса по ppid. Стенд — потомок Playwright'а, но на
        // не-Windows он detached: своя группа и сессия, сигнал группе хода его не задевает.
        // Local гасит по ppid сам (Kill(entireProcessTree)), песочница — обходом /proc от группы
        // хода (DockerProcessRunner.KillTurnScript)
        using var limit = Ceiling(ct);
        var startsStand = ConfigStartsWebServer(request);
        // Пока стенд поднимается, «0 из ?» врал бы: этап «запуск стенда» до строки
        // «Running N tests» (она сразу шлёт «0 из N»)
        if (startsStand) run.Report(new TestRunProgress("stand", "запуск стенда"));
        run.StartTesting(announce: !startsStand);
        var test = await RunPhaseAsync(launcher, TestRunPhase.Test, request, run, limit.Token);
        if (test.Aborted) return Aborted(run, test, TestRunPhase.Test, ct);
        var (reports, oversized) = ReadJsonReport(request.WorkingDirectory, artifacts, PlaywrightJsonReader.Read);
        return run.Result(test.ExitCode, TestRunPhase.Test) with { Reports = reports, OversizedReports = oversized };
    }

    private static TestRunResult Aborted(RunState run, PhaseOutcome phase, TestRunPhase where, CancellationToken callerCt)
    {
        var cancelled = callerCt.IsCancellationRequested;
        return run.Result(phase.ExitCode, where) with { Cancelled = cancelled, TimedOut = !cancelled };
    }

    // Один процесс фазы со своим TurnId (из единственного генератора ProcessTurnIds); старт,
    // вычитка и гашение — в конвейере. Сбой Start уходит наверх: слот освободит using,
    // блокировку дерева — using в RunAsync
    private Task<PhaseOutcome> RunPhaseAsync(IProcessLauncher launcher, TestRunPhase phase,
        TestRunRequest request, RunState run, CancellationToken limit, bool heavy = true)
    {
        var turnId = ProcessTurnIds.New();
        var spec = BuildSpec(phase, request, turnId, run.Results, heavy);
        run.BeginPhase(phase, spec, turnId);
        return _pipeline.RunProcessAsync(launcher, spec, run.OnLine, limit);
    }

    // Сводки всех TRX прогона; битый файл пропускается — остальные сборки важнее, огромный —
    // не читается в память вовсе. Отчёты пишет процесс агента: читаем по дескриптору под
    // потолком (TreeFiles), папку за ссылкой — не читаем вовсе
    private static (IReadOnlyList<TestReport>, IReadOnlyList<string>) ReadTrxReports(string workingDirectory,
        PhaseArtifacts artifacts)
    {
        var reports = new List<TestReport>();
        var oversized = new List<string>();
        if (!artifacts.InTree || !Directory.Exists(artifacts.Full)) return (reports, oversized);
        foreach (var file in Directory.EnumerateFiles(artifacts.Full, "*.trx").Order(StringComparer.Ordinal))
        {
            if (new FileInfo(file).Length > MaxReportBytes)
            {
                oversized.Add(Path.GetFileName(file));
                continue;
            }
            if (TreeFiles.ReadTextInTree(workingDirectory, file, (int)MaxReportBytes) is not { } text) continue;
            try { reports.Add(TrxSummaryReader.Read(text)); }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or FormatException) { }
        }
        return (reports, oversized);
    }

    // JSON-отчёт vitest/Playwright; нет файла (обрыв, падение до отчёта) или он битый — пусто
    private static (IReadOnlyList<TestReport>, IReadOnlyList<string>) ReadJsonReport(string workingDirectory,
        PhaseArtifacts artifacts, Func<string, TestReport> read)
    {
        var file = Path.Combine(artifacts.Full, JsonReportName);
        if (!artifacts.InTree || !File.Exists(file)) return ([], []);
        if (new FileInfo(file).Length > MaxReportBytes) return ([], [JsonReportName]);
        if (TreeFiles.ReadTextInTree(workingDirectory, file, (int)MaxReportBytes) is not { } text) return ([], []);
        try { return ([read(text)], []); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException)
        {
            return ([], []);
        }
    }

    // --- Проверка стенда Playwright (только local) ---

    // Адрес стенда по умолчанию — первый loopback-URL в конфиге (у нас «http://localhost:5000»);
    // env.PLAYWRIGHT_BASE_URL его переопределяет. Адреса в конфиге нет — проверять нечего
    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1|\[::1\])(?::\d{1,5})?", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackUrl();

    private static readonly string[] PlaywrightConfigs =
        ["playwright.config.ts", "playwright.config.mts", "playwright.config.js", "playwright.config.mjs"];

    internal static Uri? StandUrl(TestRunRequest request)
    {
        if (request.Env.TryGetValue("PLAYWRIGHT_BASE_URL", out var fromEnv))
            return Uri.TryCreate(fromEnv, UriKind.Absolute, out var u) ? u : null;
        if (PlaywrightConfigText(request) is not { } text) return null;
        var match = LoopbackUrl().Match(text);
        return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var url) ? url : null;
    }

    [GeneratedRegex(@"\bwebServer\b")]
    private static partial Regex WebServerKey();

    // Поднимает ли Playwright стенд сам (webServer в конфиге). Честная оговорка: конфиг — код
    // на TS/JS, сервер его НЕ исполняет, это грубая проверка текста — ключ webServer где-то в
    // файле. Ложное «да» (ключ в комментарии) лишь снимает предварительную проверку стенда:
    // тесты упадут по своим таймаутам; ложное «нет» (webServer собран в другом модуле) —
    // прежний отказ с подсказкой про env.PLAYWRIGHT_BASE_URL
    internal static bool ConfigStartsWebServer(TestRunRequest request) =>
        request.Kind == TestRunKind.Playwright && PlaywrightConfigText(request) is { } text && WebServerKey().IsMatch(text);

    // Текст первого найденного конфига Playwright в каталоге прогона; огромный не читаем
    private static string? PlaywrightConfigText(TestRunRequest request)
    {
        var dir = ProcessDirectory(request);
        foreach (var name in PlaywrightConfigs)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) continue;
            try { return File.ReadAllText(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    private async Task<string?> StandRefusalAsync(TestRunRequest request, CancellationToken ct)
    {
        if (StandUrl(request) is not { } url) return null;
        if (await _standProbe(url, ct)) return null;
        return $"Стенд {url} не отвечает — e2e гоняются против уже запущенного приложения, сами его не "
            + "поднимают. Подними стенд (dotnet run, docs/operations/dev-stand-host.md) или передай адрес "
            + "живого в env.PLAYWRIGHT_BASE_URL (или опиши webServer в конфиге — тогда стенд поднимет сам "
            + "Playwright); иначе каждый тест упал бы по таймауту.";
    }

    // Любой HTTP-ответ — стенд жив (401/404 тоже); отказ соединения или тайм-аут — лежит.
    // Без прокси: локальный адрес не должен уехать в системный прокси машины
    private static readonly HttpClient StandClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<bool> ProbeStandAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await StandClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // Итог для модели: сводка под потолком MaxResultBytes
    public string FormatResult(TestRunResult result) =>
        TestRunSummaryFormatter.Format(result, _options.MaxResultBytes, _options.CeilingSeconds);

    // Состояние одного прогона: хвост вывода, полный лог в файл, разбор строк фаз под вид
    // прогона и живой счётчик. Строки приходят из двух потоков сразу — всё под одним замком
    private sealed class RunState : IDisposable
    {
        private readonly TestRunService _owner;
        private readonly TestRunKind _kind;
        private readonly Action<TestRunProgress>? _progress;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Queue<string> _tail = new();
        private readonly TextWriter _log;
        private readonly List<string> _buildErrors = [];
        private readonly VsTestConsoleParser.ListCounter _listCounter = new();
        private readonly List<string> _failedNames = [];
        // Playwright: исход по названию теста — повтор (retry) перезаписывает, а не добавляет
        private readonly Dictionary<string, TestLineOutcome> _byTitle = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _listedFiles;
        private int _filesDone;
        private TestRunPhase _phase;
        private TestCounts _counts;
        private TimeSpan _lastProgress = TimeSpan.MinValue;
        private string? _turnId;
        private string? _lastLabel;
        private int? _total;

        // log null — лог не пишется (папка за ссылкой или файл подложен), прогон идёт
        public RunState(TestRunService owner, TestRunKind kind, StreamWriter? log, Action<TestRunProgress>? progress,
            string? artifacts, string results)
        {
            _owner = owner;
            _kind = kind;
            _progress = progress;
            Artifacts = artifacts;
            Results = results;
            _log = log ?? TextWriter.Null;
        }

        // Папка артефактов относительно рабочего дерева — для модели; null — хост её не вёл
        public string? Artifacts { get; }

        // Та же папка относительно каталога процесса — аргумент результатов
        public string Results { get; }

        public int? Total
        {
            get { lock (_gate) return _total; }
            set { lock (_gate) _total = value; }
        }

        // Итог подсчёта: тесты dotnet или файлы vitest
        public int? ListedCount
        {
            get { lock (_gate) return _kind == TestRunKind.Vitest ? _listedFiles : _listCounter.Count; }
        }

        public void Report(TestRunProgress progress) => _progress?.Invoke(progress);

        public void BeginPhase(TestRunPhase phase, ProcessSpec spec, string turnId)
        {
            lock (_gate)
            {
                _phase = phase;
                _turnId = turnId;
                _log.WriteLine($"=== {spec.FileName} {string.Join(' ', spec.Args)}");
            }
        }

        // Начало фазы прогона: нулевой счётчик сразу (announce), дальше — по шагу прогресса
        public void StartTesting(bool announce = true)
        {
            TestRunProgress first;
            lock (_gate)
            {
                _phase = TestRunPhase.Test;
                _lastProgress = _watch.Elapsed;
                first = ProgressNow();
            }
            if (announce) Report(first);
        }

        // Итог разбора строки регулярками под вид прогона: считается ВНЕ замка (фаза в этот миг
        // ещё не известна — разбираются все разборы вида, а нужный выбирает фаза под замком)
        private readonly record struct ParsedLine(bool ListedFile, VitestFileLine? VitestFile, int? PlaywrightTotal,
            PlaywrightOutcomeLine? PlaywrightOutcome, TestOutcomeLine? DotnetOutcome);

        private ParsedLine Parse(string line) => _kind switch
        {
            TestRunKind.Vitest => new(VitestReporterParser.IsListedFile(line), VitestReporterParser.ParseFileLine(line),
                null, null, null),
            TestRunKind.Playwright => new(false, null, PlaywrightListParser.ParseTotal(line),
                PlaywrightListParser.ParseOutcome(line), null),
            _ => new(false, null, null, null, VsTestConsoleParser.ParseOutcome(line)),
        };

        // Разбор строки (регулярки) — до замка: строку печатает код агента, и медленный разбор не
        // должен держать замок, в который упрётся Result после «Стоп»; под замком — только счётчики
        public void OnLine(string line)
        {
            var parsed = Parse(line);
            TestRunProgress? toSend = null;
            lock (_gate)
            {
                _log.WriteLine(line);
                _tail.Enqueue(line);
                while (_tail.Count > _owner._options.TailLines) _tail.Dequeue();

                var (counted, force) = (false, false);
                switch (_phase)
                {
                    case TestRunPhase.Build:
                        // Кандидаты в ошибки — дёшево, точный разбор в BuildErrors
                        if (_buildErrors.Count < MaxBuildErrorCandidates
                            && (line.Contains(" error ", StringComparison.Ordinal) || line.Contains(":error ", StringComparison.Ordinal)))
                            _buildErrors.Add(line);
                        break;
                    case TestRunPhase.List when _kind == TestRunKind.Vitest:
                        if (parsed.ListedFile) _listedFiles++;
                        break;
                    case TestRunPhase.List:
                        _listCounter.Feed(line);
                        break;
                    case TestRunPhase.Test:
                        (counted, force) = _kind switch
                        {
                            TestRunKind.Vitest => OnVitestLine(parsed.VitestFile),
                            TestRunKind.Playwright => OnPlaywrightLine(parsed.PlaywrightTotal, parsed.PlaywrightOutcome),
                            _ => OnDotnetLine(parsed.DotnetOutcome),
                        };
                        break;
                }
                // Не чаще шага прогресса: тысячи строк в секунду не должны стать тысячами
                // событий в ленту; первое общее число (Playwright) — сразу
                if (counted || force)
                {
                    var now = _watch.Elapsed;
                    if (force || now - _lastProgress >= _owner._progressInterval)
                    {
                        _lastProgress = now;
                        toSend = ProgressNow();
                    }
                }
            }
            if (toSend is not null) Report(toSend);
        }

        private (bool Counted, bool Force) OnDotnetLine(TestOutcomeLine? parsed)
        {
            if (parsed is not { } outcome) return (false, false);
            _counts = Add(_counts, outcome.Outcome, +1);
            if (outcome.Outcome == TestLineOutcome.Failed && _failedNames.Count < MaxConsoleFailures)
                _failedNames.Add(outcome.Name);
            return (true, false);
        }

        private (bool Counted, bool Force) OnVitestLine(VitestFileLine? parsed)
        {
            if (parsed is not { } file) return (false, false);
            _filesDone++;
            var passed = Math.Max(0, file.Tests - file.Failed - file.Skipped);
            // Не загрузившийся файл — одно падение: тестов в нём нет, а прогон красный
            var failed = file.Tests == 0 && file.FileFailed ? 1 : file.Failed;
            _counts = new TestCounts(_counts.Passed + passed, _counts.Failed + failed, _counts.Skipped + file.Skipped);
            if (file.FileFailed && _failedNames.Count < MaxConsoleFailures)
                _failedNames.Add(file.Tests > 0 ? $"{file.File} (упало {failed})" : $"{file.File} (файл не загрузился)");
            return (true, false);
        }

        private (bool Counted, bool Force) OnPlaywrightLine(int? parsedTotal, PlaywrightOutcomeLine? parsed)
        {
            if (_total is null && parsedTotal is { } total)
            {
                _total = total;
                return (false, true);
            }
            if (parsed is not { } outcome) return (false, false);
            if (_byTitle.TryGetValue(outcome.Title, out var previous)) _counts = Add(_counts, previous, -1);
            _byTitle[outcome.Title] = outcome.Outcome;
            _counts = Add(_counts, outcome.Outcome, +1);
            return (true, false);
        }

        private static TestCounts Add(TestCounts c, TestLineOutcome outcome, int delta) => outcome switch
        {
            TestLineOutcome.Passed => c with { Passed = c.Passed + delta },
            TestLineOutcome.Failed => c with { Failed = c.Failed + delta },
            _ => c with { Skipped = c.Skipped + delta },
        };

        // Прогресс сейчас (под замком): vitest — по файлам, остальные — по тестам
        private TestRunProgress ProgressNow()
        {
            var vitest = _kind == TestRunKind.Vitest;
            var done = vitest ? _filesDone : _counts.Done;
            var label = vitest
                ? TestRunSummaryFormatter.FilesProgressLabel(_filesDone, _counts.Failed, _total)
                : TestRunSummaryFormatter.ProgressLabel(_counts, _total);
            _lastLabel = label;
            return new TestRunProgress("running", label, TestRunSummaryFormatter.ProgressPercent(done, _total),
                _total is > 0 ? true : null);
        }

        public IEnumerable<string> BuildErrorCandidates()
        {
            lock (_gate) return [.. _buildErrors];
        }

        public TestRunResult Result(int? exitCode, TestRunPhase phase)
        {
            lock (_gate)
            {
                _log.Flush();
                List<string> failedNames = _kind == TestRunKind.Playwright
                    ? [.. _byTitle.Where(p => p.Value == TestLineOutcome.Failed).Select(p => p.Key).Take(MaxConsoleFailures)]
                    : [.. _failedNames];
                return new TestRunResult(null, exitCode, false, false, _watch.Elapsed, [.. _tail], _turnId)
                {
                    Kind = _kind,
                    Phase = phase,
                    Total = _total,
                    Counts = _counts,
                    ProgressLabel = _lastLabel,
                    FailedFromConsole = failedNames,
                    ArtifactsPath = Artifacts,
                };
            }
        }

        public void Dispose()
        {
            lock (_gate) _log.Dispose();
        }
    }
}
