using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Mcp;

// Схемы MCP статичны (ADR-025 §8): пропорции и ступени модели в схему не попадают — иначе схема зависела бы от
// каталога (живого у Higgsfield) и префов, а это перезапуск CLI со всеми серверами и обнулённый prefix cache.
// Сторож: схемы инструментов при разных каталогах и разных префах побайтно одинаковы. Состав tools/list
// держит McpToolsetStabilityTests.
public class McpSchemaParityTests : IDisposable
{
    private const string Owner = "user-b";
    private const string ChatId = "chat-b";
    private const string ProjectId = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-parity-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditorToolset Toolset(IImageEditor[] editors, string sub, ImageProjectPrefs? savedPrefs = null)
    {
        var root = Path.Combine(_dir, sub);
        Directory.CreateDirectory(root);
        var workspace = new ImageEditWorkspace(Path.Combine(root, "ws"));
        var jobs = new ImageEditJobService(editors, workspace, NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(),
            new RecordingBroadcaster());
        _services.Add(jobs);
        var session = new Session { Id = ChatId, OwnerId = Owner, ProjectId = ProjectId };
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(ChatId)).Returns(session);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = root });
        var prefsStore = new ImageProjectPrefsStore(Path.Combine(root, "prefs"));
        if (savedPrefs is not null) prefsStore.Save(Owner, ProjectId, savedPrefs);
        var prefs = new ImageProjectPrefsService(prefsStore, NullLogger<ImageProjectPrefsService>.Instance, projects.Object);
        var threads = new ImageThreadService(new ImageThreadStore(Path.Combine(root, "threads")),
            NullLogger<ImageThreadService>.Instance, directory.Object, prefs: prefs);
        var launcher = new ImageEditLaunchAssembler(editors, jobs, new SkiaImageRaster(), threads);
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(ChatId, Owner)).Returns(session);
        return new ImageEditorToolset(accessor.Object, projects.Object, editors, threads, launcher,
            new Mock<IDelegatedTurnGate>().Object, jobs, prefs: prefs,
            config: TestImages.Config((ImageEditorToolset.AgentLaunchKey, "true")));
    }

    private static string Schemas(ImageEditorToolset toolset) =>
        new JsonArray([.. toolset.ToolsFor(new McpToolCallContext(Owner, ChatId, ChatId))
            .Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["schema"] = t.InputSchema.DeepClone() })])
            .ToJsonString();

    // Каталог со своими пропорциями и ступенями — совсем другой, чем у Higgsfield
    private static IImageEditor FakeEditorWithFormat()
    {
        var caps = new MediaFormatCaps("Фейк", ["7:5", "5:7"],
            [new FormatTierSpec(FormatTier.Standard, "1 Мп", new Dictionary<string, FormatSize>())], null, "resolution");
        var model = new ImageEditModelInfo("fake-model", "Фейк",
            new ImageEditCaps([ImageEditOp.Generate], MaskSupport.None, 0, 4, false, Format: caps));
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("fake");
        editor.SetupGet(e => e.Label).Returns("Фейк");
        editor.SetupGet(e => e.Enabled).Returns(true);
        editor.SetupGet(e => e.Models).Returns([model]);
        return editor.Object;
    }

    [Fact]
    public void Схемы_при_разных_каталогах_и_префах_побайтно_одинаковы()
    {
        var a = Toolset([HiggsfieldImageEditorTests.Create().Editor], "a");
        var withFormat = new ImageProjectPrefs(null, null, 2, true, null, Format: new MediaFormat(FormatModes.Aspect, "9:16",
            FormatTier.Max, null, null, ClaudeHomeServer.Services.ChatContext.ContextActor.Agent, DateTime.UtcNow));
        var b = Toolset([FakeEditorWithFormat()], "b", withFormat);

        Schemas(a).Should().Be(Schemas(b));
    }

    [Fact]
    public void Пропорции_и_ступени_моделей_в_схему_не_попадают()
    {
        var json = Schemas(Toolset([HiggsfieldImageEditorTests.Create().Editor], "c"));

        json.Should().NotContain("21:9", "пропорции моделей — в image_state, а не в enum схемы");
        json.Should().NotContain("27:16");
        var generate = JsonNode.Parse(json)!.AsArray().Single(n => n!["name"]!.GetValue<string>() == ImageEditorToolset.ToolGenerate)!;
        generate["schema"]!["properties"]!["aspect"]!.AsObject().ContainsKey("enum").Should().BeFalse();
        generate["schema"]!["properties"]!["quality"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("standard", "high", "max");
    }

    [Fact]
    public void У_аргументов_формата_оговорка_без_просьбы_человека_не_передавать()
    {
        var generate = JsonNode.Parse(Schemas(Toolset([HiggsfieldImageEditorTests.Create().Editor], "d")))!.AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == ImageEditorToolset.ToolGenerate)!;

        foreach (var name in new[] { "aspect", "quality", "width", "height" })
            generate["schema"]!["properties"]![name]!["description"]!.GetValue<string>().Should().Contain("без просьбы человека", name);
    }
}
