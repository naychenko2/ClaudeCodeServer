using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
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
    private readonly List<ImageEditJobService> _services = [];
    private readonly ImageThreadStore _store;
    private readonly ImageProjectPrefsStore _prefsStore;
    private IImageEditJobs _jobs = null!;

    public ImageEditorToolsetTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        Directory.CreateDirectory(Path.Combine(_root, "art"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), TestImages.Png(4, 4));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "не картинка");
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        _prefsStore = new ImageProjectPrefsStore(Path.Combine(_dir, ImageProjectPrefsStore.DirName));
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

    // Выбор человека — «Чем» контекста хода (ADR-023), персонаж префов не читается
    private ChatContextStore _ctxStore = null!;

    private ImageEditorToolset Toolset(IImageEditor[]? editors = null, bool agentLaunch = true, bool withGate = true,
        IImagePlaceSettings? placeSettings = null, IFeatureFlagGate? flags = null,
        ClaudeHomeServer.Services.ImageEditor.Faces.FaceDetectionCache? faces = null,
        ILocalImageMedia? localMedia = null, IImageEditJobs? jobsOverride = null)
    {
        editors ??= [HiggsfieldImageEditorTests.Create().Editor];
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        if (jobsOverride is not null)
        {
            _jobs = jobsOverride;
        }
        else
        {
            var realJobs = new ImageEditJobService(editors, workspace, NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster);
            _jobs = realJobs;
            _services.Add(realJobs);
        }

        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(It.IsAny<string>())).Returns((string id) => _sessions.GetValueOrDefault(id));
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });
        var prefs = new ImageProjectPrefsService(_prefsStore, NullLogger<ImageProjectPrefsService>.Instance, projects.Object);
        // Контекст чата (ADR-023): стор и раскладка входов, как в регистрации модуля
        var registry = new ContextKindRegistry([new ImageContextKind(_store, null, editors, workspace), new ProjectFileContextKind()]);
        _ctxStore = new ChatContextStore(Path.Combine(_dir, "chat-context"), registry);
        var mirror = new ChatContextFocusMirror(_ctxStore, NullLogger<ChatContextFocusMirror>.Instance);
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, _feed, _broadcaster,
            prefs: prefs, mirror: mirror);
        var context = new ImageContextLaunch(_ctxStore, registry, directory.Object, _store, workspace: workspace);
        var launcher = new ImageEditLaunchAssembler(editors, _jobs, new SkiaImageRaster(), threads, context: context,
            flags: flags, faces: faces, localMedia: localMedia);

        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var config = TestImages.Config((ImageEditorToolset.AgentLaunchKey, agentLaunch ? "true" : "false"));
        return new ImageEditorToolset(accessor.Object, projects.Object, editors, threads, launcher,
            withGate ? _turnGate.Object : null, _jobs, placeSettings: placeSettings, events: _bus, config: config, prefs: prefs,
            context: context, flags: flags, faces: faces, localMedia: localMedia);
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

    private async Task<ImageEditJobDto> WaitDone(string jobId, string scopeKey = ProjectId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = _jobs.Get(Owner, scopeKey, jobId)!;
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
            ImageEditorToolset.ToolGenerate, ImageEditorToolset.ToolFindFaces,
            ImageEditorToolset.ToolCancel, ImageEditorToolset.ToolSuggestPrompt]);
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
            ImageEditorToolset.ToolFindFaces, ImageEditorToolset.ToolSuggestPrompt]);
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
    public void Чужой_чат_и_выключенный_флаг_пустой_состав_а_личный_чат_полный()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();

        toolset.ToolsFor(Ctx()).Should().HaveCount(7, "сервер есть в любом чате проекта");
        toolset.ToolsFor(Ctx(tail: PersonalChat)).Select(t => t.Name).Should().Equal(
            toolset.ToolsFor(Ctx()).Select(t => t.Name), "схемы одни для чата проекта и личного");
        toolset.ToolsFor(Ctx(owner: Stranger)).Should().BeEmpty("чат владельца B чужаку не виден");
        toolset.ToolsFor(Ctx(owner: Stranger, tail: PersonalChat)).Should().BeEmpty("личный чат владельца B чужаку не виден");
        toolset.ToolsFor(Ctx(tail: "missing")).Should().BeEmpty();
        toolset.ToolsFor(Ctx(tail: "../chat-b")).Should().BeEmpty();
    }

    // ── Личный чат вне проекта (ADR-019, «Изменение 29.09») ───────────────────

    private const string PersonalChat = "chat-personal";

    private IReadOnlyList<ImageEditJobDto> PersonalJobs() =>
        _store.Get(Owner, PersonalChat).Events
            .Select(e => e.JobId).OfType<string>().Distinct()
            .Select(id => _jobs.Get(Owner, ImageEditScope.Personal, id)).OfType<ImageEditJobDto>().ToList();

    private async Task<string> PersonalDraft(ImageEditorToolset toolset)
    {
        var result = await Call(toolset, ImageEditorToolset.ToolNew, ctx: Ctx(tail: PersonalChat));
        result.IsError.Should().BeFalse(result.Text);
        return Parse(result)["focus"]!.GetValue<string>();
    }

    [Fact]
    public async Task Личный_чат_image_new_без_папки_заводит_черновик_тихой_строкой()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolNew, ctx: Ctx(tail: PersonalChat));

        result.IsError.Should().BeFalse(result.Text);
        var thread = _store.Get(Owner, PersonalChat).Threads.Should().ContainSingle().Subject;
        thread.File.Should().BeNull();
        _store.Get(Owner, PersonalChat).Focus.Should().Be(thread.Id);
        Parse(result)["thread"]!["draft"]!["note"]!.GetValue<string>().Should().Contain("скачает")
            .And.NotContain("проект");
        _feed.Records.Where(r => r.SessionId == PersonalChat && r.Record.RecordType == ImageThreadService.RecordTypes.Focus)
            .Should().ContainSingle().Which.Record.Fallback.Should().Be("Claude взял в работу: новая картинка");
    }

    // Файл и папка есть в проекте владельца — но у личной области проекта нет, и отказ идёт до диска
    [Fact]
    public async Task Личный_чат_файл_и_папка_проекта_отказ_без_следов()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();

        var focus = await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "images/hero.png" },
            Ctx(tail: PersonalChat));
        var draft = await Call(toolset, ImageEditorToolset.ToolNew, new JsonObject { ["folder"] = "art" },
            Ctx(tail: PersonalChat));

        focus.IsError.Should().BeTrue();
        focus.Text.Should().Contain("вне проекта").And.Contain("image_new");
        draft.IsError.Should().BeTrue();
        draft.Text.Should().Contain("вне проекта");
        _store.Get(Owner, PersonalChat).Threads.Should().BeEmpty();
        _feed.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Image_state_версия_со_сходством_и_пометкой_самой_похожей()
    {
        var thread = Thread();
        _store.AddLaunch(Owner, ChatId, thread, new ImageThreadLaunch("job-1", ImageThreadVersion.OriginId, null,
            _store.Now(), ImageThreadLaunchStatus.Running, "agent", "в куртке"));
        _store.AddLaunchVersions(Owner, ChatId, thread, "job-1", [(1, "s1", 0.64), (2, "s2", 0.77), (3, "s3", null)]);
        _store.FinishLaunch(Owner, ChatId, thread, "job-1", ImageThreadLaunchStatus.Done, []);

        var state = Parse(await Call(Toolset(), ImageEditorToolset.ToolState, ctx: Ctx()));

        var versions = state["threads"]!.AsArray().Single(t => t!["thread"]!["threadId"]!.GetValue<string>() == thread)!["thread"]!["versions"]!.AsArray();
        string? Similarity(int variant) => versions.Single(v => v!["variant"]?.GetValue<int>() == variant)!["similarity"]?.GetValue<string>();
        Similarity(2).Should().Be("сходство с персонажем 0,77 — самый похожий в запуске");
        Similarity(1).Should().Be("сходство с персонажем 0,64");
        Similarity(3).Should().Be("сходство с персонажем не посчитано (лицо не найдено)");
    }

    [Fact]
    public void Хвост_хода_сходство_пометка_лицо_не_найдено_и_запуск_без_сходства()
    {
        var thread = Thread();
        _store.AddLaunch(Owner, ChatId, thread, new ImageThreadLaunch("job-1", ImageThreadVersion.OriginId, null,
            _store.Now(), ImageThreadLaunchStatus.Running, "agent", "в куртке"));
        _store.AddLaunchVersions(Owner, ChatId, thread, "job-1", [(1, "s1", 0.64), (2, "s2", 0.77), (3, "s3", null)]);
        _store.FinishLaunch(Owner, ChatId, thread, "job-1", ImageThreadLaunchStatus.Done, []);
        _store.AddLaunch(Owner, ChatId, thread, new ImageThreadLaunch("job-2", ImageThreadVersion.OriginId, null,
            _store.Now(), ImageThreadLaunchStatus.Running, "agent", "без лица"));
        _store.AddLaunchVersions(Owner, ChatId, thread, "job-2", [(1, "s4", null), (2, "s5", null)]);
        _store.FinishLaunch(Owner, ChatId, thread, "job-2", ImageThreadLaunchStatus.Done, []);

        var text = ClaudeHomeServer.Services.ImageEditor.Chats.ImageEditorStateContributor.RenderThreads(
            _store.Get(Owner, ChatId), [], _ => null);

        var lines = text.Split('\n');
        string Line(string prompt, int variant) => lines.Single(l => l.Contains($"вариант {variant} запуска «{prompt}»"));
        Line("в куртке", 2).Should().Contain("сходство с персонажем 0,77 — самый похожий в запуске");
        Line("в куртке", 1).Should().Contain("сходство с персонажем 0,64").And.NotContain("самый похожий");
        Line("в куртке", 3).Should().Contain("сходство с персонажем не посчитано (лицо не найдено)");
        Line("без лица", 1).Should().NotContain("сходство");
        Line("без лица", 2).Should().NotContain("сходство");
    }

    [Fact]
    public async Task Личный_чат_image_state_без_проектных_подсказок()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();

        var state = await Call(toolset, ImageEditorToolset.ToolState, ctx: Ctx(tail: PersonalChat));

        state.IsError.Should().BeFalse(state.Text);
        Parse(state)["note"]!.GetValue<string>().Should().Contain("image_new").And.NotContain("image_focus с file");
    }

    [Fact]
    public async Task Личный_чат_локальная_генерация_ключом_personal_трата_без_проекта_и_потолок_хода()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var media = new LocalImageEditorTests.FakeMedia();
        var toolset = Toolset([HiggsfieldImageEditorTests.Create().Editor,
            new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }]);
        var thread = await PersonalDraft(toolset);
        var args = () => new JsonObject { ["threadId"] = thread, ["prompt"] = "кот", ["provider"] = "local" };

        var first = await Call(toolset, ImageEditorToolset.ToolGenerate, args(), Ctx(tail: PersonalChat));
        first.IsError.Should().BeFalse(first.Text);
        var jobId = Parse(first)["jobId"]!.GetValue<string>();
        Parse(first)["quote"]!["provider"]!.GetValue<string>().Should().Be("local");
        _jobs.Get(Owner, ProjectId, jobId).Should().BeNull("задача личного чата живёт в области personal");
        var job = _jobs.Get(Owner, ImageEditScope.Personal, jobId)!;
        job.Initiator.Should().Be(ImageEditInitiator.Agent);
        job.ChatSessionId.Should().Be(PersonalChat);

        (await Call(toolset, ImageEditorToolset.ToolGenerate, args(), Ctx(tail: PersonalChat))).IsError.Should().BeFalse();
        var third = await Call(toolset, ImageEditorToolset.ToolGenerate, args(), Ctx(tail: PersonalChat));
        third.IsError.Should().BeTrue();
        third.Text.Should().Contain("не больше 2");
        PersonalJobs().Should().HaveCount(2).And.OnlyContain(j => j.Provider == "local");

        await WaitDone(jobId, ImageEditScope.Personal);
        _spend.Records.Should().NotBeEmpty().And.OnlyContain(r => r.ProjectId == null && r.OwnerId == Owner
            && r.Initiator == SpendInitiators.Agent && r.SessionId == PersonalChat);
    }

    [Fact]
    public async Task Личный_чат_делегированный_ход_отказ_задачи_нет()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        _turnGate.Setup(g => g.Deny(Owner, PersonalChat, It.IsAny<string>()))
            .Returns("Запуск генерации недоступно на делегированном ходу");
        var toolset = Toolset();
        var thread = await PersonalDraft(toolset);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread), Ctx(tail: PersonalChat));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("делегированном ходу");
        PersonalJobs().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Личный_чат_образцы_и_персонаж_проекта_отказ_до_котировки()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();
        var thread = await PersonalDraft(toolset);

        var withRefs = Gen(thread);
        withRefs["references"] = new JsonArray(new JsonObject { ["path"] = "images/hero.png", ["role"] = "style" });
        var withCharacter = Gen(thread);
        withCharacter["character"] = "anya";

        foreach (var args in new[] { withRefs, withCharacter })
        {
            var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args, Ctx(tail: PersonalChat));
            result.IsError.Should().BeTrue();
            result.Text.Should().Contain("вне проекта");
        }
        PersonalJobs().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "a"), Ctx(tail: PersonalChat))).IsError
            .Should().BeFalse("отказы не съели место в потолке хода");
    }

    [Fact]
    public async Task Личный_чат_чужого_владельца_не_виден_и_его_нити_не_трогаются()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = Toolset();
        var thread = await PersonalDraft(toolset);
        var stranger = Ctx(owner: Stranger, tail: PersonalChat);

        (await Call(toolset, ImageEditorToolset.ToolState, ctx: stranger)).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolNew, ctx: stranger)).IsError.Should().BeTrue();
        (await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread), stranger)).IsError.Should().BeTrue();
        _store.Get(Stranger, PersonalChat).Threads.Should().BeEmpty();
        _store.Get(Owner, PersonalChat).Threads.Should().ContainSingle();
        PersonalJobs().Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
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
    public async Task Агент_запускает_генерацию_в_нить_с_якорем_запуска_внизу_ленты()
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
        var launch = _store.Get(Owner, ChatId).Threads.Single().Launches.Should().ContainSingle().Subject;
        launch.JobId.Should().Be(jobId);
        launch.Initiator.Should().Be(SpendInitiators.Agent);
        launch.BaseVersionId.Should().Be(ImageThreadVersion.OriginId);
        Lines(ImageThreadService.RecordTypes.Launch).Should().BeEmpty("тихая строка ручного запуска больше не пишется");
        Lines(ImageThreadService.RecordTypes.LaunchVersions).Should().ContainSingle()
            .Which.Data!.Value.GetProperty("jobId").GetString().Should().Be(jobId);
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
    public async Task Явный_local_админа_при_лежащем_ComfyUI_отказывает_с_котировкой_облака_без_запуска()
    {
        var fal = new FakeImageEditor("fal", enabled: true, models: FakeImageEditor.Model("fal-edit"));
        var media = new LocalImageEditorTests.FakeMedia { Available = false };
        var place = new Mock<IImagePlaceSettings>();
        place.Setup(p => p.ProviderFor(ImagePlaceKeys.ImageEditor)).Returns(LocalImageEditor.ProviderKey);
        var toolset = Toolset([fal, new LocalImageEditor(media)], placeSettings: place.Object);

        var state = Parse(await Call(toolset, ImageEditorToolset.ToolState));
        state["defaultProvider"]!.GetValue<string>().Should().Be(LocalImageEditor.ProviderKey);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(Thread()));

        result.IsError.Should().BeTrue();
        var body = Parse(result);
        body["code"]!.GetValue<string>().Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        body["error"]!.GetValue<string>().Should().Contain("сейчас недоступен");
        body["retryQuote"]!["provider"]!.GetValue<string>().Should().Be("fal");
        JobsOfChat().Should().BeEmpty("облако пришло котировкой, а не запуском: RunAsync заглушки fal бросает");
        media.Submitted.Should().BeEmpty();
        _spend.Records.Should().BeEmpty();
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
        // Фокус агент видит из контекста чата: человек выбрал эту картинку основной
        _ctxStore.SetPrimary(Owner, ChatId, ChatContextFocusMirror.NewItem("image", thread, ContextActor.Human), null);

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
        // Прогон на вариант: генерация по тексту идёт по одному, как и правка
        media.Submitted.Should().HaveCount(job.Count).And.OnlyContain(s =>
            s.Op == LocalImageOp.Generate && s.Count == 1 && s.Images.Count == 0, "у черновика исходника нет — генерация по тексту");
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

    // ── Выбор человека в полосе «Картинки» ────────────────────────────────────

    private void Prefs(ImageProjectPrefs prefs) => _prefsStore.Save(Owner, ProjectId, prefs);

    private (ImageEditorToolset Toolset, LocalImageEditorTests.FakeMedia Media) WithLocal()
    {
        var media = new LocalImageEditorTests.FakeMedia();
        var toolset = Toolset([HiggsfieldImageEditorTests.Create().Editor,
            new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }]);
        return (toolset, media);
    }

    private string Character() =>
        CharacterStore.Create(_root, new CharacterDraft("Аня", null, CharacterStoreTests.Photos(3)), DateTime.UtcNow)
            .Value!.Manifest.Slug;

    [Fact]
    public async Task image_new_наследует_выбор_проекта_и_сообщает_его()
    {
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 3, false, null));
        var toolset = Toolset();

        var result = await Call(toolset, ImageEditorToolset.ToolNew);

        result.IsError.Should().BeFalse(result.Text);
        _store.Get(Owner, ChatId).Threads.Single().Settings.Should()
            .Be(new ImageThreadSettings(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 3, false));
        var choice = Parse(result)["humanChoice"]!;
        choice["text"]!.GetValue<string>().Should()
            .Be("Выбор человека в строке контекста: поставщик local, модель qwen-image-2.1, вариантов 3");
        choice["rule"]!.GetValue<string>().Should().Be(ClaudeHomeServer.Services.ImageEditor.Chats.ImageEditorStateContributor.ChoiceRule);
    }

    [Fact]
    public async Task image_generate_не_читает_персонажа_из_префов_проекта()
    {
        var slug = Character();
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 3, true, slug));
        var (toolset, media) = WithLocal();
        var draft = Draft();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот в шляпе"));

        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        media.Submitted.Should().NotBeEmpty().And.OnlyContain(r => r.Images.Count == 0,
            "персонаж префов проекта при строке контекста не едет в генерацию — он ref контекста");
    }

    // ── Образцы контекста у агента (ADR-023 §Д2.1, 2б-3) ──

    private JsonObject LocalGen(string threadId) =>
        new() { ["threadId"] = threadId, ["prompt"] = "кот", ["provider"] = LocalImageEditor.ProviderKey, ["count"] = 1 };

    private void ContextWithStyleRef(string threadId)
    {
        _ctxStore.SetPrimary(Owner, ChatId, new ContextItem("p", "image", new JsonObject { ["threadId"] = threadId }, null,
            ContextActor.Human, DateTime.UtcNow), null);
        _ctxStore.AddRef(Owner, ChatId, new ContextItem("r", "project-file", new JsonObject { ["path"] = "images/hero.png" },
            "style", ContextActor.Human, DateTime.UtcNow), null);
    }

    [Fact]
    public async Task image_generate_без_references_берёт_образцы_контекста()
    {
        var (toolset, media) = WithLocal();
        var draft = Draft();
        ContextWithStyleRef(draft);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, LocalGen(draft));

        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        media.Submitted.Should().NotBeEmpty().And.OnlyContain(r => r.Images.Count == 1, "образец стиля из контекста уехал в запуск");
    }

    [Fact]
    public async Task image_focus_ставит_основной_объект_от_агента_а_выбор_человека_гасит_звёздочку()
    {
        var toolset = Toolset();

        var focus = await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "images/hero.png" });

        focus.IsError.Should().BeFalse(focus.Text);
        var threadId = Parse(focus)["focus"]!.GetValue<string>();
        var primary = _ctxStore.Get(Owner, ChatId).Primary!;
        primary.Kind.Should().Be("image");
        ChatContextFocusMirror.ThreadOf(primary).Should().Be(threadId);
        primary.By.Should().Be(ContextActor.Agent, "image_focus — выбор агента: чип ✦");

        // Человек выбрал тот же объект — звёздочка гаснет (ADR-023 §2.4)
        _ctxStore.SetPrimary(Owner, ChatId, ChatContextFocusMirror.NewItem("image", threadId, ContextActor.Human), null);
        _ctxStore.Get(Owner, ChatId).Primary!.By.Should().Be(ContextActor.Human);
    }

    [Fact]
    public async Task image_state_показывает_основной_объект_контекста_а_не_запись_нити()
    {
        var toolset = Toolset();
        var a = Draft();
        var b = Draft();
        _store.SetFocus(Owner, ChatId, a, _store.Get(Owner, ChatId).Revision);
        // Человек ставит основным B ручкой контекста: запись нити по-прежнему говорит «A»
        _ctxStore.SetPrimary(Owner, ChatId, ChatContextFocusMirror.NewItem("image", b, ContextActor.Human), null);
        _store.Get(Owner, ChatId).Focus.Should().Be(a);

        var state = Parse(await Call(toolset, ImageEditorToolset.ToolState));

        state["focus"]!.GetValue<string>().Should().Be(b, "агент видит выбор человека, а не сырой Focus нити");
    }

    [Fact]
    public async Task пустой_references_агента_заменяет_контекст()
    {
        var (toolset, media) = WithLocal();
        var draft = Draft();
        ContextWithStyleRef(draft);
        var args = LocalGen(draft);
        args["references"] = new JsonArray();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        media.Submitted.Should().NotBeEmpty().And.OnlyContain(r => r.Images.Count == 0, "явный пустой массив — без образцов");
    }

    [Fact]
    public async Task image_focus_по_файлу_новая_нить_наследует_выбор_проекта()
    {
        Prefs(new ImageProjectPrefs("higgsfield", null, 4, true, null));
        var toolset = Toolset();

        (await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["file"] = "images/hero.png" })).IsError.Should().BeFalse();

        _store.Get(Owner, ChatId).Threads.Single().Settings.Should().Be(new ImageThreadSettings("higgsfield", null, 4, true));
    }

    [Fact]
    public async Task image_generate_берёт_настройки_нити_раньше_выбора_проекта()
    {
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, null, 3, true, null));
        var (toolset, _) = WithLocal();
        var thread = Thread();
        _store.SetSettings(Owner, ChatId, thread, new ImageThreadSettings("higgsfield", null, 1, true), _store.Get(Owner, ChatId).Revision);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread));

        result.IsError.Should().BeFalse(result.Text);
        var job = _jobs.Get(Owner, ProjectId, Parse(result)["jobId"]!.GetValue<string>())!;
        job.Provider.Should().Be("higgsfield");
        job.Count.Should().Be(1);
    }

    [Fact]
    public async Task Явный_аргумент_агента_перекрывает_выбор_человека()
    {
        var slug = Character();
        Prefs(new ImageProjectPrefs("higgsfield", null, 3, true, slug));
        var (toolset, media) = WithLocal();
        var draft = Draft();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = draft, ["prompt"] = "кот", ["provider"] = "local", ["count"] = 1, ["character"] = "nope",
        });

        result.IsError.Should().BeTrue("явный персонаж агента перекрывает полосу — а такого в проекте нет");
        Parse(result)["error"]!.GetValue<string>().Should().Be("Персонаж не найден");

        var ok = await Call(toolset, ImageEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = draft, ["prompt"] = "кот", ["provider"] = "local", ["count"] = 1,
        });
        ok.IsError.Should().BeFalse(ok.Text);
        var job = _jobs.Get(Owner, ProjectId, Parse(ok)["jobId"]!.GetValue<string>())!;
        job.Provider.Should().Be("local");
        job.Count.Should().Be(1);
    }

    [Fact]
    public async Task image_generate_по_тексту_берёт_выбор_Создать_а_правка_выбор_Править()
    {
        Prefs(new ImageProjectPrefs("higgsfield", null, 3, true, null,
            new ImageCreatePrefs(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 1),
            new ImageEditPrefs("higgsfield", null, 2, "edit", null, null)));
        var (toolset, _) = WithLocal();
        var draft = Draft();
        // У черновика свои настройки (старый фронт пишет их в нить) — генерация по тексту их не берёт
        _store.SetSettings(Owner, ChatId, draft, new ImageThreadSettings("higgsfield", null, 4, true), _store.Get(Owner, ChatId).Revision);
        var thread = Thread();

        var created = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот"));
        var edited = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(thread, "ярче"));

        created.IsError.Should().BeFalse(created.Text);
        var createJob = _jobs.Get(Owner, ProjectId, Parse(created)["jobId"]!.GetValue<string>())!;
        createJob.Provider.Should().Be(LocalImageEditor.ProviderKey);
        createJob.Model.Should().Be(LocalImageEditor.QwenImage);
        createJob.Count.Should().Be(1);
        edited.IsError.Should().BeFalse(edited.Text);
        var editJob = _jobs.Get(Owner, ProjectId, Parse(edited)["jobId"]!.GetValue<string>())!;
        editJob.Provider.Should().Be("higgsfield", "правка идёт по выбору «Править», а не «Создать»");
        editJob.Count.Should().Be(2);
    }

    [Fact]
    public async Task Без_выбора_Создать_генерация_по_тексту_берёт_настройки_нити_как_раньше()
    {
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, null, 3, true, null));
        var (toolset, _) = WithLocal();
        var draft = Draft();
        _store.SetSettings(Owner, ChatId, draft, new ImageThreadSettings("higgsfield", null, 1, true), _store.Get(Owner, ChatId).Revision);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(draft, "кот"));

        result.IsError.Should().BeFalse(result.Text);
        var job = _jobs.Get(Owner, ProjectId, Parse(result)["jobId"]!.GetValue<string>())!;
        job.Provider.Should().Be("higgsfield", "старый фронт пишет выбор в нить черновика");
        job.Count.Should().Be(1);
    }

    [Fact]
    public async Task Явный_поставщик_агента_не_подменяется_и_не_берёт_чужую_модель_выбора()
    {
        Prefs(ImageProjectPrefs.Default with
        {
            Create = new ImageCreatePrefs(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 1),
        });
        var (toolset, _) = WithLocal();
        var draft = Draft();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = draft, ["prompt"] = "кот", ["provider"] = "higgsfield" });

        result.IsError.Should().BeFalse(result.Text);
        var job = _jobs.Get(Owner, ProjectId, Parse(result)["jobId"]!.GetValue<string>())!;
        job.Provider.Should().Be("higgsfield");
        job.Model.Should().NotBe(LocalImageEditor.QwenImage, "модель qwen выбрана у поставщика local — у higgsfield идёт «Авто»");
        job.Count.Should().Be(1, "число вариантов — из выбора «Создать»");
    }

    // Быстрое действие со своей моделью — как у человека (quickUsesOwnModel на фронте): из полосы
    // не едут ни персонаж, ни чужая модель, ни число вариантов
    [Fact]
    public async Task Улучшить_лица_без_аргументов_не_наследует_персонажа_модель_и_число_из_полосы()
    {
        var slug = Character();
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, LocalImageEditor.QwenImage, 3, true, slug));
        // Персонаж в полосе есть, но без фото: подставь его сервер — запуск упал бы
        // «Персонаж не найден», а FaceDetailer фото молча игнорирует и иначе подстановку не видно
        foreach (var photo in Directory.GetFiles(CharacterStore.CharacterDir(_root, slug)!)
                     .Where(f => Path.GetFileName(f) != CharacterStore.ManifestFile))
            File.Delete(photo);
        var (toolset, media) = WithLocal();
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["op"] = "enhanceFaces" });

        result.IsError.Should().BeFalse(result.Text);
        var job = await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        job.Provider.Should().Be(LocalImageEditor.ProviderKey, "поставщик из полосы наследуется");
        job.Model.Should().Be(LocalImageEditor.FaceDetailer, "модель полосы qwen-image лица не улучшает");
        job.Count.Should().Be(1);
        media.Submitted.Should().ContainSingle().Which.Images.Should().HaveCount(1, "едет только исходник");
    }

    [Fact]
    public async Task Правка_по_промпту_не_берёт_персонажа_из_префов_проекта()
    {
        var slug = Character();
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, null, 1, true, slug));
        var (toolset, media) = WithLocal();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, Gen(Thread(), "надеть шляпу"));

        result.IsError.Should().BeFalse(result.Text);
        await WaitDone(Parse(result)["jobId"]!.GetValue<string>());
        media.Submitted.Should().NotBeEmpty().And.OnlyContain(r => r.Images.Count == 1,
            "только холст: персонаж префов проекта не едет — он ref контекста чата");
    }

    [Fact]
    public async Task Явный_персонаж_агента_едет_и_в_улучшение_лиц()
    {
        Prefs(new ImageProjectPrefs(LocalImageEditor.ProviderKey, null, 1, true, null));
        var (toolset, _) = WithLocal();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = Thread(), ["op"] = "enhanceFaces", ["character"] = "nope" });

        result.IsError.Should().BeTrue("явный персонаж агента не отбрасывается — а такого в проекте нет");
        Parse(result)["error"]!.GetValue<string>().Should().Be("Персонаж не найден");
    }

    [Fact]
    public void Описания_provider_model_character_просят_не_указывать_без_просьбы()
    {
        var props = Toolset().ToolsFor(Ctx()).Single(t => t.Name == ImageEditorToolset.ToolGenerate).InputSchema["properties"]!;

        foreach (var name in new[] { "provider", "model", "character" })
            props[name]!["description"]!.GetValue<string>().Should()
                .Contain("Не указывай без просьбы человека — по умолчанию берётся выбор человека из строки контекста");
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

    // ── image_find_faces и face у image_generate (ADR-028, этап 2) ───────────

    private static IFeatureFlagGate Flags(bool faceSwapOn)
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.CharacterFaceSwap)).Returns(faceSwapOn);
        return flags.Object;
    }

    private static ILocalImageMedia FakeMedia(IReadOnlyList<LocalDetectedFace> faces,
        bool busy = false, string? error = null)
    {
        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new LocalFaceDetectResult(200, 200, faces, error, busy)));
        return media.Object;
    }

    // FakeMedia с размерами, совпадающими с тестовой картинкой 4×4
    private static ILocalImageMedia FaceMedia(IReadOnlyList<LocalDetectedFace> faces,
        bool busy = false, string? error = null)
    {
        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new LocalFaceDetectResult(4, 4, faces, error, busy)));
        return media.Object;
    }

    private string CreateCharacter(string name)
    {
        var created = CharacterStore.Create(_root,
            new CharacterDraft(name, null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        return created.Value!.Manifest.Slug;
    }

    // Toolset с локальным редактором + face-зависимости
    private ImageEditorToolset FaceToolset(IFeatureFlagGate? flags = null,
        ClaudeHomeServer.Services.ImageEditor.Faces.FaceDetectionCache? faces = null,
        ILocalImageMedia? localMedia = null)
    {
        var media = new LocalImageEditorTests.FakeMedia();
        return Toolset(
            [HiggsfieldImageEditorTests.Create().Editor,
             new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }],
            flags: flags, faces: faces, localMedia: localMedia);
    }

    // image_find_faces: успех — возвращает лица
    [Fact]
    public async Task image_find_faces_успех_возвращает_лица()
    {
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: FaceMedia(faces));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread });

        result.IsError.Should().BeFalse(result.Text);
        var json = Parse(result);
        json["faces"]!.AsArray().Should().HaveCount(1);
        json["faces"]![0]!["n"]!.GetValue<int>().Should().Be(1);
    }

    // image_find_faces: флаг выключен
    [Fact]
    public async Task image_find_faces_флаг_выключен()
    {
        var toolset = FaceToolset(flags: Flags(false), faces: new FaceDetectionCache());
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("выключена");
    }

    // image_find_faces: личный чат
    [Fact]
    public async Task image_find_faces_личный_чат_отказ()
    {
        AddChat(PersonalChat, Owner, projectId: null);
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache());
        var thread = Thread(chat: PersonalChat);

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread }, Ctx(tail: PersonalChat));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("только в чате проекта");
    }

    // image_find_faces: нет локальных моделей
    [Fact]
    public async Task image_find_faces_нет_локальных_моделей()
    {
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: null);
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("не настроены");
    }

    // image_find_faces: видеокарта занята
    [Fact]
    public async Task image_find_faces_busy()
    {
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces, busy: true));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("занята");
    }

    // image_find_faces: ошибка детекции
    [Fact]
    public async Task image_find_faces_ошибка_детекции()
    {
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces, error: "ComfyUI не ответил"));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("ComfyUI");
    }

    // image_generate с face: нет персонажа
    [Fact]
    public async Task image_generate_face_без_персонажа()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1 });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("персонажа");
    }

    // image_generate с face: провайдер не local
    [Fact]
    public async Task image_generate_face_провайдер_не_local()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slug, ["provider"] = "fal" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("локальными");
    }

    // image_generate с face: успех — FaceTarget записан, контекст не читается
    [Fact]
    public async Task image_generate_face_успех_FaceTarget_контекст_не_читается()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slug, ["provider"] = "local" });

        result.IsError.Should().BeFalse(result.Text);
        var json = Parse(result);
        json["jobId"]!.GetValue<string>().Should().NotBeEmpty();
        json["baseVersion"]!.AsObject()["versionId"]!.GetValue<string>().Should().NotBeEmpty();
    }

    // Мутация: если face-ветка передаёт ContextRevision, ассемблер отвергнет
    // (ImageEditLaunchAssembler.cs:297: «Подстановка на лицо несовместима с контекстом чата»).
    // Тест шва: ставим файл проекта в контекст (revision > 0), запускаем face —
    // успех значит ContextRevision = null, т.е. face-ветка контекст не читает.
    [Fact]
    public async Task Мутация_face_с_ContextRevision_отвергается()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        // Картинка в контексте (вид image — primary): revision > 0, если face-ветка прочитает
        var item = new ContextItem("i-mut", "image",
            new JsonObject { ["threadId"] = thread }, "image", ContextActor.Human, DateTime.UtcNow);
        _ctxStore.SetPrimary(Owner, ChatId, item, 0);
        _ctxStore.Get(Owner, ChatId).Revision.Should().BeGreaterThan(0, "контекст не пуст — мутация видна");

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slug, ["provider"] = "local" });

        // Успех: FaceTarget не читает контекст, ContextRevision = null
        result.IsError.Should().BeFalse(result.Text);
    }

    // ── Лимит image_find_faces за ход (MaxFindFacesPerTurn) ─────────────────────

    // Нить с N версиями: N запусков по одному варианту
    private string ThreadWithVersions(int count)
    {
        var thread = Thread();
        for (var i = 0; i < count; i++)
        {
            var job = $"job-ff-{i}";
            _store.AddLaunch(Owner, ChatId, thread, new ImageThreadLaunch(job, ImageThreadVersion.OriginId, null,
                _store.Now(), ImageThreadLaunchStatus.Running, "agent", $"v{i}"));
            _store.AddLaunchVersions(Owner, ChatId, thread, job, [(1, $"s-ff-{i}", null)]);
            _store.FinishLaunch(Owner, ChatId, thread, job, ImageThreadLaunchStatus.Done, []);
        }
        return thread;
    }

    private IReadOnlyList<string> VersionIds(string threadId) =>
        _store.Get(Owner, ChatId).Threads.Single(t => t.Id == threadId).Versions
            .Where(v => !v.IsOrigin).Select(v => v.Id).ToList();

    // (а) девять вызовов на девяти версиях: первые восемь — успех, девятый — лимит
    [Fact]
    public async Task image_find_faces_лимит_девять_вызовов_девятый_отказ()
    {
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: FaceMedia(faces));
        var thread = ThreadWithVersions(9);
        var versionIds = VersionIds(thread);
        versionIds.Should().HaveCount(9);

        for (var i = 0; i < 8; i++)
        {
            var result = await Call(toolset, ImageEditorToolset.ToolFindFaces,
                new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[i] });
            result.IsError.Should().BeFalse($"вызов {i + 1}: {result.Text}");
        }

        var ninth = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[8] });
        ninth.IsError.Should().BeTrue();
        ninth.Text.Should().Contain("Лимит поиска лиц за ход исчерпан");
    }

    // (б) после OnTurnCompleted счётчик сброшен
    [Fact]
    public async Task image_find_faces_после_хода_счётчик_сброшен()
    {
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: FaceMedia(faces));
        var thread = ThreadWithVersions(9);
        var versionIds = VersionIds(thread);

        for (var i = 0; i < 8; i++)
            (await Call(toolset, ImageEditorToolset.ToolFindFaces,
                new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[i] })).IsError.Should().BeFalse();

        (await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[8] })).IsError.Should().BeTrue();

        await _bus.PublishAsync(new TurnCompleted(new TurnContext(ChatId, Owner, 1, 0), "success"));

        var afterReset = await Call(toolset, ImageEditorToolset.ToolFindFaces,
            new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[0] });
        afterReset.IsError.Should().BeFalse($"после сброса: {afterReset.Text}");
    }

    // (в) лимит find_faces не мешает image_generate с face (свой лимит запусков)
    [Fact]
    public async Task image_find_faces_лимит_не_мешает_face_запуску()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: FaceMedia(faces));
        var thread = ThreadWithVersions(9);
        var versionIds = VersionIds(thread);

        for (var i = 0; i < 8; i++)
            (await Call(toolset, ImageEditorToolset.ToolFindFaces,
                new JsonObject { ["threadId"] = thread, ["versionId"] = versionIds[i] })).IsError.Should().BeFalse();

        var gen = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slug, ["provider"] = "local" });
        gen.IsError.Should().BeFalse($"face-запуск после 8 find_faces: {gen.Text}");
    }

    // (г) image_generate с face без character, но персонаж «A» в контексте чата →
    // отказ: персонаж из контекста на чужое лицо не подставляется
    [Fact]
    public async Task image_generate_face_без_character_персонаж_контекста_не_подставляется()
    {
        var slug = CreateCharacter("A");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var toolset = FaceToolset(flags: Flags(true), faces: new FaceDetectionCache(), localMedia: FaceMedia(faces));
        var thread = Thread();

        // Персонаж «A» в контексте чата: primary-картинка + ref с kind image-character
        _ctxStore.SetPrimary(Owner, ChatId,
            new ContextItem("p-img", ImageContextKind.Kind, new JsonObject { ["threadId"] = thread },
                null, ContextActor.Human, DateTime.UtcNow), null);
        _ctxStore.AddRef(Owner, ChatId,
            new ContextItem("char-a", ImageContextKind.CharacterKind, new JsonObject { ["slug"] = slug },
                ImageContextRoles.Character, ContextActor.Human, DateTime.UtcNow), null);

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1 });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("контекста", "персонаж из контекста на чужое лицо не подставляется");
    }

    // ── faces: несколько лиц за один запуск (ADR-028, этап 3) ───────────────────

    // Фейк IImageEditJobs: записывает quote request (с FaceTarget) и launch request (CharacterSlug),
    // возвращает успех. В QuoteAsync читает счётчик запусков хода — доказательство «ровно один TryReserveLaunch».
    private sealed class RecordingJobs : IImageEditJobs
    {
        public ImageEditQuoteRequest? LastQuoteRequest;
        public ImageEditJobInput? LastLaunchInput;
        public int? LaunchCountAtQuote;
        private ImageEditorToolset? _toolset;

        public void SetToolset(ImageEditorToolset toolset) => _toolset = toolset;

        // Текущее число запусков хода: после отказа слот освобождается → 0
        public int CurrentLaunchCount => _toolset is null ? 0 : LaunchCount(_toolset);

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct)
        {
            LastQuoteRequest = request;
            LaunchCountAtQuote = _toolset is null ? 0 : LaunchCount(_toolset);
            var dto = new ImageEditQuoteDto("q1", "local", "auto",
                new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false, ImageEditEstimateSources.Provider),
                DateTime.UtcNow.AddMinutes(10), 60);
            return Task.FromResult(ImageEditCallResult<ImageEditQuoteDto>.Ok(dto));
        }

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            LastLaunchInput = input;
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto("j1")));
        }

        // FacePasses — число лиц из последнего записанного quote request: сборщик сверяет его
        // с faceTarget.Faces.Count (шаг 7), поэтому мок отвечает согласованным числом, а не null
        public ImageEditQuoteInfo? QuoteInfo(string ownerId, string projectId, string quoteId)
            => new ImageEditQuoteInfo(ImageEditOp.Edit, null, "local",
                LastQuoteRequest?.FaceTarget?.Faces.Count);

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct)
            => Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => null;

        private static int LaunchCount(ImageEditorToolset toolset)
        {
            var field = typeof(ImageEditorToolset).GetField("_launches",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var dict = (System.Collections.IDictionary)field.GetValue(toolset)!;
            var total = 0;
            foreach (System.Collections.DictionaryEntry entry in dict)
                total += (int)entry.Value!;
            return total;
        }
    }

    // Toolset с фейковым IImageEditJobs: quote request перехватывается, запуск «уходит» в фейк
    private (ImageEditorToolset, RecordingJobs) FacesToolset(
        IFeatureFlagGate? flags = null,
        ClaudeHomeServer.Services.ImageEditor.Faces.FaceDetectionCache? faces = null,
        ILocalImageMedia? localMedia = null)
    {
        var media = new LocalImageEditorTests.FakeMedia();
        var recordingJobs = new RecordingJobs();
        var toolset = Toolset(
            [HiggsfieldImageEditorTests.Create().Editor,
             new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) }],
            flags: flags, faces: faces, localMedia: localMedia, jobsOverride: recordingJobs);
        recordingJobs.SetToolset(toolset);
        return (toolset, recordingJobs);
    }

    private static JsonObject FacesArg(params (int face, string character)[] faces)
    {
        var array = new JsonArray();
        foreach (var (face, character) in faces)
            array.Add(new JsonObject { ["face"] = face, ["character"] = character });
        return new JsonObject { ["faces"] = array };
    }

    // faces на 2 лица расходует 1 из MaxLaunchesPerTurn (мутация «резерв на каждое лицо» → краснеет)
    [Fact]
    public async Task faces_на_2_лица_расходует_1_запуск()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var faces = new[] {
            new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false),
        };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA), (2, slugB));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        // Исход запуска здесь не проверяется (его держат тесты сборщика и сквозной HTTP-тест) — только котировка
        jobs.LastQuoteRequest.Should().NotBeNull("quote request записан");
        jobs.LastQuoteRequest!.FaceTarget!.Faces.Should().HaveCount(2);
        jobs.LastQuoteRequest!.FaceTarget!.Faces[0].Face.Should().Be(1);
        jobs.LastQuoteRequest!.FaceTarget!.Faces[0].CharacterSlug.Should().Be(slugA);
        jobs.LastQuoteRequest!.FaceTarget!.Faces[1].Face.Should().Be(2);
        jobs.LastQuoteRequest!.FaceTarget!.Faces[1].CharacterSlug.Should().Be(slugB);
        // Ровно один TryReserveLaunch: счётчик = 1, не 2
        jobs.LaunchCountAtQuote.Should().Be(1, "faces на 2 лица расходует 1 запуск, не 2");
    }

    // faces + face/character → отказ
    [Fact]
    public async Task faces_и_face_вместе_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";
        args["face"] = 1;

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("либо faces, либо face");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // faces + одиночный character (без face) → тот же отказ о несовместимости
    [Fact]
    public async Task faces_и_character_вместе_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia([new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false)]));

        var args = FacesArg((1, slugA));
        args["threadId"] = Thread();
        args["prompt"] = "фон";
        args["provider"] = "local";
        args["character"] = slugB;

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("либо faces, либо face");
        jobs.LastQuoteRequest.Should().BeNull("до котировки не дошло");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Пустой faces → отказ про пустой массив, а не «Не больше 2 лиц за раз»
    [Fact]
    public async Task faces_пустой_отказ()
    {
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia([new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false)]));

        var args = FacesArg();
        args["threadId"] = Thread();
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("faces пуст");
        jobs.LastQuoteRequest.Should().BeNull("до котировки не дошло");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Элемент faces не-объект не выбрасывается молча (иначе ушло бы меньше лиц, чем просил агент)
    [Fact]
    public async Task faces_элемент_не_объект_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia([new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false)]));

        var args = FacesArg((1, slugA));
        args["faces"]!.AsArray().Add(2);
        args["threadId"] = Thread();
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("объект {face, character}");
        jobs.LastQuoteRequest.Should().BeNull("до котировки не дошло");
    }

    // Номер лица меньше 1 → понятный отказ до котировки, без исключения
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task faces_номер_лица_меньше_1_отказ(int faceNo)
    {
        var slugA = CreateCharacter("Аня");
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia([new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false)]));

        var args = FacesArg((faceNo, slugA));
        args["threadId"] = Thread();
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("целое число от 1");
        jobs.LastQuoteRequest.Should().BeNull("до котировки не дошло");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Порядок назначений — как у агента, не по номеру лица: (2 → Боря), (1 → Аня)
    [Fact]
    public async Task faces_порядок_назначений_как_в_аргументе()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia([
                new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
                new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false)]));

        var args = FacesArg((2, slugB), (1, slugA));
        args["threadId"] = Thread();
        args["prompt"] = "фон";
        args["provider"] = "local";

        await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        jobs.LastQuoteRequest.Should().NotBeNull();
        jobs.LastQuoteRequest!.FaceTarget!.Faces.Select(f => (f.Face, f.CharacterSlug))
            .Should().Equal((2, slugB), (1, slugA));
        jobs.LastQuoteRequest!.CharacterSlug.Should().BeNull("персонаж — только в FaceTarget.Faces");
    }

    // повтор персонажа → отказ
    [Fact]
    public async Task faces_повтор_персонажа_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var faces = new[] {
            new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false),
        };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA), (2, slugA));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("свой персонаж");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // три лица → отказ
    [Fact]
    public async Task faces_три_лица_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var slugC = CreateCharacter("Вера");
        var faces = new[] {
            new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(3, 1, 1, 3, 3, 0.9, false),
        };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA), (2, slugB), (3, slugC));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("2 лиц");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // повтор номера лица → отказ
    [Fact]
    public async Task faces_повтор_номера_лица_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var faces = new[] {
            new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false),
        };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA), (1, slugB));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("указано дважды");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Пустой/пробельный character в элементе faces → явный отказ про персонажа,
    // а не вводящий в заблуждение «Не больше 2 лиц за раз» (мутация: вернуть continue → краснеет)
    [Fact]
    public async Task faces_пустой_character_отказ_про_персонажа()
    {
        var slugA = CreateCharacter("Аня");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, "  "));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "local";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("укажи персонажа", "причина — отсутствие персонажа, а не лимит лиц");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Отказ не тратит слот запуска: повторный запуск в том же ходе после отказа проходит.
    // Если бы отказ не освободил слот, второй успешный запуск уперся бы в лимит MaxLaunchesPerTurn
    [Fact]
    public async Task faces_отказ_не_тратит_слот_повторный_запуск_в_том_же_ходе_проходит()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        // Один детектированный face 1: для успешных запусков; отказной вызов (повтор персонажа)
        // отбивается в тулсете до детекции, поэтому второе лицо детекции не нужно
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        // 1) успешный запуск (слот 1)
        var ok = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slugA, ["provider"] = "local" });
        ok.IsError.Should().BeFalse(ok.Text);

        // 2) отказной запуск: повтор персонажа (резервирует слот, затем освобождает)
        var denyArgs = FacesArg((1, slugA), (2, slugA));
        denyArgs["threadId"] = thread;
        denyArgs["prompt"] = "фон";
        denyArgs["provider"] = "local";
        var deny = await Call(toolset, ImageEditorToolset.ToolGenerate, denyArgs);
        deny.IsError.Should().BeTrue();
        deny.Text.Should().Contain("свой персонаж");
        jobs.CurrentLaunchCount.Should().Be(1, "после отказа слот освобождён — остался только первый запуск");

        // 3) повторный запуск в том же ходе: если бы отказ тратил слот, уперся бы в лимит
        var again = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slugB, ["provider"] = "local" });
        again.IsError.Should().BeFalse(again.Text);
    }

    // faces без provider local → отказ как у одиночной подстановки
    [Fact]
    public async Task faces_без_provider_local_отказ()
    {
        var slugA = CreateCharacter("Аня");
        var slugB = CreateCharacter("Боря");
        var faces = new[] {
            new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false),
            new LocalDetectedFace(2, 1, 1, 3, 3, 0.9, false),
        };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var args = FacesArg((1, slugA), (2, slugB));
        args["threadId"] = thread;
        args["prompt"] = "фон";
        args["provider"] = "fal";

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("локальными");
        jobs.CurrentLaunchCount.Should().Be(0, "отказ не тратит слот запуска");
    }

    // Замечание Глеба (b8cca6705): в запросе запуска CharacterSlug == null,
    // FaceTarget.Faces содержит ожидаемый элемент
    [Fact]
    public async Task face_успех_CharacterSlug_null_Faces_содержит_элемент()
    {
        var slug = CreateCharacter("Тест");
        var faces = new[] { new LocalDetectedFace(1, 1, 1, 3, 3, 0.9, false) };
        var (toolset, jobs) = FacesToolset(flags: Flags(true), faces: new FaceDetectionCache(),
            localMedia: FaceMedia(faces));
        var thread = Thread();

        var result = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = thread, ["prompt"] = "фон", ["face"] = 1,
                ["character"] = slug, ["provider"] = "local" });

        result.IsError.Should().BeFalse(result.Text);
        jobs.LastQuoteRequest.Should().NotBeNull();
        jobs.LastQuoteRequest!.FaceTarget.Should().NotBeNull();
        jobs.LastQuoteRequest!.FaceTarget!.Faces.Should().ContainSingle();
        jobs.LastQuoteRequest!.FaceTarget!.Faces[0].Face.Should().Be(1);
        jobs.LastQuoteRequest!.FaceTarget!.Faces[0].CharacterSlug.Should().Be(slug);
        // Верхний CharacterSlug не едет: персонаж — только в FaceTarget.Faces
        jobs.LastQuoteRequest!.CharacterSlug.Should().BeNull(
            "персонаж задаётся в FaceTarget.Faces, верхний CharacterSlug — null");
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
