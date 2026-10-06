using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// «Формат» в исполнителе съёмки (ADR-025, Б2): желание → действующий формат под модель → котировка (ступень входит в
// пробу и в ETA), подрезка кадров до модели, стадия upscaling и отмена только увеличения, params resolution/size — отказ
public sealed class VideoFormatJobTests : IDisposable
{
    private const string Owner = "u1";
    private const string Session = "s1";
    private static readonly VideoEditScope Scope = new("proj1", null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfmt-" + Guid.NewGuid().ToString("N"));
    private readonly VideoThreadStore _store;
    private readonly Broadcasts _events = new();
    private readonly VideoPrefsStore _prefsStore;

    public VideoFormatJobTests()
    {
        _store = new VideoThreadStore(Path.Combine(_root, "threads"));
        _prefsStore = new VideoPrefsStore(Path.Combine(_root, "prefs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Broadcasts : ISessionBroadcaster
    {
        public List<ServerMessage> Sent { get; } = [];
        public Task ToOwner(string ownerId, ServerMessage message) { lock (Sent) Sent.Add(message); return Task.CompletedTask; }
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    // Кадры: байты [k] ↔ размер в таблице; Probe читает её, Apply режет и помечает результат [9, ширина/100]
    private sealed class FakeRaster : IImageRaster
    {
        public Dictionary<byte, (int W, int H)> Sizes { get; } = new() { [1] = (1920, 1080), [2] = (1080, 1920) };
        public List<ImageFractionRect> Crops { get; } = [];

        public RasterProbe? Probe(byte[] data) =>
            Sizes.TryGetValue(data[0], out var s) ? new RasterProbe(ImageEncodeFormat.Png, "png", s.W, s.H, 1, data.Length) : null;

        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null)
        {
            var rect = ((CropOp)ops.Single()).Rect;
            Crops.Add(rect);
            return RasterOutcome.Success(new RasterImage([9, data[0]], ImageEncodeFormat.Png, 1, 1));
        }

        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => throw new NotSupportedException();
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => throw new NotSupportedException();
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => throw new NotSupportedException();
    }

    private sealed class Frames(Dictionary<string, byte> byThread) : IImageFrameSource
    {
        public Task<ImageFrame?> GetAsync(string ownerId, string threadId, string versionId, CancellationToken ct) =>
            Task.FromResult<ImageFrame?>(byThread.TryGetValue(threadId, out var b) ? new ImageFrame([b], "image/png") : null);

        public Task<ImageFrameDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder, CancellationToken ct) =>
            Task.FromResult<ImageFrameDraft?>(null);
    }

    // Движок-фейк «как local H3»: ступени и пост-шаг из настоящего каталога; запросы и прогон записываются
    private sealed class H3Fake(Func<VideoRequest, IProgress<VideoProgress>, CancellationToken, Task<VideoResult>>? run = null)
        : IVideoEngine, IVideoQuoter
    {
        public string Key => "local";
        public string Label => "Локальные модели";
        public string PriceUnit => VideoPriceUnits.Free;
        public bool Enabled => true;
        public List<VideoRequest> Probes { get; } = [];
        public List<VideoRequest> Runs { get; } = [];
        public List<string> Cancelled { get; } = [];
        public List<UpscaleRetryRequest> Retries { get; } = [];
        // «Повторить увеличение»: причина отказа (null — можно) и прогон; без прогона повтор молча удаётся
        public string? RetryRefusal { get; set; }
        public Func<UpscaleRetryRequest, IProgress<VideoProgress>, CancellationToken, Task<VideoResult>>? Retry { get; set; }

        public string? UpscaleRetryRefusal(string shotTicket) => RetryRefusal;

        public Task<VideoResult> RetryUpscaleAsync(UpscaleRetryRequest request, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            lock (Retries) Retries.Add(request);
            return Retry!(request, progress, ct);
        }

        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new(LocalVideoEngine.MiniMaxH3, "MiniMax H3",
                new VideoCaps([5], ["16:9", "9:16"], true, true, VideoLicense.NotStated, VideoPriceUnits.Free,
                    Format: VideoFormatCatalog.LocalH3(["16:9", "9:16"]))),
        ];

        public IReadOnlySet<string>? ParamNames(VideoModelInfo model) => new HashSet<string> { "fast" };

        public int? ExpectedSeconds(VideoModelInfo model, VideoRequest request) => null;

        public Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct)
        {
            Probes.Add(request);
            var max = request.Format?.PostStep == FormatPostSteps.Upscale;
            return Task.FromResult(new VideoEstimate(0, VideoPriceUnits.Free, false, VideoEstimateSources.Provider,
                max ? 333 + 915 : 333, 0, max ? 915 : null, max ? FormatPostSteps.Upscale : null, max ? 333 : null));
        }

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            Runs.Add(req);
            return run is null
                ? Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([1, 2, 3], "video/mp4", ".mp4", 5, true), null, false, "t", null))
                : run(req, progress, ct);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct)
        {
            lock (Cancelled) Cancelled.Add(remoteId);
            return Task.FromResult(true);
        }
    }

    private sealed class CountingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) { lock (Records) Records.Add(record); }
    }

    // Облачный сосед с моделью под сцену: при отказе local исполнитель предложил бы «Повторить через …»
    private sealed class Neighbor : IVideoEngine
    {
        public string Key => "fal";
        public string Label => "fal";
        public string PriceUnit => VideoPriceUnits.Usd;
        public bool Enabled => true;

        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new("fal-model", "fal модель", new VideoCaps([5], ["16:9"], false, true, VideoLicense.Commercial, VideoPriceUnits.Usd),
                new VideoPriceHint(0.1, VideoPriceUnits.Usd, "sec")),
        ];

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }

    private (VideoEditJobService Svc, VideoPrefsService Prefs) Service(H3Fake engine, FakeRaster? raster = null, Frames? frames = null,
        bool withRaster = true, ISpendCollector? spend = null, bool withNeighbor = false)
    {
        var workspace = new VideoEditWorkspace(Path.Combine(_root, "ws"));
        var prefs = new VideoPrefsService(_prefsStore);
        var threads = new VideoJobThreads(_store, NullLogger<VideoJobThreads>.Instance, broadcaster: _events);
        frames ??= new Frames(new() { ["t16x9"] = 1, ["t9x16"] = 2 });
        var reader = new VideoFrameReader(frames, workspace, withRaster ? raster ?? new FakeRaster() : null);
        var svc = new VideoEditJobService(withNeighbor ? new IVideoEngine[] { engine, new Neighbor() } : [engine], workspace, threads, NullLogger<VideoEditJobService>.Instance,
            prefs, spend, _events, flags: null, frames: reader);
        return (svc, prefs);
    }

    private static MediaFormat Wish(string aspect, FormatTier? tier = null) =>
        new(FormatModes.Aspect, aspect, tier, null, null, ContextActor.Human, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));

    private string AddScene(MediaFormat? format = null, string? aspect = null, FrameRef? frameA = null, VideoFrameFitDto? fit = null,
        string? provider = "local")
    {
        // У H3 первый кадр обязателен: сцена без кадров ему не подходит. По умолчанию — кадр 16:9 из «Картинок»
        var settings = new VideoSceneSettingsDto(frameA ?? FrameRef.Image("t16x9", "v1"), null, "закат", provider, null, 5, aspect,
            null, null, format, fit);
        return _store.AddScene(Owner, Session, "", settings, null).Scene!.SceneId;
    }

    private static VideoQuoteRequest Quote(string scene, MediaFormat? format = null, string? aspect = null) =>
        new(Session, scene, "local", null, null, 5, aspect, null, Format: format);

    private async Task<string> Shoot(VideoEditJobService svc, string scene, MediaFormat? format = null, JsonObject? @params = null)
    {
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene, format), default)).Value!;
        var started = await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene, Params: @params), default);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);
        return started.Value.JobId;
    }

    // ── Котировка: ступень входит в пробу и в ETA ────────────────────────────────

    [Fact]
    public async Task Quote_carries_resolved_tier_into_probe_and_eta_is_shoot_plus_upscale_for_max()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);
        var scene = AddScene();

        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene, Wish("9:16", FormatTier.Max)), default)).Value!;

        engine.Probes.Single().Format.Should().Match<ResolvedFormat>(f =>
            f.Aspect == "9:16" && f.Tier == FormatTier.Max && f.WireValue == "full" && f.PostStep == FormatPostSteps.Upscale
            && f.Width == 1440 && f.Height == 2528);
        engine.Probes.Single().Aspect.Should().Be("9:16");
        quote.Price.Eta.Should().Be(333 + 915);
        quote.Price.UpscaleEta.Should().Be(915);
        quote.Price.ShotEta.Should().Be(333, "время съёмки отдаёт сервер, разность eta − upscaleEta на клиенте не считается");
        quote.Price.PostStep.Should().Be("upscale");
        quote.Format!.Aspect.Should().Be("9:16");
    }

    [Fact]
    public async Task Quote_of_a_plain_tier_has_no_shot_eta_and_count_multiplies_it()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);
        var scene = AddScene();

        var plain = (await svc.QuoteAsync(Owner, Scope, Quote(scene, Wish("16:9", FormatTier.Standard)), default)).Value!;
        var max = (await svc.QuoteAsync(Owner, Scope, Quote(scene, Wish("16:9", FormatTier.Max)) with { Count = 2 }, default)).Value!;

        plain.Price.ShotEta.Should().BeNull("у ступени без увеличения нет разбиения на две части");
        max.Price.ShotEta.Should().Be(333 * 2);
        max.Price.UpscaleEta.Should().Be(915 * 2);
    }

    // Котировка конкретной ступени приходит без автора и времени и ничего не пишет: ни в префы, ни в сцену
    [Fact]
    public async Task Quote_with_format_without_author_and_time_writes_neither_prefs_nor_scene()
    {
        var engine = new H3Fake();
        var (svc, prefs) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.Standard));
        var prefsBefore = JsonSerializer.Serialize(prefs.Get(Owner, Scope));
        var threadBefore = JsonSerializer.Serialize(_store.Get(Owner, Session));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var body = JsonSerializer.Deserialize<VideoQuoteRequest>(
            $$$"""{"sessionId":"{{{Session}}}","sceneId":"{{{scene}}}","provider":"local","count":1,"durationSec":5,"format":{"mode":"aspect","aspect":"9:16","tier":"max"}}""",
            web)!;

        var quote = (await svc.QuoteAsync(Owner, Scope, body, default)).Value!;

        quote.Format!.Should().Match<ResolvedFormat>(f => f.Aspect == "9:16" && f.Tier == FormatTier.Max);
        quote.Price.ShotEta.Should().Be(333);
        JsonSerializer.Serialize(prefs.Get(Owner, Scope)).Should().Be(prefsBefore, "котировка не пишет в префы");
        JsonSerializer.Serialize(_store.Get(Owner, Session)).Should().Be(threadBefore, "котировка не пишет в сцену");
    }

    [Fact]
    public async Task Quote_wish_chain_is_request_then_scene_then_prefs_and_prefs_format_is_used_when_scene_has_none()
    {
        var engine = new H3Fake();
        var (svc, prefs) = Service(engine);
        prefs.Save(Owner, Scope, new VideoPrefsDto(null, null, null, null, null, null, Wish("9:16", FormatTier.Max)));
        var plain = AddScene();
        var own = AddScene(Wish("16:9", FormatTier.Standard));

        var fromPrefs = (await svc.QuoteAsync(Owner, Scope, Quote(plain), default)).Value!;
        var fromScene = (await svc.QuoteAsync(Owner, Scope, Quote(own), default)).Value!;
        var fromRequest = (await svc.QuoteAsync(Owner, Scope, Quote(own, Wish("9:16", FormatTier.Max)), default)).Value!;

        (fromPrefs.Format!.Aspect, fromPrefs.Format.Tier).Should().Be(("9:16", FormatTier.Max), "ступень префов не теряется, а не заменяется умолчанием драйвера");
        (fromScene.Format!.Aspect, fromScene.Format.Tier).Should().Be(("16:9", FormatTier.Standard));
        (fromRequest.Format!.Aspect, fromRequest.Format.Tier).Should().Be(("9:16", FormatTier.Max));
    }

    [Fact]
    public async Task Old_aspect_only_client_gets_driver_default_tier_full_for_local_h3_not_half()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);
        var scene = AddScene(aspect: "9:16");

        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;

        quote.Format!.Tier.Should().Be(FormatTier.High, "раньше local всегда снимал full — старый клиент не должен молча получить 480p");
        quote.Format.WireValue.Should().Be("full");
        quote.Format.PostStep.Should().BeNull();
    }

    [Fact]
    public async Task No_format_at_all_is_as_the_model_does_resolved_format_is_null()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);

        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(AddScene()), default)).Value!;

        quote.Format.Should().BeNull();
        engine.Probes.Single().Format.Should().BeNull();
        quote.Price.PostStep.Should().BeNull();
    }

    [Fact]
    public async Task Stored_wish_survives_substitution_A_then_B_then_A()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);
        var wish = Wish("1:1", FormatTier.High);
        var scene = AddScene(wish, aspect: "1:1");

        var jobId = await Shoot(svc, scene);

        engine.Runs.Single().Format!.Aspect.Should().NotBe("1:1", "H3 1:1 не снимает — резолвер подменил на ближайшее");
        engine.Runs.Single().Format!.Substituted.Should().BeTrue();
        var saved = _store.Get(Owner, Session).Scenes.Single().Settings;
        saved.Format.Should().Be(wish, "хранится желание, а не подмена под модель");
        saved.Aspect.Should().Be("1:1", "зеркало Aspect — желание, а не подставленное значение");
        svc.Get(Owner, Scope.Key, jobId)!.Status.Should().Be("completed");
    }

    // ── params: размер задаёт формат ─────────────────────────────────────────────

    [Theory]
    [InlineData("size")]
    public async Task Size_in_params_is_refused_before_money_with_hint_to_quality(string name)
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.High));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;

        var started = await svc.StartAsync(Owner, Scope,
            new VideoLaunchRequest(quote.QuoteId, Session, scene, Params: new JsonObject { [name] = "half" }), default);

        started.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        started.Error.Should().Contain("quality").And.Contain(name);
        engine.Runs.Should().BeEmpty();
    }

    // ── Подрезка кадров до модели ────────────────────────────────────────────────

    [Fact]
    public async Task Frames_are_cropped_to_scene_aspect_with_anchor_before_the_model_and_marked_on_the_scene()
    {
        var engine = new H3Fake();
        var raster = new FakeRaster();
        var (svc, _) = Service(engine, raster, new Frames(new() { ["t16x9"] = 1 }));
        var scene = AddScene(Wish("9:16", FormatTier.High), frameA: FrameRef.Image("t16x9", "v1"), fit: new VideoFrameFitDto("left"));

        await Shoot(svc, scene);

        // 16:9 → 9:16: режутся бока, якорь «слева» прижимает рамку к левому краю
        var crop = raster.Crops.Should().ContainSingle().Subject;
        (crop.X, crop.Y, crop.Height).Should().Be((0d, 0d, 1d));
        crop.Width.Should().BeApproximately(9.0 / 16 / (16.0 / 9), 1e-6);
        engine.Runs.Single().FrameA!.Bytes.Should().Equal(new byte[] { 9, 1 }, "в модель уходит подрезанный кадр, а не исходный");
        var cropped = _store.Get(Owner, Session).Scenes.Single().FramesCropped;
        cropped.Should().Be(new VideoFramesCroppedDto("9:16", "16:9", null, "left"));
    }

    [Fact]
    public async Task Frame_already_in_scene_aspect_is_untouched()
    {
        var engine = new H3Fake();
        var raster = new FakeRaster();
        var (svc, _) = Service(engine, raster, new Frames(new() { ["t9x16"] = 2 }));
        var scene = AddScene(Wish("9:16", FormatTier.High), frameA: FrameRef.Image("t9x16", "v1"));

        await Shoot(svc, scene);

        raster.Crops.Should().BeEmpty();
        engine.Runs.Single().FrameA!.Bytes.Should().Equal(new byte[] { 2 });
        _store.Get(Owner, Session).Scenes.Single().FramesCropped.Should().BeNull();
    }

    [Fact]
    public async Task Without_raster_cropping_is_dsp_unavailable_and_nothing_is_launched()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine, frames: new Frames(new() { ["t"] = 1 }), withRaster: false);
        var scene = AddScene(Wish("9:16", FormatTier.High), frameA: FrameRef.Image("t", "v1"));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;

        var started = await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default);

        started.ErrorCode.Should().Be(VideoEditorErrors.DspUnavailable);
        engine.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_format_frames_go_as_is_and_raster_is_not_needed()
    {
        var engine = new H3Fake();
        var (svc, _) = Service(engine, frames: new Frames(new() { ["t"] = 1 }), withRaster: false);
        var scene = AddScene(frameA: FrameRef.Image("t", "v1"));

        await Shoot(svc, scene);

        engine.Runs.Single().FrameA!.Bytes.Should().Equal(new byte[] { 1 });
    }

    [Fact]
    public async Task Preview_dry_run_gives_the_same_crop_as_the_shot_and_writes_nothing()
    {
        var engine = new H3Fake();
        var raster = new FakeRaster();
        var (svc, _) = Service(engine, raster, new Frames(new() { ["t16x9"] = 1 }));
        var scene = AddScene(Wish("9:16", FormatTier.High), frameA: FrameRef.Image("t16x9", "v1"));
        var before = _store.Get(Owner, Session).Revision;

        var preview = (await svc.PreviewFramesAsync(Owner, Scope, new VideoFramesPreviewRequest(Session, scene, Anchor: "right"), default)).Value!;

        preview.Aspect.Should().Be("9:16");
        preview.FrameA.Should().Match<VideoFramePreviewDto>(p => p.Cropped && p.SourceAspect == "16:9" && p.ResultWidth == 608
            && p.ResultHeight == 1080 && p.X > 0.6);
        preview.FrameB.Should().BeNull();
        raster.Crops.Should().BeEmpty("dryRun не режет файлы");
        _store.Get(Owner, Session).Revision.Should().Be(before);
    }

    // ── Стадия upscaling и отмена только увеличения ──────────────────────────────

    [Fact]
    public async Task Upscaling_stage_reaches_status_and_event_with_own_eta_and_percent_then_version_has_size()
    {
        var gate = new TaskCompletionSource();
        var engine = new H3Fake(async (r, p, ct) =>
        {
            p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "t-shot", Accepted: true, UpscaleEtaSeconds: 915));
            p.Report(new VideoProgress(VideoStage.Running, EtaSeconds: 333, RemoteId: "t-shot", Percent: 0.5, UpscaleEtaSeconds: null));
            p.Report(new VideoProgress(VideoStage.Upscaling, EtaSeconds: 915, RemoteId: "t-up", Percent: 0.25));
            await gate.Task;
            return new VideoResult(VideoOutcome.Ok, new VideoFile([1], "video/mp4", ".mp4", 5, true, 2528, 1440), null, false, "t-up", null);
        });
        var (svc, _) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.Max));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;
        var jobId = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default)).Value!.JobId;

        // Стадия видна в статусе задачи, пока апскейл идёт
        var dto = await WaitFor(() => svc.Get(Owner, Scope.Key, jobId)!, j => j.Status == "upscaling");
        dto.EtaSeconds.Should().Be(915);
        gate.SetResult();
        await svc.WhenDone(jobId);

        var progress = _events.Sent.OfType<VideoEditProgressMessage>().ToList();
        progress.Select(m => m.Stage).Should().ContainInOrder("queued", "running", "upscaling");
        progress[0].UpscaleEtaSeconds.Should().Be(915);
        progress.Single(m => m.Stage == "upscaling").Should().Match<VideoEditProgressMessage>(m => m.EtaSeconds == 915 && m.Percent == 0.25);
        var version = _store.Get(Owner, Session).Scenes.Single().Versions.Should().ContainSingle().Subject;
        (version.Width, version.Height, version.UpscaleError).Should().Be((2528, 1440, null));
    }

    [Fact]
    public async Task Failed_upscale_version_carries_the_mark_and_the_shot_size()
    {
        var engine = new H3Fake((r, p, ct) =>
        {
            p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "t", Accepted: true));
            return Task.FromResult(new VideoResult(VideoOutcome.Ok,
                new VideoFile([1], "video/mp4", ".mp4", 5, true, 1344, 768, "Увеличение до 1440p не удалось: oom"), null, false, "t", null));
        });
        var (svc, _) = Service(engine);

        await Shoot(svc, AddScene(Wish("16:9", FormatTier.Max)));

        var version = _store.Get(Owner, Session).Scenes.Single().Versions.Single();
        (version.Width, version.Height).Should().Be((1344, 768));
        version.UpscaleError.Should().Contain("не удалось");
        version.UpscaleCancelled.Should().BeNull("сбой — не отмена");
        _store.Get(Owner, Session).Scenes.Single().Launches.Single().Status.Should().Be(VideoLaunchStatus.Done);
    }

    [Fact]
    public async Task Cancel_upscale_cancels_only_the_upscale_ticket_and_the_job_keeps_running()
    {
        var gate = new TaskCompletionSource();
        var engine = new H3Fake(async (r, p, ct) =>
        {
            p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "t-shot", Accepted: true));
            p.Report(new VideoProgress(VideoStage.Upscaling, EtaSeconds: 915, RemoteId: "t-up"));
            await gate.Task;
            return new VideoResult(VideoOutcome.Ok, new VideoFile([1], "video/mp4", ".mp4", 5, true, 1344, 768, "Увеличение отменено", true), null, false, "t", null);
        });
        var (svc, _) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.Max));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;
        var jobId = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default)).Value!.JobId;
        await WaitFor(() => svc.Get(Owner, Scope.Key, jobId)!, j => j.Status == "upscaling");

        var cancelled = await svc.CancelUpscaleAsync(Owner, Scope.Key, jobId, default);

        cancelled.ErrorCode.Should().BeNull();
        engine.Cancelled.Should().Equal("t-up");
        svc.Get(Owner, Scope.Key, jobId)!.Status.Should().Be("upscaling", "запуск не отменён — съёмка 768p дойдёт версией");
        gate.SetResult();
        await svc.WhenDone(jobId);
        var version = _store.Get(Owner, Session).Scenes.Single().Versions.Single();
        version.UpscaleError.Should().Be("Увеличение отменено");
        version.UpscaleCancelled.Should().BeTrue("отмена — явный признак, а не строка");
    }

    [Fact]
    public async Task Cancel_upscale_outside_the_upscale_stage_is_refused()
    {
        var gate = new TaskCompletionSource();
        var engine = new H3Fake(async (r, p, ct) =>
        {
            p.Report(new VideoProgress(VideoStage.Running, RemoteId: "t-shot", Accepted: true));
            await gate.Task;
            return new VideoResult(VideoOutcome.Ok, new VideoFile([1], "video/mp4", ".mp4", 5, true), null, false, "t", null);
        });
        var (svc, _) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.High));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(scene), default)).Value!;
        var jobId = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default)).Value!.JobId;
        await WaitFor(() => svc.Get(Owner, Scope.Key, jobId)!, j => j.Status == "running");

        var cancelled = await svc.CancelUpscaleAsync(Owner, Scope.Key, jobId, default);

        cancelled.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        engine.Cancelled.Should().BeEmpty();
        gate.SetResult();
        await svc.WhenDone(jobId);
    }


    // ── «Повторить увеличение» ───────────────────────────────────────────────────

    private static VideoResult ShotWithMark(string ticket = "t-shot") =>
        new(VideoOutcome.Ok, new VideoFile([1], "video/mp4", ".mp4", 5, true, 1344, 768, "Увеличение до 1440p не удалось: oom"),
            null, false, ticket, null);

    // Съёмка «Макс», у которой упало увеличение: версия 768p с пометкой; возвращает сцену и версию
    private async Task<(string Scene, string Version)> FailedUpscaleVersion(VideoEditJobService svc)
    {
        var scene = AddScene(Wish("16:9", FormatTier.Max));
        await Shoot(svc, scene);
        var version = _store.Get(Owner, Session).Scenes.Single().Versions.Single();
        version.UpscaleError.Should().NotBeNull();
        return (scene, version.VersionId);
    }

    private static Task<VideoResult> Big(UpscaleRetryRequest r, IProgress<VideoProgress> p, CancellationToken ct)
    {
        p.Report(new VideoProgress(VideoStage.Upscaling, EtaSeconds: 915, RemoteId: "t-up2"));
        return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([9, 9], "video/mp4", ".mp4", 5, true, 2528, 1440), null,
            false, "t-up2", null));
    }

    [Fact]
    public async Task Retry_upscale_accepts_a_cancelled_version_the_same_way_as_a_failed_one()
    {
        var engine = new H3Fake((r, p, ct) => Task.FromResult(new VideoResult(VideoOutcome.Ok,
            new VideoFile([1], "video/mp4", ".mp4", 5, true, 1344, 768, "Увеличение отменено", true), null, false, "t-shot", null)))
        { Retry = Big };
        var (svc, _) = Service(engine);
        var (scene, versionId) = await FailedUpscaleVersion(svc);
        _store.Get(Owner, Session).Scenes.Single().Versions.Single().UpscaleCancelled.Should().BeTrue();

        var started = await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default);

        started.ErrorCode.Should().BeNull("кнопка «Увеличить до 1440p» у отменённой зовёт ту же ручку");
        await svc.WhenDone(started.Value!.JobId);
        _store.Get(Owner, Session).Scenes.Single().Versions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Retry_upscale_adds_a_new_1440p_version_keeps_768p_and_writes_no_spend()
    {
        var engine = new H3Fake((r, p, ct) => Task.FromResult(ShotWithMark())) { Retry = Big };
        var spend = new CountingSpend();
        var (svc, _) = Service(engine, spend: spend);
        var (scene, versionId) = await FailedUpscaleVersion(svc);
        var spendBefore = spend.Records.Count;

        var started = await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default);
        started.ErrorCode.Should().BeNull();
        started.Value!.SourceVersionId.Should().Be(versionId);
        await svc.WhenDone(started.Value.JobId);

        engine.Retries.Should().ContainSingle().Which.ShotTicket.Should().Be("t-shot");
        engine.Runs.Should().ContainSingle("пересъёмки нет: второй запуск — только увеличение");
        var versions = _store.Get(Owner, Session).Scenes.Single().Versions;
        versions.Should().HaveCount(2);
        var old = versions.Single(v => v.VersionId == versionId);
        (old.Width, old.Height).Should().Be((1344, 768));
        old.UpscaleError.Should().NotBeNull("версия 768p остаётся как была");
        var fresh = versions.Single(v => v.VersionId != versionId);
        (fresh.Width, fresh.Height, fresh.UpscaleError).Should().Be((2528, 1440, null), "пометки на новой версии нет");
        _store.Get(Owner, Session).Scenes.Single().CurrentVersionId.Should().Be(fresh.VersionId);
        svc.Get(Owner, Scope.Key, started.Value.JobId)!.Status.Should().Be("completed");
        spend.Records.Should().HaveCount(spendBefore, "повтор — не новая генерация: траты нет");
    }

    [Fact]
    public async Task Retry_upscale_runs_as_its_own_job_with_upscaling_stage_and_can_be_cancelled_without_a_new_version()
    {
        var gate = new TaskCompletionSource();
        var engine = new H3Fake((r, p, ct) => Task.FromResult(ShotWithMark()))
        {
            Retry = async (r, p, ct) =>
            {
                p.Report(new VideoProgress(VideoStage.Upscaling, EtaSeconds: 915, RemoteId: "t-up2"));
                await gate.Task;
                return VideoResult.Fail(VideoOutcome.Cancelled, "Увеличение отменено");
            },
        };
        var (svc, _) = Service(engine);
        var (scene, versionId) = await FailedUpscaleVersion(svc);

        var jobId = (await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default)).Value!.JobId;
        await WaitFor(() => svc.Get(Owner, Scope.Key, jobId)!, j => j.Status == "upscaling");
        (await svc.CancelUpscaleAsync(Owner, Scope.Key, jobId, default)).ErrorCode.Should().BeNull();
        engine.Cancelled.Should().Equal("t-up2");
        gate.SetResult();
        await svc.WhenDone(jobId);

        svc.Get(Owner, Scope.Key, jobId)!.Status.Should().Be("cancelled");
        _store.Get(Owner, Session).Scenes.Single().Versions.Should().ContainSingle("отмена повтора версий не добавляет");
    }

    [Fact]
    public async Task Retry_upscale_failure_adds_no_version_and_offers_no_other_provider()
    {
        var engine = new H3Fake((r, p, ct) => Task.FromResult(ShotWithMark()))
        {
            Retry = (r, p, ct) => Task.FromResult(VideoResult.Fail(VideoOutcome.Failed, "Увеличение до 1440p не удалось: oom")),
        };
        var (svc, _) = Service(engine, withNeighbor: true);
        var (scene, versionId) = await FailedUpscaleVersion(svc);

        var jobId = (await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default)).Value!.JobId;
        await svc.WhenDone(jobId);

        svc.Get(Owner, Scope.Key, jobId)!.Status.Should().Be("failed");
        _store.Get(Owner, Session).Scenes.Single().Versions.Should().ContainSingle();
        _events.Sent.OfType<VideoEditFailedMessage>().Last().RetryQuote.Should().BeNull("повтор увеличения не предлагает «снять у соседа»");
    }

    [Fact]
    public async Task Retry_upscale_refused_with_unavailable_when_the_shot_ticket_is_gone()
    {
        var engine = new H3Fake((r, p, ct) => Task.FromResult(ShotWithMark())) { Retry = Big };
        var (svc, _) = Service(engine);
        var (scene, versionId) = await FailedUpscaleVersion(svc);
        engine.RetryRefusal = VideoUpscaleRetryTexts.Gone;

        var result = await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default);

        result.ErrorCode.Should().Be(VideoEditorErrors.UpscaleRetryUnavailable);
        result.Error.Should().Be("Повторить увеличение уже нельзя — переснимите сцену");
        engine.Retries.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_upscale_is_unavailable_after_server_restart_context_is_in_memory_only()
    {
        var engine = new H3Fake((r, p, ct) => Task.FromResult(ShotWithMark())) { Retry = Big };
        var (svc, _) = Service(engine);
        var (scene, versionId) = await FailedUpscaleVersion(svc);
        engine.RetryRefusal = null;
        // «Рестарт»: новый исполнитель над теми же нитями на диске — контекста тикета в нём нет
        var (restarted, _) = Service(engine);

        var result = await restarted.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default);

        result.ErrorCode.Should().Be(VideoEditorErrors.UpscaleRetryUnavailable);
    }

    [Fact]
    public async Task Retry_upscale_of_a_version_without_mark_or_unknown_ids_is_refused()
    {
        var engine = new H3Fake { Retry = Big };
        var (svc, _) = Service(engine);
        var scene = AddScene(Wish("16:9", FormatTier.High));
        await Shoot(svc, scene);
        var healthy = _store.Get(Owner, Session).Scenes.Single().Versions.Single().VersionId;

        (await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, healthy, VideoInitiators.Human, default))
            .Should().Match<VideoEditCallResult<VideoUpscaleRetryResult>>(r => r.ErrorCode == VideoEditorErrors.InvalidRequest
                && r.Error == VideoUpscaleRetryTexts.NotFailed);
        (await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, "нет-такой", VideoInitiators.Human, default))
            .ErrorCode.Should().Be(VideoEditorErrors.VersionNotFound);
        (await svc.RetryUpscaleAsync(Owner, Scope, Session, "нет-такой", healthy, VideoInitiators.Human, default))
            .ErrorCode.Should().Be(VideoEditorErrors.SceneNotFound);
        (await svc.RetryUpscaleAsync("чужой", Scope, Session, scene, healthy, VideoInitiators.Human, default))
            .ErrorCode.Should().Be(VideoEditorErrors.SceneNotFound, "чужая сцена неотличима от несуществующей");
        engine.Retries.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_upscale_goes_through_the_same_heavy_slot_as_a_shoot()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var engine = new H3Fake(async (r, p, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return ShotWithMark();
            await gate.Task;
            return ShotWithMark("t-other");
        }) { Retry = Big };
        var (svc, _) = Service(engine);
        var (scene, versionId) = await FailedUpscaleVersion(svc);
        var second = AddScene(Wish("16:9", FormatTier.Max));
        var quote = (await svc.QuoteAsync(Owner, Scope, Quote(second), default)).Value!;
        var busy = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, second), default)).Value!.JobId;

        var result = await svc.RetryUpscaleAsync(Owner, Scope, Session, scene, versionId, VideoInitiators.Human, default);

        result.ErrorCode.Should().Be(VideoEditorErrors.HeavyBusy, "локальная карта одна: второй тяжёлой задаче очереди помимо слота нет");
        gate.SetResult();
        await svc.WhenDone(busy);
    }

    private static async Task<T> WaitFor<T>(Func<T> read, Func<T, bool> done)
    {
        for (var i = 0; i < 400; i++)
        {
            var value = read();
            if (done(value)) return value;
            await Task.Delay(10);
        }
        throw new TimeoutException("Состояние не наступило");
    }
}
