using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
    [Fact]
    public void ПриватныйЧатПроекта_ВидитТолькоАвтор_Остальным404()
    {
        var priv = new Session { Id = "priv", ProjectId = _projectId, PrivateToUserId = _editorId };
        var own = new Session { Id = "own", ProjectId = _projectId, PrivateToUserId = _ownerId };
        var open = new Session { Id = "open", ProjectId = _projectId };
        var dir = new WithSessions(priv, own, open);
        var access = Access();

        bool Image(string u, string s) => new ImageEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _);
        bool Audio(string u, string s) => new AudioEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _);
        bool Video(string u, string s) => new VideoEditScopeGate(_projects, dir, access).TryProjectChat(u, _projectId, s, out _, out _);

        foreach (var (name, gate) in new (string, Func<string, string, bool>)[] { ("image", Image), ("audio", Audio), ("video", Video) })
        {
            gate(_ownerId, "priv").Should().BeFalse($"{name}: владелец проекта не видит чужой приватный чат");
            gate(_ownerId, "own").Should().BeTrue($"{name}: автор приватного чата проходит");
            gate(_ownerId, "open").Should().BeTrue($"{name}: общий чат проходит как раньше");
        }
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
