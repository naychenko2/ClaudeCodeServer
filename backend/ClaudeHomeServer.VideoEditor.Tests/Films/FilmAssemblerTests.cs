using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Сборка фильма (ADR-022 §4) поверх подставного шва IVideoDsp: заявка, потолки модуля (1 на владельца, 2 на инстанс),
// итог film.mp4 → film.v2.mp4 CreateNew, запись builds[] с хешем входов, отмена, нулевая трата. Сама сборка ffmpeg под
// тяжёлым слотом и в scope проверена на настоящем ffmpeg в Images.Tests и ClaudeHomeServer.Tests
public sealed class FilmAssemblerTests : IDisposable
{
    private readonly FakeDsp _dsp = new();
    private readonly FilmWorld _w;
    private readonly Spend _spend = new();

    public FilmAssemblerTests() => _w = new FilmWorld(_dsp);

    public void Dispose() => _w.Dispose();

    private sealed class Spend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    private FilmAssembler Assembler(IConfiguration? config = null, IVideoDsp? dsp = null) =>
        new(_w.Service, _w.Registry, config ?? new ConfigurationBuilder().Build(), NullLogger<FilmAssembler>.Instance,
            dsp ?? _dsp, _spend);

    private void ThreeClipFilm(string path = "video/a/a.film")
    {
        foreach (var f in new[] { "video/a/1.mp4", "video/a/2.mp4", "video/a/3.mp4" }) _w.WriteFile(f);
        var doc = new FilmDocument(1, "16:9",
            [Item("video/a/1.mp4", 0, 5), Item("video/a/2.mp4", 1, 99), Item("video/a/3.mp4", 0, 2)],
            [new FilmCut(FilmCutTypes.Dissolve, 0.5), new FilmCut(FilmCutTypes.Fade, 1)],
            new FilmMusic("music/m.mp3", 60, 3), []);
        _w.WriteFile("music/m.mp3");
        _w.WriteFilm(path, doc);
    }

