using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Движок сборки `dotnet build` с прогрессом «N из M проектов» (будущий инструмент `dev build`,
// docs/research/build-stand-progress-2026-10.md, этап 2; тулсет и узел CLI — этап 4). Запуск
// только через среду проекта (ILauncherFactory.ForProject) на ОБЩЕМ с run_tests конвейере фаз:
//  • блокировка «одно дерево — один тяжёлый прогон» общая с тестами — `dotnet build` и
//    `dotnet test` в одном дереве дерутся за obj/bin; конвейер берётся из DI, а не new;
//  • spec Heavy — слот BuildConcurrencyGate на всю сборку, свой TurnId из ProcessTurnIds;
//  • «Стоп» (обрыв вызова) и серверный потолок гасят ДЕРЕВО процесса (узлы MSBuild — потомки);
//  • цель — путь ОТНОСИТЕЛЬНО дерева, уже проверенный вызывающим; второй рубеж — Validate:
//    ни опции, ни response-файла.
// Прогресс и проценты — DotnetBuildProgress; память прошлой успешной сборки цели —
// BuildRunMemory в data/build-memory сервера (вне дерева агента); лог — .cc-attachments/build-runs/{runId}/.
public sealed class DotnetBuildService
{
    public const string ArtifactsSubdir = "build-runs";

    private readonly ILauncherFactory _launchers;
    private readonly TestRunsOptions _options;
    private readonly PhasePipeline _pipeline;
    private readonly TimeSpan _progressInterval;
    private readonly TimeSpan _estimateTimeout;
    private readonly Func<string, string?, CancellationToken, int?> _estimate;

    // Потолок подготовки (чтение памяти и оценка M обходом XML): она идёт под взятой блокировкой
    // дерева и не должна её держать
    public static readonly TimeSpan EstimateTimeout = TimeSpan.FromSeconds(5);

    // DI: конвейер — синглтон подсистемы, общий с TestRunService
    public DotnetBuildService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline)
        : this(launchers, options, pipeline, progressInterval: null) { }

    // Для тестов: свой шаг прогресса (по умолчанию — раз в секунду), свои оценщик M и его потолок
    internal DotnetBuildService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline,
        TimeSpan? progressInterval, Func<string, string?, CancellationToken, int?>? estimate = null,
        TimeSpan? estimateTimeout = null)
    {
        _launchers = launchers;
        _options = options;
        _pipeline = pipeline;
        _progressInterval = progressInterval ?? TimeSpan.FromSeconds(1);
        _estimate = estimate ?? DotnetBuildProgress.EstimateTotal;
        _estimateTimeout = estimateTimeout ?? EstimateTimeout;
    }

    // Конвейер этого движка — для проверки, что он общий с тестами
    internal PhasePipeline Pipeline => _pipeline;

    internal static string? Validate(DotnetBuildRequest request) =>
        TestRunService.LooksLikeOption(request.Target)
            ? "target не может начинаться с «-» или «@»: это была бы опция dotnet (или response-файл)."
            : null;

    internal static ProcessSpec BuildSpec(DotnetBuildRequest request, string turnId)
    {
        var args = new List<string> { "build" };
        if (!string.IsNullOrWhiteSpace(request.Target)) args.Add(request.Target);
        return new ProcessSpec
        {
            FileName = "dotnet",
            Args = args,
            WorkingDirectory = TestRunService.UpperDriveLetter(request.WorkingDirectory),
            Env = new Dictionary<string, string>
            {
                // Разбору нужен английский вывод: строки `->` и `error CS…`
                ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                ["DOTNET_NOLOGO"] = "1",
            },
            StdioEncoding = new UTF8Encoding(false),
            RedirectStdin = false,
            Heavy = true,
            // Узлы MSBuild и компилятор — только свои: не переживают сборку и не гибнут
            // от «Стопа» чужого дерева (ProcessSpec.PrivateBuildNodes)
            PrivateBuildNodes = true,
            Track = true,
            TurnId = turnId,
            SessionId = request.SessionId,
        };
    }

    // Сборка. progress — живые фазы для карточки (queued / build); исключения из колбэка не
    // глотаются — вызывающий обязан сделать его безопасным (тулсет шлёт fire-and-forget)
    public async Task<BuildRunResult> RunAsync(DotnetBuildRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct)
    {
        if (Validate(request) is { } refusal) return BuildRunResult.Refused(refusal);

        using var treeLock = _pipeline.TryLockTree(request.WorkingDirectory);
        if (treeLock is null)
            return BuildRunResult.Refused("В этом дереве уже идёт сборка или прогон тестов — дождись его конца: "
                + "два процесса подерутся за obj/bin." + PhasePipeline.BusyWaitHint);

        var launcher = _launchers.ForProject(request.Project);
        var memoryPath = _options.MemoryDirectory is { } memoryDir
            ? BuildRunMemory.PathFor(memoryDir, request.WorkingDirectory, request.Target)
            : null;
        var (previous, estimate) = await PrepareAsync(request, memoryPath, ct);
        var (artifacts, log) = await PhasePipeline.CreateArtifactsAsync(request.WorkingDirectory, ArtifactsSubdir,
            PhasePipeline.ArtifactsTimeout, ct);
        using var run = new RunState(new DotnetBuildProgress(previous, estimate), log, _options.TailLines,
            log is null ? null : artifacts.Relative);

        using var limit = PhasePipeline.Ceiling(_options.CeilingSeconds, ct);
        var turnId = ProcessTurnIds.New();
        var spec = BuildSpec(request, turnId);
        var slot = await _pipeline.AcquireAsync(spec,
            busy => progress?.Invoke(new TestRunProgress("queued", $"ждёт очереди сборок (занято {busy})")), limit.Token);
        if (slot is null)
            // «Погашен» здесь было бы враньём: процесса не было
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
            // Между строками `->` полоса ползёт по времени: тикер, а не только строки
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
            if (outcome.ExitCode == 0 && memoryPath is not null) run.SaveMemory(memoryPath, previous);
            return result;
        }
    }

    // Память прошлой сборки, а без неё — оценка M: подсказки для процента. Всё чтение (и
    // память, и обход XML) — в пуле под потолком и токеном: не уложилось, отменено или упало
    // чем угодно — сборка идёт без подсказки, а блокировку дерева синхронное чтение не держит
    // (обход сам останавливается на следующем файле по токену)
    private async Task<(BuildRunMemory? Previous, int? Estimate)> PrepareAsync(DotnetBuildRequest request,
        string? memoryPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_estimateTimeout);
        try
        {
            return await Task.Run(() =>
            {
                var previous = memoryPath is null ? null : BuildRunMemory.Load(memoryPath);
                return (previous, previous is null ? _estimate(request.WorkingDirectory, request.Target, timeout.Token) : null);
            }, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception)
        {
            return (null, null);
        }
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

    // Состояние одной сборки: разбор строк, хвост, полный лог в файл. Строки идут из двух
    // потоков сразу, тикер — из третьего: состояние под одним замком, разбор строк — вне его
    // log null — лог не пишется (папка артефактов за ссылкой или файл подложен), artifacts тогда null
    private sealed class RunState(DotnetBuildProgress progress, StreamWriter? log, int tailLines, string? artifacts)
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
                _log.WriteLine($"=== {spec.FileName} {string.Join(' ', spec.Args)}");
            }
        }

        // Разбор строки (регулярка проекта) — ВНЕ замка, под замком только счётчики, хвост и лог
        public void OnLine(string line)
        {
            var project = DotnetBuildProgress.ParseProjectLine(line);
            // Кандидаты в ошибки — дёшево, точный разбор в BuildErrors; длина строки — под
            // потолком, иначе 2000 гигантских строк держали бы память до конца сборки
            var error = line.Contains(" error ", StringComparison.Ordinal) || line.Contains(":error ", StringComparison.Ordinal);
            lock (_gate)
            {
                _log.WriteLine(line);
                _tail.Enqueue(line);
                while (_tail.Count > tailLines) _tail.Dequeue();
                progress.Apply(project, _watch.Elapsed);
                if (error && _errors.Count < TestRunService.MaxBuildErrorCandidates)
                    _errors.Add(line.Length > VsTestConsoleParser.MaxOutcomeLineLength
                        ? line[..VsTestConsoleParser.MaxOutcomeLineLength]
                        : line);
            }
        }

        public TestRunProgress Snapshot()
        {
            lock (_gate) return _lastSent = progress.Snapshot(_watch.Elapsed);
        }

        // Тикер шлёт только перемены: одинаковая подпись с тем же процентом — не событие
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
            // Память — подсказка для процента; не записалась — сборка от этого не хуже
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
                errors = exitCode is 0 ? [] : [.. _errors];
                result = new BuildRunResult(null, exitCode, false, false, _watch.Elapsed, [.. _tail], turnId)
                {
                    Done = progress.Done,
                    Total = progress.Total,
                    ProgressLabel = progress.Label(),
                    ArtifactsPath = artifacts,
                };
            }
            return result with { BuildErrors = VsTestConsoleParser.BuildErrors(errors) };
        }

        public void Dispose()
        {
            lock (_gate) _log.Dispose();
        }
    }
}

// Запрос сборки: проект (по нему выбирается среда), рабочее дерево чата (ХОСТОВЫЙ путь),
// чат-вызыватель и цель — проект/решение/каталог ОТНОСИТЕЛЬНО дерева (пусто — корень)
public sealed record DotnetBuildRequest(ClaudeHomeServer.Models.Project Project, string WorkingDirectory,
    string SessionId, string? Target = null);
