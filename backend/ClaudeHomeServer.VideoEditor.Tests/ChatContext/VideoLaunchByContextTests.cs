using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.ChatContext;

// Съёмка по ревизии контекста (КТ-5, 3б-1): сцена и кадры берутся из стора, а не из полей сцены и тела;
// текст поля ввода едет в params.request; ревизия котировки обязана совпасть с ревизией запуска
public sealed class VideoLaunchByContextTests : IDisposable
{
    private const string Owner = "u1";
    private const string Chat = "c1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vlaunch-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly VideoEditScope _scope;
    private readonly VideoThreadStore _threads;
    private readonly ChatContextStore _context;
    private readonly VideoEditJobService _svc;
    private readonly Camera _camera = new();

    public VideoLaunchByContextTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "video"));
        File.WriteAllBytes(Path.Combine(_project, "video", "a.png"), [10, 11]);
        File.WriteAllBytes(Path.Combine(_project, "video", "b.png"), [20, 21]);
        _scope = new VideoEditScope("proj1", new Project { Id = "proj1", RootPath = _project });
        _threads = new VideoThreadStore(Path.Combine(_root, "threads"));
        var kind = new VideoContextKind(_threads);
        var registry = new ContextKindRegistry([kind, new ProjectFileContextKind()]);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), registry);
        var jobThreads = new VideoJobThreads(_threads, NullLogger<VideoJobThreads>.Instance);
        _svc = new VideoEditJobService([_camera], new VideoEditWorkspace(Path.Combine(_root, "ws")), jobThreads,
            NullLogger<VideoEditJobService>.Instance, context: new VideoContextLaunch(_context, registry));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Поставщик, снимающий только от кадра A (с необязательным B): запоминает, что получил
    private sealed class Camera : IVideoEngine
    {
        public string Key => "fal";
        public string Label => "fal";
        public string PriceUnit => "usd";
        public bool Enabled => true;
        public VideoRequest? Seen;
        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new VideoModelInfo("cam", "cam", new VideoCaps([5], ["16:9"], false, true, VideoLicense.Commercial, "usd"),
                new VideoPriceHint(0.1, "usd", "sec")),
        ];

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            Seen = req;
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r", Accepted: true));
            return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([1], "video/mp4", ".mp4", 5, false), null, true, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }

    private string AddScene(FrameRef? settingsFrame = null)
    {
        var settings = new VideoSceneSettingsDto(settingsFrame, null, "закат", null, null, null, null, null, null);
        return _threads.AddScene(Owner, Chat, "", settings, null).Scene!.SceneId;
    }

    private static ContextItem Scene(string id) =>
        new("p", "video-scene", new JsonObject { ["sceneId"] = id }, null, ContextActor.Human, DateTime.UtcNow);

    private static ContextItem Ref(string kind, JsonObject reference, string role, DateTime? at = null) =>
        new("r" + Guid.NewGuid().ToString("N")[..6], kind, reference, role, ContextActor.Human, at ?? DateTime.UtcNow);

    private static JsonObject File_(string path) => new() { ["path"] = path };

    private long Revision => _context.Get(Owner, Chat).Revision;

    private ChatContextState Prepare(string scene, params ContextItem[] refs)
    {
        _context.SetPrimary(Owner, Chat, Scene(scene), null);
        foreach (var r in refs) _context.AddRef(Owner, Chat, r, null);
        return _context.Get(Owner, Chat);
    }

    private Task<VideoEditCallResult<VideoQuoteResponse>> Quote(long revision, string sceneInBody = "") =>
        _svc.QuoteAsync(Owner, _scope, new VideoQuoteRequest(Chat, sceneInBody, null, null, 1, 5, null, null, revision), default);

    private async Task<VideoEditCallResult<VideoJobCreated>> Launch(VideoQuoteResponse quote, long? revision, JsonObject? p = null)
    {
        var started = await _svc.StartAsync(Owner, _scope,
            new VideoLaunchRequest(quote.QuoteId, Chat, "", null, p, null, revision), default);
        if (started.Value is { } job) await _svc.WhenDone(job.JobId);
        return started;
    }

    [Fact]
    public async Task Сцена_и_кадры_берутся_из_контекста_а_не_из_тела_и_полей_сцены()
    {
        var scene = AddScene();
        var state = Prepare(scene,
            Ref("project-file", File_("video/a.png"), "frame-a"), Ref("project-file", File_("video/b.png"), "frame-b"));

        var quote = await Quote(state.Revision);
        quote.Error.Should().BeNull();
        var started = await Launch(quote.Value!, state.Revision);

        started.Error.Should().BeNull();
        _camera.Seen!.FrameA!.Bytes.Should().Equal(10, 11);
        _camera.Seen.FrameB!.Bytes.Should().Equal(20, 21);
        var saved = _threads.Get(Owner, Chat).Scenes.Single().Settings;
        saved.FrameA.Should().Be(FrameRef.File("video/a.png"));
        saved.FrameB.Should().Be(FrameRef.File("video/b.png"));
    }

    [Fact]
    public async Task Кадр_в_полях_сцены_при_ревизии_игнорируется()
    {
        // В настройках сцены кадр A есть, в контексте — нет: при ревизии съёмка идёт без кадров, и модели,
        // которой нужен первый кадр, для такой сцены нет
        var scene = AddScene(FrameRef.File("video/a.png"));
        var state = Prepare(scene);

        var quote = await Quote(state.Revision);

        quote.Value.Should().BeNull();
        quote.ErrorCode.Should().Be(VideoEditorErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task Текст_поля_ввода_дописывается_к_тексту_сцены_а_сама_сцена_не_меняется()
    {
        var scene = AddScene();
        var state = Prepare(scene, Ref("project-file", File_("video/a.png"), "frame-a"));
        var quote = (await Quote(state.Revision)).Value!;

        var started = await Launch(quote, state.Revision, new JsonObject { ["request"] = "добавь туман" });

        started.Error.Should().BeNull();
        _camera.Seen!.Text.Should().Be("закат\n\nдобавь туман");
        _threads.Get(Owner, Chat).Scenes.Single().Settings.Text.Should().Be("закат");
    }

    [Fact]
    public async Task Устаревшая_ревизия_котировки_и_запуска_даёт_context_changed_со_свежим_контекстом()
    {
        var scene = AddScene();
        var state = Prepare(scene, Ref("project-file", File_("video/a.png"), "frame-a"));
        var stale = await Quote(state.Revision - 1);
        stale.ErrorCode.Should().Be(VideoEditorErrors.ContextChanged);
        stale.Context.Should().BeNull("в тестах нет справочника чатов — свежего DTO собрать не из чего");

        var quote = (await Quote(state.Revision)).Value!;
        _context.AddRef(Owner, Chat, Ref("project-file", File_("video/b.png"), "frame-b"), null);
        var late = await Launch(quote, state.Revision);

        late.ErrorCode.Should().Be(VideoEditorErrors.ContextChanged);
        _camera.Seen.Should().BeNull("до поставщика запуск не дошёл");
    }

    [Fact]
    public async Task Котировка_с_ревизией_не_запускается_без_ревизии_и_на_чужой_ревизии()
    {
        var scene = AddScene();
        var state = Prepare(scene, Ref("project-file", File_("video/a.png"), "frame-a"));
        var quote = (await Quote(state.Revision)).Value!;

        (await Launch(quote, null)).ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);

        var next = _context.AddRef(Owner, Chat, Ref("project-file", File_("video/b.png"), "frame-b"), null);
        (await Launch(quote, next.Revision)).ErrorCode.Should().Be(VideoEditorErrors.ContextChanged,
            "ревизия стора совпала, но котировка выписана на прежний состав кадров");
    }

    [Fact]
    public async Task Кадр_из_картинок_без_версии_и_основной_не_сцена_отказ_до_денег()
    {
        var scene = AddScene();
        var state = Prepare(scene, Ref("project-file", File_("video/a.png"), "frame-a"));
        var imageKind = new ContextKindRegistry([new VideoContextKind(_threads), new ProjectFileContextKind(), new FakeImageKind()]);
        var withImage = new ChatContextStore(Path.Combine(_root, "ctx2"), imageKind);
        withImage.SetPrimary(Owner, Chat, Scene(scene), null);
        var rev = withImage.AddRef(Owner, Chat,
            Ref("image", new JsonObject { ["threadId"] = "t1" }, "frame-a"), null).Revision;
        var svc = new VideoEditJobService([_camera], new VideoEditWorkspace(Path.Combine(_root, "ws2")),
            new VideoJobThreads(_threads, NullLogger<VideoJobThreads>.Instance), NullLogger<VideoEditJobService>.Instance,
            context: new VideoContextLaunch(withImage, imageKind));

        var noVersion = await svc.QuoteAsync(Owner, _scope, new VideoQuoteRequest(Chat, "", null, null, 1, 5, null, null, rev), default);
        noVersion.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        noVersion.Error.Should().Contain("версию");

        _context.SetPrimary(Owner, Chat, null, null);
        var noScene = await Quote(Revision);
        noScene.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        noScene.Error.Should().Be(VideoContextLaunch.NotSceneText);
        state.Primary.Should().NotBeNull();
    }

    // Вид «image» принадлежит другой вертикали; здесь нужен лишь зарегистрированный Kind
    private sealed class FakeImageKind : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["image"];
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("кадр", null, null, false);
        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];
        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }
}
