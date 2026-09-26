using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

/// <summary>
/// Тулсет local-media: гейты на каждый вызов (сессия владельца, тумблер, чат проекта, проект на
/// сервере по ProjectCapabilities) и сквозной путь «поставить → дождаться → файл в проекте» на
/// фейковом ComfyUI. Состав постоянный: 11 инструментов, не зависящих от хода.
/// </summary>
public class LocalMediaToolsetTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly FakeComfy _comfy = new();

    public LocalMediaToolsetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "local_media_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private sealed record Env(LocalMediaToolset Toolset, McpToolCallContext Context, Session Session, Project Project,
        SessionManager Sessions, ProjectManager Projects);

    private Env Build(bool enabled = true, bool withService = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:PollIntervalMs"] = "500",
        }).Build();
        var (sessions, projects) = BuildSessionManager(config);
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект картинок", dir, TestUserId, TestUsername);
        var session = sessions.CreateAsync(project.Id, ClaudeMode.Auto).GetAwaiter().GetResult();

        LocalMediaService? media = null;
        if (withService)
        {
            var store = new LocalMediaJobStore(config);
            var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
            media = new LocalMediaService(client, store, new LocalMediaProjectAccess(projects), config,
                NullLogger<LocalMediaService>.Instance);
        }
        var toolset = new LocalMediaToolset(sessions, projects, media);
        return new Env(toolset, new McpToolCallContext(TestUserId, session.Id, session.Id), session, project,
            sessions, projects);
    }

    [Fact]
    public void СвояСессия_ОдиннадцатьИнструментов_ОдинаковыйСостав()
    {
        var env = Build();

        var names = env.Toolset.ToolsFor(env.Context).Select(t => t.Name).ToList();

        names.Should().Equal("local_generate_image", "local_edit_image", "local_face_detail",
            "local_text_to_video", "local_image_to_video", "local_reference_to_video", "local_video_upscale",
            "local_video_inpaint", "local_job_status", "local_jobs_wait", "local_models");
        env.Toolset.ToolsFor(env.Context).Should().BeSameAs(env.Toolset.ToolsFor(env.Context),
            "состав статичный — не зависит ни от хода, ни от вызова");
    }

    [Fact]
    public async Task ЧужаяСессия_НиСостава_НиВызова()
    {
        var env = Build();
        var foreign = new McpToolCallContext("someone-else", env.Session.Id, env.Session.Id);

        env.Toolset.ToolsFor(foreign).Should().BeEmpty();
        var result = await env.Toolset.CallAsync("local_models", new JsonObject(), foreign, default);
        result.IsError.Should().BeTrue();
        _comfy.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Выключено_ЧестныйОтказ(bool enabled, bool withService)
    {
        var env = Build(enabled, withService);

        var result = await env.Toolset.CallAsync("local_generate_image",
            new JsonObject { ["prompt"] = "котик" }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("выключена");
        _comfy.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ЛокальныйПроект_Отказ_ДоComfy()
    {
        var env = Build();
        // Проект на устройстве (ADR-016): серверного диска у него нет
        env.Project.DeviceId = "device-1";

        var result = await env.Toolset.CallAsync("local_generate_image",
            new JsonObject { ["prompt"] = "котик" }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be(ProjectCapabilities.ServerContentOffReason);
        _comfy.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ЧатВнеПроекта_Отказ()
    {
        var env = Build();
        // Чат вне проекта: владелец — у самого чата
        env.Session.OwnerId = TestUserId;
        env.Session.ProjectId = null;

        var result = await env.Toolset.CallAsync("local_generate_image",
            new JsonObject { ["prompt"] = "котик" }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("только в чате проекта");
    }

    [Fact]
    public async Task ЧужойJobId_НеНайден()
    {
        var env = Build();

        var result = await env.Toolset.CallAsync("local_job_status",
            new JsonObject { ["job_id"] = "lm_" + new string('a', 32) }, env.Context, default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("не найдена");
    }

    [Fact]
    public async Task СквознойПуть_Поставить_Дождаться_ФайлВПроекте()
    {
        var env = Build();

        var submitted = await env.Toolset.CallAsync("local_generate_image",
            new JsonObject { ["prompt"] = "котик", ["aspect"] = "16:9", ["seed"] = 7 }, env.Context, default);
        submitted.IsError.Should().BeFalse(submitted.Text);
        var queued = JsonNode.Parse(submitted.Text)!.AsObject();
        queued["status"]!.GetValue<string>().Should().Be("queued");
        queued["seed"]!.GetValue<long>().Should().Be(7);
        queued["note"]!.GetValue<string>().Should().Contain("local_jobs_wait");
        var jobId = queued["job_id"]!.GetValue<string>();

        var sent = _comfy.Prompts.Single()["prompt"]!;
        sent["lat"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(1664);

        _comfy.Complete(_comfy.Pending.Single(), ($"{jobId}_00001_.png", LocalMediaTestImages.Png(1664, 928)));
        var waited = await env.Toolset.CallAsync("local_jobs_wait",
            new JsonObject { ["job_ids"] = new JsonArray(jobId) }, env.Context, default);

        var body = JsonNode.Parse(waited.Text)!.AsObject();
        body["all_done"]!.GetValue<bool>().Should().BeTrue();
        var image = body["jobs"]![0]!["images"]![0]!.AsObject();
        var path = image["path"]!.GetValue<string>();
        path.Should().StartWith(".cc-attachments/local-media/").And.EndWith($"{jobId}-1.png");
        image["url"]!.GetValue<string>().Should().Be(
            $"/api/projects/{env.Project.Id}/files/stream?path={Uri.EscapeDataString(path)}");
        image["content_type"]!.GetValue<string>().Should().Be("image/png");
        image["width"]!.GetValue<int>().Should().Be(1664);
        File.Exists(Path.Combine(env.Project.RootPath, path)).Should().BeTrue();
    }

    [Fact]
    public async Task Модели_ОчередьИОперации()
    {
        var env = Build();
        _comfy.Running.Add("чужой");

        var result = await env.Toolset.CallAsync("local_models", new JsonObject(), env.Context, default);

        var json = JsonNode.Parse(result.Text)!.AsObject();
        json["comfy_available"]!.GetValue<bool>().Should().BeTrue();
        json["queue_length"]!.GetValue<int>().Should().Be(1);
        // Каждая операция генерации описана в local_models
        var generating = env.Toolset.ToolsFor(env.Context).Select(t => t.Name)
            .Where(n => n is not ("local_job_status" or "local_jobs_wait" or "local_models"));
        json["operations"]!.AsArray().Select(o => o!["tool"]!.GetValue<string>()).Should().Equal(generating);
    }

    [Fact]
    public void КонтекстХода_ТолькоПриТумблереИВПроектеНаСервере()
    {
        var on = Build(enabled: true);
        on.Sessions.BuildLocalMediaContext(TestUserId, on.Project.Id, persona: null).Should().NotBeNull();
        on.Sessions.BuildLocalMediaContext(TestUserId, projectId: null, persona: null)
            .Should().BeNull("вне проекта результат некуда сохранить");
        on.Sessions.BuildLocalMediaContext(TestUserId, on.Project.Id,
                new Persona { Access = PersonaAccess.ReadOnly })
            .Should().BeNull("сервер пишет файлы — ReadOnly-персоне не положен, как higgsfield");
        on.Project.DeviceId = "device-1";
        on.Sessions.BuildLocalMediaContext(TestUserId, on.Project.Id, persona: null)
            .Should().BeNull("у локального проекта нет серверного диска");

        var off = Build(enabled: false);
        off.Sessions.BuildLocalMediaContext(TestUserId, off.Project.Id, persona: null)
            .Should().BeNull("выключенный LocalMedia:Enabled — узла в конфиге хода нет");
    }

    // Минимальный граф SessionManager — как в WebSearchToolsetTests: тулсету нужен
    // только резолв «хвост маршрута → чат владельца»
    private static (SessionManager Sessions, ProjectManager Projects) BuildSessionManager(IConfiguration config)
    {
        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var history = new ChatHistoryService(config);
        var broadcaster = new TestSessionBroadcaster();
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster), projectManager);
    }
}
