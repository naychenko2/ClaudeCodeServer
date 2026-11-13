using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// ADR-040, Ф2-6: фильмы «Личного» в Personal/video/ — создание, список, сохранение сцены в фильм, ручки личного
// чата. GET папку не создаёт; пишущие пути создают её лениво; ключ области остаётся personal
public sealed class PersonalFilmsTests : IDisposable
{
    private const string PersonalSession = "pc1";

    private readonly FilmWorld _w = new();
    private readonly Mock<IPersonalProjects> _store = new();
    private readonly string _personalRoot;
    private readonly Project _personal;
    private readonly VideoEditScope _scope;
    private int _jobs;

    public PersonalFilmsTests()
    {
        _personalRoot = Path.Combine(_w.Dir, "Personal");
        _personal = new Project { Id = "personal-id", OwnerId = Owner, RootPath = _personalRoot, Kind = ProjectKind.Personal };
        _scope = new VideoEditScope(VideoEditScope.Personal, _personal) { TenantOwnerId = Owner };
        var chat = new ClaudeHomeServer.Models.Session { Id = PersonalSession, OwnerId = Owner };
        _w.Directory.Setup(d => d.GetById(PersonalSession)).Returns(chat);
        _w.Directory.Setup(d => d.ResolveOwnerId(chat)).Returns(Owner);
        _store.Setup(s => s.GetPersonalOf(Owner)).Returns((Project?)null);
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Directory.CreateDirectory(_personalRoot);
            return _personal;
        });
    }

    public void Dispose() => _w.Dispose();

    private PersonalFolderResolver Resolver() => new(_store.Object);

    private string Full(string relative) => Path.Combine(_personalRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private (VideoSceneDto Scene, VideoClipVersionDto Version) SceneWithClip(string folder)
    {
        var settings = new VideoSceneSettingsDto(null, null, "Камера у окна", "fal", "veo", 5, "16:9", false, 1);
        var job = "pjob" + Interlocked.Increment(ref _jobs);
        var scene = _w.Threads.AddScene(Owner, PersonalSession, folder, settings, null, "Сцена " + _jobs).Scene!;
        _w.Threads.AddLaunch(Owner, PersonalSession, scene.SceneId, new VideoLaunchDto(job, DateTime.UtcNow.AddSeconds(-30),
            VideoLaunchStatus.Running, false, VideoInitiators.Human, "fal", "veo", 1, "т", null, null));
        var finished = _w.Threads.FinishLaunch(Owner, PersonalSession, scene.SceneId, job, VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 1.5))], VideoSignatures.Snapshot(settings));
        _w.Workspace.SaveClip(Owner, job, 1, [9, 9, 9, 9], ".mp4");
        return (_w.Threads.Get(Owner, PersonalSession).Scenes.Single(s => s.SceneId == scene.SceneId), finished.NewVersions.Single());
    }

    private PersonalSceneSaver Saver(bool withFilms = true) => new(_w.JobThreads, _w.Workspace, Resolver(),
        withFilms ? new FilmSceneSaver(_w.Service, _w.Workspace, NullLogger<FilmSceneSaver>.Instance) : null);

    private Task<FilmCallResult<SaveSceneResult>> Save(PersonalSceneSaver saver, VideoSceneDto scene, VideoClipVersionDto version,
        string? folder = null, string? filmPath = null) =>
        saver.SaveAsync(Owner, new VideoEditScope(VideoEditScope.Personal, null) { TenantOwnerId = Owner },
            new SaveSceneRequest(PersonalSession, scene.SceneId, version.VersionId, folder, null, filmPath),
            VideoInitiators.Human, default, Owner);

    private PersonalFilmController Controller(IPersonalProjects? store = null)
    {
        var projects = new Mock<IProjectManager>();
        var access = new Mock<IProjectAccess>();
        var gate = new VideoEditScopeGate(projects.Object, _w.Directory.Object, access.Object,
            new PersonalFolderResolver(store ?? _store.Object));
        var assembler = new FilmAssembler(_w.Service, _w.Registry, new ConfigurationBuilder().Build(), NullLogger<FilmAssembler>.Instance);
        var music = new FilmMusicComposer(_w.Service, _w.Side, _w.JobThreads, projects.Object, NullLogger<FilmMusicComposer>.Instance);
        return new PersonalFilmController(gate, _w.Service, assembler, music)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner)], "test")),
                },
            },
        };
    }

    private static int Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200,
    };

    // ── Сервис: фильмы личной области с папкой ────────────────────────────────────

    [Fact]
    public async Task Фильм_Личного_создаётся_в_Personal_video_и_виден_в_списке_и_состоянии()
    {
        Directory.CreateDirectory(_personalRoot);

        var created = await _w.Service.CreateAsync(Owner, Owner, _scope, new FilmCreateRequest("video/утро/утро.film"), default);

        created.IsOk.Should().BeTrue(created.Error);
        File.Exists(Full("video/утро/утро.film")).Should().BeTrue();
        _w.Service.List(_scope).Value!.Select(f => f.Path).Should().Equal("video/утро/утро.film");
        _w.Service.State(Owner, Owner, _scope, "video/утро/утро.film").IsOk.Should().BeTrue();
        FilmService.ResolveFilm(_scope, "music/a.mp3", Owner).ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
    }

    [Fact]
    public void Без_папки_отказ_до_диска_с_текстом_папка_недоступна()
    {
        var noFolder = new VideoEditScope(VideoEditScope.Personal, null) { TenantOwnerId = Owner };

        var result = FilmService.ProjectOf(noFolder);

        result.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        result.Error.Should().Be(PersonalFolderResolver.UnavailableText);
    }

    // ── Сохранение сцены ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Сцена_с_папкой_фильма_встаёт_в_фильм_Личного_и_папка_создаётся_здесь()
    {
        var (scene, version) = SceneWithClip("");
        Directory.Exists(_personalRoot).Should().BeFalse();

        var result = await Save(Saver(), scene, version, folder: "video/утро");

        result.IsOk.Should().BeTrue(result.Error);
        result.Value!.Path.Should().Be("video/утро/scene-01.mp4");
        result.Value.AddedToFilm.Should().BeTrue();
        File.ReadAllBytes(Full("video/утро/scene-01.mp4")).Should().Equal(9, 9, 9, 9);
        _w.Films.ReadFile(Full("video/утро/утро.film")).Document!.Items.Should().ContainSingle()
            .Which.File.Should().Be("video/утро/scene-01.mp4");
        _w.Threads.Get(Owner, PersonalSession).Scenes.Single().FilmRef.Should().Be(new VideoFilmRefDto("video/утро/утро.film", 0));
        _store.Verify(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Папка_сцены_без_запроса_тоже_ведёт_в_фильм_а_filmPath_называет_фильм()
    {
        var (scene, version) = SceneWithClip("video/лето");
        var viaScene = await Save(Saver(), scene, version);
        viaScene.Value!.AddedToFilm.Should().BeTrue();
        viaScene.Value.Path.Should().Be("video/лето/scene-01.mp4");

        var (second, secondVersion) = SceneWithClip("");
        var viaFilm = await Save(Saver(), second, secondVersion, filmPath: "video/лето/лето.film");

        viaFilm.IsOk.Should().BeTrue(viaFilm.Error);
        viaFilm.Value!.AddedToFilm.Should().BeTrue();
        _w.Films.ReadFile(Full("video/лето/лето.film")).Document!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Без_фильма_и_без_папки_по_прежнему_плоский_клип_в_video()
    {
        var (scene, version) = SceneWithClip("");

        var result = await Save(Saver(), scene, version, folder: "video");

        result.Value!.Path.Should().Be("video/scene-01.mp4");
        result.Value.AddedToFilm.Should().BeFalse();
        Directory.GetFiles(Full("video"), "*.film", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Без_сохранителя_фильмов_запрос_с_фильмом_отказывает_и_не_пишет_плоский_клип()
    {
        var (scene, version) = SceneWithClip("");

        var result = await Save(Saver(withFilms: false), scene, version, filmPath: "video/x/x.film");

        result.ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        Directory.Exists(_personalRoot).Should().BeFalse("до записи отказ");
    }

    [Fact]
    public async Task Фильм_вне_video_Личного_не_принимается()
    {
        var (scene, version) = SceneWithClip("");

        var result = await Save(Saver(), scene, version, filmPath: "docs/x/x.film");

        result.IsOk.Should().BeFalse();
        File.Exists(Full("docs/x/x.film")).Should().BeFalse();
    }

    // ── Ручки личного чата ────────────────────────────────────────────────────────

    [Fact]
    public void GET_без_папки_пуст_и_папку_не_создаёт()
    {
        var controller = Controller();

        var list = controller.List(PersonalSession);
        var state = controller.State(PersonalSession, "video/a/a.film");
        var status = controller.BuildStatus(PersonalSession, "video/a/a.film");

        Status(list).Should().Be(200);
        ((OkObjectResult)list).Value.Should().BeAssignableTo<IEnumerable<FilmSummaryDto>>().Which.Should().BeEmpty();
        Status(state).Should().Be(404);
        Status(status).Should().Be(404);
        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Directory.Exists(_personalRoot).Should().BeFalse();
    }

    [Fact]
    public async Task Правка_сборка_и_музыка_без_папки_отвечают_404_и_папку_не_создают()
    {
        var controller = Controller();

        var patch = await controller.Patch(PersonalSession, "video/a/a.film", new FilmPatch("r", []), default);
        var build = controller.Build(PersonalSession, "video/a/a.film");
        var cancel = controller.CancelBuild(PersonalSession, "video/a/a.film");
        var music = await controller.ComposeMusic(PersonalSession, "video/a/a.film", default);

        new[] { Status(patch), Status(build), Status(cancel), Status(music) }.Should().AllBeEquivalentTo(404);
        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Создание_фильма_ручкой_создаёт_папку_и_отвечает_201_а_потом_GET_видит_фильм()
    {
        var controller = Controller();

        var created = await controller.Create(PersonalSession, new FilmCreateRequest("video/a/a.film"), default);

        Status(created).Should().Be(201);
        _store.Verify(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()), Times.Once);
        File.Exists(Full("video/a/a.film")).Should().BeTrue();

        _store.Setup(s => s.GetPersonalOf(Owner)).Returns(_personal);
        var list = Controller().List(PersonalSession);
        ((OkObjectResult)list).Value.Should().BeAssignableTo<IEnumerable<FilmSummaryDto>>()
            .Which.Select(f => f.Path).Should().Equal("video/a/a.film");
    }

    [Fact]
    public async Task Сбой_папки_при_создании_фильма_отвечает_папка_недоступна_без_падения()
    {
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("нет домашней папки"));

        var created = await Controller().Create(PersonalSession, new FilmCreateRequest("video/a/a.film"), default);

        Status(created).Should().Be(400);
        ((created as ObjectResult)!.Value!.ToString()).Should().Contain(VideoEditorErrors.PersonalScopeNoFilms);
    }

    [Fact]
    public void Чужой_чат_и_чат_проекта_получают_404_на_всех_ручках_фильмов()
    {
        var other = new ClaudeHomeServer.Models.Session { Id = "other", OwnerId = "stranger" };
        var inProject = new ClaudeHomeServer.Models.Session { Id = "inproj", OwnerId = Owner, ProjectId = "p1" };
        _w.Directory.Setup(d => d.GetById("other")).Returns(other);
        _w.Directory.Setup(d => d.GetById("inproj")).Returns(inProject);
        var controller = Controller();

        Status(controller.List("other")).Should().Be(404);
        Status(controller.List("inproj")).Should().Be(404);
        Status(controller.State("other", "video/a/a.film")).Should().Be(404);
    }

    // ── Сборка ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Сборка_в_личной_области_без_папки_отказывает_до_диска()
    {
        var assembler = new FilmAssembler(_w.Service, _w.Registry, new ConfigurationBuilder().Build(), NullLogger<FilmAssembler>.Instance);

        assembler.Start(Owner, Owner, new VideoEditScope(VideoEditScope.Personal, null), "video/a/a.film", VideoInitiators.Human)
            .ErrorCode.Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
    }

    // ── Музыка ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Музыка_под_фильм_Личного_ложится_в_папку_Личного_а_ожидание_ключуется_personal()
    {
        Directory.CreateDirectory(_personalRoot);
        _store.Setup(s => s.GetPersonalOf(Owner)).Returns(_personal);
        await _w.Service.CreateAsync(Owner, Owner, _scope, new FilmCreateRequest("video/a/a.film"), default);
        var audio = new FakeAudio();
        var hub = new MediaEventHub();
        var projects = new Mock<IProjectManager>();
        var music = new FilmMusicComposer(_w.Service, _w.Side, _w.JobThreads, projects.Object,
            NullLogger<FilmMusicComposer>.Instance, audio, Resolver());
        var subscriber = new FilmMediaSubscriber(new FilmFrameFollower(_w.JobThreads, NullLogger<FilmFrameFollower>.Instance), music, hub);
        await subscriber.StartAsync(default);
        try
        {
            var draft = await music.CreateDraftAsync(Owner, Owner, _scope, "video/a/a.film", new FilmMusicRequest(PersonalSession), default);
            draft.IsOk.Should().BeTrue(draft.Error);
            _w.Side.Get(Owner, VideoEditScope.Personal, "video/a/a.film").PendingMusic.Should().NotBeNull();
            audio.Files["v1"] = new AudioTrackFile([1, 2, 3], ".mp3");

            await hub.PublishAsync(new AudioVersionAdded(Owner, PersonalSession, VideoEditScope.Personal, "thread-1", "v1", "human"));

            File.ReadAllBytes(Full("music/a.mp3")).Should().Equal(1, 2, 3);
            _w.Films.ReadFile(Full("video/a/a.film")).Document!.Music!.File.Should().Be("music/a.mp3");
            _w.Side.Get(Owner, VideoEditScope.Personal, "video/a/a.film").PendingMusic.Should().BeNull();
        }
        finally { await subscriber.StopAsync(default); }
    }

    private sealed class FakeAudio : IAudioTrackSource
    {
        public Dictionary<string, AudioTrackFile> Files { get; } = [];

        public Task<AudioTrackDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder, CancellationToken ct) =>
            Task.FromResult<AudioTrackDraft?>(new AudioTrackDraft("thread-1"));

        public Task<AudioTrackFile?> GetMainFileAsync(string ownerId, string threadId, string versionId, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(versionId, out var file) ? file : null);
    }
}
