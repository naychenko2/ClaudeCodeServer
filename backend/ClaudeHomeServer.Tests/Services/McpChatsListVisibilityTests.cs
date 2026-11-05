using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-4e (путь 9 разреза 4bc3d04f): MCP wsp <c>chats_list</c> отдаёт только чаты, видимые
/// актору хода, — та же видимость, что у человеческих списков (S2d-4a, SessionAccess.CanSee):
/// чужой приватный чат не отдаётся даже владельцу проекта; автор своего приватного видит;
/// актор неизвестен (фон, исполнитель без инициатора) — приватные не отдаются (fail-closed).
///
/// Проверяется без хоста: тулсет собран с живыми сервисами (состав SessionManager — образец
/// PrivateChatListVisibilityTests S2d-4a, тот же шаг фичи), вызов идёт через CallAsync с
/// чатом-вызывателем в хвосте маршрута, ответ читается из сериализованного JSON — то есть
/// проверяется то, что реально уходит на wire, а не список до маппинга. Актор хода — подмена
/// реестра (образец ActorRightGateTests), рамка — Narrow (живой вердикт общего проекта, S2d-1b),
/// гейт актора настоящий: chats_list в таблице — чтение, открытое участнику (ActorToolRights),
/// поэтому отказ здесь — именно приватность чата, а не гейт.
/// </summary>
public sealed class McpChatsListVisibilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly WorkspaceToolset _toolset;
    private readonly Actors _actors = new();
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;
    private readonly string _personaId;

    // Сид: чат участника, приватный участнику; чат участника общий; чат владельца приватный
    // владельцу; чат владельца общий; чат-вызыватель (проектный, с проектной персоной, общий).
    private const string ForeignPrivateId = "priv-foreign";
    private const string MemberOpenId = "member-open";
    private const string OwnPrivateId = "priv-own";
    private const string OwnerOpenId = "owner-open";
    private const string AgentChatId = "agent-chat";
    private const string ForeignPrivateName = "Приватный чат участника";
    private const string OwnPrivateName = "Приватный чат владельца";

    public McpChatsListVisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_lists_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Членство в проекте оформляется только при включённом рубильнике — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("kuzma-editor", "pwd", "user").Id;
        _users.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        _personas = new PersonaManager(_config);
        _personaId = _personas.Create(_ownerId, "Персона проекта", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;

        WriteSessions(Path.Combine(_tempDir, "sessions.json"), [
            new Session { Id = ForeignPrivateId, ProjectId = _projectId, Name = ForeignPrivateName,
                CreatedByUserId = _editorId, PrivateToUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = MemberOpenId, ProjectId = _projectId, CreatedByUserId = _editorId,
                Status = SessionStatus.Finished },
            new Session { Id = OwnPrivateId, ProjectId = _projectId, Name = OwnPrivateName,
                CreatedByUserId = _ownerId, PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OwnerOpenId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = AgentChatId, ProjectId = _projectId, PersonaId = _personaId,
                Status = SessionStatus.Finished },
        ]);

        _sessions = CreateSessionManager(appSettings);

        // access и гейт актора — настоящие: отказ по приватности обязан быть видимым сквозь них
        // (chats_list в ActorToolRights — чтение, открытое актору-участнику).
        var access = new ProjectAccessService(_projects, _sessions, null);
        _toolset = new WorkspaceToolset(
            _projects, null!, _sessions, _personas, null!, null!, null!, null!, null!, null!,
            null!, null!, _users, null!, null!, null!, null!, null!, null!, null!,
            null!, access, new NarrowFrame(), _actors, new ActorRightGate(_actors, access, new NarrowFrame()));
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sessions.ToList()));

    // Живой SessionManager без процессов и LLM-вызовов (состав — PrivateChatListVisibilityTests)
    private SessionManager CreateSessionManager(AppSettingsService appSettings)
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, knowledge,
            new SkillsService(), _users, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, new ChatHistoryService(_config), _config, adapters,
            new FalCostService(new Mock<IHttpClientFactory>().Object, _config), new UsageService(_config),
            appSettings, _users, jwt, server.Object, llmProviders, new FeatureFlagService(_users),
            _personas, bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    // Рамка Narrow — живой вердикт общего проекта для любого актора (S2d-1b: Wide только
    // приватному чату автора-арендатора). Сужение зоны в CallAsync тогда настоящее.
    private sealed class NarrowFrame : ITurnFrameSource
    {
        public TurnFrameKind Frame(string? sessionId, string ownerId) => TurnFrameKind.Narrow;
    }

    // Реестр актора — подмена живого ядра (образец ActorRightGateTests): id или null (фон).
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    // Ответ как он уходит на wire: сериализованный JSON списка чатов
    private async Task<string> ChatsListJsonAsync(string? actorId)
    {
        _actors.Map[AgentChatId] = actorId;
        var result = await _toolset.CallAsync("chats_list",
            new JsonObject { ["projectId"] = _projectId },
            new McpToolCallContext(_ownerId, AgentChatId, AgentChatId), CancellationToken.None);
        result.IsError.Should().BeFalse(result.Text);
        return result.Text;
    }

    private static List<string> IdsOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task ХодВладельца_ЧужойПриватныйУчастникаНеВСписке_СвойПриватныйИОбщиеКакПрежде()
    {
        var json = await ChatsListJsonAsync(_ownerId);
        var ids = IdsOf(json);

        ids.Should().NotContain(ForeignPrivateId,
            "агент владельца в общем чате не видит приватный чат редактора — ни id, ни названием");
        json.Should().NotContain(ForeignPrivateName);
        ids.Should().Contain([OwnPrivateId, MemberOpenId, OwnerOpenId, AgentChatId],
            "свой приватный, общие чаты проекта и сам чат-вызыватель — прежнее поведение");
    }

    [Fact]
    public async Task ХодАвтора_СвойПриватныйВСписке_ПриватныйВладельцаНет()
    {
        var json = await ChatsListJsonAsync(_editorId);
        var ids = IdsOf(json);

        ids.Should().Contain([ForeignPrivateId, MemberOpenId, OwnerOpenId],
            "агент автора своего приватного видит (приватность — его поле, не чужой запрет)");
        json.Should().Contain(ForeignPrivateName);
        ids.Should().NotContain(OwnPrivateId, "приватный чат владельца участнику не виден — та же точка, что у людей");
    }

    [Fact]
    public async Task АкторНеизвестен_ПриватныеНеВидны_ОбщиеКакПрежде()
    {
        var json = await ChatsListJsonAsync(null);
        var ids = IdsOf(json);

        ids.Should().NotContain([ForeignPrivateId, OwnPrivateId],
            "приватность некому отнести — приватные не отдаются (fail-closed)");
        json.Should().NotContain(ForeignPrivateName).And.NotContain(OwnPrivateName);
        ids.Should().Contain([MemberOpenId, OwnerOpenId, AgentChatId],
            "общие чаты и чат-вызыватель на фоне остаются — фильтр добавляет только приватность");
    }
}
