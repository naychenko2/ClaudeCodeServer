using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Конвейер фаз: общая часть движков, запускающих тяжёлые процессы агента в среде проекта, —
// прогона тестов (TestRunService) и сборки/стенда инструментов `dev`
// (docs/research/build-stand-progress-2026-10.md, раздел «Движок»). Что умеет сам конвейер:
//  • блокировка «одно рабочее дерево — один тяжёлый прогон»: `dotnet build` и `dotnet test`
//    в одном дереве дерутся за obj/bin, поэтому блокировка общая для всех видов прогонов —
//    экземпляр конвейера обязан быть один на процесс (DI-синглтон);
//  • папка артефактов `.cc-attachments/{подкаталог}/{runId}`;
//  • серверный потолок, общий на весь конвейер вместе с ожиданием очереди;
//  • захват слота BuildConcurrencyGate с сигналом «встал в очередь»;
//  • фаза-процесс: старт через лаунчер, вычитка обоих потоков построчно, гашение ДЕРЕВА по
//    отмене или потолку (в песочнице — по TurnId из spec).
// Разбор строк, этапы и форма итога — у вызывающего движка: конвейер о видах прогонов не знает.
public sealed class PhasePipeline
{
    private readonly BuildConcurrencyGate? _gate;
    private readonly TimeSpan _exitGrace;

    // Деревья с идущим прогоном; ключ — нормализованный полный путь
    private readonly ConcurrentDictionary<string, byte> _running =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public PhasePipeline() : this(gate: null, exitGrace: TimeSpan.FromSeconds(10)) { }

    // Для тестов: свой экземпляр гейта вместо процесс-глобального и короткая пауза на выход
    internal PhasePipeline(BuildConcurrencyGate? gate, TimeSpan exitGrace)
    {
        _gate = gate;
        _exitGrace = exitGrace;
    }

    // Гейт читается на каждый прогон: Configure со старта хоста подменяет Instance
    public BuildConcurrencyGate Gate => _gate ?? BuildConcurrencyGate.Instance;

    // Занять дерево под прогон; null — в нём уже идёт другой. Освобождение — Dispose
    public IDisposable? TryLockTree(string workingDirectory)
    {
        var key = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _running.TryAdd(key, 0) ? new TreeLock(_running, key) : null;
    }

    private sealed class TreeLock(ConcurrentDictionary<string, byte> running, string key) : IDisposable
    {
        public void Dispose() => running.TryRemove(key, out _);
    }

    public const string LogName = "console.log";

    // Папка артефактов прогона: Relative — относительно рабочего дерева (для модели),
    // Full — полный путь хоста. Каталог создаёт бэкенд на ХОСТЕ в дереве, которое пишет агент:
    // ни один каталог от корня до неё не должен быть ссылкой (проверка до создания и после),
    // иначе хост создавал бы каталоги и писал лог за деревом. Ссылка по пути — InTree=false:
    // прогон идёт как обычно, но хост в эту папку не пишет и отчёты из неё не читает
    public static PhaseArtifacts CreateArtifacts(string workingDirectory, string subdir)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var relative = $"{TreeExcludes.AttachmentsDir}/{subdir}/{runId}";
        var full = Path.Combine(workingDirectory, TreeExcludes.AttachmentsDir, subdir, runId);
        var inTree = TreeFiles.NoLinksUnder(workingDirectory, full);
        if (inTree)
        {
            try
            {
                Directory.CreateDirectory(full);
                inTree = TreeFiles.NoLinksUnder(workingDirectory, full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                inTree = false;
            }
        }
        return new PhaseArtifacts(runId, relative, full, inTree);
    }

    // Полный лог прогона — НОВЫЙ файл console.log в папке артефактов (подложенная ссылка или
    // файл на его месте — отказ, а не запись сквозь). null — лог не пишется, прогон идёт
    public static StreamWriter? OpenLog(string workingDirectory, PhaseArtifacts artifacts) =>
        artifacts.InTree && TreeFiles.CreateNewInTree(workingDirectory, Path.Combine(artifacts.Full, LogName)) is { } stream
            ? new StreamWriter(stream, new System.Text.UTF8Encoding(false))
            : null;

