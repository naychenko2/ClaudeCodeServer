using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Замок формата фильма (ADR-025 §11): сцена на позиции > 0 следует пропорциям фильма (lockedBy), сцена 1 их задаёт;
// несовпадение — formatMismatch у сцены и у строки фильма; формат фильма — формат первой сцены (кеш doc.Aspect)
public sealed class FilmFormatLockTests : IDisposable
{
    private const string Film = "video/утро/утро.film";
    private readonly Dsp _dsp = new();
    private readonly FilmWorld _w;

    public FilmFormatLockTests() => _w = new FilmWorld(_dsp);

    public void Dispose() => _w.Dispose();

    private sealed class Dsp : IVideoDsp
    {
        public (int W, int H) Size { get; set; } = (1920, 1080);
        public bool Available => true;
        public Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct) =>
            Task.FromResult<VideoDspInfo?>(new VideoDspInfo(10, Size.W, Size.H, 24, true));
        public Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);
        public Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);
        public Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);
    }

    private FilmSceneSaver Saver() => new(_w.Service, _w.Workspace, NullLogger<FilmSceneSaver>.Instance);

    private async Task<VideoSceneDto> SaveScene(string aspect, string name)
    {
        var settings = new VideoSceneSettingsDto(null, null, name, "fal", "veo", 5, aspect, false, 1);
        var (scene, version) = _w.SceneWithClip(settings: settings, name: name);
        var saved = await Saver().SaveAsync(Owner, _w.Scope,
            new SaveSceneRequest(Session, scene.SceneId, version.VersionId, "video/утро", null, Film), VideoInitiators.Human, default);
        saved.IsOk.Should().BeTrue(saved.Error);
        return _w.Threads.Get(Owner, Session).Scenes.Single(s => s.SceneId == scene.SceneId);
    }

    [Fact]
    public async Task First_scene_sets_the_format_and_is_not_locked_following_scenes_are()
    {
        _w.WriteFilm(Film, Doc());

        var first = await SaveScene("16:9", "Сцена 1");
        var second = await SaveScene("16:9", "Сцена 2");

        first.FilmRef!.Position.Should().Be(0);
        first.LockedBy.Should().BeNull("сцена 1 задаёт формат фильма");
        second.FilmRef!.Position.Should().Be(1);
        second.LockedBy.Should().Be(new VideoFormatLockDto(Film, "16:9"));
        VideoStale.Mismatch(second).Should().BeNull();
    }

    [Fact]
    public async Task Scene_in_other_aspect_gets_format_mismatch_and_the_film_row_is_marked()
    {
        _w.WriteFilm(Film, Doc());
        await SaveScene("16:9", "Сцена 1");
        await SaveScene("9:16", "Сцена 2");

        var scenes = _w.Threads.Get(Owner, Session).ToDto().Scenes;
        scenes.Single(s => s.Name == "Сцена 2").FormatMismatch.Should().BeTrue();
        scenes.Single(s => s.Name == "Сцена 1").FormatMismatch.Should().BeNull();
        var marks = _w.Service.State(Owner, _w.Scope, Film).Value!.Marks;
        marks.Select(m => m.FormatMismatch).Should().Equal(null, true);
    }

    [Fact]
    public async Task Bringing_the_scene_to_film_aspect_clears_the_mismatch_and_nothing_is_reshot()
    {
        _w.WriteFilm(Film, Doc());
        await SaveScene("16:9", "Сцена 1");
        var second = await SaveScene("9:16", "Сцена 2");

        // «Привести к 16:9» — смена формата сцены: аспект совпадает с замком, force не нужен
        var settings = second.Settings with { Aspect = "16:9", Format = null };
        var scenes = new VideoSceneService(_w.JobThreads, new Prefs.VideoPrefsService(new Prefs.VideoPrefsStore(Path.Combine(_w.Dir, "prefs"))));
        var call = await scenes.SettingsAsync(Owner, _w.Scope, Session, second.SceneId, settings, null);

        call.ErrorCode.Should().BeNull();
        var after = _w.Threads.Get(Owner, Session).ToDto().Scenes.Single(s => s.SceneId == second.SceneId);
        after.FormatMismatch.Should().BeNull();
        after.Versions.Should().HaveCount(1, "переснимать или нет, решает человек");
    }

    [Fact]
    public async Task Moving_a_scene_to_first_place_drops_its_lock_and_the_old_first_gets_one()
    {
        _w.WriteFilm(Film, Doc());
        var first = await SaveScene("16:9", "Сцена 1");
        var second = await SaveScene("16:9", "Сцена 2");
        var revision = _w.Films.ReadFile(_w.Full(Film)).Revision;

        var patched = await _w.Service.PatchAsync(Owner, _w.Scope, Film,
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Move, From: 1, To: 0)]), VideoInitiators.Human, default, Session);

        patched.IsOk.Should().BeTrue(patched.Error);
        var scenes = _w.Threads.Get(Owner, Session).Scenes;
        scenes.Single(s => s.SceneId == second.SceneId).LockedBy.Should().BeNull();
        scenes.Single(s => s.SceneId == first.SceneId).LockedBy.Should().NotBeNull();
    }

    // ── doc.Aspect — кеш формата первой сцены ───────────────────────────────────

    [Fact]
    public async Task Patch_that_puts_another_clip_first_refreshes_the_cached_aspect_from_the_clip()
    {
        _w.WriteFile("video/утро/a.mp4");
        _w.WriteFilm(Film, Doc());
        var revision = _w.Films.ReadFile(_w.Full(Film)).Revision;
        _dsp.Size = (1080, 1920);

        var patched = await _w.Service.PatchAsync(Owner, _w.Scope, Film,
            new FilmPatch(revision, [new FilmPatchOp(FilmPatchOps.Add, File: "video/утро/a.mp4")]), VideoInitiators.Human, default, Session);

        patched.IsOk.Should().BeTrue(patched.Error);
        _w.Films.ReadFile(_w.Full(Film)).Document!.Aspect.Should().Be("9:16", "формат фильма — формат первой сцены");
    }

    [Fact]
    public void Old_film_without_cached_aspect_variants_still_reads_and_unknown_wide_ratio_is_valid()
    {
        var doc = new FilmDocument(1, "21:9", [Item("video/a/1.mp4")], [], null, []);

        FilmFormat.Validate(doc).Should().BeNull();
        FilmFormat.AspectOfSize(2560, 1080).Should().Be("21:9");
        FilmFormat.AspectOfSize(1344, 768).Should().Be("16:9", "1344×768 — «16:9» в пределах 2 %, а не «7:4»");
        FilmFormat.AspectOfSize(1000, 600).Should().Be("5:3");
    }
}
