using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Higgsfield;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Engines;

// Драйвер Higgsfield для видео на фейковом MCP: живой каталог с отбором по ролям кадров, кеш, цепочка
// get_cost → generate_video → jobs_wait → скачивание, траты в кредитах, повтор только до создания задания
public sealed class HiggsfieldVideoEngineTests
{
    private static readonly VideoEditScope Scope = VideoEditScope.Of(new Project { Id = "p-1", RootPath = "/srv/p" });

    // Живой ответ models_explore(list, video) от 2026-10-02 целиком
    private static readonly string CatalogFixture = File.ReadAllText(FixturePath("higgsfield-models-explore-video.json"));

    private static string FixturePath(string name, [CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "..", "Fixtures", name);

    private const string JobId = "8c516bb9-f774-4523-9777-d9f5cb2ca3bf";
    private const string MediaA = "a9ee96f2-1edc-47af-809a-395fdfd3f400";
    private const string MediaB = "b9ee96f2-1edc-47af-809a-395fdfd3f401";

    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70];
    private static readonly VideoFrameBytes FrameA = new([1, 2, 3], "image/png");
    private static readonly VideoFrameBytes FrameB = new([4, 5, 6], "image/png");

    private static JsonObject? GenerateArgs(FakeHttp.Call c) => FakeHttp.Arguments(c)?["params"] as JsonObject;

    private static bool IsCost(FakeHttp.Call c) => FakeHttp.Tool(c) == "generate_video" && GenerateArgs(c)?["get_cost"] is not null;

    private static FakeHttp.Call[] Launches(FakeHttp http) =>
        [.. http.Calls.Where(c => FakeHttp.Tool(c) == "generate_video" && !IsCost(c))];

    private static int Uploads(FakeHttp http) => http.Calls.Count(c => FakeHttp.Tool(c) == "media_upload");