    // Потолок создания папки артефактов и лога (CreateArtifactsAsync)
    public static readonly TimeSpan ArtifactsTimeout = TimeSpan.FromSeconds(5);

    // Папка артефактов и лог — синхронная работа с диском дерева агента, а зовут её под
    // блокировкой дерева: поэтому в пуле под потолком и токеном. Не уложились или отменено —
    // прогон идёт без лога (InTree=false, Log=null); опоздавший лог закроется, когда доедет.
    // Брать их ДО блокировки нельзя без мусора: отказ «дерево занято» оставлял бы пустые папки
    public static async Task<(PhaseArtifacts Artifacts, StreamWriter? Log)> CreateArtifactsAsync(
        string workingDirectory, string subdir, TimeSpan timeout, CancellationToken ct)
    {
        var work = Task.Run(() =>
        {
            var artifacts = CreateArtifacts(workingDirectory, subdir);
            return (artifacts, OpenLog(workingDirectory, artifacts));
        }, CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            _ = work.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Item2?.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return (new PhaseArtifacts("", "", "", InTree: false), null);
        }
    }

    // Серверный потолок включает ожидание очереди: иначе долгая очередь съела бы запас до
    // обрыва вызова CLI
    public static CancellationTokenSource Ceiling(int seconds, CancellationToken ct)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        return limit;
    }

    // Захват слота гейта. Свободно — сразу; иначе onQueued(лимит гейта) и ожидание. null —
    // оборвано в очереди (отмена или потолок): процесс не запускался
    public async Task<IDisposable?> AcquireAsync(ProcessSpec spec, Action<int> onQueued, CancellationToken limit)
    {
        var gate = Gate;
        if (gate.TryAcquire(spec) is { } slot) return slot;
        onQueued(gate.Limit);
        try
        {
            return await gate.AcquireAsync(spec, limit);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // Один процесс фазы: старт, вычитка обоих потоков (onLine зовётся из двух потоков сразу —
    // синхронизация на вызывающем), гашение дерева по отмене/потолку. TurnId берётся из spec:
    // без метки kill docker-клиента не трогает процесс в контейнере. Сбой Start (нет
    // dotnet/node, отказ среды) уходит наверх: слот и блокировку освобождают using вызывающего
    public async Task<PhaseOutcome> RunProcessAsync(IProcessLauncher launcher, ProcessSpec spec,
        Action<string> onLine, CancellationToken limit)
    {
        using var process = launcher.Start(spec);
        var readers = Task.WhenAll(
            PumpAsync(process.StandardOutput, onLine),
            PumpAsync(process.StandardError, onLine));

        var aborted = false;
        try
        {
            await process.WaitForExitAsync(limit);
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            // Гасим ДЕРЕВО: testhost, воркеры vitest, браузер, узлы MSBuild — потомки; в песочнице — по TurnId
            launcher.Kill(process, spec.TurnId);
            try { await process.WaitForExitAsync().WaitAsync(_exitGrace); }
            catch (TimeoutException) { /* процесс не умер за паузу — итог всё равно отдаём */ }
        }

        // Потоки закрываются с выходом процесса; внук, державший пайп, не должен задержать
        // ответ навсегда
        try { await readers.WaitAsync(_exitGrace); }
        catch (TimeoutException) { }

        int? exit = process.HasExited ? SafeExitCode(process) : null;
        return new PhaseOutcome(exit, aborted);
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) onLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Поток закрыт гашением процесса — вывод до этого места уже учтён
        }
    }
}

// Папка артефактов одного прогона (см. PhasePipeline.CreateArtifacts); InTree=false — по пути
// ссылка, хост туда не пишет и оттуда не читает
public sealed record PhaseArtifacts(string RunId, string Relative, string Full, bool InTree);

// Итог процесса фазы: код выхода (null — процесс не завершился) и признак гашения
public readonly record struct PhaseOutcome(int? ExitCode, bool Aborted);
