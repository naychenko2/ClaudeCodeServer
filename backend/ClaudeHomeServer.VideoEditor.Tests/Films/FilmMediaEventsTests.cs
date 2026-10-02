using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// «Видео» узнаёт о соседях ТОЛЬКО событием IMediaEvents (ADR-022 §3): кадр с follow переходит на новую версию нити
// «Картинок» (клип уже снят — «переснять»), а первая готовая версия нити «Звука» «Сочинить под фильм» сама ложится
// в music/<фильм>.mp3 и в .film
public sealed class FilmMediaEventsTests : IDisposable
{
    private readonly FilmWorld _w = new();
    private readonly MediaEventHub _hub = new();

    private readonly FakeAudio _audio = new();
    private readonly FilmMusicComposer _music;
    private readonly FilmMediaSubscriber _subscriber;

    public FilmMediaEventsTests()
    {
        // Сквозь хаб, как в бою: подписчик ставится тем же классом, что и в регистрации
        _music = Music(_audio);
        _subscriber = new FilmMediaSubscriber(new FilmFrameFollower(_w.JobThreads, NullLogger<FilmFrameFollower>.Instance), _music, _hub);
        _subscriber.StartAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _subscriber.StopAsync(default).GetAwaiter().GetResult();
        _w.Dispose();
    }

    private static ImageVersionAdded Image(string thread, string version) => new(Owner, Session, ProjectId, thread, version, "human");

    private static VideoSceneSettingsDto Frames(FrameRef? a, FrameRef? b = null) =>
        new(a, b, "Камера", "fal", "veo", 5, "16:9", false, 1);

    // ── Кадры ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Кадр_с_follow_переходит_на_новую_версию_нити()
    {
        var (scene, _) = _w.SceneWithClip(settings: Frames(FrameRef.Image("t1", "v1"), FrameRef.Image("t2", "v9")));

        await _hub.PublishAsync(Image("t1", "v2"));

        var after = _w.Threads.Get(Owner, Session).Scenes.Single(s => s.SceneId == scene.SceneId).Settings;
        after.FrameA.Should().Be(FrameRef.Image("t1", "v2"));
        after.FrameB.Should().Be(FrameRef.Image("t2", "v9"), "кадр другой нити не трогаем");
    }

