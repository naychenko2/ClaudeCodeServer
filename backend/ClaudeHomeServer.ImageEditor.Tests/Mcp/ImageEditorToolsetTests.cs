using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Threads;
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

// MCP-сервер редактора для агента любого чата проекта (ADR-019 §4, решение Андрея 1): агент
// видит нити, берёт картинку в работу и заводит черновик — всегда тихой строкой в ленте; запуск
// только в нить по обязательному threadId тем же путём, что у человека; взять вариант,
// откатиться и сохранить агент не может. Плюс сторожа v2: трата на владельца чата с
// инициатором «агент», потолок запусков за ход, отказ на делегированном ходу, изоляция чужого
// чата и чужой задачи, состав не зависит от фокуса и нитей.
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
    private readonly RecordingFeed _feed = new();
    private readonly Mock<IDelegatedTurnGate> _turnGate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly HashSet<string> _flagOn = [Owner, Stranger];
    private readonly List<ImageEditJobService> _services = [];
    private readonly ImageThreadStore _store;
    private ImageEditJobService _jobs = null!;

    public ImageEditorToolsetTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        Directory.CreateDirectory(Path.Combine(_root, "art"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), TestImages.Png(4, 4));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "не картинка");
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        AddChat(ChatId, Owner, ProjectId);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private void AddChat(string id, string owner, string? projectId) =>
        _sessions[id] = new Session { Id = id, OwnerId = owner, ProjectId = projectId };

    private ImageEditorToolset Toolset(IImageEditor[]? editors = null, bool agentLaunch = true, bool withGate = true)
    {
        editors ??= [HiggsfieldImageEditorTests.Create().Editor];
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        _jobs = new ImageEditJobService(editors, workspace, NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster);
        _services.Add(_jobs);

        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(It.IsAny<string>())).Returns((string id) => _sessions.GetValueOrDefault(id));
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, _feed, _broadcaster);
        var launcher = new ImageEditLaunchAssembler(editors, _jobs, new SkiaImageRaster(), threads);

        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(It.IsAny<string>(), FeatureFlagKeys.ImageEditor))
            .Returns((string user, string _) => _flagOn.Contains(user));
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });

        var config = TestImages.Config((ImageEditorToolset.AgentLaunchKey, agentLaunch ? "true" : "false"));
        return new ImageEditorToolset(accessor.Object, flags.Object, projects.Object, editors, threads, launcher,
            withGate ? _turnGate.Object : null, _jobs, events: _bus, config: config);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string tail = ChatId) => new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(ImageEditorToolset toolset, string tool, JsonObject? args = null,
        McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    private static JsonObject Parse(McpToolCallResult result) => JsonNode.Parse(result.Text)!.AsObject();

    // Картинка чата: нить по файлу, как её завёл бы человек из дерева
    private string Thread(string file = "images/hero.png", string chat = ChatId) =>
        _store.Open(Owner, chat, file, null, _store.Get(Owner, chat).Revision).Thread!.Id;

    private string Draft(string folder = "art") =>
        _store.Open(Owner, ChatId, null, folder, _store.Get(Owner, ChatId).Revision).Thread!.Id;

    private JsonObject Gen(string threadId, string prompt = "фон") => new() { ["threadId"] = threadId, ["prompt"] = prompt };

    private IReadOnlyList<ImageEditJobDto> JobsOfChat() =>
        _store.Get(Owner, ChatId).Events
            .Select(e => e.JobId).OfType<string>().Distinct()
            .Select(id => _jobs.Get(Owner, ProjectId, id)).OfType<ImageEditJobDto>().ToList();

    private IEnumerable<StoredModuleRecord> Lines(string recordType) =>
        _feed.Records.Where(r => r.SessionId == ChatId && r.Record.RecordType == recordType).Select(r => r.Record);

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

    // ── Состав: решение Андрея 1 ──────────────────────────────────────────────

    [Fact]
    public void Инструментов_взять_откатить_и_сохранить_у_агента_нет()
    {
        var names = Toolset().ToolsFor(Ctx()).Select(t => t.Name).ToList();

        names.Should().BeEquivalentTo([
            ImageEditorToolset.ToolState, ImageEditorToolset.ToolFocus, ImageEditorToolset.ToolNew,
            ImageEditorToolset.ToolGenerate, ImageEditorToolset.ToolCancel, ImageEditorToolset.ToolSuggestPrompt]);
        names.Should().NotContain(n => n.Contains("take") || n.Contains("rollback") || n.Contains("undo")
            || n.Contains("save") || n.Contains("dismiss") || n.Contains("remove"),
            "что становится шагом и файлом проекта, решает только человек");
    }

    [Fact]
    public void В_схеме_image_generate_threadId_обязателен()
    {
        var generate = Toolset().ToolsFor(Ctx()).Single(t => t.Name == ImageEditorToolset.ToolGenerate);

        generate.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("threadId");
    }

    [Fact]
    public async Task image_generate_без_threadId_отказ_без_задачи_и_лимит_цел()
    {
        var toolset = Toolset();
        var thread = Thread();
        _store.SetFocus(Owner, ChatId, thread, _store.Get(Owner, ChatId).Revision);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject { ["prompt"] = "фон" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("threadId", "запуск «по текущему фокусу» ушёл бы не на ту картинку");
        _spend.Records.Should().BeEmpty();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "a"))).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "b"))).IsError
            .Should().BeFalse("отказ без threadId не съел место в потолке хода");
    }

    [Fact]
    public async Task image_generate_с_чужой_или_несуществующей_нитью_отказ()
    {
        AddChat("chat-b2", Owner, ProjectId);
        var foreign = Thread(chat: "chat-b2");
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(foreign))).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen("nope"))).IsError.Should().BeTrue();
        _spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Без_запуска_агентом_в_составе_чтение_выбор_и_предложение()
    {
        var toolset = Toolset(agentLaunch: false);

        toolset.ToolsFor(Ctx()).Select(t => t.Name).Should().BeEquivalentTo([
            ImageEditorToolset.ToolState, ImageEditorToolset.ToolFocus, ImageEditorToolset.ToolNew,
            ImageEditorToolset.ToolSuggestPrompt]);
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(Thread()))).IsError.Should().BeTrue();
        _spend.Records.Should().BeEmpty();
    }

    // Состав tools/list входит в сигнатуру запуска CLI: смена фокуса или новая нить не должны его менять
    [Fact]
    public async Task Состав_не_зависит_от_фокуса_и_нитей()
    {
        var toolset = Toolset();
        var empty = toolset.ToolsFor(Ctx()).Select(t => t.Name).ToList();

        await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "images/hero.png" });
        var focused = toolset.ToolsFor(Ctx()).Select(t => t.Name).ToList();
        await Call(toolset, ImageEditorToolset.ToolFocus);
        var unfocused = toolset.ToolsFor(Ctx()).Select(t => t.Name).ToList();

        focused.Should().Equal(empty);
        unfocused.Should().Equal(empty);
    }

    [Fact]
    public void Чужой_чат_чат_вне_проекта_и_выключенный_флаг_пустой_состав()
    {
        AddChat("outside", Owner, projectId: null);
        var toolset = Toolset();

        toolset.ToolsFor(Ctx()).Should().HaveCount(6, "сервер есть в любом чате проекта");
        toolset.ToolsFor(Ctx(owner: Stranger)).Should().BeEmpty("чат владельца B чужаку не виден");
        toolset.ToolsFor(Ctx(tail: "missing")).Should().BeEmpty();
        toolset.ToolsFor(Ctx(tail: "../chat-b")).Should().BeEmpty();
        toolset.ToolsFor(Ctx(tail: "outside")).Should().BeEmpty("чат вне проекта картинок не имеет");
        _flagOn.Remove(Owner);
        toolset.ToolsFor(Ctx()).Should().BeEmpty("флаг image-editor владельца выключен");
    }

    // ── Выбор картинки агентом: всегда тихой строкой ──────────────────────────

    [Fact]
    public async Task image_focus_по_файлу_берёт_картинку_в_работу_тихой_строкой_и_якорем()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "images/hero.png" });

        result.IsError.Should().BeFalse(result.Text);
        var state = _store.Get(Owner, ChatId);
        var thread = state.Threads.Should().ContainSingle().Subject;
        state.Focus.Should().Be(thread.Id);
        thread.File.Should().Be("images/hero.png");
        Lines(ImageThreadService.RecordTypes.Focus).Should().ContainSingle()
            .Which.Fallback.Should().Be("Claude взял в работу: images/hero.png");
        Lines(ImageThreadService.RecordTypes.Thread).Should().ContainSingle("новая нить — её карточка в ленте");
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageThreadChangedMessage>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task image_focus_по_threadId_и_снятие_выбора_тоже_видны_в_ленте()
    {
        var thread = Thread();
        _store.SetFocus(Owner, ChatId, null, _store.Get(Owner, ChatId).Revision);
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["threadId"] = thread })).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["threadId"] = thread })).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolFocus)).IsError.Should().BeFalse();

        Lines(ImageThreadService.RecordTypes.Focus).Select(r => r.Fallback).Should().Equal(
            "Claude взял в работу: images/hero.png",
            "Claude снял выбор: images/hero.png");
        _store.Get(Owner, ChatId).Focus.Should().BeNull();
        Lines(ImageThreadService.RecordTypes.Thread).Should().BeEmpty("нить уже была — второй карточки нет");
    }

    [Fact]
    public async Task image_focus_на_то_что_не_картинка_проекта_отказ_без_следов()
    {
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "notes.txt" })).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "../outside.png" })).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["threadId"] = "nope" })).IsError.Should().BeTrue();

        _store.Get(Owner, ChatId).Threads.Should().BeEmpty();
        _feed.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task image_new_заводит_черновик_в_папке_тихой_строкой()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolNew, new JsonObject { ["folder"] = "art" });

        result.IsError.Should().BeFalse(result.Text);
        var thread = _store.Get(Owner, ChatId).Threads.Should().ContainSingle().Subject;
        thread.File.Should().BeNull();
        thread.DraftFolder.Should().Be("art");
        _store.Get(Owner, ChatId).Focus.Should().Be(thread.Id);
        Lines(ImageThreadService.RecordTypes.Focus).Should().ContainSingle()
            .Which.Fallback.Should().Be("Claude взял в работу: новая картинка");
        (await Call(toolset, ImageEditorToolset.ToolNew, new JsonObject { ["folder"] = "missing" })).IsError.Should().BeTrue();
    }

    // ── Запуск в нить ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Агент_запускает_генерацию_в_нить_варианты_ждут_человека()
    {
        var thread = Thread();
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "убрать провод", ["count"] = 2 });

        result.IsError.Should().BeFalse(result.Text);
        var body = Parse(result);
        var jobId = body["jobId"]!.GetValue<string>();
        body["threadId"]!.GetValue<string>().Should().Be(thread);
        body["quote"]!["provider"]!.GetValue<string>().Should().Be("higgsfield");

        var job = _jobs.Get(Owner, ProjectId, jobId)!;
        job.Initiator.Should().Be(ImageEditInitiator.Agent);
        job.ChatSessionId.Should().Be(ChatId);
        job.ThreadId.Should().Be(thread);
        job.Count.Should().Be(2);
        _store.Get(Owner, ChatId).Threads.Single().PendingJobId.Should().Be(jobId);
        Lines(ImageThreadService.RecordTypes.Launch).Should().BeEmpty("запуск агента виден карточкой вызова, а не тихой строкой");
    }

    // ADR-018 §7: трата агента — одна запись на владельца чата, инициатор — агент, SessionId — чат
    [Theory]
    [InlineData(null)]
    [InlineData("persona-artist")]
    public async Task Трата_агента_ложится_на_владельца_чата_с_инициатором_агент(string? personaId)
    {
        _sessions[ChatId].PersonaId = personaId;
        var thread = Thread();
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread));
        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());

        var record = _spend.Records.Should().ContainSingle().Subject;
        record.OwnerId.Should().Be(Owner);
        record.Initiator.Should().Be(SpendInitiators.Agent);
        record.SessionId.Should().Be(ChatId);
        record.ProjectId.Should().Be(ProjectId);
    }

    [Fact]
    public async Task Агент_выбирает_локальные_модели_и_потолок_хода_действует()
    {
        var media = new LocalImageEditorTests.FakeMedia();
        var toolset = Toolset([HiggsfieldImageEditorTests.Create().Editor,
            new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }]);
        var thread = Thread();
        var args = () => new JsonObject { ["threadId"] = thread, ["prompt"] = "удали провод", ["provider"] = "local" };

        var first = await Call(toolset, ImageEditorToolset.ToolGenerate, args());
        first.IsError.Should().BeFalse(first.Text);
        var quote = Parse(first)["quote"]!;
        quote["provider"]!.GetValue<string>().Should().Be("local");
        quote["estimate"]!["unit"]!.GetValue<string>().Should().Be(ImageEditPriceUnits.Free);
        (await Call(toolset, ImageEditorToolset.ToolGenerate, args())).IsError.Should().BeFalse();
        var third = await Call(toolset, ImageEditorToolset.ToolGenerate, args());

        third.IsError.Should().BeTrue();
        third.Text.Should().Contain("не больше 2");
        JobsOfChat().Should().HaveCount(2).And.OnlyContain(j => j.Provider == "local");
    }

    [Fact]
    public async Task Третий_запуск_за_ход_отказ_без_задачи_а_после_хода_счётчик_сброшен()
    {
        var thread = Thread();
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread))).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread))).IsError.Should().BeFalse();
        var third = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread));

        third.IsError.Should().BeTrue();
        third.Text.Should().Contain("не больше 2");
        JobsOfChat().Should().HaveCount(2, "третий вызов задачи не создаёт");

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("other-chat", Owner, 1, 0), "success"));
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread))).IsError.Should().BeTrue();
        await _bus.PublishAsync(new TurnCompleted(new TurnContext(ChatId, Owner, 1, 0), "success"));
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread))).IsError.Should().BeFalse();
        JobsOfChat().Should().HaveCount(3);
    }

    [Fact]
    public async Task Делегированный_ход_отказ_задачи_нет_и_лимит_не_расходуется()
    {
        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>()))
            .Returns("Запуск генерации недоступно на делегированном ходу");
        var thread = Thread();
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("делегированном ходу");
        JobsOfChat().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();

        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>())).Returns((string?)null);
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "a"))).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "b"))).IsError
            .Should().BeFalse("отказанный вызов не съел место в потолке хода");
    }

    [Fact]
    public async Task Без_гейта_хода_запуск_запрещён_по_построению()
    {
        var toolset = Toolset(withGate: false);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(Thread()));

        result.IsError.Should().BeTrue();
        JobsOfChat().Should().BeEmpty();
    }

    [Fact]
    public async Task Чужой_владелец_отказ_без_задачи()
    {
        var thread = Thread();
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread), Ctx(owner: Stranger))).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolState, ctx: Ctx(owner: Stranger))).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["threadId"] = thread }, Ctx(owner: Stranger)))
            .IsError.Should().BeTrue();
        JobsOfChat().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_задачи_другого_чата_неотличима_от_несуществующей()
    {
        AddChat("chat-b2", Owner, ProjectId);
        var toolset = Toolset();
        var started = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(Thread()));
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
            ["threadId"] = Thread(),
            ["prompt"] = "как на образце",
            ["references"] = new JsonArray { new JsonObject { ["path"] = "images/ref.png", ["role"] = "style" } },
        });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("символическую ссылку");
        JobsOfChat().Should().BeEmpty();
    }

    // Файл нити читается общим путём сборщика (ReadProjectImageAsync): ссылка наружу
    // отвергается до котировки, а не доезжает байтами чужого файла до поставщика
    [Fact]
    public async Task Файл_нити_через_символическую_ссылку_отвергается_до_котировки()
    {
        var outside = Path.Combine(_dir, "outside.png");
        File.WriteAllBytes(outside, TestImages.Png(4, 4));
        var link = Path.Combine(_root, "images", "linked.png");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows без прав — проверка идёт в CI на Linux
        var thread = Thread("images/linked.png");
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread));

        result.IsError.Should().BeTrue();
        result.Text.Should().Be("Файл картинки вне папки проекта или идёт через символическую ссылку");
        JobsOfChat().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Недоступный_поставщик_даёт_retryQuote_соседа_без_запуска()
    {
        var fal = new FakeImageEditor("fal", enabled: true, models: FakeImageEditor.Model("fal-edit"));
        var toolset = Toolset([fal, new FakeImageEditor("higgsfield", enabled: false)]);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = Thread(), ["prompt"] = "фон", ["provider"] = "higgsfield" });

        result.IsError.Should().BeTrue();
        var body = Parse(result);
        body["code"]!.GetValue<string>().Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        body["retryQuote"]!["provider"]!.GetValue<string>().Should().Be("fal");
        JobsOfChat().Should().BeEmpty("RunAsync заглушки бросает — запуска не было");
    }

    [Fact]
    public async Task Предложение_промпта_ничего_не_запускает_и_не_меняет()
    {
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolSuggestPrompt,
            new JsonObject { ["prompt"] = "закатное небо", ["count"] = 3 });

        result.IsError.Should().BeFalse();
        Parse(result)["prompt"]!.GetValue<string>().Should().Be("закатное небо");
        _store.Get(Owner, ChatId).Revision.Should().Be(0);
        _broadcaster.ToOwnerCalls.Should().BeEmpty();
        _feed.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Состояние_отдаёт_нити_фокус_и_поставщиков()
    {
        var thread = Thread();
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolState);

        result.IsError.Should().BeFalse(result.Text);
        var body = Parse(result);
        body["focus"]!.GetValue<string>().Should().Be(thread);
        var first = body["threads"]!.AsArray().Single()!;
        first["thread"]!["threadId"]!.GetValue<string>().Should().Be(thread);
        first["thread"]!["file"]!.GetValue<string>().Should().Be("images/hero.png");
        first["size"]!["width"]!.GetValue<int>().Should().Be(4);
        body["defaultProvider"]!.GetValue<string>().Should().Be("higgsfield");
        body["providers"]!.AsArray().Should().NotBeEmpty();
    }

    // ── Черновик «Новая картинка»: файла ещё нет ───────────────────────────────

    [Fact]
    public async Task Черновик_image_generate_рисует_по_тексту_и_пишет_трату_на_владельца()
    {
        var draft = Draft();
        var media = new LocalImageEditorTests.FakeMedia();
        var toolset = Toolset([new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }]);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = draft, ["prompt"] = "кот в шляпе", ["provider"] = "local" });

        result.IsError.Should().BeFalse(result.Text);
        var job = await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        job.Status.Should().Be(ImageEditJobStatus.Completed);
        job.ThreadId.Should().Be(draft, "варианты черновика попадают в его карточку");
        var submitted = media.Submitted.Should().ContainSingle().Subject;
        submitted.Op.Should().Be(LocalImageOp.Generate);
        submitted.Images.Should().BeEmpty("у черновика исходника нет — генерация по тексту");
        var record = _spend.Records.Should().ContainSingle().Subject;
        record.OwnerId.Should().Be(Owner);
        record.Initiator.Should().Be(SpendInitiators.Agent);
        record.SessionId.Should().Be(ChatId);
    }

    [Fact]
    public async Task Черновик_правка_без_картинки_отказ_без_задачи_и_лимит_цел()
    {
        var draft = Draft();
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = draft, ["prompt"] = "убери фон", ["op"] = "removeBackground" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Картинки ещё нет");
        _spend.Records.Should().BeEmpty();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот"))).IsError.Should().BeFalse("отказ не расходует лимит хода");
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот 2"))).IsError.Should().BeFalse();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот 3")))
            .Text.Should().Contain("не больше 2", "лимит 2 за ход действует и у черновика");
    }

    // Имена в списке авторазрешения (Core) обязаны совпадать со схемами тулсета
    [Fact]
    public void Автоматически_разрешённые_инструменты_ровно_схемы_тулсета()
    {
        var names = Toolset().ToolsFor(Ctx()).Select(t => $"mcp__{ImageEditorToolset.ServerName}__{t.Name}").ToList();
        names.Should().BeEquivalentTo(ImageEditorAgentTools.AutoAllowTools);
    }

    private sealed class RecordingFeed : IChatFeed
    {
        public List<(string SessionId, StoredModuleRecord Record)> Records { get; } = [];

        public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default)
        {
            lock (Records) Records.Add((sessionId, record));
            return Task.FromResult(true);
        }
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
