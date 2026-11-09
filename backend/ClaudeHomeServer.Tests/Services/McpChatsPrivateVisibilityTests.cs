using Microsoft.Extensions.DependencyInjection;
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
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// FIN3-1 (пункт critical FINAL-3): MCP wsp <c>chats_history/chats_send/chats_update/
/// chats_archive/chats_delete</c> проверяют видимость приватного чата в ЛЮБОЙ рамке —
/// та же точка, что у chats_list (S2d-4e) и chats_continue (S2d-7b-1): чужой приватный чат
/// не виден даже владельцу проекта; автор своего приватного видит; актор неизвестен —
/// приватные не видны (fail-closed). До правки приватность держала только узкая рамка
/// (NarrowDenied): в широкой владелец читал/писал/архивировал/удалял чужой приватный чат.
///
/// Проверяется без хоста: тулсет собран с живыми сервисами (образец —
/// McpChatsListVisibilityTests S2d-4e, тот же шаг фичи), рамка — Wide (в живом вердикте
/// Wide бывает у приватного автору-арендатора чата; здесь — подмена ITurnFrameSource),
/// актор хода — владелец (в таблице ActorToolRights OwnerOnly-права у него есть, поэтому
/// отказ здесь — именно приватность чата, а не гейт актора). Отказ чужого приватного
/// неотличим от отказа несуществующего id: текст ровно тот же, «не найдена», без имени
/// и без раскрытия существования. Свой приватный и общие чаты — прежнее поведение;
/// цель chats_send сидируется занятой (Working) — сообщение уходит в очередь без живого
/// хода (образец — SessionManagerTestBase.MkBusySessionAsync).
/// </summary>
public sealed class McpChatsPrivateVisibilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly TaskManager _tasks;
    private readonly WorkspaceToolset _toolset;
    private readonly Actors _actors = new();
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;
    private readonly string _personaId;

    // Сид: чат участника, приватный участнику; чат участника общий; чат владельца приватный
    // владельцу; чат владельца общий; чат-вызыватель (проектный, с проектной персоной, общий);
    // два занятых (Working) чата — общий и приватный владельцу: цели positive chats_send,
    // чтобы сообщение шло в очередь без живого хода.
    private const string ForeignPrivateId = "priv-foreign";
    private const string MemberOpenId = "member-open";
    private const string OwnPrivateId = "priv-own";
    private const string OwnerOpenId = "owner-open";
    private const string AgentChatId = "agent-chat";
    private const string SendOpenId = "send-open";
    private const string SendOwnPrivateId = "send-priv-own";
    private const string MissingId = "no-such-chat";
    private const string ForeignPrivateName = "Приватный чат участника";

    public McpChatsPrivateVisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_priv_" + Guid.NewGuid().ToString("N"));
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
        // chats_delete живёт за флагом workspace-destructive (SessionManager.BuildWorkspacePlan,
        // секция destructive) — без него план персоны её не монтирует (образец —
        // McpFilesPrivateAttachmentTests)
        _users.SetFeatureFlag(_ownerId, FeatureFlagKeys.WorkspaceDestructive, true);

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
            new Session { Id = OwnPrivateId, ProjectId = _projectId, Name = "Приватный чат владельца",
                CreatedByUserId = _ownerId, PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OwnerOpenId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = AgentChatId, ProjectId = _projectId, PersonaId = _personaId,
                Status = SessionStatus.Finished },
            new Session { Id = SendOpenId, ProjectId = _projectId, Status = SessionStatus.Working },
            new Session { Id = SendOwnPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                PrivateToUserId = _ownerId, Status = SessionStatus.Working },
        ]);

        _sessions = CreateSessionManager(appSettings);

        // access, messaging и гейт актора — настоящие: отказ по приватности обязан быть
        // видимым сквозь них (chats_* в ActorToolRights — OwnerOnly, у владельца права есть).
        var access = new ProjectAccessService(_projects, _sessions, null);
        _tasks = new TaskManager(_config);
        _toolset = new WorkspaceToolset(
            _projects, null!, _sessions, _personas, null!, null!, null!, null!, null!, null!,
            null!, null!, _users, null!, new SessionMessagingService(_sessions, _projects, _tasks),
            _tasks, null!, null!, null!, null!,
            access, new WideFrame(), _actors, new ActorRightGate(_actors, access, new WideFrame()));
    }

    public void Dispose()
    {
        // Реестр задач гасится ДО удаления каталога: Flush пишет tasks.json в _tempDir
        // (порядок — SessionManagerTestBase.Dispose).
        _tasks.Dispose();
        Helpers.TestFs.DeleteDirectoryResilient(_tempDir);
    }

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sessions.ToList()));

    // Живой SessionManager без процессов и LLM-вызовов (состав — McpChatsListVisibilityTests)
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
        return SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(_projects);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(new ChatHistoryService(_config));
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(_config);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(adapters);
            s.AddSingleton<global::ClaudeHomeServer.Services.FalCostService>(new FalCostService(new Mock<IHttpClientFactory>().Object, _config));
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.UsageService>(new UsageService(_config));
            s.AddSingleton<global::ClaudeHomeServer.Services.AppSettingsService>(appSettings);
            s.AddSingleton<global::ClaudeHomeServer.Services.UserStore>(_users);
            s.AddSingleton<global::ClaudeHomeServer.Services.JwtService>(jwt);
            s.AddSingleton<global::Microsoft.AspNetCore.Hosting.Server.IServer>(server.Object);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.LlmProviderRegistry>(llmProviders);
            s.AddSingleton<global::ClaudeHomeServer.Services.FeatureFlagService>(new FeatureFlagService(_users));
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaManager>(_personas);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaBindingsService>(bindings);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ClaudeSubscriptionPool>(subPool);
            s.AddSingleton<global::Microsoft.Extensions.Logging.ILogger<ClaudeHomeServer.Services.SessionManager>>(NullLogger<SessionManager>.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.ILauncherFactory>(TestLauncherFactory.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.SandboxManager>(sandbox);
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(new TestSessionBroadcaster());
        }).Manager;
    }

    // Рамка Wide — широкая рамка хода (в живом вердикте S2d-1b Wide — приватному автору-
    // арендатору чату; здесь подмена: проверяется поведение инструментов в широкой рамке,
    // где NarrowDenied не работает вовсе).
    private sealed class WideFrame : ITurnFrameSource
    {
        public TurnFrameKind Frame(string? sessionId, string ownerId) => TurnFrameKind.Wide;
    }

    // Реестр актора — подмена живого ядра (образец ActorRightGateTests): ход владельца.
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    private static JsonObject ArgsFor(string tool, string sessionId) => tool switch
    {
        "chats_update" => new JsonObject { ["sessionId"] = sessionId, ["name"] = "Новое имя" },
        "chats_archive" => new JsonObject { ["sessionId"] = sessionId, ["archived"] = true },
        "chats_send" => new JsonObject { ["sessionId"] = sessionId, ["text"] = "проверка видимости", ["wait"] = "none" },
        _ => new JsonObject { ["sessionId"] = sessionId },
    };

    private async Task<McpToolCallResult> CallAsync(string tool, JsonObject args)
    {
        _actors.Map[AgentChatId] = _ownerId;
        return await _toolset.CallAsync(tool, args,
            new McpToolCallContext(_ownerId, AgentChatId, AgentChatId), CancellationToken.None);
    }

    private static readonly string[] ChatsTools =
        ["chats_history", "chats_update", "chats_archive", "chats_send", "chats_delete"];

    [Theory]
    [InlineData("chats_history")]
    [InlineData("chats_update")]
    [InlineData("chats_archive")]
    [InlineData("chats_send")]
    [InlineData("chats_delete")]
    public async Task ШирокаяРамкаХодВладельца_ЧужойПриватныйОтказываетсяКакНесуществующий_КаждыйИнструмент(string tool)
    {
        var deniedForeign = await CallAsync(tool, ArgsFor(tool, ForeignPrivateId));
        var deniedMissing = await CallAsync(tool, ArgsFor(tool, MissingId));

        deniedForeign.IsError.Should().BeTrue($"{tool} обязан отказать на чужом приватном чате");
        // Тот же текст, что у несуществующего id: существование не раскрывается, имя не отдаётся
        deniedForeign.Text.Should().Be($"Сессия {ForeignPrivateId} не найдена.",
            $"{tool}: чужой приватный неотличим от несуществующего");
        deniedForeign.Text.Should().NotContain(ForeignPrivateName);
        deniedMissing.Text.Should().Be($"Сессия {MissingId} не найдена.",
            "контроль: отказ несуществующего — та же форма");
    }

    [Theory]
    [InlineData("chats_history")]
    [InlineData("chats_update")]
    [InlineData("chats_archive")]
    [InlineData("chats_send")]
    [InlineData("chats_delete")]
    public async Task ШирокаяРамкаХодВладельца_СвойПриватныйИОбщиеРаботают_КаждыйИнструмент(string tool)
    {
        // chats_send: цели — занятые (Working) чаты, сообщение уходит в очередь без живого
        // хода (сидируются Working — образец MkBusySessionAsync); остальные инструменты —
        // по обычным сидам. Свой приватный владельца и общие чаты проекта — прежнее поведение.
        var targets = tool == "chats_send"
            ? new[] { SendOwnPrivateId, SendOpenId }
            : new[] { OwnPrivateId, MemberOpenId, OwnerOpenId };
        foreach (var id in targets)
        {
            var result = await CallAsync(tool, ArgsFor(tool, id));
            result.IsError.Should().BeFalse($"{tool} на {id}: {result.Text}");
        }
    }
}
