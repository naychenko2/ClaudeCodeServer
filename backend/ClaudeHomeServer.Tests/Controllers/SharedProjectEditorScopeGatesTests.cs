using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using FluentAssertions;
using System.Security.Claims;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Дефект R-S1-3a: шлюзы областей редакторов картинок/звука/видео пускают в проект ТОЛЬКО через
/// настоящую точку доступа (<see cref="ProjectAccessService"/>), поэтому участник общего проекта
/// (Editor и Reader) в S1 не пишет в каталог владельца, а при выключенном рубильнике его не
/// пускает вовсе. Отказ шлюза — 404 (редакторы не раскрывают существование), владелец проходит
/// как раньше. Полный хост не нужен: ручки редакторов зовут именно эти шлюзы первым действием.
/// </summary>
public sealed class SharedProjectEditorScopeGatesTests : IDisposable
{
    private readonly string _dir;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _editorId;
    private readonly string _readerId;

    public SharedProjectEditorScopeGatesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shared_gates_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = Config(true);
        _users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("gates-editor", "pwd", "user").Id;
        _readerId = _users.Add("gates-reader", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _readerId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);
        _projects = new ProjectManager(config, _users, new AppSettingsService(config));
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        _projectId = _projects.Create("Общий проект", root, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _readerId, Role = ProjectRole.Reader, AddedBy = _ownerId },
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config(bool instanceEnabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = instanceEnabled ? "true" : "false",
        }).Build();

    private ProjectAccessService Access(bool instanceEnabled = true) =>
        new(_projects, new NoSessions(), new SharedProjectsGate(Config(instanceEnabled), new FeatureFlagService(_users)));

    // Три шлюза одним списком: (имя, вызов TryProject → пустил ли и какой результат отказа)
    private static (string Name, bool Allowed, IActionResult? Denied)[] TryAll(
        IProjectManager projects, IProjectAccess access, string userId, string projectId)
    {
        var dir = new NoSessions();
        var image = new ImageEditScopeGate(projects, dir, access).TryProject(userId, projectId, out _, out var d1);
        var audio = new AudioEditScopeGate(projects, dir, access).TryProject(userId, projectId, out _, out var d2);
        var video = new VideoEditScopeGate(projects, dir, access).TryProject(userId, projectId, out _, out var d3);
        return [("image", image, d1), ("audio", audio, d2), ("video", video, d3)];
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public void Участник_ПишетЧерезРедакторы_404(string role)
    {
        var userId = role == "editor" ? _editorId : _readerId;

        foreach (var (name, allowed, denied) in TryAll(_projects, Access(), userId, _projectId))
        {
            allowed.Should().BeFalse($"{name}: участник в S1 только читает, запись в проект владельца закрыта");
            denied.Should().BeOfType<NotFoundObjectResult>(name);
        }
    }

    [Fact]
    public void РубильникВыключен_Участник_404()
    {
        foreach (var (name, allowed, denied) in TryAll(_projects, Access(instanceEnabled: false), _editorId, _projectId))
        {
            allowed.Should().BeFalse(name);
            denied.Should().BeOfType<NotFoundObjectResult>(name);
        }
    }

    [Fact]
    public void Владелец_ПроходитКакРаньше_ДажеПриВыключенномРубильнике()
    {
        foreach (var instanceEnabled in new[] { true, false })
            foreach (var (name, allowed, _) in TryAll(_projects, Access(instanceEnabled), _ownerId, _projectId))
                allowed.Should().BeTrue($"{name}: владелец проекта пишет всегда (рубильник {instanceEnabled})");
    }

    // S2d-1d: приватный чат проекта виден только автору — владелец проекта с Write получает тот же 404,
    // что и «нет такого чата»; общий чат проходит как раньше
    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("video")]
    public void ПриватныйЧатПроекта_ВидитТолькоАвтор_Остальным404(string name)
    {
        var priv = new Session { Id = "priv", ProjectId = _projectId, PrivateToUserId = _editorId };
        var own = new Session { Id = "own", ProjectId = _projectId, PrivateToUserId = _ownerId };
        var open = new Session { Id = "open", ProjectId = _projectId };
        var dir = new WithSessions(priv, own, open);
        var access = Access();

        bool Gate(string u, string s) => name switch
        {
            "image" => new ImageEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _),
            "audio" => new AudioEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _),
            _ => new VideoEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _),
        };

        Gate(_ownerId, "priv").Should().BeFalse($"{name}: владелец проекта не видит чужой приватный чат");
        Gate(_ownerId, "own").Should().BeTrue($"{name}: автор приватного чата проходит");
        Gate(_ownerId, "open").Should().BeTrue($"{name}: общий чат проходит как раньше");
    }

    // S2d-1e: sessionId из тела/query/формы (TryOptionalChat) и чат, к которому привязан артефакт по id
    // (TryArtifactChat), проходят ту же видимость, что и sessionId в маршруте
    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("video")]
    public void SessionIdИзТелаИАртефакт_ПроходятПроверкуПриватностиЧата(string name)
    {
        var priv = new Session { Id = "priv", ProjectId = _projectId, PrivateToUserId = _editorId };
        var open = new Session { Id = "open", ProjectId = _projectId };
        var foreign = new Session { Id = "foreign", ProjectId = "other-project" };
        var dir = new WithSessions(priv, open, foreign);
        var access = Access();

        (bool Optional, IActionResult? Denied) Optional(string? s)
        {
            IActionResult? d;
            var ok = name switch
            {
                "image" => new ImageEditScopeGate(_projects, dir, access).TryOptionalChat(_ownerId, _projectId, s, out d),
                "audio" => new AudioEditScopeGate(_projects, dir, access).TryOptionalChat(_ownerId, _projectId, s, out d),
                _ => new VideoEditScopeGate(_projects, dir, access).TryOptionalChat(_ownerId, _projectId, s, out d),
            };
            return (ok, d);
        }

        bool Artifact(string? s) => name switch
        {
            "image" => new ImageEditScopeGate(_projects, dir, access).TryArtifactChat(_ownerId, _projectId, s),
            "audio" => new AudioEditScopeGate(_projects, dir, access).TryArtifactChat(_ownerId, _projectId, s),
            _ => new VideoEditScopeGate(_projects, dir, access).TryArtifactChat(_ownerId, _projectId, s),
        };

        Optional(null).Optional.Should().BeTrue($"{name}: чата в запросе нет — гейтить нечего");
        Optional(" ").Optional.Should().BeTrue(name);
        Optional("open").Optional.Should().BeTrue($"{name}: общий чат проходит");
        var (okPriv, deniedPriv) = Optional("priv");
        okPriv.Should().BeFalse($"{name}: чужой приватный чат из тела закрыт");
        deniedPriv.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        Optional("foreign").Optional.Should().BeFalse($"{name}: чат другого проекта из тела закрыт");
        Optional("missing").Optional.Should().BeFalse($"{name}: несуществующий чат — 404");

        Artifact(null).Should().BeTrue($"{name}: артефакт без чата не гейтим");
        Artifact("open").Should().BeTrue(name);
        Artifact("priv").Should().BeFalse($"{name}: артефакт чужого приватного чата скрыт");
        Artifact("missing").Should().BeFalse($"{name}: чата уже нет — fail-closed");
    }

    // S2d-1e: настоящие контроллеры. Владелец проекта шлёт sessionId чужого приватного чата в теле/форме/query —
    // каждая ручка отвечает 404 до тела ручки (зависимости тела — null!: до них дело не доходит)
    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    private (ISessionDirectory Dir, ProjectAccessService Access) PrivateChat() =>
        (new WithSessions(new Session { Id = "priv", ProjectId = _projectId, PrivateToUserId = _editorId }), Access());

    // 404 именно от гейта чата (а не «котировка не найдена» и т.п. из тела ручки)
    private static void Denied404(IActionResult? result, string what, string code = "chat_not_found")
    {
        var obj = result.Should().BeOfType<ObjectResult>(what).Subject;
        obj.StatusCode.Should().Be(404, what);
        System.Text.Json.JsonSerializer.Serialize(obj.Value).Should().Contain(code, what);
    }

    [Fact]
    public async Task ImageEditor_SessionIdВТелеИАртефакты_ЧужойПриватныйЧат404()
    {
        var (dir, access) = PrivateChat();
        var gate = new ImageEditScopeGate(_projects, dir, access);
        var jobs = new Mock<ClaudeHomeServer.Services.ImageEditor.IImageEditJobs>();
        jobs.Setup(j => j.Get(It.IsAny<string>(), It.IsAny<string>(), "job-priv"))
            .Returns(new ImageEditJobDto("job-priv", _projectId, default, "p", "m", [1], null, null, null, null, null,
                DateTime.UtcNow, "priv"));
        jobs.Setup(j => j.Get(It.IsAny<string>(), It.IsAny<string>(), "job-open"))
            .Returns(new ImageEditJobDto("job-open", _projectId, default, "p", "m", [1], null, null, null, null, null,
                DateTime.UtcNow));
        var c = new ImageEditorController(gate, [], null!, jobs: jobs.Object, saver: Mock.Of<IImageEditSaver>())
            { ControllerContext = AsUser(_ownerId) };

        Denied404(await c.Quote(_projectId, new ImageEditQuoteRequest("p", "m", default, default, 1, false, SessionId: "priv"), default), "quote");
        Denied404(await c.Start(_projectId, new ImageEditorEndpoints.StartJobForm { SessionId = "priv" }, default), "jobs");
        Denied404(await c.Save(_projectId, new ImageEditSaveRequest("j", 0, null, null, null, SessionId: "priv")), "save");
        Denied404(c.GetJob(_projectId, "job-priv"), "GET job (артефакт)", "job_not_found");
        Denied404(await c.Cancel(_projectId, "job-priv", default), "DELETE job (артефакт)", "job_not_found");
        Denied404(c.Variant(_projectId, "job-priv", 1), "variant (артефакт)", "job_not_found");
        c.GetJob(_projectId, "job-open").Should().BeOfType<OkObjectResult>("задача без чата не гейтится");
    }

    [Fact]
    public async Task AudioEditor_SessionIdВТелеИФорме_ЧужойПриватныйЧат404()
    {
        var (dir, access) = PrivateChat();
        var c = new AudioEditorController(new AudioEditScopeGate(_projects, dir, access), [], null!, null!, null!, null!, null!, null!)
            { ControllerContext = AsUser(_ownerId) };

        Denied404(await c.Quote(_projectId, new AudioQuoteRequest("voice", SessionId: "priv"), default), "quote");
        Denied404(await c.Start(_projectId, new AudioStartJobForm { SessionId = "priv" }, default), "jobs");
    }

    [Fact]
    public async Task VideoEditor_SessionIdВТелеИQuery_ЧужойПриватныйЧат404()
    {
        var (dir, access) = PrivateChat();
        var gate = new VideoEditScopeGate(_projects, dir, access);
        var c = new VideoEditorController(gate, [], null!, null!, null!, null!, null!) { ControllerContext = AsUser(_ownerId) };
        var films = new FilmController(gate, null!, null!, null!, null!) { ControllerContext = AsUser(_ownerId) };

        Denied404(await c.Quote(_projectId, new VideoQuoteRequest("priv", "s", null, null, null, null, null, null), default), "quote");
        Denied404(await c.Start(_projectId, new VideoLaunchRequest("q", "priv", "s"), default), "jobs");
        Denied404(await c.FramesPreview(_projectId, new VideoFramesPreviewRequest("priv", "s"), default), "frames/preview");
        Denied404(await films.Patch(_projectId, "films/a.film", new FilmPatch("r", []), default, "priv"), "PATCH films?sessionId=");
        Denied404(films.Build(_projectId, "films/a.film", "priv"), "POST films/build?sessionId=");
    }

    private sealed class WithSessions(params Session[] sessions) : ISessionDirectory
    {
        public Session? GetById(string id) => sessions.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => sessions;
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
