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
[Collection(TestCollections.ProcessGlobalState)]
public sealed class VideoAssembleIsolationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-video-iso-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Сборка_фильма_идёт_в_scope_slice_под_занятым_слотом()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        if (LocalProcessRunner.FindSystemdRun(Environment.GetEnvironmentVariable("PATH")) is null) return;

        Directory.CreateDirectory(_dir);
        var gate = new BuildConcurrencyGate(1);
        var launchers = new RecordingLaunchers();
        var dsp = new FfmpegVideoDsp(new ConfigurationBuilder().Build(), NullLogger<FfmpegVideoDsp>.Instance, launchers, gate);
        if (!dsp.Available) return;

        // Исходник побольше, а сборка — в 1080p60: процесс живёт достаточно, чтобы заглянуть в его cgroup
        var clip = Path.Combine(_dir, "src.mp4");
        RunSync("ffmpeg", ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=1280x720:rate=30:duration=40", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", clip]);
        var plan = new FilmPlan(1920, 1080, 60, [new FilmPlanClip(clip, 0, 40, false)], [], null, TimeSpan.FromMinutes(5));

        const string slice = "ccs-isotest.slice";
        var prev = IsolationOptions.Instance;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, Slice = slice, MemoryMax = "4G" };
        try
        {
            using var cts = new CancellationTokenSource();
            var running = dsp.AssembleAsync(plan, Path.Combine(_dir, "film.mp4"), null, cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (launchers.Pid is null && DateTime.UtcNow < deadline) await Task.Delay(20);
            launchers.Pid.Should().NotBeNull("сборка стартовала через ILauncherFactory.Local");
            launchers.Spec!.Heavy.Should().BeTrue();

            // Пока systemd-run не переложил процесс в свой scope, /proc/<pid>/cgroup показывает cgroup РОДИТЕЛЯ
            // (у агента он тоже в scope) — ждём именно нашего slice, а не «любой .scope»
            string cgroup = "";
            while (!cgroup.Contains(slice) && DateTime.UtcNow < deadline)
            {
                try { cgroup = await File.ReadAllTextAsync($"/proc/{launchers.Pid}/cgroup"); }
                catch (IOException) { break; }
                await Task.Delay(20);
            }
            gate.Available.Should().Be(0, "слот единого BuildConcurrencyGate занят, пока ffmpeg жив");
            cgroup.Should().Contain(slice).And.Contain(".scope", "процесс сборки уходит в scope ccs-agents.slice");
            output.WriteLine("cgroup сборки: " + cgroup.Trim());

            cts.Cancel();
            await running.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
            gate.Available.Should().Be(1, "отмена отдаёт слот");
        }
        finally
        {
            IsolationOptions.Instance = prev;
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

    private sealed class RecordingLaunchers : ILauncherFactory
    {
        public int? Pid { get; set; }
        public ProcessSpec? Spec { get; set; }
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
                return process;
            }
            public void Kill(Process process, string? turnId = null) => Real.Kill(process, turnId);
            public int EstimateCommandLineLength(ProcessSpec spec) => Real.EstimateCommandLineLength(spec);
        }
    }
}
