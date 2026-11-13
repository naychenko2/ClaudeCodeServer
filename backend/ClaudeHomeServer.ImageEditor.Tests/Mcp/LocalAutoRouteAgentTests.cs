using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Mcp;

// Дефект a2a894cf: агент в личном чате звал image_generate по черновику без provider/model, строка
// «Чем» говорила «Локальные модели · Авто», а запуск ушёл на MOSS — модель выбора «Создать», которую
// человек оставил с замеров. MCP и «Чем» обязаны читать один и тот же выбор (LaunchSettings), а «Авто»
// на пути MCP — получать промпт и выбирать по таблице
public class LocalAutoRouteAgentTests : IDisposable
{
    private const string Owner = "user-r";
    private const string ChatId = "chat-r";
    private const string Fisher = "Фотография: бородатый рыбак в непромокаемой куртке и резиновых сапогах стоит на "
        + "деревянной лодке у озера и с гордостью держит двумя руками огромную рыбу — щуку длиной почти в его рост.";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-route-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Session _session = new() { Id = ChatId, OwnerId = Owner, ProjectId = null };
    private readonly ImageThreadStore _store;
    private readonly ImageProjectPrefsStore _prefsStore;
    private readonly ImageProjectPrefsService _prefs;
    private readonly ImageEditJobService _jobs;
    private readonly ImageEditorToolset _toolset;
    private readonly IImageEditor[] _editors = [new LocalImageEditor(new LocalImageEditorTests.FakeMedia())];

    public LocalAutoRouteAgentTests()
    {
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        _jobs = new ImageEditJobService(_editors, workspace, NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), new RecordingBroadcaster());
        var steps = new ImageEditSteps(new SkiaImageRaster(), workspace, _jobs);

        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(ChatId)).Returns(_session);
        var projects = new Mock<IProjectManager>();
        _prefsStore = new ImageProjectPrefsStore(Path.Combine(_dir, ImageProjectPrefsStore.DirName));
        _prefs = new ImageProjectPrefsService(_prefsStore, NullLogger<ImageProjectPrefsService>.Instance, projects.Object);
        var feed = new Mock<IChatFeed>();
        feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, feed.Object,
            new RecordingBroadcaster(), steps, _prefs);
        var launcher = new ImageEditLaunchAssembler(_editors, _jobs, new SkiaImageRaster(), threads);

        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(ChatId, Owner)).Returns(_session);
        _toolset = new ImageEditorToolset(accessor.Object, projects.Object, _editors, threads, launcher,
            new Mock<IDelegatedTurnGate>().Object, _jobs, steps: steps, prefs: _prefs,
            config: TestImages.Config((ImageEditorToolset.AgentLaunchKey, "true")));
    }

    public void Dispose()
    {
        _jobs.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static ImageEditScope Personal => ImageEditScope.Of(new Session { Id = ChatId, OwnerId = Owner, ProjectId = null });

    // Черновик, как его заводит image_new: настройки нити засеяны выбором «Править» (локальные, «Авто»)
    private string Draft()
    {
        var id = _store.Open(Owner, ChatId, null, "", _store.Get(Owner, ChatId).Revision).Thread!.Id;
        _store.SetSettings(Owner, ChatId, id, new ImageThreadSettings("local", null, 1, true), _store.Get(Owner, ChatId).Revision);
        return id;
    }

    private Task SetCreate(string? model, int count) =>
        _prefs.SetAsync(Owner, Personal, ImageProjectPrefs.Default with
        {
            Create = new ImageCreatePrefs("local", model, count),
            Edit = new ImageEditPrefs("local", null, 1, null, null, null),
        });

    private async Task<JsonObject> Generate(string threadId)
    {
        var result = await _toolset.CallAsync(ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = threadId, ["prompt"] = Fisher }, new McpToolCallContext(Owner, ChatId, ChatId), default);
        result.IsError.Should().BeFalse(result.Text);
        return JsonNode.Parse(result.Text)!.AsObject();
    }

    private string Executor(string threadId) =>
        new ImageContextKind(_store, _prefsStore, _editors).DescribeExecutor(new ContextScope(Owner, _session, null),
            new ContextItem("ci", ImageContextKind.Kind, new JsonObject { ["threadId"] = threadId }, null, ContextActor.Human, DateTime.UtcNow))!;

    [Fact]
    public async Task MCP_image_generate_с_Авто_передаёт_промпт_в_таблицу_и_фото_уходит_на_Krea()
    {
        await SetCreate("auto", 1);
        var id = Draft();

        var json = await Generate(id);

        json["quote"]!["model"]!.GetValue<string>().Should().Be(LocalImageEditor.Krea2Turbo,
            "«Фотография» — признак Photo, правило people-gen");
        Executor(id).Should().StartWith("Локальные модели · Авто");
    }

    [Fact]
    public async Task Чем_черновика_называет_модель_Создать_ту_же_что_берёт_MCP_image_generate()
    {
        // Человек оставил в «Создать» ручной MOSS, «Править» — «Авто»
        await SetCreate(LocalImageEditor.MossImage, 2);
        var id = Draft();

        var executor = Executor(id);
        var json = await Generate(id);

        var model = json["quote"]!["model"]!.GetValue<string>();
        model.Should().Be(LocalImageEditor.MossImage, "ручной выбор человека главнее «Авто»");
        var label = _editors[0].Models.Single(m => m.Id == model).Label;
        executor.Should().Contain(label, "агент должен видеть ту модель, на которой пойдёт запуск, а не «Авто» нити");
    }
}
