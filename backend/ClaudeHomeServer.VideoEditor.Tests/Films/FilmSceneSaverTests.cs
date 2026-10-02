using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// «Сохранить сцену» (ADR-022 §2): версия клипа → video/<фильм>/scene-NN.mp4, повтор → .v2.mp4, только CreateNew;
// кадры-нити той же группой в кадры/; новый файл сам встаёт в фильм; писать только в video/**
public sealed class FilmSceneSaverTests : IDisposable
{
    private readonly FilmWorld _w = new();
    private static readonly VideoEditScope Personal = new(VideoEditScope.Personal, null);

    public void Dispose() => _w.Dispose();

    private FilmSceneSaver Saver(IImageFrameSource? images = null) =>
        new(_w.Service, _w.Workspace, NullLogger<FilmSceneSaver>.Instance, images);

    private Task<FilmCallResult<SaveSceneResult>> Save(FilmSceneSaver saver, VideoSceneDto scene, VideoClipVersionDto version,
        string? folder = null, string? fileName = null, VideoEditScope? scope = null, string initiator = VideoInitiators.Human) =>
        saver.SaveAsync(Owner, scope ?? _w.Scope, new SaveSceneRequest(Session, scene.SceneId, version.VersionId, folder, fileName),
            initiator, default);

    [Fact]
    public async Task Версия_сохраняется_в_папку_фильма_и_сама_встаёт_в_фильм()
    {
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version);

        result.IsOk.Should().BeTrue(result.Error);
        result.Value!.Path.Should().Be("video/утро/scene-01.mp4");
        result.Value.AddedToFilm.Should().BeTrue();
        File.ReadAllBytes(_w.Full("video/утро/scene-01.mp4")).Should().Equal(9, 9, 9, 9);

        var film = _w.Films.ReadFile(_w.Full("video/утро/утро.film")).Document!;
        var item = film.Items.Should().ContainSingle().Subject;
        item.File.Should().Be("video/утро/scene-01.mp4");
        item.Trim.Should().Equal(0, 5);
        item.Scene.Should().Match<FilmSceneSnapshot>(s => s.Text == "Камера у окна" && s.Provider == "fal" && s.Model == "veo");

