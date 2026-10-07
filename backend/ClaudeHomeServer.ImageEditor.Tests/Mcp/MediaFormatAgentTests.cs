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
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Mcp;

// «Формат» у агента (ADR-025): op = crop — бесплатная обрезка новой версией тем же методом, что у кнопки человека;
// edit с чужими пропорциями — отказ с подсказкой; явный формат генерации пишется в префы проекта с ✦.
public class MediaFormatAgentTests : IDisposable
{
    private const string Owner = "user-b";
    private const string ChatId = "chat-b";
    private const string ProjectId = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-fmt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly List<(string SessionId, StoredModuleRecord Record)> _records = [];
    private readonly ImageThreadStore _store;
    private readonly ImageProjectPrefsService _prefs;
    private readonly ImageProjectPrefsStore _prefsStore;
    private readonly ImageThreadService _threads;
    private readonly ImageEditorToolset _toolset;
    private readonly ImageEditJobService _jobs;
    private readonly Mock<IImageEditor> _editor = new();

    public MediaFormatAgentTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        using (var bitmap = new SKBitmap(1600, 1200))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            File.WriteAllBytes(Path.Combine(_root, "images", "wide.png"), image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));

        var editors = new IImageEditor[] { HiggsfieldImageEditorTests.Create().Editor };
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        _jobs = new ImageEditJobService(editors, workspace, NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), new RecordingBroadcaster());
        var steps = new ImageEditSteps(new SkiaImageRaster(), workspace, _jobs);

        var directory = new Mock<ISessionDirectory>();
        var session = new Session { Id = ChatId, OwnerId = Owner, ProjectId = ProjectId };
        directory.Setup(d => d.GetById(ChatId)).Returns(session);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });
        _prefsStore = new ImageProjectPrefsStore(Path.Combine(_dir, ImageProjectPrefsStore.DirName));
        _prefs = new ImageProjectPrefsService(_prefsStore, NullLogger<ImageProjectPrefsService>.Instance, projects.Object);
        var feed = new Mock<IChatFeed>();
        feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .Callback((string id, StoredModuleRecord r, CancellationToken _) => { lock (_records) _records.Add((id, r)); })
            .ReturnsAsync(true);
        _threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, feed.Object,
            new RecordingBroadcaster(), steps, _prefs);
        var launcher = new ImageEditLaunchAssembler(editors, _jobs, new SkiaImageRaster(), _threads);

        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(It.IsAny<string>(), FeatureFlagKeys.ImageEditor)).Returns(true);
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(ChatId, Owner)).Returns(session);
        var gate = new Mock<IDelegatedTurnGate>();
        _toolset = new ImageEditorToolset(accessor.Object, flags.Object, projects.Object, editors, _threads, launcher,
            gate.Object, _jobs, steps: steps, prefs: _prefs, config: TestImages.Config((ImageEditorToolset.AgentLaunchKey, "true")));
        _editor.SetupGet(e => e.Key).Returns("fake");
    }

    public void Dispose()
    {
        _jobs.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditScope Scope => ImageEditScope.Of(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });

    private string WideThread() =>
        _store.Open(Owner, ChatId, "images/wide.png", null, _store.Get(Owner, ChatId).Revision).Thread!.Id;

    private string Draft() =>
        _store.Open(Owner, ChatId, null, "", _store.Get(Owner, ChatId).Revision).Thread!.Id;

    private async Task<McpToolCallResult> Gen(JsonObject args) =>
        await _toolset.CallAsync(ImageEditorToolset.ToolGenerate, args, new McpToolCallContext(Owner, ChatId, ChatId), default);

    private static JsonObject Parse(McpToolCallResult r) => JsonNode.Parse(r.Text)!.AsObject();

    // ── op = crop ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Агент_обрезает_новой_версией_без_ИИ_и_бесплатно()
    {
        var id = WideThread();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["op"] = "crop", ["aspect"] = "1:1", ["anchor"] = "left" });

        result.IsError.Should().BeFalse(result.Text);
        var json = Parse(result);
        json["size"]!["width"]!.GetValue<int>().Should().Be(1200);
        json["size"]!["height"]!.GetValue<int>().Should().Be(1200);
        var thread = _store.Get(Owner, ChatId).Threads.Single();
        thread.Versions.Should().HaveCount(2, "обрезка — новая версия, а не шаг");
        thread.CurrentVersion!.JobId.Should().StartWith("crop-");
        thread.CurrentVersion.BaseVersionId.Should().Be(ImageThreadVersion.OriginId);
        _jobs.Get(Owner, ProjectId, "any").Should().BeNull();
    }

    [Fact]
    public async Task Обрезка_человека_и_агента_идёт_одним_методом_и_даёт_одну_и_ту_же_запись()
    {
        var agentThread = WideThread();
        (await Gen(new JsonObject { ["threadId"] = agentThread, ["op"] = "crop", ["aspect"] = "1:1", ["anchor"] = "left" })).IsError.Should().BeFalse();
        var agentRecord = _records.Single(r => r.Record.RecordType == ImageThreadService.RecordTypes.LaunchVersions);

        // Человек — тот же метод сервиса, который зовёт ручка …/crop: от исходника той же нити
        _records.Clear();
        var human = await _threads.CropToAspectAsync(Owner, Scope, ChatId, agentThread, ImageThreadVersion.OriginId, "1:1", "left",
            ImageEditInitiator.Human, false, default);

        human.Ok.Should().BeTrue(human.Error);
        var humanRecord = _records.Single(r => r.Record.RecordType == ImageThreadService.RecordTypes.LaunchVersions);
        agentRecord.Record.Fallback.Should().Be("✦ Claude обрезал без ИИ: 1200×1200 · 1:1 · бесплатно · версия 1");
        humanRecord.Record.Fallback.Should().Be("Вы обрезали без ИИ: 1200×1200 · 1:1 · бесплатно · версия 2",
            "одна карточка в ленте у человека и у агента, различаются только автор и номер версии");
        humanRecord.Record.Data!.Value.GetProperty("crop").ToString().Should()
            .Be(agentRecord.Record.Data!.Value.GetProperty("crop").ToString());
        _store.Get(Owner, ChatId).Threads.Single().Versions.Where(v => !v.IsOrigin).Should().HaveCount(2);
    }

    [Fact]
    public async Task Обрезка_не_трогает_формат_проекта_и_провайдера()
    {
        var id = WideThread();
        await _prefs.SetFormatAsync(Owner, Scope, new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null,
            ContextActor.Human, DateTime.UtcNow), ContextActor.Human);

        (await Gen(new JsonObject { ["threadId"] = id, ["op"] = "crop", ["aspect"] = "1:1" })).IsError.Should().BeFalse();

        _prefs.Get(Owner, Scope).Format!.Aspect.Should().Be("16:9");
        _prefs.Get(Owner, Scope).Format!.By.Should().Be(ContextActor.Human);
        _records.Should().NotContain(r => r.Record.RecordType == ImageThreadService.RecordTypes.Format);
    }

    [Fact]
    public async Task Обрезка_без_aspect_и_до_тех_же_пропорций_отказ()
    {
        var id = WideThread();

        (await Gen(new JsonObject { ["threadId"] = id, ["op"] = "crop" })).IsError.Should().BeTrue();
        var same = await Gen(new JsonObject { ["threadId"] = id, ["op"] = "crop", ["aspect"] = "4:3" });
        same.IsError.Should().BeTrue();
        same.Text.Should().Contain("обрезать нечего");
        _store.Get(Owner, ChatId).Threads.Single().Versions.Should().HaveCount(1);
    }

    // ── правка с чужими пропорциями ─────────────────────────────────────────

    [Fact]
    public async Task Правка_с_чужими_пропорциями_отказ_с_подсказкой_про_outpaint_и_crop()
    {
        var id = WideThread();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "ярче", ["aspect"] = "1:1" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("outpaint").And.Contain("crop");
        _store.Get(Owner, ChatId).Threads.Single().Launches.Should().BeEmpty("запуска не было");
    }

    [Fact]
    public async Task Правка_с_пропорциями_исходника_проходит_а_размер_качество_у_неё_отказ()
    {
        var id = WideThread();

        (await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "ярче", ["aspect"] = "4:3" })).IsError.Should().BeFalse();
        var quality = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "ярче", ["quality"] = "high" });
        quality.IsError.Should().BeTrue();
        quality.Text.Should().Contain("исходник");
    }

    // ── явный формат генерации ──────────────────────────────────────────────

    [Fact]
    public async Task Явный_формат_генерации_пишется_в_префы_проекта_с_пометкой_агента_и_тихой_строкой()
    {
        var id = Draft();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "кот", ["aspect"] = "9:16", ["quality"] = "high" });

        result.IsError.Should().BeFalse(result.Text);
        var saved = _prefs.Get(Owner, Scope).Format!;
        saved.Mode.Should().Be(FormatModes.Aspect);
        saved.Aspect.Should().Be("9:16");
        saved.Tier.Should().Be(FormatTier.High);
        saved.By.Should().Be(ContextActor.Agent);
        var line = _records.Single(r => r.Record.RecordType == ImageThreadService.RecordTypes.Format).Record;
        line.Fallback.Should().Be("✦ Claude сменил формат картинок проекта: как у модели → 9:16 · Высокое");
        Parse(result)["quote"]!["format"]!["wireValue"]!.GetValue<string>().Should().Be("2k", "формат ушёл в котировку");
    }

    // Правка Глеба по Б1: quality вне standard/high/max — явный отказ, а не молчаливое «как есть»
    [Theory]
    [InlineData("ultra")]
    [InlineData("HIGH")]
    public async Task Неизвестная_ступень_quality_отказ_и_формат_проекта_не_меняется(string quality)
    {
        var id = Draft();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "кот", ["aspect"] = "9:16", ["quality"] = quality });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("standard, high или max");
        _prefs.Get(Owner, Scope).Format.Should().BeNull();
        _records.Should().NotContain(r => r.Record.RecordType == ImageThreadService.RecordTypes.Format);
    }

    // Правка Глеба по Б1: «пропорции» без пропорций (и без ступени, и со ступенью) отклоняет валидация, а не маскирует
    [Fact]
    public void Желание_пропорции_без_пропорций_отклоняется_валидацией()
    {
        var broken = new MediaFormat(FormatModes.Aspect, null, null, null, null, ContextActor.Human, DateTime.UtcNow);

        ImageProjectPrefsService.ValidateFormat(broken).Should().NotBeNull();
        ImageProjectPrefsService.ValidateFormat(broken with { Tier = FormatTier.High }).Should().NotBeNull();
        ImageProjectPrefsService.ValidateFormat(broken with { Aspect = "16:9" }).Should().BeNull();
    }

    // Правка Глеба по Б1: обрезка бесплатная и сознательно не считается в MaxLaunchesPerTurn
    [Fact]
    public async Task Обрезка_не_выедает_лимит_запусков_хода_генерации()
    {
        var thread = WideThread();
        for (var i = 0; i < ImageEditorToolset.MaxLaunchesPerTurn + 2; i++)
        {
            var crop = await Gen(new JsonObject { ["threadId"] = thread, ["op"] = "crop", ["aspect"] = i % 2 == 0 ? "1:1" : "16:9" });
            crop.IsError.Should().BeFalse($"обрезка №{i + 1}: {crop.Text}");
        }
    }

    [Fact]
    public async Task Ручная_запись_формата_снимает_пометку_агента()
    {
        await _prefs.SetFormatAsync(Owner, Scope, new MediaFormat(FormatModes.Aspect, "9:16", null, null, null,
            ContextActor.Agent, DateTime.UtcNow), ContextActor.Agent);
        _prefs.Get(Owner, Scope).Format!.By.Should().Be(ContextActor.Agent);

        var human = _prefs.Get(Owner, Scope) with
        {
            Format = new MediaFormat(FormatModes.Aspect, "1:1", FormatTier.Max, null, null, ContextActor.Agent, DateTime.UtcNow),
        };
        await _prefs.SetAsync(Owner, Scope, human);

        _prefs.Get(Owner, Scope).Format!.By.Should().Be(ContextActor.Human, "автора ставит сервер, ✦ от клиента не получить");
    }

    [Fact]
    public async Task Генерация_без_явного_формата_берёт_формат_проекта_и_не_пишет_строку()
    {
        await _prefs.SetFormatAsync(Owner, Scope, new MediaFormat(FormatModes.Aspect, "3:4", FormatTier.Standard, null, null,
            ContextActor.Human, DateTime.UtcNow), ContextActor.Human);
        var id = Draft();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "кот" });

        result.IsError.Should().BeFalse(result.Text);
        Parse(result)["quote"]!["format"]!["aspect"]!.GetValue<string>().Should().Be("3:4");
        _records.Should().NotContain(r => r.Record.RecordType == ImageThreadService.RecordTypes.Format);
    }

    [Fact]
    public async Task Совместимость_префы_без_формата_это_Как_у_модели()
    {
        var id = Draft();

        var result = await Gen(new JsonObject { ["threadId"] = id, ["prompt"] = "кот" });

        result.IsError.Should().BeFalse(result.Text);
        _prefs.Get(Owner, Scope).Format.Should().BeNull();
        Parse(result)["quote"]!["format"]!["mode"]!.GetValue<string>().Should().Be(FormatModes.Model);
    }

    // ── строка «Формат:» хвоста хода ───────────────────────────────────────

    private string TailFormat()
    {
        var editors = new IImageEditor[] { HiggsfieldImageEditorTests.Create().Editor };
        var kind = new ImageContextKind(_store, _prefsStore, editors);
        var primary = new ContextItem("ci", ImageContextKind.Kind, new JsonObject { ["threadId"] = Draft() }, null, ContextActor.Human, DateTime.UtcNow);
        var session = new Session { Id = ChatId, OwnerId = Owner, ProjectId = ProjectId };
        return kind.DescribeFormat(new ContextScope(Owner, session, new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root }), primary)!;
    }

    [Fact]
    public async Task Хвост_без_сохранённого_формата_говорит_Как_у_модели_и_не_просит_передавать()
    {
        TailFormat().Should().StartWith("как у модели").And.Contain("не передавай");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Хвост_несёт_формат_проекта_с_метками_автора_и_подменой()
    {
        await _prefs.SetFormatAsync(Owner, Scope, new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null,
            ContextActor.Agent, DateTime.UtcNow), ContextActor.Agent);

        // Higgsfield по умолчанию — Nano Banana 2: 16:9 есть, «Высокое» = 2k
        TailFormat().Should().Be("16:9 · Высокое — ✦ поставил Claude");

        await _prefs.SetFormatAsync(Owner, Scope, new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null,
            ContextActor.Human, DateTime.UtcNow), ContextActor.Human);
        TailFormat().Should().Be("16:9 · Высокое — выбор человека");
    }

    // Личный чат вне проекта: формат хранится под ключом «personal», и хвост хода обязан читать именно его, а не умолчание
    [Fact]
    public async Task Хвост_личного_чата_несёт_сохранённый_формат_личной_области()
    {
        var personal = ImageEditScope.Of(new Session { Id = ChatId, OwnerId = Owner, ProjectId = null });
        await _prefs.SetFormatAsync(Owner, personal, new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null,
            ContextActor.Agent, DateTime.UtcNow), ContextActor.Agent);
        var editors = new IImageEditor[] { HiggsfieldImageEditorTests.Create().Editor };
        var kind = new ImageContextKind(_store, _prefsStore, editors);
        var primary = new ContextItem("ci", ImageContextKind.Kind, new JsonObject { ["threadId"] = Draft() }, null, ContextActor.Human, DateTime.UtcNow);
        var session = new Session { Id = ChatId, OwnerId = Owner, ProjectId = null };

        kind.DescribeFormat(new ContextScope(Owner, session, null), primary)
            .Should().Be("16:9 · Высокое — ✦ поставил Claude");
    }
}