    [Fact]
    public async Task Клип_уже_снят_значит_кадр_изменён_переснять_с_тихой_строкой_и_журналом()
    {
        var (scene, _) = _w.SceneWithClip(settings: Frames(FrameRef.Image("t1", "v1")));
        _w.Feed.Records.Clear();

        await _hub.PublishAsync(Image("t1", "v2"));

        var state = _w.Threads.Get(Owner, Session);
        VideoStale.Compute(state.Scenes.Single(s => s.SceneId == scene.SceneId))!.FrameA.Should().BeTrue("кадр A изменён после съёмки");
        state.Events.Should().Contain(e => e.Kind == VideoThreadEventKinds.FrameChanged && e.Text.Contains("переснять"));
        _w.Feed.Records.Should().ContainSingle(r => r.SessionId == Session && r.Record.RecordType == VideoThreadRecordTypes.Note)
            .Which.Record.Fallback.Should().Contain("переснять");
        _w.Broadcaster.Sent.OfType<VideoThreadChangedMessage>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Клипа_ещё_нет_кадр_просто_переезжает_без_пересъёмки_и_шума()
    {
        var added = _w.Threads.AddScene(Owner, Session, "video/утро", Frames(FrameRef.Image("t1", "v1")), null);
        _w.Feed.Records.Clear();

        await _hub.PublishAsync(Image("t1", "v2"));

        var scene = _w.Threads.Get(Owner, Session).Scenes.Single(s => s.SceneId == added.Scene!.SceneId);
        scene.Settings.FrameA.Should().Be(FrameRef.Image("t1", "v2"));
        _w.Feed.Records.Should().BeEmpty("снимать нечего — переснимать тоже");
        _w.Threads.Get(Owner, Session).Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Кадр_без_follow_файл_и_чужой_владелец_не_двигаются()
    {
        var (noFollow, _) = _w.SceneWithClip(settings: Frames(FrameRef.Image("t1", "v1", follow: false)), name: "Без follow");
        var (file, _) = _w.SceneWithClip(settings: Frames(FrameRef.File("video/утро/a.png")), name: "Файл");

        await _hub.PublishAsync(Image("t1", "v2"));
        await _hub.PublishAsync(new ImageVersionAdded("чужой", Session, ProjectId, "t1", "v3", "human"));

        var scenes = _w.Threads.Get(Owner, Session).Scenes;
        scenes.Single(s => s.SceneId == noFollow.SceneId).Settings.FrameA!.VersionId.Should().Be("v1");
        scenes.Single(s => s.SceneId == file.SceneId).Settings.FrameA.Should().Be(FrameRef.File("video/утро/a.png"));
    }

    // ── Музыка ───────────────────────────────────────────────────────────────────

    private FilmMusicComposer Music(FakeAudio? audio) =>
        new(_w.Service, _w.Side, _w.JobThreads, ProjectsMock().Object, NullLogger<FilmMusicComposer>.Instance, audio);

    private Mock<IProjectManager> ProjectsMock()
    {
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(_w.Project);
        return projects;
    }

    [Fact]
    public async Task Сочинить_под_фильм_заводит_черновик_и_запоминает_нить_у_фильма()
    {
        var music = _music; var audio = _audio;
        _w.WriteFilm("video/a/a.film", Doc());

        var result = await music.CreateDraftAsync(Owner, _w.Scope, "video/a/a.film", new FilmMusicRequest(Session), default);

        result.IsOk.Should().BeTrue(result.Error);
        result.Value!.ThreadId.Should().Be("thread-1");
        audio.Drafts.Should().Equal((Owner, ProjectId, Session, "music"));
        _w.Side.Get(Owner, ProjectId, "video/a/a.film").PendingMusic.Should().Be(
            new FilmPendingMusic(Session, "thread-1", _w.Side.Get(Owner, ProjectId, "video/a/a.film").PendingMusic!.At));
    }

    [Fact]
    public async Task Первая_готовая_версия_ложится_в_music_и_в_фильм_и_нить_больше_не_ждём()
    {
        var music = _music; var audio = _audio; var hub = _hub;
        _w.WriteFilm("video/a/a.film", Doc(Item("video/a/1.mp4")));
        await music.CreateDraftAsync(Owner, _w.Scope, "video/a/a.film", new FilmMusicRequest(Session), default);
        audio.Files["v1"] = new AudioTrackFile([1, 2, 3], ".mp3");
        audio.Files["v2"] = new AudioTrackFile([9, 9], ".mp3");

        await hub.PublishAsync(new AudioVersionAdded(Owner, Session, ProjectId, "thread-1", "v1", "human"));
        await hub.PublishAsync(new AudioVersionAdded(Owner, Session, ProjectId, "thread-1", "v2", "human"));

        File.ReadAllBytes(_w.Full("music/a.mp3")).Should().Equal(1, 2, 3);
        File.Exists(_w.Full("music/a.v2.mp3")).Should().BeFalse("вторая версия музыку фильма не меняет");
        var doc = _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!;
        doc.Music.Should().Be(new FilmMusic("music/a.mp3", FilmMusicComposer.DefaultVolume, FilmMusicComposer.DefaultFadeOut));
        _w.Side.Get(Owner, ProjectId, "video/a/a.film").PendingMusic.Should().BeNull();
        _w.Feed.Records.Should().Contain(r => r.Record.RecordType == VideoThreadRecordTypes.Note && r.Record.Fallback!.Contains("Музыка фильма"));
    }

    [Fact]
    public async Task Занятое_имя_музыки_уходит_в_v2_а_версия_без_основного_файла_фильм_не_закрывает()
    {
        var music = _music; var audio = _audio; var hub = _hub;
        _w.WriteFilm("video/a/a.film", Doc());
        _w.WriteFile("music/a.mp3", [5, 5]);
        await music.CreateDraftAsync(Owner, _w.Scope, "video/a/a.film", new FilmMusicRequest(Session), default);

        await hub.PublishAsync(new AudioVersionAdded(Owner, Session, ProjectId, "thread-1", "stems-only", "human"));
        _w.Side.Get(Owner, ProjectId, "video/a/a.film").PendingMusic.Should().NotBeNull("нет основного файла — ждём следующую версию");

        audio.Files["v1"] = new AudioTrackFile([1], ".mp3");
        await hub.PublishAsync(new AudioVersionAdded(Owner, Session, ProjectId, "thread-1", "v1", "human"));

        File.ReadAllBytes(_w.Full("music/a.mp3")).Should().Equal(5, 5);
        File.ReadAllBytes(_w.Full("music/a.v2.mp3")).Should().Equal(1);
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Music!.File.Should().Be("music/a.v2.mp3");
    }

    [Fact]
    public async Task Чужая_нить_и_фильм_без_ожидания_события_звука_не_трогают()
    {
        var audio = _audio; var hub = _hub;
        _w.WriteFilm("video/a/a.film", Doc());
        audio.Files["v1"] = new AudioTrackFile([1], ".mp3");

        await hub.PublishAsync(new AudioVersionAdded(Owner, Session, ProjectId, "другая-нить", "v1", "human"));

        Directory.Exists(_w.Full("music")).Should().BeFalse();
        _w.Films.ReadFile(_w.Full("video/a/a.film")).Document!.Music.Should().BeNull();
    }

    [Fact]
    public async Task Без_редактора_звука_Сочинить_отказывает_а_личная_область_и_чужой_чат_тоже()
    {
        _w.WriteFilm("video/a/a.film", Doc());
        var without = Music(null);

        (await without.CreateDraftAsync(Owner, _w.Scope, "video/a/a.film", new FilmMusicRequest(Session), default))
            .ErrorCode.Should().Be(VideoEditorErrors.ProviderUnavailable);
        var with = Music(new FakeAudio());
        (await with.CreateDraftAsync(Owner, new VideoEditScope(VideoEditScope.Personal, null), "video/a/a.film", new FilmMusicRequest(Session), default))
            .ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        (await with.CreateDraftAsync(Owner, _w.Scope, "video/a/a.film", new FilmMusicRequest("чужой-чат"), default))
            .ErrorCode.Should().Be(VideoEditorErrors.ChatNotFound);
    }

    private sealed class FakeAudio : IAudioTrackSource
    {
        public List<(string, string, string, string)> Drafts { get; } = [];
        public Dictionary<string, AudioTrackFile> Files { get; } = [];

        public Task<AudioTrackDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder, CancellationToken ct)
        {
            Drafts.Add((ownerId, scopeKey, sessionId, folder));
            return Task.FromResult<AudioTrackDraft?>(new AudioTrackDraft("thread-" + Drafts.Count));
        }

        public Task<AudioTrackFile?> GetMainFileAsync(string ownerId, string threadId, string versionId, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(versionId, out var file) ? file : null);
    }
}
