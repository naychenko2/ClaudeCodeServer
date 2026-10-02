using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Engines;

public sealed class FalVideoEngineTests
{
    private const string Queue = "https://q.test";
    private const string Api = "https://api.test/v1";

    private static readonly VideoEditScope Personal = new(VideoEditScope.Personal, null);
    private static readonly VideoFrameBytes FrameA = new([1, 2, 3], "image/png");
    private static readonly VideoFrameBytes FrameB = new([4, 5], "image/jpeg");

    private readonly FakeFal _fal = new();

    private FalVideoEngine Engine(string? key = "fal-key") => new(new Factory(_fal), new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Пустая строка, а не отсутствие ключа: иначе драйвер возьмёт FAL_KEY машины
            ["Fal:ApiKey"] = key ?? "",
            ["Fal:QueueBase"] = Queue,
            ["Fal:ApiBase"] = Api,
        }).Build(), NullLogger<FalVideoEngine>.Instance)
    {
        PollInterval = TimeSpan.Zero,
        // cdn.test — «публичный» хост, прочее — боевая проверка SsrfGuard
        Downloader = new SafeMediaDownloader(_fal, (uri, ct) => uri.Host == "cdn.test"
            ? Task.FromResult(SsrfGuard.AddressCheck.Public)
            : SsrfGuard.CheckAsync(uri, ct)),
    };

    private static VideoRequest Request(string model, int duration = 5, VideoFrameBytes? last = null, string? aspect = null,
        bool sound = false, JsonObject? @params = null, long? seed = null) =>
        new(model, Personal, "Камера медленно приближается к окну", FrameA, last, duration, aspect, sound, @params, seed);

    private static VideoModelInfo Model(string id) => FalVideoCatalog.Find(id)!.Info;

    // ── Нет ключа ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoKey_DisabledAndRefusesByValue_WithoutNetwork()
    {
        var engine = Engine(key: null);

        engine.Enabled.Should().BeFalse();
        var result = await engine.RunAsync(Request(FalVideoCatalog.KlingO1), new Recorder(), CancellationToken.None);
        var quote = () => engine.EstimateAsync(Model(FalVideoCatalog.KlingO1), Request(FalVideoCatalog.KlingO1),
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        result.Charged.Should().BeFalse();
        await quote.Should().ThrowAsync<VideoEngineUnavailableException>();
        _fal.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Identity_KeyLabelUnit_PersonalScopeAllowed()
    {
        var engine = Engine();

        engine.Key.Should().Be("fal");
        engine.Label.Should().Be("fal.ai");
        engine.PriceUnit.Should().Be(VideoPriceUnits.Usd);
        ((IVideoEngine)engine).ScopeRefusal(Personal).Should().BeNull();
        engine.Models.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(m => m.Caps.FirstFrame && m.Caps.LastFrame);
    }

    // ── Отправка, опрос, скачивание ─────────────────────────────────────────────

    [Fact]
    public async Task Run_SubmitPollDownload_AcceptedWithRemoteId_BodyByLayout()
    {
        var endpoint = FalVideoCatalog.VeoLite;
        _fal.Job(endpoint, "r1", ["IN_QUEUE:2", "IN_PROGRESS", "COMPLETED"],
            """{"video":{"url":"https://cdn.test/v/out.mp4","content_type":"video/mp4"}}""");
        _fal.File("https://cdn.test/v/out.mp4", [9, 9, 9]);
        var progress = new Recorder();

        var result = await Engine().RunAsync(Request(endpoint, duration: 6, last: FrameB, aspect: "16:9", sound: true,
            @params: new JsonObject { ["resolution"] = "1080p" }, seed: 42), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be("r1");
        result.File!.Bytes.Should().Equal(9, 9, 9);
        result.File.ContentType.Should().Be("video/mp4");
        result.File.Extension.Should().Be(".mp4");
        result.File.HasSound.Should().BeTrue();

        var submit = _fal.Requests.First(r => r.Method == HttpMethod.Post);
        submit.Url.Should().Be($"{Queue}/{endpoint}");
        submit.Auth.Should().Be("Key fal-key");
        var body = JsonNode.Parse(submit.Body!)!.AsObject();
        body["prompt"]!.GetValue<string>().Should().Be("Камера медленно приближается к окну");
        body["first_frame_url"]!.GetValue<string>().Should().Be("data:image/png;base64,AQID");
        body["last_frame_url"]!.GetValue<string>().Should().Be("data:image/jpeg;base64,BAU=");
        body["duration"]!.GetValue<string>().Should().Be("6s");
        body["aspect_ratio"]!.GetValue<string>().Should().Be("16:9");
        body["generate_audio"]!.GetValue<bool>().Should().BeTrue();
        body["seed"]!.GetValue<long>().Should().Be(42);
        body["resolution"]!.GetValue<string>().Should().Be("1080p");

        // Принятие — первым же событием, с id задачи у fal
        var first = progress.Values.First();
        first.Accepted.Should().BeTrue();
        first.RemoteId.Should().Be("r1");
        first.Stage.Should().Be(VideoStage.Queued);
        progress.Values.Should().Contain(p => p.Stage == VideoStage.Queued && p.QueuePosition == 2);
        progress.Values.Select(p => p.Stage).Should().ContainInOrder(VideoStage.Queued, VideoStage.Running, VideoStage.Downloading);
    }

    [Fact]
    public async Task Run_KlingOptionalLast_DurationAsString_NoAspectNoSoundFields()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r2", ["COMPLETED"], """{"video":{"url":"https://cdn.test/v/k.mp4"}}""");
        _fal.File("https://cdn.test/v/k.mp4", [1]);

        var result = await Engine().RunAsync(Request(endpoint, duration: 5, aspect: "16:9", sound: true),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.HasSound.Should().BeNull();
        var body = JsonNode.Parse(_fal.Requests.First(r => r.Method == HttpMethod.Post).Body!)!.AsObject();
        body.Select(p => p.Key).Should().BeEquivalentTo("prompt", "start_image_url", "duration");
        body["duration"]!.GetValue<string>().Should().Be("5");
    }

    [Fact]
    public async Task Run_MiniMaxDefaultResolution_ParamsOverride()
    {
        var endpoint = FalVideoCatalog.MiniMaxH3;
        _fal.Job(endpoint, "r3", ["COMPLETED"], """{"video":{"url":"https://cdn.test/v/m.mp4"}}""");
        _fal.File("https://cdn.test/v/m.mp4", [1]);

        await Engine().RunAsync(Request(endpoint, duration: 10), new Recorder(), CancellationToken.None);

        var body = JsonNode.Parse(_fal.Requests.First(r => r.Method == HttpMethod.Post).Body!)!;
        body["resolution"]!.GetValue<string>().Should().Be("768P");
        body["duration"]!.GetValue<int>().Should().Be(10);
        body["image_url"]!.GetValue<string>().Should().StartWith("data:image/png;base64,");
    }

    // ── Отказы до отправки ───────────────────────────────────────────────────────

    public static TheoryData<string, int, bool, string?, string> BadInputs => new()
    {
        { FalVideoCatalog.FluxDraft, 5, false, null, "нужен и последний кадр" },
        { FalVideoCatalog.VeoLite, 5, true, null, "не берёт длительность 5 с" },
        { FalVideoCatalog.VeoLite, 4, true, "4:3", "не берёт пропорцию 4:3" },
        { FalVideoCatalog.KlingO1, 5, false, "webhook_url", "не знает параметр «webhook_url»" },
    };

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task Run_BadInput_RefusedBeforeSubmit_NotCharged(string model, int duration, bool withLast, string? extra,
        string error)
    {
        var aspect = extra is { } e && e.Contains(':') ? e : null;
        var @params = extra is { } p && !p.Contains(':') ? new JsonObject { [p] = "https://evil.test/hook" } : null;

        var result = await Engine().RunAsync(Request(model, duration, withLast ? FrameB : null, aspect, @params: @params),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeFalse();
        result.Error.Should().Contain(error);
        _fal.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_FrameOverCap_RefusedBeforeSubmit()
    {
        var huge = new VideoFrameBytes(new byte[FalVideoCatalog.MaxFrameBytes + 1], "image/png");

        var result = await Engine().RunAsync(Request(FalVideoCatalog.KlingO1) with { FrameA = huge },
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("Первый кадр больше");
        _fal.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_NoBalance_InsufficientCredits_NotChargedNotAccepted()
    {
        _fal.Respond(HttpMethod.Post, $"{Queue}/{FalVideoCatalog.KlingO1}", HttpStatusCode.PaymentRequired,
            """{"detail":"User is locked. Reason: Exhausted balance."}""");
        var progress = new Recorder();

        var result = await Engine().RunAsync(Request(FalVideoCatalog.KlingO1), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
        result.Error.Should().Be("User is locked. Reason: Exhausted balance.");
        progress.Values.Should().NotContain(p => p.Accepted);
    }

    [Fact]
    public async Task Network_Down_UnavailableNotCharged()
    {
        _fal.Throw = true;

        var result = await Engine().RunAsync(Request(FalVideoCatalog.KlingO1), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        result.Charged.Should().BeFalse();
        result.Error.Should().StartWith("fal.ai не ответил");
    }

    [Fact]
    public async Task Submit_QueueUrlOutsideFalHosts_RefusedByValue_KeyNotSent()
    {
        _fal.Respond(HttpMethod.Post, $"{Queue}/{FalVideoCatalog.KlingO1}", HttpStatusCode.OK,
            """{"request_id":"r1","status_url":"https://evil.test/s","response_url":"https://q.test/requests/r1","cancel_url":"https://q.test/requests/r1/cancel"}""");

        var result = await Engine().RunAsync(Request(FalVideoCatalog.KlingO1), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("вне своих хостов");
        _fal.Requests.Should().ContainSingle();
    }

    // ── Сбои после принятия ─────────────────────────────────────────────────────

    [Fact]
    public async Task Result_Failed_AfterAcceptance_NotCharged()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r4", ["COMPLETED"], """{"detail":"Image too small"}""", resultCode: HttpStatusCode.UnprocessableEntity);
        var progress = new Recorder();

        var result = await Engine().RunAsync(Request(endpoint), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeFalse("неуспешный запрос fal не тарифицирует");
        result.RemoteId.Should().Be("r4");
        result.Error.Should().Be("Image too small");
        progress.Values.Should().Contain(p => p.Accepted);
    }

    [Fact]
    public async Task StatusPoll_NetworkDrop_AfterAcceptance_ChargedUnknown()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r5", ["IN_PROGRESS"], "{}", repeatLast: true);
        var progress = new Recorder { OnReport = p => { if (p.Stage == VideoStage.Running) _fal.Throw = true; } };

        var result = await Engine().RunAsync(Request(endpoint), progress, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeNull();
        result.RemoteId.Should().Be("r5");
        result.Error.Should().StartWith("Сбой связи с fal.ai");
    }

    [Fact]
    public async Task Download_InternalUrl_RefusedByValue_Charged()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r6", ["COMPLETED"], """{"video":{"url":"https://169.254.169.254/latest/meta-data"}}""");

        var result = await Engine().RunAsync(Request(endpoint), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.Error.Should().Contain("внутренний");
        _fal.Requests.Should().NotContain(r => r.Url.Contains("169.254"));
    }

    [Fact]
    public async Task Download_UsesVideoCap()
    {
        Engine().MaxDownloadBytes.Should().Be(SafeMediaDownloader.VideoMaxBytes);
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r7", ["COMPLETED"], """{"video":{"url":"https://cdn.test/v/big.mp4"}}""");
        _fal.File("https://cdn.test/v/big.mp4", new byte[11]);
        var engine = new FalVideoEngine(new Factory(_fal), Config(), NullLogger<FalVideoEngine>.Instance)
        {
            PollInterval = TimeSpan.Zero,
            MaxDownloadBytes = 10,
            Downloader = new SafeMediaDownloader(_fal, (_, _) => Task.FromResult(SsrfGuard.AddressCheck.Public)),
        };

        var result = await engine.RunAsync(Request(endpoint), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("больше");
    }

    [Fact]
    public async Task Ceiling_Exceeded_FailedChargedUnknown_RevokedAtFal()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r8", ["IN_QUEUE:1"], "{}", repeatLast: true);
        var engine = Engine();
        engine.Ceiling = TimeSpan.FromMilliseconds(200);
        engine.PollInterval = TimeSpan.FromMilliseconds(20);

        var result = await engine.RunAsync(Request(endpoint), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Charged.Should().BeNull();
        result.RemoteId.Should().Be("r8");
        result.Error.Should().StartWith("fal.ai не ответил за");
        _fal.Requests.Should().Contain(r => r.Method == HttpMethod.Put && r.Url.EndsWith("/requests/r8/cancel"));
    }

    // ── Отмена ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_OutsideWhileQueued_RevokesAtFalAndThrows()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r9", ["IN_QUEUE:1"], "{}", repeatLast: true);
        using var cts = new CancellationTokenSource();
        var progress = new Recorder { OnReport = p => { if (p.QueuePosition == 1) cts.Cancel(); } };

        var run = () => Engine().RunAsync(Request(endpoint), progress, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        _fal.Requests.Should().Contain(r => r.Method == HttpMethod.Put && r.Url.EndsWith("/requests/r9/cancel"));
    }

    [Fact]
    public async Task CancelRemote_ByRequestIdWhileRunning_ForgottenAfter()
    {
        var endpoint = FalVideoCatalog.KlingO1;
        _fal.Job(endpoint, "r10", ["IN_PROGRESS"], "{}", repeatLast: true);
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Recorder
        {
            OnReport = p =>
            {
                if (p.Stage != VideoStage.Running || cancelled.Task.IsCompleted) return;
                cancelled.TrySetResult(engine.CancelRemoteAsync("r10", CancellationToken.None).GetAwaiter().GetResult());
                cts.Cancel();
            },
        };

        var run = () => engine.RunAsync(Request(endpoint), progress, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await cancelled.Task).Should().BeTrue();
        (await engine.CancelRemoteAsync("r10", CancellationToken.None)).Should().BeFalse("после конца задачи cancel_url забыт");
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estimate_PerSecondPrice_TimesDuration_Cached()
    {
        _fal.Price(FalVideoCatalog.KlingO1, 0.084, "seconds");
        var engine = Engine();
        var model = Model(FalVideoCatalog.KlingO1);

        var estimate = await engine.EstimateAsync(model, Request(FalVideoCatalog.KlingO1, duration: 10), CancellationToken.None);
        await engine.EstimateAsync(model, Request(FalVideoCatalog.KlingO1, duration: 10), CancellationToken.None);

        estimate.Amount.Should().BeApproximately(0.84, 1e-12);
        estimate.Unit.Should().Be(VideoPriceUnits.Usd);
        estimate.Approx.Should().BeFalse();
        estimate.Source.Should().Be(VideoEstimateSources.Provider);
        estimate.EtaSeconds.Should().Be(engine.ExpectedSeconds(model, Request(FalVideoCatalog.KlingO1)));
        _fal.Requests.Count(r => r.Url.Contains("/models/pricing")).Should().Be(1);
    }

    [Fact]
    public async Task Estimate_PriceVariesByOptions_Approx_PerRunUnit_NotMultiplied()
    {
        _fal.Price(FalVideoCatalog.VeoLite, 0.05, "seconds");
        _fal.Price(FalVideoCatalog.MiniMaxH3, 0.4, "videos");

        var veo = await Engine().EstimateAsync(Model(FalVideoCatalog.VeoLite), Request(FalVideoCatalog.VeoLite, duration: 8),
            CancellationToken.None);
        var perRun = await Engine().EstimateAsync(Model(FalVideoCatalog.MiniMaxH3), Request(FalVideoCatalog.MiniMaxH3, duration: 8),
            CancellationToken.None);

        veo.Amount.Should().BeApproximately(0.4, 1e-12);
        veo.Approx.Should().BeTrue();
        perRun.Amount.Should().BeApproximately(0.4, 1e-12);
    }

    [Fact]
    public async Task Estimate_PricingDown_CatalogHint()
    {
        var estimate = await Engine().EstimateAsync(Model(FalVideoCatalog.FluxDraft), Request(FalVideoCatalog.FluxDraft, duration: 10),
            CancellationToken.None);

        estimate.Source.Should().Be(VideoEstimateSources.Catalog);
        estimate.Amount.Should().BeApproximately(0.3, 1e-12);
        estimate.Unit.Should().Be(VideoPriceUnits.Usd);
        estimate.Approx.Should().BeTrue();
    }

    // ── Подставки ────────────────────────────────────────────────────────────────

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fal:ApiKey"] = "fal-key",
            ["Fal:QueueBase"] = Queue,
            ["Fal:ApiBase"] = Api,
        }).Build();

    private sealed class Recorder : IProgress<VideoProgress>
    {
        public List<VideoProgress> Values { get; } = [];
        public Action<VideoProgress>? OnReport { get; init; }

        public void Report(VideoProgress value)
        {
            lock (Values) Values.Add(value);
            OnReport?.Invoke(value);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // fal по сценарию: очередь (submit → статусы → результат), файлы CDN, прайс; всё прочее — 404
    private sealed class FakeFal : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<(string, string), Queue<(HttpStatusCode, string, bool)>> _routes = new();
        private readonly ConcurrentDictionary<string, byte[]> _files = new();

        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];
        public volatile bool Throw;

        public void Respond(HttpMethod method, string url, HttpStatusCode code, string body, bool repeat = false) =>
            _routes.GetOrAdd((method.Method, url), _ => new Queue<(HttpStatusCode, string, bool)>()).Enqueue((code, body, repeat));

        public void Job(string endpoint, string id, string[] statuses, string result, bool repeatLast = false,
            HttpStatusCode resultCode = HttpStatusCode.OK)
        {
            var baseUrl = $"{Queue}/{endpoint.Split('/')[0]}/requests/{id}";
            Respond(HttpMethod.Post, $"{Queue}/{endpoint}", HttpStatusCode.OK,
                $$"""{"request_id":"{{id}}","status_url":"{{baseUrl}}/status","response_url":"{{baseUrl}}","cancel_url":"{{baseUrl}}/cancel"}""");
            for (var i = 0; i < statuses.Length; i++)
            {
                var parts = statuses[i].Split(':');
                var json = parts.Length > 1
                    ? $$"""{"status":"{{parts[0]}}","queue_position":{{parts[1]}}}"""
                    : $$"""{"status":"{{parts[0]}}"}""";
                Respond(HttpMethod.Get, $"{baseUrl}/status", HttpStatusCode.OK, json, repeatLast && i == statuses.Length - 1);
            }
            Respond(HttpMethod.Get, baseUrl, resultCode, result);
            Respond(HttpMethod.Put, $"{baseUrl}/cancel", HttpStatusCode.OK, """{"status":"CANCELLATION_REQUESTED"}""", repeat: true);
        }

        public void File(string url, byte[] bytes) => _files[url] = bytes;

        public void Price(string endpoint, double price, string unit) =>
            Respond(HttpMethod.Get, $"{Api}/models/pricing?endpoint_id={Uri.EscapeDataString(endpoint)}", HttpStatusCode.OK,
                $$"""{"prices":[{"endpoint_id":"{{endpoint}}","unit_price":{{price.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"unit":"{{unit}}","currency":"USD"}]}""",
                repeat: true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method, url, body, request.Headers.Authorization?.ToString()));
            if (Throw && request.Method != HttpMethod.Put) throw new HttpRequestException("connection refused");

            if (request.Method == HttpMethod.Get && _files.TryGetValue(url, out var bytes))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (_routes.TryGetValue((request.Method.Method, url), out var queue))
            {
                (HttpStatusCode Code, string Body, bool Repeat) next;
                lock (queue)
                {
                    if (queue.Count == 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    next = queue.Peek();
                    if (!next.Repeat) queue.Dequeue();
                }
                return new HttpResponseMessage(next.Code) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }
    }
}