    private static HttpResponseMessage Happy(FakeHttp.Call c) => c switch
    {
        _ when c.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.OK),
        _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(Mp4, "video/mp4"),
        _ => FakeHttp.Tool(c) switch
        {
            "models_explore" => FakeHttp.McpText(CatalogFixture),
            // Первым грузится кадр A, вторым — кадр B
            "media_upload" => FakeHttp.McpText(UploadText(FakeHttp.Arguments(c)!["filename"]!.ToString().StartsWith("end") ? MediaB : MediaA)),
            "media_confirm" => FakeHttp.McpText("Media confirmed."),
            "generate_video" when GenerateArgs(c) is null => FakeHttp.McpText("params: Invalid input", isError: true),
            "generate_video" when IsCost(c) => FakeHttp.McpText($"Cost preflight for {GenerateArgs(c)!["model"]}: 10 credits (10 exact). No job submitted."),
            "generate_video" => FakeHttp.McpText($$"""{"results":[{"id":"{{JobId}}","type":"video","status":"pending"}]}"""),
            "jobs_wait" => FakeHttp.McpText(Completed("https://cdn.test/out.mp4")),
            _ => FakeHttp.McpText("unknown tool", isError: true),
        },
    };

    private static string UploadText(string mediaId) =>
        $"Upload URLs:\n- {mediaId}: run curl -X PUT -H 'Content-Type: image/png' " +
        "--data-binary @start.png 'https://s3.test/user/frame.png?X-Amz-Signature=abc'. Then call media_confirm.";

    private static string Completed(string url) =>
        $$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"completed","result_url":"{{url}}"}],"all_terminal":true}""";

    private const string Pending = $$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"in_progress"}],"all_terminal":false}""";

    private static (HiggsfieldVideoEngine Engine, FakeHttp Http, StepTime Time) Create(
        Func<FakeHttp.Call, HttpResponseMessage>? route = null, string? token = "admin-token", SafeMediaDownloader? downloader = null)
    {
        var http = new FakeHttp(route ?? Happy);
        var time = new StepTime();
        var engine = new HiggsfieldVideoEngine(FakeHttp.Client(http, token), time, downloader ?? http.Downloader())
        {
            PollInterval = TimeSpan.Zero,
        };
        return (engine, http, time);
    }

    private static async Task<(HiggsfieldVideoEngine Engine, FakeHttp Http)> Loaded(Func<FakeHttp.Call, HttpResponseMessage>? route = null,
        SafeMediaDownloader? downloader = null)
    {
        var (engine, http, _) = Create(route, downloader: downloader);
        await engine.RefreshModelsAsync(CancellationToken.None);
        return (engine, http);
    }

    private static VideoRequest Scene(string model = "kling3_0", int duration = 5, VideoFrameBytes? a = null, VideoFrameBytes? b = null,
        string? aspect = "16:9", bool sound = false, JsonObject? fields = null) =>
        new(model, Scope, "Камера медленно отъезжает от окна", a, b, duration, aspect, sound, fields);

    private sealed class Recorder : IProgress<VideoProgress>
    {
        public List<VideoProgress> Items { get; } = [];
        public void Report(VideoProgress value) => Items.Add(value);
    }

    // ── Каталог ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_LiveFixture_FramesByRoles()
    {
        var (engine, _, _) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);

        // start_image + end_image — «кадр A → кадр B»
        var kling3 = engine.Models.Single(m => m.Id == "kling3_0");
        kling3.Caps.FirstFrame.Should().BeTrue();
        kling3.Caps.LastFrame.Should().BeTrue();
        kling3.Caps.Durations.Should().Equal(Enumerable.Range(3, 13));
        kling3.Caps.Aspects.Should().Equal("16:9", "9:16", "1:1");
        kling3.Caps.Sound.Should().BeTrue();
        kling3.Caps.PriceUnit.Should().Be(VideoPriceUnits.Credits);

        // Только start_image — один кадр
        var kling26 = engine.Models.Single(m => m.Id == "kling2_6");
        kling26.Caps.FirstFrame.Should().BeTrue();
        kling26.Caps.LastFrame.Should().BeFalse();
        kling26.Caps.Durations.Should().Equal(5, 10);

        // Без start_image — только текст
        var wan26 = engine.Models.Single(m => m.Id == "wan2_6");
        wan26.Caps.FirstFrame.Should().BeFalse();
        wan26.Caps.LastFrame.Should().BeFalse();
        wan26.Caps.Durations.Should().Equal(5, 10, 15);

        // Длительности из durations и duration_range
        engine.Models.Single(m => m.Id == "cinematic_studio_video").Caps.Durations.Should().Equal(5, 10);
        engine.Models.Single(m => m.Id == "cinematic_studio_3_0").Caps.Durations.Should().Equal(Enumerable.Range(4, 12));
        // «-1 — длину выберет модель» в список не идёт
        engine.Models.Single(m => m.Id == "wan3_0").Caps.Durations.Should().StartWith([1, 2]).And.EndWith(30);

        // Одноимённые модели различаются по id
        engine.Models.Select(m => m.Label).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("video_background_remover")] // обработка готового ролика
    [InlineData("voice_change")]              // вход input_video
    [InlineData("draw_to_video")]             // роль video
    [InlineData("higgsfield_preset")]         // обязательный preset_id
    [InlineData("gemini_omni_flash_1_1")]     // обязательный mode
    [InlineData("clipify")]                   // обязательные urls
    [InlineData("veo3")]                      // длительности не объявлены
    [InlineData("marketing_studio_v2_video")] // свои роли и обязательный type
    public async Task Catalog_LiveFixture_NotSceneModels_Skipped(string id)
    {
        var (engine, _, _) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Models.Should().NotContain(m => m.Id == id);
    }

    [Fact]
    public async Task Catalog_CachedForTtl_ThenReloaded()
    {
        var (engine, http, time) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);
        time.Advance(HiggsfieldVideoEngine.ModelsTtl - TimeSpan.FromSeconds(1));
        await engine.RefreshModelsAsync(CancellationToken.None);
        http.Calls.Count(c => FakeHttp.Tool(c) == "models_explore").Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(2));
        await engine.RefreshModelsAsync(CancellationToken.None);

        var explores = http.Calls.Where(c => FakeHttp.Tool(c) == "models_explore").ToList();
        explores.Should().HaveCount(2);
        FakeHttp.Arguments(explores[0])!["type"]!.ToString().Should().Be("video");
        FakeHttp.Arguments(explores[0])!["action"]!.ToString().Should().Be("list");
        HiggsfieldVideoEngine.ModelsTtl.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task Catalog_FailedReload_KeepsPreviousList_FirstFailureEmpty()
    {
        var broken = true;
        var (engine, _, time) = Create(c => broken && FakeHttp.Tool(c) == "models_explore"
            ? FakeHttp.McpText("boom", isError: true)
            : Happy(c));
        await engine.RefreshModelsAsync(CancellationToken.None);
        engine.Models.Should().BeEmpty();

        broken = false;
        await engine.RefreshModelsAsync(CancellationToken.None);
        var count = engine.Models.Count;
        count.Should().BeGreaterThan(10);

        broken = true;
        time.Advance(HiggsfieldVideoEngine.ModelsTtl + TimeSpan.FromSeconds(1));
        await engine.RefreshModelsAsync(CancellationToken.None);
        engine.Models.Should().HaveCount(count);
    }

    [Fact]
    public async Task NoAccess_Disabled_NoCalls()
    {
        var (engine, http, _) = Create(token: null);
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Enabled.Should().BeFalse();
        engine.Models.Should().BeEmpty();
        var result = await engine.RunAsync(Scene(), new Recorder(), CancellationToken.None);
        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        http.Calls.Should().BeEmpty();
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_FirstAndLastFrame_AcceptedThenDownloaded_CreditsCharged()
    {
        var (engine, http) = await Loaded();
        var progress = new Recorder();

        var result = await engine.RunAsync(Scene(a: FrameA, b: FrameB, fields: new JsonObject { ["mode"] = "pro" }), progress,
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.Bytes.Should().Equal(Mp4);
        result.File.ContentType.Should().Be("video/mp4");
        result.File.Extension.Should().Be(".mp4");
        result.ActualCost.Should().Be(new VideoCost(10, VideoPriceUnits.Credits));
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be(JobId);

        progress.Items.First().Should().Be(new VideoProgress(VideoStage.Queued, RemoteId: JobId, Accepted: true));

        var args = GenerateArgs(Launches(http).Single())!;
        args["model"]!.ToString().Should().Be("kling3_0");
        args["prompt"]!.ToString().Should().Be("Камера медленно отъезжает от окна");
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        args["duration"]!.GetValue<int>().Should().Be(5);
        args["aspect_ratio"]!.ToString().Should().Be("16:9");
        args["sound"]!.ToString().Should().Be("off");
        args["mode"]!.ToString().Should().Be("pro");
        args.ContainsKey("get_cost").Should().BeFalse();
        var medias = args["medias"]!.AsArray().Select(m => (m!["role"]!.ToString(), m["value"]!.ToString()));
        medias.Should().Equal(("start_image", MediaA), ("end_image", MediaB));

        http.Calls.Count(c => c.Method == HttpMethod.Put).Should().Be(2);
        http.Calls.Where(c => FakeHttp.Tool(c) == "media_confirm")
            .Should().OnlyContain(c => FakeHttp.Arguments(c)!["type"]!.ToString() == "image");
        // Препроверка цены идёт до запуска
        var tools = http.Calls.Select(FakeHttp.Tool).Where(t => t is "generate_video" or "jobs_wait").ToList();
        tools.Should().Equal("generate_video", "generate_video", "jobs_wait");
        IsCost(http.Calls.First(c => FakeHttp.Tool(c) == "generate_video")).Should().BeTrue();
    }

    [Fact]
    public async Task Run_SoundAsBool_TextOnlyModel_NoMedias()
    {
        var (engine, http) = await Loaded();

        var result = await engine.RunAsync(Scene("wan2_6", duration: 10, sound: true), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        var args = GenerateArgs(Launches(http).Single())!;
        args.ContainsKey("medias").Should().BeFalse();
        Uploads(http).Should().Be(0);

        var (engine2, http2) = await Loaded();
        await engine2.RunAsync(Scene("seedance1_5", duration: 4, a: FrameA, b: FrameB, sound: true), new Recorder(), CancellationToken.None);
        GenerateArgs(Launches(http2).Single())!["generate_audio"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("kling3_0", 20, true, false, "не снимает 20 с")]
    [InlineData("kling2_6", 5, true, true, "не берёт последний кадр")]
    [InlineData("wan2_6", 5, true, false, "не берёт кадр")]
    [InlineData("kling3_0", 5, false, true, "только вместе с первым")]
    public async Task Run_RequestBeyondModel_RejectedBeforeSpending(string model, int duration, bool withA, bool withB, string reason)
    {
        var (engine, http) = await Loaded();

        var result = await engine.RunAsync(Scene(model, duration, withA ? FrameA : null, withB ? FrameB : null), new Recorder(),
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Rejected);
        result.Error.Should().Contain(reason);
        result.Charged.Should().BeFalse();
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_video" || FakeHttp.Tool(c) == "media_upload");
    }

    [Fact]
    public async Task Run_ParamsOutsideSchemaOrReserved_Dropped_BadValueRejected()
    {
        var (engine, http) = await Loaded();
        var fields = new JsonObject
        {
            ["mode"] = "std", ["use_unlim"] = true, ["batch_size"] = 4, ["get_cost"] = true, ["duration"] = 15, ["stranger"] = 1,
        };

        var result = await engine.RunAsync(Scene(fields: fields), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        var args = GenerateArgs(Launches(http).Single())!;
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        args["duration"]!.GetValue<int>().Should().Be(5);
        args.ContainsKey("batch_size").Should().BeFalse();
        args.ContainsKey("get_cost").Should().BeFalse();
        args.ContainsKey("stranger").Should().BeFalse();
        engine.ParamNames(engine.Models.Single(m => m.Id == "kling3_0")).Should().BeEquivalentTo(["mode"]);

        var bad = await engine.RunAsync(Scene(fields: new JsonObject { ["mode"] = "8k" }), new Recorder(), CancellationToken.None);
        bad.Outcome.Should().Be(VideoOutcome.Rejected);
        Launches(http).Should().HaveCount(1);
    }

    [Fact]
    public async Task Run_RefusedBeforeAcceptance_NotCharged_NoAccepted()
    {
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "generate_video" && !IsCost(c)
            ? FakeHttp.McpText("Model is temporarily unavailable", isError: true)
            : Happy(c));
        var progress = new Recorder();

        var result = await engine.RunAsync(Scene(a: FrameA), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeFalse();
        result.Error.Should().Contain("temporarily unavailable");
        progress.Items.Should().NotContain(p => p.Accepted);
        Launches(http).Should().HaveCount(1);
    }

    [Fact]
    public async Task Run_JobFailedAfterAcceptance_ChargedWithCost_NoRelaunch()
    {
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "jobs_wait"
            ? FakeHttp.McpText($$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"failed","error":"generation failed"}],"all_terminal":true}""")
            : Happy(c));
        var progress = new Recorder();

        var result = await engine.RunAsync(Scene(a: FrameA), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.ActualCost.Should().Be(new VideoCost(10, VideoPriceUnits.Credits));
        result.RemoteId.Should().Be(JobId);
        result.Error.Should().Contain("generation failed");
        progress.Items.Should().ContainSingle(p => p.Accepted && p.RemoteId == JobId);
        Launches(http).Should().HaveCount(1);
    }

    [Fact]
    public async Task Run_InsufficientCredits_OnLaunch()
    {
        var (engine, _) = await Loaded(c => FakeHttp.Tool(c) == "generate_video" && !IsCost(c)
            ? FakeHttp.McpText("Insufficient credits: top up your balance", isError: true)
            : Happy(c));

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
    }

    [Fact]
    public async Task Run_InsufficientCredits_OnPreflight_NotLaunched()
    {
        var (engine, http) = await Loaded(c => IsCost(c)
            ? FakeHttp.McpText("Not enough credits for this generation", isError: true)
            : Happy(c));

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
        Launches(http).Should().BeEmpty();
    }

    // ── Повтор: только до создания задания ───────────────────────────────────────

    [Fact]
    public async Task Run_UploadDropped_RetriedBeforeJob()
    {
        var uploads = 0;
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "media_upload" && Interlocked.Increment(ref uploads) == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Happy(c));

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        Uploads(http).Should().Be(2);
        Launches(http).Should().HaveCount(1);
    }

    [Fact]
    public async Task Run_MediaUnknownOnLaunch_ReuploadedAndRelaunchedOnce()
    {
        var launches = 0;
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "generate_video" && !IsCost(c) && Interlocked.Increment(ref launches) == 1
            ? FakeHttp.McpText("Media not found: " + MediaA, isError: true)
            : Happy(c));

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        Uploads(http).Should().Be(2);
        Launches(http).Should().HaveCount(2);
    }

    // Обрыв связи на самом generate_video: задание могло создаться — второго запуска нет, «не списано» не утверждаем
    [Fact]
    public async Task Run_LaunchDropped_NotRepeated_ChargeUnknown()
    {
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "generate_video" && !IsCost(c)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Happy(c));
        var progress = new Recorder();

        var result = await engine.RunAsync(Scene(a: FrameA), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        result.Charged.Should().BeNull();
        Launches(http).Should().HaveCount(1);
        progress.Items.Should().NotContain(p => p.Accepted);
    }

    // После id задания повторяется только опрос
    [Fact]
    public async Task Run_PollDroppedAfterAcceptance_PollRepeated_LaunchNot()
    {
        var waits = 0;
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "jobs_wait"
            ? Interlocked.Increment(ref waits) switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.BadGateway),
                2 => FakeHttp.McpText(Pending),
                _ => FakeHttp.McpText(Completed("https://cdn.test/out.mp4")),
            }
            : Happy(c));
        var progress = new Recorder();

        var result = await engine.RunAsync(Scene(a: FrameA), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        Launches(http).Should().HaveCount(1);
        waits.Should().Be(3);
        progress.Items.Should().Contain(p => p.Stage == VideoStage.Running);
    }

    [Fact]
    public async Task Run_PollDeadAfterAcceptance_GivesUp_Charged()
    {
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "jobs_wait"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Happy(c));

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().NotBe(VideoOutcome.Ok);
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be(JobId);
        Launches(http).Should().HaveCount(1);
    }

    // ── Отмена и скачивание ──────────────────────────────────────────────────────

    [Fact]
    public async Task Run_CancelledOutside_Throws_RemoteCancelBestEffort()
    {
        using var cts = new CancellationTokenSource();
        var (engine, _) = await Loaded(c =>
        {
            if (FakeHttp.Tool(c) != "jobs_wait") return Happy(c);
            cts.Cancel();
            return FakeHttp.McpText(Pending);
        });

        var act = () => engine.RunAsync(Scene(a: FrameA), new Recorder(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await engine.CancelRemoteAsync(JobId, CancellationToken.None)).Should().BeFalse();
    }

    // Ссылку на результат прислал поставщик: на внутренний адрес бэкенд не идёт вовсе
    [Fact]
    public async Task Run_ResultUrlPrivate_NotDownloaded()
    {
        FakeHttp? holder = null;
        var (engine, http) = await Loaded(c => FakeHttp.Tool(c) == "jobs_wait"
            ? FakeHttp.McpText(Completed("https://127.0.0.1/out.mp4"))
            : Happy(c), downloader: new SafeMediaDownloader(new FakeHttp(c => { holder?.Calls.Enqueue(c); return FakeHttp.Bytes(Mp4, "video/mp4"); })));
        holder = http;

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.File.Should().BeNull();
        http.Calls.Should().NotContain(c => c.Url.StartsWith("https://127.0.0.1/"));
    }

    // Потолок видео свой: больше звукового проходит, больше видеопотолка — нет
    [Theory]
    [InlineData(SafeMediaDownloader.AudioMaxBytes + 1, VideoOutcome.Ok)]
    [InlineData(SafeMediaDownloader.VideoMaxBytes + 1, VideoOutcome.Failed)]
    public async Task Run_ResultSize_VideoCeiling(long declaredLength, VideoOutcome expected)
    {
        var (engine, _) = await Loaded(c =>
        {
            if (!c.Url.StartsWith("https://cdn.test/")) return Happy(c);
            var response = FakeHttp.Bytes(Mp4, "video/mp4");
            response.Content.Headers.ContentLength = declaredLength;
            return response;
        });

        var result = await engine.RunAsync(Scene(a: FrameA), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(expected);
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estimate_CreditsFromGetCost_NoLaunchNoUpload()
    {
        var (engine, http) = await Loaded();
        var model = engine.Models.Single(m => m.Id == "kling3_0");

        var estimate = await engine.EstimateAsync(model, Scene(a: FrameA, b: FrameB), CancellationToken.None);

        estimate.Amount.Should().Be(10);
        estimate.Unit.Should().Be(VideoPriceUnits.Credits);
        estimate.Source.Should().Be(VideoEstimateSources.Provider);
        estimate.Approx.Should().BeFalse();
        Launches(http).Should().BeEmpty();
        Uploads(http).Should().Be(0);
        GenerateArgs(http.Calls.Single(IsCost))!["use_unlim"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Estimate_InsufficientCredits_Refused()
    {
        var (engine, _) = await Loaded(c => IsCost(c) ? FakeHttp.McpText("Insufficient credits", isError: true) : Happy(c));
        var model = engine.Models.Single(m => m.Id == "kling3_0");

        var act = () => engine.EstimateAsync(model, Scene(), CancellationToken.None);

        await act.Should().ThrowAsync<VideoEngineUnavailableException>();
    }

    [Fact]
    public void Identity_KeyLabelCredits()
    {
        var (engine, _, _) = Create();
        engine.Key.Should().Be("higgsfield");
        engine.Label.Should().Be("Higgsfield");
        engine.PriceUnit.Should().Be(VideoPriceUnits.Credits);
        engine.SpendSource.Should().Be(SpendSources.Higgsfield);
    }

    // ── Фейки ────────────────────────────────────────────────────────────────────

    // Фейковый HTTP для Core-клиента Higgsfield: маршрут по функции, журнал запросов с телами
    private sealed class FakeHttp(Func<FakeHttp.Call, HttpResponseMessage> route) : HttpMessageHandler, IHttpClientFactory
    {
        public sealed record Call(HttpMethod Method, string Url, string Body);

        public ConcurrentQueue<Call> Calls { get; } = new();

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var call = new Call(request.Method, request.RequestUri!.ToString(), body);
            Calls.Enqueue(call);
            return route(call);
        }

        public static HttpResponseMessage Bytes(byte[] bytes, string? contentType) =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = contentType is null ? null : new(contentType) } },
            };

        // Ответ MCP tools/call в SSE, как отвечает mcp.higgsfield.ai
        public static HttpResponseMessage McpText(string text, bool isError = false)
        {
            var rpc = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: message\ndata: {rpc.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }

        public static string? Tool(Call call) =>
            call.Body.StartsWith('{') && JsonNode.Parse(call.Body)?["params"]?["name"]?.ToString() is { } name ? name : null;

        public static JsonObject? Arguments(Call call) =>
            JsonNode.Parse(call.Body)?["params"]?["arguments"] as JsonObject;

        // Загрузчик поверх этого же фейка: хосты *.test не резолвятся, проверку адреса пропускаем
        public SafeMediaDownloader Downloader() =>
            new(this, (_, _) => Task.FromResult(SsrfGuard.AddressCheck.Public));

        public static HiggsfieldMcpClient Client(FakeHttp http, string? token = "admin-token") =>
            new(http, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Higgsfield:McpUrl"] = "https://mcp.test/mcp" })
                .Build(), new FakeAccess(token))
            {
                Downloader = http.Downloader(),
            };
    }

    private sealed class FakeAccess(string? token) : IHiggsfieldAccess
    {
        public string? AccessToken() => token;
    }

    private sealed class StepTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
