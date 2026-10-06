using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов обработки видео без ИИ (ADR-022 §4) на настоящем ffmpeg: фильм из трёх клипов разного размера (один без
// звука) с наплывом, затемнением и музыкой. Нет ffmpeg — явный пропуск с причиной; на CI он обязан быть.
// Тяжёлость запуска (метка Heavy, слот ДО старта процесса) и отмена проверяются на подставном раннере.
public sealed class FfmpegVideoDspTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-video-dsp-" + Guid.NewGuid().ToString("N"));

    public FfmpegVideoDspTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FfmpegVideoDsp Build(ILauncherFactory? launchers = null, BuildConcurrencyGate? gate = null) =>
        new(new ConfigurationBuilder().Build(), NullLogger<FfmpegVideoDsp>.Instance,
            launchers ?? new LocalLaunchers(), gate ?? new BuildConcurrencyGate(2));

    private static FfmpegVideoDsp Dsp()
    {
        var dsp = Build();
        if (!dsp.Available && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("На CI ffmpeg и ffprobe обязаны быть установлены");
        Skip.IfNot(dsp.Available, "ffmpeg/ffprobe не найдены на этой машине — проверка на настоящем ffmpeg пропущена");
        return dsp;
    }

    // Клип из lavfi: testsrc нужного размера и длины; со звуком — синус, без — только видеодорожка
    private string Clip(string name, int width, int height, double seconds, bool audio, int fps = 25)
    {
        var path = Path.Combine(_dir, name);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate={fps}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"];
        if (audio)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"]);
        args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);
        Run("ffmpeg", args);
        return path;
    }

    private string Music(string name, double seconds)
    {
        var path = Path.Combine(_dir, name);
        Run("ffmpeg", ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i",
            $"sine=frequency=220:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", path]);
        return path;
    }

    private static void Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "исходник для теста должен собраться: " + err.Result);
    }

    private FilmPlan ThreeClipsPlan(string music, bool withMusic = true) => new(640, 360, 24,
        [
            new FilmPlanClip(Clip("a.mp4", 320, 240, 3, audio: true), 0, 3, HasAudio: true),
            new FilmPlanClip(Clip("b.mp4", 640, 360, 3, audio: false), 0, 3, HasAudio: false),
            new FilmPlanClip(Clip("c.mp4", 200, 200, 2, audio: true), 0, 2, HasAudio: true),
        ],
        [new FilmPlanCut("dissolve", 0.5), new FilmPlanCut("fade", 1)],
        withMusic ? new FilmPlanMusic(music, 60, 2) : null,
        TimeSpan.FromMinutes(2));

    [SkippableFact]
    public async Task Фильм_из_трёх_клипов_разного_размера_один_без_звука_собирается_с_наплывом_затемнением_и_музыкой()
    {
        var dsp = Dsp();
        var plan = ThreeClipsPlan(Music("m.mp3", 20));
        var outPath = Path.Combine(_dir, "film.mp4");
        var reports = new List<VideoAssembleProgress>();

        var result = await dsp.AssembleAsync(plan, outPath, new Progress<VideoAssembleProgress>(reports.Add), default);

        result.Error.Should().BeNull();
        result.Ok.Should().BeTrue();
        var info = await dsp.ProbeAsync(outPath, default);
        info.Should().NotBeNull();
        info!.Width.Should().Be(640);
        info.Height.Should().Be(360);
        info.Fps.Should().BeApproximately(24, 0.5);
        info.HasAudio.Should().BeTrue();
        // 3 + 3 − 0,5 (наплыв) + 2 (затемнение складывается без потери длины) = 7,5
        info.Seconds.Should().BeApproximately(7.5, 0.4);
        reports.Should().Contain(r => r.Stage == VideoAssembleProgress.Running);
    }

    [SkippableFact]
    public async Task Без_музыки_и_со_встык_склейкой_длины_складываются()
    {
        var dsp = Dsp();
        var plan = ThreeClipsPlan("", withMusic: false) with { Cuts = [new FilmPlanCut("butt", 0), new FilmPlanCut("butt", 0)] };
        var outPath = Path.Combine(_dir, "butt.mp4");

        var result = await dsp.AssembleAsync(plan, outPath, null, default);

        result.Error.Should().BeNull();
        (await dsp.ProbeAsync(outPath, default))!.Seconds.Should().BeApproximately(8, 0.4);
    }

    [SkippableFact]
    public async Task Обрезка_клипа_берёт_только_кусок()
    {
        var dsp = Dsp();
        var plan = new FilmPlan(320, 240, 25, [new FilmPlanClip(Clip("a.mp4", 320, 240, 4, audio: true), 1, 3, true)],
            [], null, TimeSpan.FromMinutes(1));
        var outPath = Path.Combine(_dir, "trim.mp4");

        (await dsp.AssembleAsync(plan, outPath, null, default)).Error.Should().BeNull();

        (await dsp.ProbeAsync(outPath, default))!.Seconds.Should().BeApproximately(2, 0.3);
    }

    [SkippableFact]
    public async Task Полоса_кадров_и_последний_кадр_снимаются_файлами()
    {
        var dsp = Dsp();
        var clip = Clip("a.mp4", 320, 240, 3, audio: false);
        var strip = Path.Combine(_dir, "strip.jpg");
        var last = Path.Combine(_dir, "last.png");

        (await dsp.FilmstripAsync(clip, strip, 6, 48, default)).Error.Should().BeNull();
        (await dsp.LastFrameAsync(clip, last, default)).Error.Should().BeNull();

        new FileInfo(strip).Length.Should().BeGreaterThan(0);
        new FileInfo(last).Length.Should().BeGreaterThan(0);
        var probe = await dsp.ProbeAsync(clip, default);
        probe!.HasAudio.Should().BeFalse();
        probe.Width.Should().Be(320);
    }

    [SkippableFact]
    public async Task Не_видео_и_относительный_путь_не_разбираются()
    {
        var dsp = Dsp();
        var junk = Path.Combine(_dir, "junk.mp4");
        await File.WriteAllTextAsync(junk, "это не видео");

        (await dsp.ProbeAsync(junk, default)).Should().BeNull();
        (await dsp.ProbeAsync("a.mp4", default)).Should().BeNull("пути только полные");
    }

    [SkippableFact]
    public async Task Повреждённый_клип_даёт_отказ_с_причиной_и_не_оставляет_файла()
    {
        var dsp = Dsp();
        var junk = Path.Combine(_dir, "junk.mp4");
        await File.WriteAllTextAsync(junk, "это не видео");
        var plan = new FilmPlan(320, 240, 25, [new FilmPlanClip(junk, 0, 2, false)], [], null, TimeSpan.FromMinutes(1));
        var outPath = Path.Combine(_dir, "bad.mp4");

        var result = await dsp.AssembleAsync(plan, outPath, null, default);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("ffmpeg отказал");
        File.Exists(outPath).Should().BeFalse();
    }

    // ── Тяжёлый запуск: слот до старта, отмена ────────────────────────────────────

    [Fact]
    public void Спека_сборки_тяжёлая_и_гейт_берёт_под_неё_слот()
    {
        var spec = FfmpegVideoDsp.BuildSpec("ffmpeg", ["-version"], _dir);

        spec.Heavy.Should().BeTrue("без метки ffmpeg прошёл бы мимо общего потолка тяжёлых запусков");
        BuildConcurrencyGate.IsHeavy(spec).Should().BeTrue();
        var gate = new BuildConcurrencyGate(1);
        using var slot = gate.TryAcquire(spec);
        slot.Should().NotBeNull();
        gate.TryAcquire(spec).Should().BeNull("слот единственный и занят");
    }

    [SkippableFact]
    public async Task Процесс_не_стартует_пока_слот_занят_и_стартует_тяжёлым_после_освобождения()
    {
        var dsp0 = Dsp();
        var gate = new BuildConcurrencyGate(1);
        var launchers = new CountingLaunchers();
        var dsp = Build(launchers, gate);
        var plan = ThreeClipsPlan("", withMusic: false) with { Cuts = [new FilmPlanCut("butt", 0), new FilmPlanCut("butt", 0)] };
        var outPath = Path.Combine(_dir, "slot.mp4");
        var reports = new List<VideoAssembleProgress>();
        var held = gate.TryAcquire(FfmpegVideoDsp.BuildSpec("x", [], null))!;

        var running = dsp.AssembleAsync(plan, outPath, new Progress<VideoAssembleProgress>(reports.Add), default);
        await Task.Delay(300);

        running.IsCompleted.Should().BeFalse("слот занят — сборка ждёт");
        launchers.Specs.Should().BeEmpty("процесс не стартует до получения слота");
        held.Dispose();
        (await running.WaitAsync(TimeSpan.FromMinutes(1))).Error.Should().BeNull();

        launchers.Specs.Should().ContainSingle().Which.Heavy.Should().BeTrue();
        gate.Available.Should().Be(1, "слот отдан после сборки");
        reports.Select(r => r.Stage).Should().Contain(VideoAssembleProgress.Waiting);
        dsp0.Available.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Отмена_убивает_процесс_удаляет_временный_файл_и_отдаёт_слот()
    {
        Skip.If(OperatingSystem.IsWindows(), "подставной долгий процесс — sleep");
        var dsp0 = Dsp();
        var gate = new BuildConcurrencyGate(1);
        var launchers = new SleepingLaunchers();
        var dsp = Build(launchers, gate);
        var plan = new FilmPlan(320, 240, 25, [new FilmPlanClip(Clip("a.mp4", 320, 240, 2, false), 0, 2, false)], [], null,
            TimeSpan.FromMinutes(5));
        var outPath = Path.Combine(_dir, "cancel.mp4");
        await File.WriteAllTextAsync(outPath, "недописанный результат");
        using var cts = new CancellationTokenSource();

        var running = dsp.AssembleAsync(plan, outPath, null, cts.Token);
        await Until(() => launchers.Pid is not null);
        cts.Cancel();

        await running.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(outPath).Should().BeFalse("временный файл при отмене удаляется");
        await Until(() => !Alive(launchers.Pid!.Value));
        gate.Available.Should().Be(1);
        dsp0.Available.Should().BeTrue();
    }

    [Fact]
    public async Task Неверный_план_отказывает_без_запуска()
    {
        var dsp = Build();
        if (!dsp.Available) return;
        var plan = new FilmPlan(641, 360, 24, [new FilmPlanClip("/tmp/a.mp4", 0, 3, true)], [], null, TimeSpan.FromMinutes(1));

        (await dsp.AssembleAsync(plan, Path.Combine(_dir, "x.mp4"), null, default)).Error.Should().Contain("чётные");
        (await dsp.AssembleAsync(plan with { Width = 640, Cuts = [new FilmPlanCut("butt", 0)] },
            Path.Combine(_dir, "x.mp4"), null, default)).Error.Should().Contain("на одну меньше");
    }

    // ── Фильтр-граф ───────────────────────────────────────────────────────────────

    [Fact]
    public void Граф_наплыв_затемнение_тишина_и_музыка()
    {
        var plan = new FilmPlan(640, 360, 24,
            [new FilmPlanClip("/a.mp4", 0, 3, true), new FilmPlanClip("/b.mp4", 1, 4, false), new FilmPlanClip("/c.mp4", 0, 2, true)],
            [new FilmPlanCut("dissolve", 0.5), new FilmPlanCut("fade", 1)], new FilmPlanMusic("/m.mp3", 60, 2),
            TimeSpan.FromMinutes(1));

        var graph = FfmpegVideoDsp.BuildGraph(plan, out var total, out var video, out var audio);

        graph.Should().Contain("xfade=transition=fade:duration=0.5:offset=2.5");
        graph.Should().Contain("acrossfade=d=0.5");
        graph.Should().Contain("anullsrc=r=48000:cl=stereo:d=3", "клип без звука получает тишину своей длины");
        graph.Should().Contain("fade=t=out").And.Contain("fade=t=in", "затемнение через чёрное");
        graph.Should().Contain("scale=640:360:force_original_aspect_ratio=decrease,pad=640:360");
        graph.Should().Contain("amix=inputs=2:duration=first").And.Contain("volume=0.6").And.Contain("afade=t=out");
        total.Should().BeApproximately(7.5, 1e-9);
        video.Should().Be("vj2");
        audio.Should().Be("amix");

        var args = FfmpegVideoDsp.BuildArgs(plan, "/out.mp4", out _);
        args.Should().ContainInOrder("-c:v", "libx264", "-preset", "veryfast");
        args.Should().Contain("+faststart").And.Contain("-progress");
        args.Count(a => a == "-i").Should().Be(4, "три клипа и музыка");
    }

    [Fact]
    public void Наплыв_длиннее_половины_клипа_обрезается()
    {
        var plan = new FilmPlan(640, 360, 24, [new FilmPlanClip("/a.mp4", 0, 1, true), new FilmPlanClip("/b.mp4", 0, 1, true)],
            [new FilmPlanCut("dissolve", 5)], null, TimeSpan.FromMinutes(1));

        FfmpegVideoDsp.BuildGraph(plan, out var total, out _, out _);

        total.Should().BeApproximately(1.5, 1e-9, "наплыв не длиннее половины меньшего соседа: 1 + 1 − 0,5");
    }

    // Процесса с таким pid больше нет (или он уже завершён): «дерево процессов убито»
    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task Until(Func<bool> probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!probe())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("не дождались");
            await Task.Delay(20);
        }
    }

    // ── Подставные раннеры ────────────────────────────────────────────────────────

    private class LocalLaunchers : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    // Настоящий local-раннер, но каждый запуск записывается: слот обязан быть взят до Start
    private sealed class CountingLaunchers : LocalLaunchers, ILauncherFactory
    {
        public List<ProcessSpec> Specs { get; } = [];
        IProcessLauncher ILauncherFactory.Local => new Recording(this);

        private sealed class Recording(CountingLaunchers owner) : IProcessLauncher
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
                lock (owner.Specs) owner.Specs.Add(spec);
                return Real.Start(spec);
            }
            public void Kill(Process process, string? turnId = null) => Real.Kill(process, turnId);
            public int EstimateCommandLineLength(ProcessSpec spec) => Real.EstimateCommandLineLength(spec);
        }
    }

    // Вместо ffmpeg стартует долгий sleep: отмена обязана его убить
    private sealed class SleepingLaunchers : LocalLaunchers, ILauncherFactory
    {
        public int? Pid { get; private set; }
        IProcessLauncher ILauncherFactory.Local => new Sleeping(this);

        private sealed class Sleeping(SleepingLaunchers owner) : IProcessLauncher
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
                var process = Real.Start(spec with { FileName = "sleep", Args = ["120"] });
                owner.Pid = process.Id;
                return process;
            }
            public void Kill(Process process, string? turnId = null) => Real.Kill(process, turnId);
            public int EstimateCommandLineLength(ProcessSpec spec) => Real.EstimateCommandLineLength(spec);
        }
    }
}