    // Правка → сразу сборка: строка «поправил» обязана встать в ленте РАНЬШЕ строки «собрал», а не через 5 с после неё
    [Fact]
    public async Task Строка_правки_идёт_в_ленте_раньше_строки_сборки()
    {
        ThreeClipFilm();
        var feed = new FilmPatchFeed(_w.JobThreads, NullLogger<FilmPatchFeed>.Instance, null, TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        var svc = new FilmService(_w.Films, _w.Side, _w.JobThreads, _w.Registry, NullLogger<FilmService>.Instance, _dsp, patchFeed: feed);
        var revision = _w.Films.ReadFile(_w.Full("video/a/a.film")).Revision;
        var patched = await svc.PatchAsync(Owner, _w.Scope, "video/a/a.film",
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 2])]), VideoInitiators.Human, default, FilmWorld.Session);
        patched.IsOk.Should().BeTrue(patched.Error);
        var assembler = new FilmAssembler(svc, _w.Registry, new ConfigurationBuilder().Build(), NullLogger<FilmAssembler>.Instance, _dsp, _spend);

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human, FilmWorld.Session).IsOk.Should().BeTrue();
        await assembler.WhenIdleAsync();
        await feed.FlushAllAsync();

        var lines = _w.Feed.Records.Select(r => r.Record.Fallback).ToList();
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("подрезали");
        lines[1].Should().Contain("собрали");
    }

    [Fact]
    public async Task Сборка_пишет_film_mp4_строку_builds_с_хешем_и_нулевую_трату()
    {
        ThreeClipFilm();
        var assembler = Assembler();

        var started = assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();

        started.Value!.State.Should().Be(FilmBuildStates.Waiting);
        File.ReadAllBytes(_w.Full("video/a/film.mp4")).Should().Equal(FakeDsp.Output);
        var doc = _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!;
        doc.Builds.Should().ContainSingle().Which.Should().Match<FilmBuild>(b =>
            b.File == "video/a/film.mp4" && b.SourceHash == FilmStaleness.SourceHash(_w.ProjectRoot, doc));
        var status = _w.Registry.Get(Owner, ProjectId, "video/a/a.film")!;
        status.State.Should().Be(FilmBuildStates.Done);
        status.Progress.Should().Be(1);
        status.File.Should().Be("video/a/film.mp4");
        _w.Service.List(_w.Scope).Value!.Single().Stale.Should().BeFalse("сборка свежая");

        var spent = _spend.Records.Should().ContainSingle().Subject;
        spent.Label.Should().Be("сборка фильма");
        spent.CostUsd.Should().Be(0);
        spent.OwnerId.Should().Be(Owner);
        spent.ProjectId.Should().Be(ProjectId);
        spent.Initiator.Should().Be(SpendInitiators.Human);
        Directory.GetFiles(_w.Full("video/a"), ".film-build-*").Should().BeEmpty("временного файла не осталось");
    }

    [Fact]
    public async Task Пересборка_даёт_film_v2_mp4_и_не_затирает_прежний_а_правка_делает_фильм_устаревшим()
    {
        ThreeClipFilm();
        var assembler = Assembler();
        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();
        File.WriteAllBytes(_w.Full("video/a/film.mp4"), [42]);

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Agent);
        await assembler.WhenIdleAsync();

        File.ReadAllBytes(_w.Full("video/a/film.mp4")).Should().Equal(42);
        File.Exists(_w.Full("video/a/film.v2.mp4")).Should().BeTrue();
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Builds.Select(b => b.File)
            .Should().Equal("video/a/film.mp4", "video/a/film.v2.mp4");
        _spend.Records.Last().Initiator.Should().Be(SpendInitiators.Agent);

        var state = _w.Service.State(Owner, _w.Scope, "video/a/a.film").Value!;
        await _w.Service.PatchAsync(Owner, _w.Scope, "video/a/a.film",
            new FilmPatch(state.Revision, [new FilmPatchOp(FilmPatchOps.Trim, Index: 0, Trim: [0, 3])]), VideoInitiators.Human, default);
        _w.Service.List(_w.Scope).Value!.Single().Stale.Should().BeTrue("вход изменился после сборки");
    }

    [Fact]
    public async Task План_собирается_из_документа_размер_по_соотношению_обрезка_по_длине_клипа()
    {
        ThreeClipFilm();
        _dsp.Info = new VideoDspInfo(5, 640, 360, 25, HasAudio: true);
        _dsp.AudioByFile["2.mp4"] = false;
        var assembler = Assembler(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["VideoEditor:AssembleTimeoutMinutes"] = "7" }).Build());

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();

        var plan = _dsp.Plans.Should().ContainSingle().Subject;
        (plan.Width, plan.Height).Should().Be((1280, 720));
        plan.Timeout.Should().Be(TimeSpan.FromMinutes(7));
        plan.Clips.Select(c => (Path.GetFileName(c.Path), c.StartSeconds, c.EndSeconds, c.HasAudio)).Should().Equal(
            ("1.mp4", 0.0, 5.0, true), ("2.mp4", 1.0, 5.0, false), ("3.mp4", 0.0, 2.0, true));
        plan.Cuts.Select(c => (c.Type, c.Seconds)).Should().Equal(("dissolve", 0.5), ("fade", 1.0));
        plan.Music.Should().Match<FilmPlanMusic>(m => m.VolumePercent == 60 && m.FadeOutSeconds == 3 && m.Path.EndsWith("m.mp3"));
    }

    [Fact]
    public async Task Таймаут_по_умолчанию_двадцать_минут()
    {
        ThreeClipFilm();
        var assembler = Assembler();

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();

        _dsp.Plans.Single().Timeout.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Нет_ffmpeg_или_шва_значит_dsp_unavailable_без_заявки()
    {
        ThreeClipFilm();

        var unavailable = new FakeDsp { IsAvailable = false };
        var off = Assembler(dsp: unavailable);
        var withoutDsp = new FilmAssembler(_w.Service, _w.Registry, new ConfigurationBuilder().Build(),
            NullLogger<FilmAssembler>.Instance, null, _spend);

        withoutDsp.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human).ErrorCode.Should().Be(VideoEditorErrors.DspUnavailable);
        off.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human).ErrorCode.Should().Be(VideoEditorErrors.DspUnavailable);
        _w.Registry.ActiveCount().Should().Be(0);
    }

    [Fact]
    public void Личная_область_и_пустой_фильм_и_нет_клипа_отказывают_сразу()
    {
        ThreeClipFilm();
        _w.WriteFilm("video/empty/empty.film", Doc());
        File.Delete(_w.Full("video/a/2.mp4"));
        var assembler = Assembler();

        assembler.Start(Owner, new VideoEditScope(VideoEditScope.Personal, null), "video/a/a.film", VideoInitiators.Human)
            .ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        assembler.Start(Owner, _w.Scope, "video/empty/empty.film", VideoInitiators.Human)
            .ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human)
            .ErrorCode.Should().Be(VideoEditorErrors.FileNotFound);
        assembler.Start(Owner, _w.Scope, "video/a/none.film", VideoInitiators.Human)
            .ErrorCode.Should().Be(VideoEditorErrors.FileNotFound);
    }

    [Fact]
    public void Клип_больше_300_МБ_не_собирается()
    {
        ThreeClipFilm();
        using (var stream = new FileStream(_w.Full("video/a/1.mp4"), FileMode.Open)) stream.SetLength(SafeMediaDownloader.VideoMaxBytes + 1);

        var result = Assembler().Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);

        result.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        result.Error.Should().Contain("300 МБ");
    }

    [Fact]
    public async Task Потолки_модуля_одна_сборка_на_владельца_две_на_инстанс_и_один_фильм_за_раз()
    {
        ThreeClipFilm();
        ThreeClipFilm("video/a/b.film");
        _dsp.Hold = new TaskCompletionSource();
        var assembler = Assembler();

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human).IsOk.Should().BeTrue();
        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human).ErrorCode
            .Should().Be(VideoEditorErrors.TooManyJobs, "тот же фильм уже собирается");
        var sameOwner = assembler.Start(Owner, _w.Scope, "video/a/b.film", VideoInitiators.Human);
        sameOwner.ErrorCode.Should().Be(VideoEditorErrors.TooManyJobs);
        sameOwner.Error.Should().Contain("уже идёт сборка");
        assembler.Start("u2", _w.Scope, "video/a/a.film", VideoInitiators.Human).IsOk.Should().BeTrue();
        var instance = assembler.Start("u3", _w.Scope, "video/a/a.film", VideoInitiators.Human);
        instance.ErrorCode.Should().Be(VideoEditorErrors.TooManyJobs);
        instance.Error.Should().Contain("слишком много");

        _dsp.Hold.SetResult();
        await assembler.WhenIdleAsync();
        _w.Registry.ActiveCount().Should().Be(0, "после сборок заявки свободны");
        assembler.Start(Owner, _w.Scope, "video/a/b.film", VideoInitiators.Human).IsOk.Should().BeTrue();
        await assembler.WhenIdleAsync();
    }

    [Fact]
    public async Task Отмена_останавливает_сборку_и_не_пишет_ни_файла_ни_строки_builds()
    {
        ThreeClipFilm();
        _dsp.Hold = new TaskCompletionSource();
        var assembler = Assembler();
        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await Until(() => _dsp.Started);

        assembler.Cancel(Owner, _w.Scope, "video/a/a.film").Should().BeTrue();
        await assembler.WhenIdleAsync();

        _w.Registry.Get(Owner, ProjectId, "video/a/a.film")!.State.Should().Be(FilmBuildStates.Cancelled);
        File.Exists(_w.Full("video/a/film.mp4")).Should().BeFalse();
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Builds.Should().BeEmpty();
        assembler.Cancel(Owner, _w.Scope, "video/a/a.film").Should().BeFalse("сборка уже не идёт");
    }

    [Fact]
    public async Task Отказ_шва_даёт_failed_с_причиной_и_без_файла()
    {
        ThreeClipFilm();
        _dsp.Failure = "Не удалось собрать фильм: ffmpeg отказал";
        var assembler = Assembler();

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();

        var status = _w.Registry.Get(Owner, ProjectId, "video/a/a.film")!;
        status.State.Should().Be(FilmBuildStates.Failed);
        status.Error.Should().Contain("ffmpeg отказал");
        File.Exists(_w.Full("video/a/film.mp4")).Should().BeFalse();
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Builds.Should().BeEmpty();
    }

    [Fact]
    public async Task Клип_не_разобран_как_видео_даёт_failed_до_запуска_ffmpeg()
    {
        ThreeClipFilm();
        _dsp.Info = null;
        var assembler = Assembler();

        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);
        await assembler.WhenIdleAsync();

        _w.Registry.Get(Owner, ProjectId, "video/a/a.film")!.Error.Should().Contain("не разобран");
        _dsp.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task Прогресс_и_ожидание_слота_доходят_до_состояния_и_событий()
    {
        ThreeClipFilm();
        _dsp.Reports = [new VideoAssembleProgress(VideoAssembleProgress.Waiting, 0), new VideoAssembleProgress(VideoAssembleProgress.Running, 0.5)];
        _dsp.Hold = new TaskCompletionSource();
        var assembler = Assembler();
        assembler.Start(Owner, _w.Scope, "video/a/a.film", VideoInitiators.Human);

        await Until(() => _w.Registry.Get(Owner, ProjectId, "video/a/a.film") is { State: FilmBuildStates.Running, Progress: >= 0.5 });
        _dsp.Hold.SetResult();
        await assembler.WhenIdleAsync();

        _w.Broadcaster.Sent.OfType<VideoFilmChangedMessage>().Should().NotBeEmpty();
        _w.Broadcaster.Sent.OfType<VideoFilmChangedMessage>().Select(m => m.State.Build?.State).Should().Contain(FilmBuildStates.Done);
    }

    private static async Task Until(Func<bool> probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!probe())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("не дождались");
            await Task.Delay(10);
        }
    }

    private sealed class FakeDsp : IVideoDsp
    {
        public static readonly byte[] Output = [0xF1, 0x1A, 0x00, 0x01];

        public bool IsAvailable { get; init; } = true;
        public VideoDspInfo? Info { get; set; } = new(10, 640, 360, 24, true);
        public Dictionary<string, bool> AudioByFile { get; } = [];
        public List<FilmPlan> Plans { get; } = [];
        public TaskCompletionSource? Hold { get; set; }
        public string? Failure { get; set; }
        public IReadOnlyList<VideoAssembleProgress> Reports { get; set; } = [];
        public volatile bool Started;

        public bool Available => IsAvailable;

        public Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct)
        {
            var hasAudio = AudioByFile.TryGetValue(Path.GetFileName(path), out var a) ? a : Info?.HasAudio ?? true;
            return Task.FromResult(Info is null ? null : Info with { HasAudio = hasAudio });
        }

        public Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);

        public Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);

        public async Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress, CancellationToken ct)
        {
            lock (Plans) Plans.Add(plan);
            Started = true;
            foreach (var r in Reports) progress?.Report(r);
            // Как настоящий шов: временный файл пишется, при отмене и отказе удаляется
            await File.WriteAllBytesAsync(outPath, Output, ct);
            try
            {
                if (Hold is not null) await Hold.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                File.Delete(outPath);
                throw;
            }
            if (Failure is not null)
            {
                File.Delete(outPath);
                return VideoDspResult.Fail(Failure);
            }
            return VideoDspResult.Success;
        }
    }
}
