using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Свидетельство блока 2 (ADR-022 §4): сборка фильма, запущенная через ILauncherFactory.Local при включённой
// изоляции, действительно живёт в scope ccs-agents.slice (здесь — тестовый slice), а слот единого
// BuildConcurrencyGate занят всё время её работы. Живой запуск — только Linux с user-шиной, systemd-run и
// ffmpeg; иначе пропуск, а не падение (CI, контейнер). Слой раннера читает глобальные IsolationOptions —
// коллекция процесс-глобального состояния.
//
// Детерминизм (дефект 62b1631a, CI-ран 41004166921): проверка слота привязана к событию СТАРТА процесса,
// а не к временному окну. Продукт берёт слот до Start и отдаёт его в finally на любом исходе
// (FfmpegVideoDsp.cs:120-122), значит в момент Start слот занят по конструкции. Прежняя версия теста
// проверяла слот после опроса cgroup: на быстром раннере сборка завершалась до проверки, и тест падал
// с Available == 1 на живом порту — проверка зависела от скорости машины, а не от продукта.
// Способность обёртки проверяется той же пробой, которой её решает продукт (LocalProcessRunner.cs:398):
// проба не ОК — изоляция в этой среде не работает, тест пропускает assertions (намерение файла:
// «иначе пропуск, а не падение»), а не валит их об окружении.
[Collection(TestCollections.ProcessGlobalState)]
public sealed class VideoAssembleIsolationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-video-iso-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Сборка_фильма_идёт_в_scope_slice_под_занятым_слотом()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        var systemdRun = LocalProcessRunner.FindSystemdRun(Environment.GetEnvironmentVariable("PATH"));
        if (systemdRun is null) return;

        Directory.CreateDirectory(_dir);
        const string slice = "ccs-isotest.slice";
        var prev = IsolationOptions.Instance;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, Slice = slice, MemoryMax = "4G" };
        try
        {
            // Способность обёртки — пробой продукта, а не наличием бинаря и переменной: это ровно тот
            // вызов, который сделает Start (результат кэшируется, боевой Start его переиспользует).
            // Без успешной пробы обёртки не будет ни у одного запуска — проверять cgroup в такой среде
            // значит валить тест об окружении, а не о продукте.
            LocalProcessRunner.ResetProbeForTests();
            var probe = LocalProcessRunner.ProbeOnce(IsolationOptions.Instance, systemdRun);
            if (probe is not { Ok: true })
            {
                output.WriteLine("Изоляция в этой среде не работает: " + (probe?.FailReason ?? "проба не выполнена"));
                return;
            }

            var gate = new BuildConcurrencyGate(1);
            var started = new TaskCompletionSource();
            var launchers = new RecordingLaunchers(gate, started);
            var dsp = new FfmpegVideoDsp(new ConfigurationBuilder().Build(), NullLogger<FfmpegVideoDsp>.Instance, launchers, gate);
            if (!dsp.Available) return;

            // Исходник побольше, а сборка — в 1080p60: процесс живёт достаточно, чтобы заглянуть в его cgroup
            var clip = Path.Combine(_dir, "src.mp4");
            RunSync("ffmpeg", ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=size=1280x720:rate=30:duration=40", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", clip]);
            var plan = new FilmPlan(1920, 1080, 60, [new FilmPlanClip(clip, 0, 40, false)], [], null, TimeSpan.FromMinutes(5));

            using var cts = new CancellationTokenSource();
            var running = dsp.AssembleAsync(plan, Path.Combine(_dir, "film.mp4"), null, cts.Token);

            // Событие удержания слота: Start ланчера — внутри продуктового `using var slot`
            // (FfmpegVideoDsp.cs:120-122), слот занят по конструкции в этот момент. Замер — здесь,
            // а не «когда-нибудь после опроса cgroup»: на быстром раннере сборка опережала прежнюю
            // проверку, и тест флакал. Safety-окно — только против зависания старта, не механизм.
            (await Task.WhenAny(started.Task, Task.Delay(30_000))).Should().BeSameAs(started.Task,
                "сборка стартовала через ILauncherFactory.Local");
            launchers.Spec!.Heavy.Should().BeTrue();
            launchers.SlotAtStart.Should().Be(0, "слот единого BuildConcurrencyGate занят в момент старта процесса");

            // Пока systemd-run не переложил процесс в свой scope, /proc/<pid>/cgroup показывает cgroup РОДИТЕЛЯ
            // (у агента он тоже в scope) — ждём именно нашего slice, а не «любой .scope». Переложение —
            // часть старта: --scope исполняет команду в своём же процессе, уже в scope
            // (LocalProcessRunner.cs:97-98), а процесс живёт секунды (сборка 40 с видео в 1080p60) —
            // наблюдение раньше завершения.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string cgroup = "";
            while (!cgroup.Contains(slice) && DateTime.UtcNow < deadline)
            {
                try { cgroup = await File.ReadAllTextAsync($"/proc/{launchers.Pid}/cgroup"); }
                catch (IOException) { break; }
                await Task.Delay(20);
            }
            cgroup.Should().Contain(slice).And.Contain(".scope", "процесс сборки уходит в scope ccs-agents.slice");
            output.WriteLine("cgroup сборки: " + cgroup.Trim());

            cts.Cancel();
            await running.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
            gate.Available.Should().Be(1, "отмена отдаёт слот");
        }
        finally
        {
            IsolationOptions.Instance = prev;
            // Свой результат пробы (со своим slice) не должен остаться следующему тесту:
            // кэш пробы — процесс-глобальное состояние, тот же приём, что у LocalProcessRunnerIsolationTests.
            LocalProcessRunner.ResetProbeForTests();
        }
    }

    private static void RunSync(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err.Result);
    }

    private sealed class RecordingLaunchers(BuildConcurrencyGate gate, TaskCompletionSource started) : ILauncherFactory
    {
        // Параметры первичного конструктора видны только телу самого класса — вложенному Recording
        // нужны явные поля.
        private readonly BuildConcurrencyGate _gate = gate;
        private readonly TaskCompletionSource _started = started;

        public int? Pid { get; private set; }
        public ProcessSpec? Spec { get; private set; }
        // Свободные слоты гейта в момент Start — замер привязан к событию, а не к позднему чтению Available
        public int SlotAtStart { get; private set; }
        public IProcessLauncher Local => new Recording(this);
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;

        private sealed class Recording(RecordingLaunchers owner) : IProcessLauncher
        {
            private static IProcessLauncher Real => LocalProcessRunner.Instance;
            public bool IsSandboxed => Real.IsSandboxed;
            public bool TargetIsWindows => Real.TargetIsWindows;
            public IPathMapper Paths => Real.Paths;
            public string ClaudeCliCommand => Real.ClaudeCliCommand;
            public string HostTempDir => Real.HostTempDir;
            public string? McpApiUrlOverride => Real.McpApiUrlOverride;
            public Process Start(ProcessSpec spec)
            {
                var process = Real.Start(spec);
                owner.Spec = spec;
                owner.Pid = process.Id;
                owner.SlotAtStart = owner._gate.Available;
                owner._started.SetResult();
                return process;
            }
            public void Kill(Process process, string? turnId = null) => Real.Kill(process, turnId);
            public int EstimateCommandLineLength(ProcessSpec spec) => Real.EstimateCommandLineLength(spec);
        }
    }
}
