using System.Diagnostics;
using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.VideoEditor;

// Приёмка блока 2 (ADR-022 §4): film.mp4 собирается настоящим ffmpeg из трёх клипов разного размера (один без звука)
// с наплывом, затемнением и музыкой; пересборка даёт film.v2.mp4 и не затирает прежний. Нет ffmpeg на машине —
// пропуск с причиной; на CI он обязан быть
public sealed class FilmAssemblyAcceptanceTests : IDisposable
{
    private const string Owner = "u1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfilm-acc-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public FilmAssemblyAcceptanceTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "video", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "music"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Launchers : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    private static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err.Result);
    }

    private static string S(double v) => v.ToString(CultureInfo.InvariantCulture);

    private void Clip(string name, int w, int h, double seconds, bool audio)
    {
        var path = Path.Combine(_root, "video", "a", name);
        var args = new List<string> { "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=25:duration={S(seconds)}" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={S(seconds)}"]);
        args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);
        Ffmpeg([.. args]);
    }

    [Fact]
    public async Task Фильм_из_трёх_клипов_собирается_а_пересборка_даёт_film_v2()
    {
        var dsp = new FfmpegVideoDsp(new ConfigurationBuilder().Build(), NullLogger<FfmpegVideoDsp>.Instance,
            new Launchers(), new BuildConcurrencyGate(2));
        if (!dsp.Available && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("На CI ffmpeg и ffprobe обязаны быть установлены");
        if (!dsp.Available) return;

        Clip("1.mp4", 320, 240, 3, audio: true);
        Clip("2.mp4", 640, 360, 3, audio: false);
        Clip("3.mp4", 200, 200, 2, audio: true);
        Ffmpeg("-f", "lavfi", "-i", "sine=frequency=220:duration=20", Path.Combine(_root, "music", "m.mp3"));

        var films = new FilmStore();
        var doc = new FilmDocument(1, "16:9",
            [new FilmItem("video/a/1.mp4", [0, 3], null), new FilmItem("video/a/2.mp4", [0, 3], null), new FilmItem("video/a/3.mp4", [0, 2], null)],
            [new FilmCut(FilmCutTypes.Dissolve, 0.5), new FilmCut(FilmCutTypes.Fade, 1)],
            new FilmMusic("music/m.mp3", 60, 2), []);
        films.WriteFile(Path.Combine(_root, "video", "a", "a.film"), doc, null, create: true).Status.Should().Be(FilmStore.WriteStatus.Ok);

        var threads = new VideoJobThreads(new VideoThreadStore(Path.Combine(_dir, "threads")), NullLogger<VideoJobThreads>.Instance);
        var registry = new FilmBuildRegistry();
        var service = new FilmService(films, new FilmSideStore(Path.Combine(_dir, "side")), threads, registry,
            NullLogger<FilmService>.Instance, dsp);
        var assembler = new FilmAssembler(service, registry, new ConfigurationBuilder().Build(), NullLogger<FilmAssembler>.Instance, dsp);
        var scope = VideoEditScope.Of(new Project { Id = "p1", OwnerId = Owner, RootPath = _root });

        assembler.Start(Owner, scope, "video/a/a.film", VideoInitiators.Human).IsOk.Should().BeTrue();
        await assembler.WhenIdleAsync();

        var status = registry.Get(Owner, "p1", "video/a/a.film")!;
        status.State.Should().Be(FilmBuildStates.Done, status.Error);
        status.File.Should().Be("video/a/film.mp4");
        var info = await dsp.ProbeAsync(Path.Combine(_root, "video", "a", "film.mp4"), default);
        info.Should().NotBeNull();
        (info!.Width, info.Height).Should().Be((1280, 720), "три клипа разного размера приведены к 16:9");
        info.HasAudio.Should().BeTrue();
        info.Seconds.Should().BeApproximately(7.5, 0.4, "3 + 3 − 0,5 наплыва + 2");
        service.List(scope).Value!.Single().Stale.Should().BeFalse();

        assembler.Start(Owner, scope, "video/a/a.film", VideoInitiators.Human).IsOk.Should().BeTrue();
        await assembler.WhenIdleAsync();

        File.Exists(Path.Combine(_root, "video", "a", "film.v2.mp4")).Should().BeTrue("пересборка не затирает film.mp4");
        File.Exists(Path.Combine(_root, "video", "a", "film.mp4")).Should().BeTrue();
        films.ReadFile(Path.Combine(_root, "video", "a", "a.film")).Document!.Builds.Select(b => b.File)
            .Should().Equal("video/a/film.mp4", "video/a/film.v2.mp4");
        Directory.GetFiles(Path.Combine(_root, "video", "a"), ".film-build-*").Should().BeEmpty();
    }
}
