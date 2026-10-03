using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Движок сборки `npm run <скрипт>` с прогрессом «этап N из M» (будущий инструмент `dev build`
// kind=npm, docs/research/build-stand-progress-2026-10.md, этап 3; тулсет — этап 4). Тот же
// конвейер фаз, что у run_tests и `dotnet build` (блокировка дерева общая: сборка фронта и прогон
// vitest в одном дереве делят node_modules/.vite и dist).
//
// Безопасность — всё, что приходит от модели, в путь бинаря и в shell не попадает:
//  • имя скрипта — белый список символов (NpmBuildRequest.ScriptName): ни `&`, ни пробела, ни
//    `;`, ни кавычек, ни ведущего «-» (была бы опция npm);
//  • npm запускается НЕ через npm.cmd (на Windows это cmd.exe /c, и `&` в аргументе стал бы второй
//    командой), а `node -e <бутстрап>`: бутстрап — константа сервера, он находит npm-cli.js рядом
//    с исполняемым node (Windows — node_modules/npm, Linux — ../lib/node_modules/npm) и отдаёт ему
//    argv; аргументы — только ArgumentList, `--` отделяет их от опций node;
//  • цель — каталог ВНУТРИ дерева (SafePath.Join, ссылки по пути наружу — отказ), package.json
//    читается одним дескриптором под потолком (TreeFiles.ReadInTree), его скрипт разбирается
//    только ради подписи этапов: исполняет его сама оболочка npm, это код репозитория;
//  • зависимости ищутся как у run_tests: ближайший node_modules от цели вверх до корня дерева,
//    не выше; ведёт ссылкой за дерево — отказ (node исполнил бы чужой код хоста);
//  • подготовка (чтение package.json, поиск node_modules, память) — в пуле под потолком и ДО
//    блокировки дерева: синхронное чтение файлов агента блокировку не держит;
//  • память прошлого прогона — BuildRunMemory в data/build-memory сервера (вне дерева агента),
//    лог — .cc-attachments/build-runs/{runId}/ через PhasePipeline.CreateArtifacts.
public sealed partial class NpmBuildService
{
    // Потолок package.json: настоящий — килобайты, больше — подлог агента
    public const int MaxPackageJsonBytes = 1024 * 1024;

    // Потолок подготовки (чтение package.json, поиск node_modules, память)
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(5);

    // Бутстрап npm без npm.cmd: находит npm-cli.js рядом с исполняемым node и передаёт ему argv
    // (process.argv[1..] у `node -e` — аргументы после `--`). Константа сервера: ничего из ввода
    // модели в код не попадает. ASCII — без сюрпризов кодировки командной строки
    internal const string NpmBootstrap =
        "const p=require('path'),f=require('fs'),d=p.dirname(process.execPath);"
        + "const c=[p.join(d,'node_modules','npm','bin','npm-cli.js'),"
        + "p.join(d,'..','lib','node_modules','npm','bin','npm-cli.js')].find(x=>f.existsSync(x));"
        + "if(!c){console.error('npm error: npm-cli.js not found next to node '+process.execPath);process.exit(127)}"
        + "process.argv=[process.execPath,c,...process.argv.slice(1)];require(c)";

    private readonly ILauncherFactory _launchers;
    private readonly TestRunsOptions _options;
    private readonly PhasePipeline _pipeline;
    private readonly TimeSpan _progressInterval;
    private readonly TimeSpan _prepareTimeout;

    // DI: конвейер — синглтон подсистемы, общий с TestRunService и DotnetBuildService
    public NpmBuildService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline)
        : this(launchers, options, pipeline, progressInterval: null) { }

    // Для тестов: свой шаг прогресса и потолок подготовки
    internal NpmBuildService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline,
        TimeSpan? progressInterval, TimeSpan? prepareTimeout = null)
    {
        _launchers = launchers;
        _options = options;
        _pipeline = pipeline;
        _progressInterval = progressInterval ?? TimeSpan.FromSeconds(1);
        _prepareTimeout = prepareTimeout ?? PrepareTimeout;
    }

    internal PhasePipeline Pipeline => _pipeline;

    // Имя скрипта: буква или цифра, дальше буквы, цифры и `:_.-`, не длиннее 100
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:_.\-]{0,99}$")]
    private static partial Regex ScriptNamePattern();

    public static bool IsScriptName(string? script) => script is not null && ScriptNamePattern().IsMatch(script);

    // Первый рубеж — форма запроса, до диска
    internal static string? Validate(NpmBuildRequest request)
    {
        if (!IsScriptName(request.Script))
            return "Имя скрипта — только латинские буквы, цифры и «:_.-», первым символом буква или цифра "
                + "(без пробелов, «&», «;», кавычек): скрипт целиком описывается в package.json.";
        if (TestRunService.LooksLikeOption(request.Target))
            return "target не может начинаться с «-» или «@»: это была бы опция.";
        return null;
    }

    // spec запуска: `node -e <бутстрап> -- run <скрипт>` в каталоге цели. Цвет выключен: разбору
    // нужны чистые строки (ANSI, которые всё же придут, режет NpmBuildProgress.StripAnsi);
    // FORCE_COLOR=0 без NO_COLOR — вместе они дают предупреждение node на каждый процесс
    internal static ProcessSpec BuildSpec(NpmBuildRequest request, string directory, string turnId) => new()
    {
        FileName = "node",
        Args = ["-e", NpmBootstrap, "--", "run", request.Script],
        WorkingDirectory = TestRunService.UpperDriveLetter(directory),
        Env = new Dictionary<string, string>
        {
            ["FORCE_COLOR"] = "0",
            ["npm_config_color"] = "false",
            ["npm_config_update_notifier"] = "false",
            ["npm_config_fund"] = "false",
            ["npm_config_audit"] = "false",
        },
        StdioEncoding = new UTF8Encoding(false),
        RedirectStdin = false,
        Heavy = true,
        Track = true,
        TurnId = turnId,
        SessionId = request.SessionId,
    };

    // Сборка. progress — живые фазы для карточки (queued / build); исключения из колбэка не
    // глотаются — вызывающий обязан сделать его безопасным (тулсет шлёт fire-and-forget)
    public async Task<BuildRunResult> RunAsync(NpmBuildRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct)
    {
        if (Validate(request) is { } refusal) return BuildRunResult.Refused(refusal);

        var memoryPath = _options.MemoryDirectory is { } memoryDir
            ? BuildRunMemory.PathFor(memoryDir, request.WorkingDirectory, request.Target, "npm:" + request.Script)
            : null;
        var prepared = await PrepareAsync(request, memoryPath, ct);
        if (prepared.Refusal is { } notReady) return BuildRunResult.Refused(notReady);

        using var treeLock = _pipeline.TryLockTree(request.WorkingDirectory);
        if (treeLock is null)
            return BuildRunResult.Refused("В этом дереве уже идёт сборка или прогон тестов — дождись его конца: "
                + "два процесса подерутся за артефакты сборки.");

        var launcher = _launchers.ForProject(request.Project);
        var (artifacts, log) = await PhasePipeline.CreateArtifactsAsync(request.WorkingDirectory,
            DotnetBuildService.ArtifactsSubdir, PhasePipeline.ArtifactsTimeout, ct);
        using var run = new RunState(new NpmBuildProgress(prepared.Stages, prepared.Previous), log, _options.TailLines,
            log is null ? null : artifacts.Relative);

        using var limit = PhasePipeline.Ceiling(_options.CeilingSeconds, ct);
        var turnId = ProcessTurnIds.New();
        var spec = BuildSpec(request, prepared.Directory!, turnId);
        var slot = await _pipeline.AcquireAsync(spec,
            busy => progress?.Invoke(new TestRunProgress("queued", $"ждёт очереди сборок (занято {busy})")), limit.Token);
        if (slot is null)
            return run.Result(null, turnId: null) with
            {
                Cancelled = ct.IsCancellationRequested,
                TimedOut = !ct.IsCancellationRequested,
                NeverStarted = true,
            };

        using (slot)
        {
            run.Start(spec);
            progress?.Invoke(run.Snapshot());
            using var ticker = new CancellationTokenSource();
            var ticking = TickAsync(run, progress, ticker.Token);
            PhaseOutcome outcome;
            try
            {
                outcome = await _pipeline.RunProcessAsync(launcher, spec, run.OnLine, limit.Token);
            }
            finally
            {
                await ticker.CancelAsync();
                await ticking;
            }

            var result = run.Result(outcome.ExitCode, turnId);
            if (outcome.Aborted)
                return result with { Cancelled = ct.IsCancellationRequested, TimedOut = !ct.IsCancellationRequested };
            if (outcome.ExitCode == 0 && memoryPath is not null) run.SaveMemory(memoryPath, prepared.Previous);
            return result;
        }
    }

    // Итог подготовки: отказ ИЛИ каталог цели, этапы скрипта и память прошлого прогона
    private sealed record Prepared(string? Refusal, string? Directory, IReadOnlyList<string> Stages,
        BuildRunMemory? Previous);

    // Всё чтение диска — в пуле под потолком и токеном: зависло или отменено — отказ, а не вечный вызов
    private async Task<Prepared> PrepareAsync(NpmBuildRequest request, string? memoryPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_prepareTimeout);
        try
        {
            return await Task.Run(() =>
            {
                if (Inspect(request.WorkingDirectory, request.Target, request.Script, out var directory, out var stages)
                    is { } refusal)
                    return new Prepared(refusal, null, [], null);
                var previous = memoryPath is null ? null : BuildRunMemory.Load(memoryPath);
                return new Prepared(null, directory, stages, previous);
            }, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new Prepared("Сборка отменена до запуска.", null, [], null);
        }
        catch (OperationCanceledException)
        {
            return new Prepared("Не удалось прочитать package.json и найти node_modules за "
                + $"{_prepareTimeout.TotalSeconds:0} с — повтори вызов.", null, [], null);
        }
    }

    // Проверки на диске: каталог цели в дереве, package.json со скриптом, node_modules в дереве.
    // null — всё годно (directory — полный путь каталога цели, stages — этапы скрипта); иначе текст отказа
    internal static string? Inspect(string workingDirectory, string? target, string script,
        out string? directory, out IReadOnlyList<string> stages)
    {
        directory = null;
        stages = [];
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dir;
        try { dir = string.IsNullOrWhiteSpace(target) ? root : SafePath.Join(root, target.Trim()); }
        catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "target выходит за пределы проекта — вызов отклонён.";
        }
        dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!TestRunService.ResolvesInside(root, dir))
            return "target ведёт символической ссылкой за пределы проекта — вызов отклонён.";
        if (!Directory.Exists(dir))
            return $"Каталога «{Shown(root, dir)}» нет — target это каталог с package.json.";

        var packageJson = Path.Combine(dir, "package.json");
        if (DotnetBuildProgress.FileInTree(root, packageJson) is null
            || TreeFiles.ReadInTree(root, packageJson, MaxPackageJsonBytes) is not { } bytes)
            return $"В «{Shown(root, dir)}» нет package.json (или это не обычный файл проекта не больше "
                + $"{MaxPackageJsonBytes / 1024} КБ).";
        if (ScriptOf(bytes, script, out var available) is not { } body)
            return available is null
                ? "package.json не разбирается как JSON с объектом scripts."
                : $"В package.json нет скрипта «{script}». Есть: {(available.Count == 0 ? "ни одного" : string.Join(", ", available))}.";

        if (FindNodeModules(root, dir, out var error) is null) return error;
        directory = dir;
        stages = NpmBuildProgress.SplitStages(body);
        return null;
    }

    // Тело скрипта из package.json; null — скрипта нет (available — имена, что есть, не больше
    // 20 и только допустимой формы) или JSON битый (available = null)
    internal static string? ScriptOf(byte[] packageJson, string script, out IReadOnlyList<string>? available)
    {
        available = null;
        try
        {
            using var doc = JsonDocument.Parse(packageJson, new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                available = [];
                return null;
            }
            if (scripts.TryGetProperty(script, out var body) && body.ValueKind == JsonValueKind.String)
                return body.GetString() ?? "";
            available = [.. scripts.EnumerateObject().Select(p => p.Name).Where(IsScriptName).Take(20)];
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Ближайший node_modules от каталога цели вверх до корня дерева (не выше: там чужое), как у
    // run_tests (TestRunService.FindNodeBin). Первый найденный обязан и после раскрытия ссылок
    // лежать в дереве. Возвращает полный путь или null с текстом ошибки
    internal static string? FindNodeModules(string root, string directory, out string error)
    {
        error = "";
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        for (var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
             ;
             dir = Path.GetDirectoryName(dir)!)
        {
            var candidate = Path.Combine(dir, "node_modules");
            if (Directory.Exists(candidate))
            {
                if (!TestRunService.ResolvesInside(rootFull, candidate))
                {
                    error = $"{Shown(rootFull, candidate)} ведёт символической ссылкой за пределы проекта — "
                        + "такие зависимости не запускаем.";
                    return null;
                }
                return candidate;
            }
            if (comparer.Equals(dir, rootFull) || Path.GetDirectoryName(dir) is null
                || !TestRunService.ResolvesInside(rootFull, Path.GetDirectoryName(dir)!))
                break;
        }
        error = $"Ни в «{Shown(rootFull, directory)}», ни выше до корня проекта нет node_modules — зависимости "
            + "не установлены (npm ci / pnpm install) или это не тот каталог.";
        return null;
    }

    private static string Shown(string rootFull, string path)
    {
        var relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
        return relative == "." ? "." : relative;
    }

    private async Task TickAsync(RunState run, Action<TestRunProgress>? progress, CancellationToken stop)
    {
        if (progress is null) return;
        using var timer = new PeriodicTimer(_progressInterval > TimeSpan.Zero ? _progressInterval : TimeSpan.FromMilliseconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                if (run.SnapshotIfChanged() is { } snapshot) progress(snapshot);
        }
        catch (OperationCanceledException) { }
    }

    // Итог для модели: сводка под потолком MaxResultBytes
    public string FormatResult(BuildRunResult result) => BuildRunSummary.Format(result, _options.MaxResultBytes);

    // Строки ошибок сборки фронта: tsc (`file(1,2): error TS…`), npm (`npm error …`, старое
    // `npm ERR!`), vite/rolldown (`✗ Build failed`, `[vite]: …`, `error during build:`)
    [GeneratedRegex(@"\berror TS\d+:|^npm (?:error|ERR!)|✗ Build failed|^\[vite\]|^error during build",
        RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex ErrorLine();

    // Строка ошибки сборки; длинная и сработавший потолок сопоставления — не она
    private static bool IsErrorLine(string trimmed)
    {
        if (trimmed.Length > VsTestConsoleParser.MaxOutcomeLineLength) return false;
        try { return ErrorLine().IsMatch(trimmed); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static IReadOnlyList<string> BuildErrors(IEnumerable<string> lines, int max = 20)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!IsErrorLine(trimmed) || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
            if (result.Count >= max) break;
        }
        return result;
    }

    // «этапа/этапов» после «из N»
    private static string StagesOf(int n) => n % 10 == 1 && n % 100 != 11 ? "этапа" : "этапов";

    // Состояние одной сборки: разбор строк, хвост, полный лог в файл. Строки идут из двух
    // потоков сразу, тикер — из третьего: состояние под одним замком. Разбор строки (регулярки) —
    // ВНЕ замка: строку печатает код агента, и даже медленный разбор не должен держать замок, в
    // который упрётся Result после «Стоп» (ревью этапа 3)
    private sealed class RunState(NpmBuildProgress progress, StreamWriter? log, int tailLines, string? artifacts)
        : IDisposable
    {
        private readonly Stopwatch _watch = new();
        private readonly Queue<string> _tail = new();
        private readonly List<string> _errors = [];
        private readonly TextWriter _log = log ?? TextWriter.Null;
        private readonly object _gate = new();
        private TestRunProgress? _lastSent;

        public void Start(ProcessSpec spec)
        {
            lock (_gate)
            {
                _watch.Start();
                // Бутстрап в логе не нужен — он константа сервера
                _log.WriteLine($"=== npm {string.Join(' ', spec.Args.Skip(3))}");
            }
        }

        public void OnLine(string raw)
        {
            var line = NpmBuildProgress.StripAnsi(raw);
            var parsed = NpmBuildProgress.Parse(line);
            // Строка ошибки длиннее потолка в кандидаты не идёт вовсе (IsErrorLine)
            var error = IsErrorLine(line.Trim());
            lock (_gate)
            {
                _log.WriteLine(line);
                _tail.Enqueue(line);
                while (_tail.Count > tailLines) _tail.Dequeue();
                progress.Apply(parsed, _watch.Elapsed);
                if (error && _errors.Count < TestRunService.MaxBuildErrorCandidates) _errors.Add(line);
            }
        }

        public TestRunProgress Snapshot()
        {
            lock (_gate) return _lastSent = progress.Snapshot(_watch.Elapsed);
        }

        public TestRunProgress? SnapshotIfChanged()
        {
            lock (_gate)
            {
                var now = progress.Snapshot(_watch.Elapsed);
                if (now == _lastSent) return null;
                return _lastSent = now;
            }
        }

        public void SaveMemory(string path, BuildRunMemory? previous)
        {
            BuildRunMemory memory;
            lock (_gate) memory = progress.ToMemory(_watch.Elapsed);
            if (!memory.Replaces(previous)) return;
            try { memory.Save(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // Под замком — только снимок; разбор ошибок — после него
        public BuildRunResult Result(int? exitCode, string? turnId)
        {
            BuildRunResult result;
            List<string> errors;
            lock (_gate)
            {
                _log.Flush();
                var total = progress.Total;
                errors = exitCode is 0 ? [] : [.. _errors];
                result = new BuildRunResult(null, exitCode, false, false, _watch.Elapsed, [.. _tail], turnId)
                {
                    Done = exitCode is 0 ? total : progress.Done,
                    Total = total,
                    ProgressLabel = exitCode is 0 ? $"{total} из {total} {StagesOf(total)}" : progress.Label(),
                    ArtifactsPath = artifacts,
                };
            }
            return result with { BuildErrors = BuildErrors(errors) };
        }

        public void Dispose()
        {
            lock (_gate) _log.Dispose();
        }
    }
}

// Запрос npm-сборки: проект (по нему выбирается среда), рабочее дерево чата (ХОСТОВЫЙ путь),
// чат-вызыватель, каталог с package.json ОТНОСИТЕЛЬНО дерева (пусто — корень) и имя скрипта
public sealed record NpmBuildRequest(ClaudeHomeServer.Models.Project Project, string WorkingDirectory,
    string SessionId, string? Target = null, string Script = "build");
