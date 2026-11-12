using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Доработка S2d-4a по вердикту R-S2d-4a (находка 1): POST /api/projects/{id}/files/changed-by —
/// панель «Изменения» (бейдж в строке файла и список «Также меняли») — человеческая выдача:
/// чата, которого вызывающий не видит, в ней нет ни id, ни названием. Раньше её строил
/// ProjectFileSessionsIndex поверх машинной GetByProject и отдавал имя чужого приватного чата
/// и владельцу проекта, и другому участнику.
///
/// Проверяется на настоящем FilesController.ChangedBy с ClaimsPrincipal вызывающего, с живым
/// ProjectFileSessionsIndex и живыми history.json чатов: посев путей — StoredFileChangedMessage
/// через ChatHistoryService (образец — ProjectFileSessionsIndexTests), посев чатов и их
/// приватности — sessions.json (образец — PrivateChatListVisibilityTests этой же фичи). Тело
/// ответа читается из OkObjectResult с той же именовательной политикой, что на wire, поэтому
/// проверяются и sessionId, и name — то, что реально уходит наружу.
///
/// Машинная сторона того же индекса (GetSessionPathsAsync → CommitAttributionService) не тронута
/// и здесь не проверяется: чужой приватный чат в атрибуции коммита живёт законно. HTTP-слой этой
/// же ручки (роутинг, [ProjectScope], ETag) — хост-набор FilesControllerChangedByTests, он на гейте.
/// </summary>
public sealed class ChangedByVisibilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly ProjectFileSessionsIndex _index;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;

    // Сид: чат участника, приватный участнику; открытый чат участника; чат владельца, приватный
    // владельцу; общий чат владельца. Все четверо меняли SharedPath; PrivateOnlyPath менял ТОЛЬКО
    // чужой приватный чат.
    private const string ForeignPrivateId = "priv-foreign";
    private const string MemberOpenId = "member-open";
    private const string OwnPrivateId = "priv-own";
    private const string OwnerOpenId = "owner-open";
    private const string ForeignPrivateName = "Приватный чат участника";
    private const string OwnPrivateName = "Приватный чат владельца";
    private const string SharedPath = "src/shared.ts";
    private const string PrivateOnlyPath = "src/private-only.ts";

    public ChangedByVisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "changed_by_vis_" + Guid.NewGuid().ToString("N"));
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
        _editorId = _users.Add("cby-editor", "pwd", "user").Id;
        _users.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        _personas = new PersonaManager(_config);
        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        // Чаты с ClaudeSessionId — иначе их истории негде лежать и индекс их не видит (см.
        // ProjectFileSessionsIndex.LoadProjectSessionPathsAsync)
        WriteSessions(Path.Combine(_tempDir, "sessions.json"), [
            new Session { Id = ForeignPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId,
                PrivateToUserId = _editorId, ClaudeSessionId = "cby-foreign", Name = ForeignPrivateName,
                Status = SessionStatus.Finished },
            new Session { Id = MemberOpenId, ProjectId = _projectId, CreatedByUserId = _editorId,
                ClaudeSessionId = "cby-member", Name = "Открытый чат участника",
                Status = SessionStatus.Finished },
            new Session { Id = OwnPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                PrivateToUserId = _ownerId, ClaudeSessionId = "cby-own", Name = OwnPrivateName,
                Status = SessionStatus.Finished },
            new Session { Id = OwnerOpenId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                ClaudeSessionId = "cby-open", Name = "Общий чат владельца",
                Status = SessionStatus.Finished },
        ]);

        _sessions = CreateSessionManager(appSettings);

        // Лента чата — один файл на чат (SaveAsync заменяет его целиком), поэтому все пути этого
        // чата пишутся одним списком: два SaveAsync на один csid — второй съедает первый
        var history = new ChatHistoryService(_config);
        foreach (var (csid, paths) in new[]
        {
            ("cby-foreign", new[] { SharedPath, PrivateOnlyPath }),
            ("cby-member", new[] { SharedPath }),
            ("cby-own", new[] { SharedPath }),
            ("cby-open", new[] { SharedPath }),
        })
            history.SaveAsync(csid, [.. paths.Select(p => (StoredMessage)new StoredFileChangedMessage(p, 1, 0))])
                .GetAwaiter().GetResult();

        _index = new ProjectFileSessionsIndex(_sessions, history, _projects);
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

    private FilesController FilesAs(string userId) =>
        new(null!, _projects, null!, null!, _config, null!, NullLogger<FilesController>.Instance,
            null!, _index, null!, null!, null!, null!, _sessions)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
                },
            },
        };

    // Тело ответа — с той же именовательной политикой, что на wire (camelCase): иначе
    // GetProperty промахнётся по регистру, а не по составу
    private static JsonElement BodyOf(Task<IActionResult> pending)
    {
        var value = ((OkObjectResult)pending.GetAwaiter().GetResult()).Value!;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;
    }

    private static List<string?> IdsOf(JsonElement body, string path) =>
        body.GetProperty("files").GetProperty(path).EnumerateArray()
            .Select(e => e.GetProperty("sessionId").GetString()).ToList();

    private static List<string?> NamesOf(JsonElement body) =>
        body.GetProperty("files").EnumerateObject()
            .SelectMany(f => f.Value.EnumerateArray())
            .Select(e => e.GetProperty("name").GetString()).ToList();

    [Fact]
    public void ПанельИзменений_ЧужойПриватныйЧат_НиIdНиНазвания_АвторСвойИОбщийКакПрежде()
    {
        var asOwner = BodyOf(FilesAs(_ownerId).ChangedBy(_projectId, new ChangedByRequest([SharedPath])));

        IdsOf(asOwner, SharedPath).Should().NotContain(ForeignPrivateId,
            "панель «Изменения» строится поверх индекса файлов, но остаётся человеческой выдачей: " +
            "чужой приватный неотличим от несуществующего, как в списках чатов");
        NamesOf(asOwner).Should().NotContain(ForeignPrivateName,
            "название чужого приватного чата — та же утечка, что и id: в панели его нет");
        IdsOf(asOwner, SharedPath).Should().Contain(new[] { MemberOpenId, OwnPrivateId, OwnerOpenId },
            "чужой открытый чат, свой приватный и общий чат — прежнее поведение");

        // Автор своего приватного видит его как раньше (приватность — его поле, не чужой запрет);
        // приватный владельцу ему не виден — та же точка с другой стороны
        var asAuthor = BodyOf(FilesAs(_editorId).ChangedBy(_projectId, new ChangedByRequest([SharedPath])));
        IdsOf(asAuthor, SharedPath).Should().Contain(new[] { ForeignPrivateId, MemberOpenId, OwnerOpenId });
        NamesOf(asAuthor).Should().Contain(ForeignPrivateName);
        IdsOf(asAuthor, SharedPath).Should().NotContain(OwnPrivateId);
        NamesOf(asAuthor).Should().NotContain(OwnPrivateName);
    }

    // Путь, который менял только невидимый чату человека чат: для вызывающего ключа нет — это то
    // же «никто не менял», что и пустой список у соседей. У автора путь остаётся как раньше.
    [Fact]
    public void ПанельИзменений_ПутьТолькоУНевидимогоЧата_БезКлюча_УАвтораКакПрежде()
    {
        var asOwner = BodyOf(FilesAs(_ownerId).ChangedBy(_projectId, new ChangedByRequest([PrivateOnlyPath])));
        asOwner.GetProperty("files").TryGetProperty(PrivateOnlyPath, out _).Should().BeFalse();

        var asAuthor = BodyOf(FilesAs(_editorId).ChangedBy(_projectId, new ChangedByRequest([PrivateOnlyPath])));
        asAuthor.GetProperty("files").TryGetProperty(PrivateOnlyPath, out var entries).Should().BeTrue();
        entries[0].GetProperty("sessionId").GetString().Should().Be(ForeignPrivateId);
        entries[0].GetProperty("name").GetString().Should().Be(ForeignPrivateName);
    }
}
