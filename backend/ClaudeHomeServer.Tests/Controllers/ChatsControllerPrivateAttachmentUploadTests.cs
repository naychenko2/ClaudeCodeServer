using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
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
/// S2d-6b-1 (разрез 4bc3d04f §1 путь 19, перепостановка после блокера): ChatsController.Upload
/// для ПРИВАТНОГО проектного чата пишет вложение в закрытое поддерево
/// .cc-attachments/private/{автор}/… — принадлежность читается из пути (PrivateAttachments),
/// её закрывают на чтение в 6b-2 (FilesController) и 6b-3 (WorkspaceToolset, WebDAV).
/// Автор — только из JWT (PrivateToUserId есть вызывающий); общий чат и чат вне проекта —
/// прежняя раскладка {AttachmentsDir}/{guid}/{имя}: формат ответа Upload для потребителей
/// (фронт, CLI) не меняется, добавляется только средний сегмент.
/// Гейт владения/видимости (GetOwnedVisible) не тронут: чужой приватный — 404, как на
/// несуществующем (снимок — GetOwnedVisibleTests).
/// Стенд бесхостовый (образец — PrivateChatListVisibilityTests/GetOwnedVisibleTests):
/// живой SessionManager с сидированным sessions.json, настоящий FileService и корень проекта,
/// ClaimsPrincipal вызывающего.
/// </summary>
public sealed class ChatsControllerPrivateAttachmentUploadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectRoot;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly FileService _files;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;

    // Сид: приватный владельцу (он же арендатор), приватный участнику, общий
    private const string OwnPrivateId = "priv-own";
    private const string ForeignPrivateId = "priv-foreign";
    private const string OpenChatId = "open-chat";

    public ChatsControllerPrivateAttachmentUploadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "priv_attach_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2d6b-editor", "pwd", "user").Id;
        _users.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        _projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", _projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        File.WriteAllText(Path.Combine(_tempDir, "sessions.json"),
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new Session { Id = OwnPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                    PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
                new Session { Id = ForeignPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId,
                    PrivateToUserId = _editorId, Status = SessionStatus.Finished },
                new Session { Id = OpenChatId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                    Status = SessionStatus.Finished },
            }));

        _sessions = CreateSessionManager(appSettings);
        _files = new FileService(new GitService(TestLauncherFactory.Instance), _projects,
            NullLogger<FileService>.Instance);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

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
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, _projects, wkStore, knowledge,
            new SkillsService(), _users, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, new ChatHistoryService(_config), _config, adapters,
            new FalCostService(new Mock<IHttpClientFactory>().Object, _config), new UsageService(_config),
            appSettings, _users, jwt, server.Object, llmProviders, new FeatureFlagService(_users),
            personas, bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, _files, null!, null!, null!, null!, _users, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        },
    };

    private static IFormFile FileNamed(string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var f = new Mock<IFormFile>();
        f.Setup(x => x.FileName).Returns(name);
        f.Setup(x => x.Length).Returns(bytes.Length);
        // в этой версии ASP.NET у IFormFile.CopyToAsync один метод с обоими параметрами
        f.Setup(x => x.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((s, _) => s.Write(bytes))
            .Returns(Task.CompletedTask);
        return f.Object;
    }

    private static string PathOf(IActionResult result)
    {
        var body = ((OkObjectResult)result).Value!;
        return (string)((dynamic)body).path;
    }

    private string AbsOf(string rel) =>
        Path.Combine(_projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Upload_ПриватныйЧатАвтора_ПишетВЗакрытоеПоддеревоPrivate()
    {
        var c = ChatsAs(_ownerId);

        var rel = PathOf(await c.Upload(OwnPrivateId, FileNamed("отчёт.txt", "содержимое")));

        rel.Should().MatchRegex($@"^\.cc-attachments/private/{_ownerId}/[0-9a-f]{{32}}/отчёт\.txt$",
            "автор своего приватного чата: путь под private/{PrivateToUserId}, guid и имя — как в общей раскладке");
        File.Exists(AbsOf(rel)).Should().BeTrue(
            "путь, который вернул Upload, обязан открываться в рабочей папке чата");
    }

    [Fact]
    public async Task Upload_ОбщийЧат_ПрежняяРаскладкаБезPrivate()
    {
        var c = ChatsAs(_ownerId);

        var rel = PathOf(await c.Upload(OpenChatId, FileNamed("отчёт.txt", "содержимое")));

        rel.Should().MatchRegex(@"^\.cc-attachments/[0-9a-f]{32}/отчёт\.txt$",
            "общий чат — раскладка без изменений: формат ответа для фронта и CLI тот же");
        File.Exists(AbsOf(rel)).Should().BeTrue();
    }

    [Fact]
    public async Task Upload_ЧужойПриватный_404КакНаНесуществующем_ГейтИРаскладкаНеТронуты()
    {
        var c = ChatsAs(_ownerId);

        (await c.Upload(ForeignPrivateId, FileNamed("отчёт.txt", "содержимое")))
            .Should().BeOfType<NotFoundResult>();

        Directory.Exists(Path.Combine(_projectRoot, ".cc-attachments")).Should().BeFalse(
            "отказ не оставил следа в проекте");
    }
}
