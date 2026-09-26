using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Mcp;

// MCP-сервер редактора для агента (ADR-018 §2, §7): запуск сразу тем же путём, что у человека,
// трата на владельца чата с инициатором «агент», потолок запусков за ход, отказ на
// делегированном ходу, изоляция чужого чата и чужой задачи
public class ImageEditorToolsetTests : IDisposable
{
    private const string Owner = "user-b";
    private const string Stranger = "user-a";
    private const string ChatId = "chat-b";
    private const string ProjectId = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly MemorySpendStore _spend = new();
    private readonly FakeTurnBus _bus = new();
    private readonly Mock<IDelegatedTurnGate> _turnGate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly HashSet<string> _flagOn = [Owner, Stranger];
    private readonly List<ImageEditJobService> _services = [];
    private ImageEditJobService _jobs = null!;
    private ImageChatStateStore _states = null!;

    public ImageEditorToolsetTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), TestImages.Png(4, 4));
        AddChat(ChatId, Owner, ProjectId, "images/hero.png");
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private void AddChat(string id, string owner, string projectId, string? imagePath) =>
        _sessions[id] = new Session
        {
            Id = id,
            OwnerId = owner,
            ProjectId = projectId,
            ImageChat = imagePath is null ? null : new SessionImageChat { CurrentPath = imagePath },
        };

    private ImageEditorToolset Toolset(IImageEditor[]? editors = null, bool agentLaunch = true, bool withGate = true)
    {
        editors ??= [HiggsfieldImageEditorTests.Create().Editor];
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        _jobs = new ImageEditJobService(editors, workspace, NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster);
        _services.Add(_jobs);
        _states = new ImageChatStateStore(workspace);

        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(It.IsAny<string>())).Returns((string id) => _sessions.GetValueOrDefault(id));
        var launcher = new ImageEditLaunchAssembler(editors, _states, NullLogger<ImageEditLaunchAssembler>.Instance,
            _jobs, new SkiaImageRaster(), directory.Object, broadcaster: _broadcaster);

        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(It.IsAny<string>(), FeatureFlagKeys.ImageEditor))
            .Returns((string user, string _) => _flagOn.Contains(user));
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });

        var config = TestImages.Config((ImageEditorToolset.AgentLaunchKey, agentLaunch ? "true" : "false"));
        return new ImageEditorToolset(accessor.Object, flags.Object, projects.Object, editors, _states, launcher,
            withGate ? _turnGate.Object : null, _jobs, events: _bus, config: config);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string tail = ChatId) => new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(ImageEditorToolset toolset, string tool, JsonObject? args = null,
        McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    private static JsonObject Parse(McpToolCallResult result) => JsonNode.Parse(result.Text)!.AsObject();

    private IReadOnlyList<ImageEditJobDto> JobsOfChat() =>
        _states.Get(Owner, ChatId).Events
            .Select(e => e.JobId).OfType<string>().Distinct()
            .Select(id => _jobs.Get(Owner, ProjectId, id)).OfType<ImageEditJobDto>().ToList();

    private async Task<ImageEditJobDto> WaitDone(string jobId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = _jobs.Get(Owner, ProjectId, jobId)!;
            if (job.Status is ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled)
                return job;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    [Fact]
    public async Task Агент_запускает_генерацию_сразу_и_меняет_состояние_редактора()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["prompt"] = "убрать провод", ["count"] = 2 });

        result.IsError.Should().BeFalse(result.Text);
        var body = Parse(result);
        var jobId = body["jobId"]!.GetValue<string>();
        body["quote"]!["provider"]!.GetValue<string>().Should().Be("higgsfield");
        body["changes"]!.AsArray().Select(c => c!["field"]!.GetValue<string>()).Should().Contain(["prompt", "count"]);

        var job = _jobs.Get(Owner, ProjectId, jobId)!;
        job.Initiator.Should().Be(ImageEditInitiator.Agent);
        job.ChatSessionId.Should().Be(ChatId);
        job.Count.Should().Be(2);

        var state = _states.Get(Owner, ChatId);
        state.Prompt.Should().Be("убрать провод");
        state.PromptAuthor.Should().Be(ImageEditInitiator.Agent);
        state.Count.Should().Be(2);
        state.Events.Should().Contain(e => e.JobId == jobId && e.Kind == ImageChatEventKinds.Launched);
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageChatStateMessage>()
            .Should().Contain(m => m.ChangedBy == ImageEditInitiator.Agent && m.SessionId == ChatId
                && m.Changes.Any(ch => ch.Field == "prompt"));
    }

    // ADR-018 §7: агент в чате владельца B запускает Higgsfield → одна запись траты на B,
    // инициатор — агент, SessionId — чат. Не на персону и не на админа Higgsfield
    [Fact]
    public async Task Трата_агента_ложится_на_владельца_чата_с_инициатором_агент()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" });
        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());

        var record = _spend.Records.Should().ContainSingle().Subject;
        record.OwnerId.Should().Be(Owner);
        record.Initiator.Should().Be(SpendInitiators.Agent);
        record.SessionId.Should().Be(ChatId);
        record.ProjectId.Should().Be(ProjectId);
    }

    [Fact]
    public async Task Третий_запуск_за_ход_отказ_без_задачи_а_после_хода_счётчик_сброшен()
    {
        var toolset = Toolset();
        var args = () => new JsonObject { ["prompt"] = "вариант" };

        (await Call(toolset, ImageEditorToolset.ToolGenerate, args())).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, args())).IsError.Should().BeFalse();
        var third = await Call(toolset, ImageEditorToolset.ToolGenerate, args());

        third.IsError.Should().BeTrue();
        third.Text.Should().Contain("не больше 2");
        JobsOfChat().Should().HaveCount(2, "третий вызов задачи не создаёт");

        // Ход другого чата счётчик этого не трогает, ход своего — сбрасывает
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("other-chat", Owner, 1, 0), "success"));
        (await Call(toolset, ImageEditorToolset.ToolGenerate, args())).IsError.Should().BeTrue();
        await _bus.PublishAsync(new TurnCompleted(new TurnContext(ChatId, Owner, 1, 0), "success"));
        (await Call(toolset, ImageEditorToolset.ToolGenerate, args())).IsError.Should().BeFalse();
        JobsOfChat().Should().HaveCount(3);
    }

    [Fact]
    public async Task Делегированный_ход_отказ_задачи_нет_и_лимит_не_расходуется()
    {
        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>()))
            .Returns("Запуск генерации недоступно на делегированном ходу");
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("делегированном ходу");
        JobsOfChat().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
        _states.Get(Owner, ChatId).Prompt.Should().BeEmpty("отказ не меняет состояние редактора");

        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>())).Returns((string?)null);
        (await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "a" })).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "b" })).IsError
            .Should().BeFalse("отказанный вызов не съел место в потолке хода");
    }

    [Fact]
    public async Task Без_гейта_хода_запуск_запрещён_по_построению()
    {
        var toolset = Toolset(withGate: false);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" });

        result.IsError.Should().BeTrue();
        JobsOfChat().Should().BeEmpty();
    }

    [Fact]
    public async Task Чужой_чат_в_хвосте_пустой_состав_и_отказ_без_задачи()
    {
        var toolset = Toolset();

        toolset.ToolsFor(Ctx()).Should().HaveCount(4);
        toolset.ToolsFor(Ctx(owner: Stranger)).Should().BeEmpty("чат владельца B чужаку не виден");
        toolset.ToolsFor(Ctx(tail: "missing")).Should().BeEmpty();
        toolset.ToolsFor(Ctx(tail: "../chat-b")).Should().BeEmpty();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" },
            Ctx(owner: Stranger));
        result.IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolState, ctx: Ctx(owner: Stranger))).IsError.Should().BeTrue();
        JobsOfChat().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
    }

    [Fact]
    public void Обычный_чат_и_выключенный_флаг_пустой_состав()
    {
        AddChat("plain", Owner, ProjectId, imagePath: null);
        var toolset = Toolset();

        toolset.ToolsFor(Ctx(tail: "plain")).Should().BeEmpty("сервер есть только в чате картинки");
        _flagOn.Remove(Owner);
        toolset.ToolsFor(Ctx()).Should().BeEmpty("флаг image-editor владельца выключен");
    }

    [Fact]
    public async Task Отмена_задачи_другого_чата_неотличима_от_несуществующей()
    {
        AddChat("chat-b2", Owner, ProjectId, "images/hero.png");
        var toolset = Toolset();
        var started = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" });
        var jobId = Parse(started)["jobId"]!.GetValue<string>();

        var foreign = await Call(toolset, ImageEditorToolset.ToolCancel, new JsonObject { ["jobId"] = jobId },
            Ctx(tail: "chat-b2"));
        var missing = await Call(toolset, ImageEditorToolset.ToolCancel, new JsonObject { ["jobId"] = "nope" });

        foreign.IsError.Should().BeTrue();
        foreign.Text.Should().Be(missing.Text);
        _jobs.Get(Owner, ProjectId, jobId)!.Status.Should().NotBe(ImageEditJobStatus.Cancelled);
    }

    [Fact]
    public async Task Образец_через_символическую_ссылку_отвергается_и_из_тулсета()
    {
        var outside = Path.Combine(_dir, "outside.png");
        File.WriteAllBytes(outside, TestImages.Png(4, 4));
        var link = Path.Combine(_root, "images", "ref.png");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows без прав — проверка идёт в CI на Linux
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject
        {
            ["prompt"] = "как на образце",
            ["references"] = new JsonArray { new JsonObject { ["path"] = "images/ref.png", ["role"] = "style" } },
        });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("символическую ссылку");
        JobsOfChat().Should().BeEmpty();
        _states.Get(Owner, ChatId).References.Should().BeEmpty("отвергнутый запуск не пишет образцы в состояние");
    }

    // Отказ поставщика — котировка соседа в результате; второго вызова драйвера нет
    [Fact]
    public async Task Недоступный_поставщик_даёт_retryQuote_соседа_без_запуска()
    {
        var fal = new FakeImageEditor("fal", enabled: true, models: FakeImageEditor.Model("fal-edit"));
        var toolset = Toolset([fal, new FakeImageEditor("higgsfield", enabled: false)]);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["prompt"] = "фон", ["provider"] = "higgsfield" });

        result.IsError.Should().BeTrue();
        var body = Parse(result);
        body["code"]!.GetValue<string>().Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        body["retryQuote"]!["provider"]!.GetValue<string>().Should().Be("fal");
        JobsOfChat().Should().BeEmpty("RunAsync заглушки бросает — запуска не было");
    }

    [Fact]
    public async Task Без_запуска_агентом_в_составе_только_состояние_и_предложение()
    {
        var toolset = Toolset(agentLaunch: false);

        toolset.ToolsFor(Ctx()).Select(t => t.Name).Should()
            .BeEquivalentTo([ImageEditorToolset.ToolState, ImageEditorToolset.ToolSuggestPrompt]);
        (await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" })).IsError.Should().BeTrue();
        JobsOfChat().Should().BeEmpty();
    }

    [Fact]
    public async Task Предложение_промпта_ничего_не_запускает_и_не_меняет()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolSuggestPrompt,
            new JsonObject { ["prompt"] = "закатное небо", ["count"] = 3 });

        result.IsError.Should().BeFalse();
        Parse(result)["prompt"]!.GetValue<string>().Should().Be("закатное небо");
        _states.Get(Owner, ChatId).Revision.Should().Be(0);
        _broadcaster.ToOwnerCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Состояние_отдаёт_файл_промпт_и_поставщиков()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolState);

        result.IsError.Should().BeFalse(result.Text);
        var body = Parse(result);
        body["file"]!["path"]!.GetValue<string>().Should().Be("images/hero.png");
        body["file"]!["width"]!.GetValue<int>().Should().Be(4);
        body["provider"]!.GetValue<string>().Should().Be("higgsfield");
        body["providers"]!.AsArray().Should().NotBeEmpty();
    }

    // Имена в AutoAllowTools чата картинки (Core) обязаны совпадать со схемами тулсета
    [Fact]
    public void Автоматически_разрешённые_инструменты_есть_в_схемах()
    {
        var names = Toolset().ToolsFor(Ctx()).Select(t => $"mcp__{ImageEditorToolset.ServerName}__{t.Name}").ToList();
        names.Should().Contain(ImageChatDefaults.AutoAllowTools);
    }

    private sealed class FakeTurnBus : ITurnEventBus
    {
        private readonly List<Func<TurnCompleted, Task>> _completed = [];

        public void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification
        {
            if (handler is Func<TurnCompleted, Task> h) _completed.Add(h);
        }

        public void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter { }

        public Task PublishAsync<T>(T e) where T : ITurnNotification =>
            e is TurnCompleted c ? Task.WhenAll(_completed.Select(h => h(c))) : Task.CompletedTask;

        public Task<T> ApplyAsync<T>(T e) where T : ITurnFilter => Task.FromResult(e);
    }
}
