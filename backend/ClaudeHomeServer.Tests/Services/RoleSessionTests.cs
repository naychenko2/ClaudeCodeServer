using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Роли в сессиях: дефолты модели/effort от роли, roleId в чатах вне проекта,
// очистка [MEMORY]-маркеров из истории (TurnAccumulator)
public class RoleSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly RoleManager _roles;
    private readonly SessionManager _sut;
    private readonly UserStore _users;

    private const string TestUserId = "u-test";

    public RoleSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rolesess_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "projects-root"),
            }).Build();

        _users = new UserStore(_config, NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        _projects = new ProjectManager(_config, _users, appSettings);
        var history = new ChatHistoryService(_config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _roles = new RoleManager(_config);
        _sut = new SessionManager(_projects, hub.Object, history, _config, new SkillsService(),
            _roles, new RoleMemoryService(_config), new WorkspaceKnowledgeStore(_config),
            new FalCostService(new Mock<IHttpClientFactory>().Object, _config), new UsageService(_config),
            appSettings, _users);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string OwnerId => _users.GetFirst()!.Id;

    // --- Дефолты роли при создании сессий ---

    [Fact]
    public async Task CreateAsync_WithRole_UsesRoleModelAndEffort()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj")).FullName;
        var project = _projects.Create("P", dir, TestUserId, "tester");
        var role = _roles.Create(project.Id, "Игорь", "", "", "", "", null, null, "claude-haiku-4-5-20251001", "low");

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, roleId: role.Id);

        session.RoleId.Should().Be(role.Id);
        session.Model.Should().Be("claude-haiku-4-5-20251001");
        session.Effort.Should().Be("low");
    }

    [Fact]
    public async Task CreateAsync_ExplicitModel_OverridesRoleDefault()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj2")).FullName;
        var project = _projects.Create("P2", dir, TestUserId, "tester");
        var role = _roles.Create(project.Id, "Игорь", "", "", "", "", null, null, "claude-haiku-4-5-20251001", null);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "claude-sonnet-5", roleId: role.Id);

        session.Model.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task CreateChatAsync_WithRole_SetsRoleIdAndDefaults()
    {
        var role = _roles.Create(null, "Оля", "Аналитик", "", "", "", null, null, "claude-haiku-4-5-20251001", "high");

        var chat = await _sut.CreateChatAsync(OwnerId, ClaudeMode.Auto, name: role.Name, roleId: role.Id);

        chat.ProjectId.Should().BeNull();
        chat.RoleId.Should().Be(role.Id);
        chat.Model.Should().Be("claude-haiku-4-5-20251001");
        chat.Effort.Should().Be("high");
    }

    [Fact]
    public async Task CreateChatAsync_UnknownRole_CreatesChatWithoutRole()
    {
        var chat = await _sut.CreateChatAsync(OwnerId, ClaudeMode.Auto, roleId: "нет-такой");
        chat.RoleId.Should().BeNull();
    }

    // --- Очистка [MEMORY] из истории (TurnAccumulator) ---

    [Fact]
    public void StripMemoryLines_RemovesMarkerLines()
    {
        var text = "Ответ по делу.\n[MEMORY] проект на .NET 9\nПродолжение.\n[memory] регистр не важен";
        TurnAccumulator.StripMemoryLines(text).Should().Be("Ответ по делу.\nПродолжение.");
    }

    [Fact]
    public void StripMemoryLines_NoMarkers_ReturnsUnchanged()
    {
        const string text = "Обычный ответ\nв две строки";
        TurnAccumulator.StripMemoryLines(text).Should().Be(text);
    }

    [Fact]
    public void Accumulator_WithStrip_HidesMarkersFromHistory()
    {
        var acc = new TurnAccumulator([], stripMemoryMarkers: true);
        acc.OnTextDelta("Привет!\n[MEMO");
        acc.OnTextDelta("RY] важный факт\nЕщё текст");
        acc.OnToolUse("t1", "Read", null);   // флашит текстовый буфер

        var text = acc.GetAll().OfType<StoredTextMessage>().Single().Text;
        text.Should().NotContain("[MEMORY]").And.NotContain("важный факт");
        text.Should().Contain("Привет!").And.Contain("Ещё текст");
    }

    [Fact]
    public void Accumulator_OnlyMarkerText_StoresNothing()
    {
        var acc = new TurnAccumulator([], stripMemoryMarkers: true);
        acc.OnTextDelta("[MEMORY] единственная строка");
        acc.OnToolUse("t1", "Read", null);

        acc.GetAll().OfType<StoredTextMessage>().Should().BeEmpty();
    }

    [Fact]
    public void Accumulator_WithoutStrip_KeepsMarkers()
    {
        var acc = new TurnAccumulator([]);
        acc.OnTextDelta("[MEMORY] факт");
        acc.OnToolUse("t1", "Read", null);

        acc.GetAll().OfType<StoredTextMessage>().Single().Text.Should().Contain("[MEMORY]");
    }
}