        var saved = _w.Threads.Get(Owner, Session).Scenes.Single();
        saved.SavedFiles.Should().ContainSingle(f => f.Path == "video/утро/scene-01.mp4" && f.VersionId == version.VersionId);
        saved.FilmRef.Should().Be(new VideoFilmRefDto("video/утро/утро.film", 0));
        _w.Feed.Records.Should().Contain(r => r.Record.RecordType == VideoThreadRecordTypes.Saved);
        _w.Service.State(Owner, _w.Scope, "video/утро/утро.film").Value!.Marks.Single().Updated
            .Should().BeTrue("новый файл сцены встаёт в фильм с точкой «обновлена»");
    }

    [Fact]
    public async Task Повторное_сохранение_идёт_в_v2_и_ничего_не_затирает()
    {
        var (scene, version) = _w.SceneWithClip();
        var saver = Saver();
        await Save(saver, scene, version);
        File.WriteAllBytes(_w.Full("video/утро/scene-01.mp4"), [7, 7]);

        var again = await Save(saver, scene, version);
        var third = await Save(saver, scene, version);

        again.Value!.Path.Should().Be("video/утро/scene-01.v2.mp4");
        third.Value!.Path.Should().Be("video/утро/scene-01.v3.mp4");
        File.ReadAllBytes(_w.Full("video/утро/scene-01.mp4")).Should().Equal(7, 7);
        _w.Films.ReadFile(_w.Full("video/утро/утро.film")).Document!.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Занятое_имя_названное_человеком_даёт_name_taken_без_перезаписи()
    {
        var (scene, version) = _w.SceneWithClip();
        _w.WriteFile("video/утро/final.mp4", [5, 5]);

        var result = await Save(Saver(), scene, version, fileName: "final.mp4");

        result.ErrorCode.Should().Be(VideoEditorErrors.NameTaken);
        File.ReadAllBytes(_w.Full("video/утро/final.mp4")).Should().Equal(5, 5);
    }

    [Theory]
    [InlineData("../evil.mp4")]
    [InlineData("a/b.mp4")]
    [InlineData("clip.mov")]
    [InlineData("..")]
    public async Task Имя_файла_только_простое_с_mp4(string name)
    {
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version, fileName: name);

        result.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
    }

    [Theory]
    [InlineData("docs/x")]
    [InlineData("video")]
    [InlineData("music/x")]
    [InlineData("video/../x")]
    [InlineData("/etc")]
    public async Task Папка_вне_video_фильм_отказывает_и_ничего_не_пишет(string folder)
    {
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version, folder: folder);

        result.IsOk.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf(VideoEditorErrors.OutsideAllowedFolders, VideoEditorErrors.InvalidRequest);
        Directory.Exists(_w.Full("docs")).Should().BeFalse();
        Directory.Exists(Path.Combine(_w.Dir, "x")).Should().BeFalse();
    }

    [Fact]
    public async Task Символическая_ссылка_наружу_вместо_папки_фильма_отказывает()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_w.Dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(_w.Full("video"));
        Directory.CreateSymbolicLink(_w.Full("video/link"), outside);
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version, folder: "video/link");

        result.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        Directory.GetFiles(outside).Should().BeEmpty("за ссылку наружу ничего не записано");
    }

    [Fact]
    public async Task Личная_область_отказывает_до_диска()
    {
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version, scope: Personal);

        result.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
    }

    [Fact]
    public async Task Кадры_нитей_ложатся_той_же_группой_в_кадры_а_кадры_файлы_остаются_как_есть()
    {
        _w.WriteFile("video/утро/b.png", [3, 3]);
        var settings = new VideoSceneSettingsDto(FrameRef.Image("t1", "v1"), FrameRef.File("video/утро/b.png"),
            "Камера", "fal", "veo", 5, "16:9", false, 1);
        var (scene, version) = _w.SceneWithClip(settings: settings);
        var images = new FakeFrames(new ImageFrame([8, 8, 8], "image/png"));

        var result = await Save(Saver(images), scene, version);

        result.Value!.FramePaths.Should().Equal("video/утро/кадры/кадр-1.png");
        File.ReadAllBytes(_w.Full("video/утро/кадры/кадр-1.png")).Should().Equal(8, 8, 8);
        images.Asked.Should().Equal(("u1", "t1", "v1"));
        var snapshot = _w.Films.ReadFile(_w.Full("video/утро/утро.film")).Document!.Items.Single().Scene!;
        snapshot.FrameA.Should().Be("video/утро/кадры/кадр-1.png");
        snapshot.FrameB.Should().Be("video/утро/b.png");
    }

    [Fact]
    public async Task Фильм_чужой_схемы_не_правится_но_клип_сохраняется()
    {
        Directory.CreateDirectory(_w.Full("video/утро"));
        File.WriteAllText(_w.Full("video/утро/утро.film"), """{ "schema": 9, "aspect": "16:9", "items": [], "cuts": [], "builds": [] }""");
        var (scene, version) = _w.SceneWithClip();

        var result = await Save(Saver(), scene, version);

        result.IsOk.Should().BeTrue();
        result.Value!.AddedToFilm.Should().BeFalse();
        File.Exists(_w.Full("video/утро/scene-01.mp4")).Should().BeTrue();
        File.ReadAllText(_w.Full("video/утро/утро.film")).Should().Contain("\"schema\": 9", "чужой фильм не тронут");
    }

    [Fact]
    public async Task Клип_больше_300_МБ_не_сохраняется()
    {
        var (scene, version) = _w.SceneWithClip();
        var clip = _w.Workspace.FindClip(Owner, version.JobId, version.Variant)!;
        using (var stream = new FileStream(clip, FileMode.Open)) stream.SetLength(SafeMediaDownloader.VideoMaxBytes + 1);

        var result = await Save(Saver(), scene, version);

        result.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        File.Exists(_w.Full("video/утро/scene-01.mp4")).Should().BeFalse();
    }

    [Fact]
    public async Task Чужая_сцена_и_несуществующая_версия_отказывают_до_записи()
    {
        var (scene, version) = _w.SceneWithClip();

        var noScene = await Saver().SaveAsync(Owner, _w.Scope, new SaveSceneRequest(Session, "нет-такой", null, null, null), VideoInitiators.Human, default);
        var noVersion = await Saver().SaveAsync(Owner, _w.Scope, new SaveSceneRequest(Session, scene.SceneId, "нет-версии", null, null), VideoInitiators.Human, default);

        noScene.ErrorCode.Should().Be(VideoEditorErrors.SceneNotFound);
        noVersion.ErrorCode.Should().Be(VideoEditorErrors.VersionNotFound);
        Directory.Exists(_w.Full("video")).Should().BeFalse();
        version.Should().NotBeNull();
    }

    [Fact]
    public async Task Сохранение_агентом_ставит_пометку_Claude_на_новой_строке()
    {
        var (scene, version) = _w.SceneWithClip();

        await Save(Saver(), scene, version, initiator: VideoInitiators.Agent);

        _w.Service.State(Owner, _w.Scope, "video/утро/утро.film").Value!.Marks.Single().Claude.Should().BeTrue();
    }

    private sealed class FakeFrames(ImageFrame? frame) : IImageFrameSource
    {
        public List<(string, string, string)> Asked { get; } = [];

        public Task<ImageFrame?> GetAsync(string ownerId, string threadId, string versionId, CancellationToken ct)
        {
            Asked.Add((ownerId, threadId, versionId));
            return Task.FromResult(frame);
        }

        public Task<ImageFrameDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder, CancellationToken ct) =>
            Task.FromResult<ImageFrameDraft?>(null);
    }
}
