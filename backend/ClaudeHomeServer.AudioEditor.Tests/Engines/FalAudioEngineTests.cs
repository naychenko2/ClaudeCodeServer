using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Schema;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Schema;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

public sealed class FalAudioEngineTests
{
    internal const string Queue = "https://q.test";
    internal const string Api = "https://api.test/v1";

    private static readonly AudioEditScope Personal = new(AudioEditScope.Personal, null);

    private readonly FakeFal _fal = new();

    private FalAudioEngine Engine(string? key = "fal-key") => new(new Factory(_fal), new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Пустая строка, а не отсутствие ключа: иначе драйвер возьмёт FAL_KEY машины
            ["Fal:ApiKey"] = key ?? "",
            ["Fal:QueueBase"] = Queue,
            ["Fal:ApiBase"] = Api,
        }).Build(), NullLogger<FalAudioEngine>.Instance)
    {
        PollInterval = TimeSpan.Zero,
        // cdn.test — «публичный» хост, всё прочее не резолвится
        Resolve = (host, _) => Task.FromResult(host == "cdn.test" ? [IPAddress.Parse("93.184.216.34")] : Array.Empty<IPAddress>()),
    };

    private static AudioModelInfo Model(string id) => AudioCatalog.FindFal(id)!.Info;

    // ── Нет ключа ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoKey_DisabledAndRefusesByValue_WithoutNetwork()
    {
        var engine = Engine(key: null);

        engine.Enabled.Should().BeFalse();
        var result = await engine.RunAsync(new AudioRequest(AudioOp.Speak, AudioCatalog.FalMiniMaxHd, Personal, Text: "Привет"),
            new Recorder(), CancellationToken.None);
        var quote = () => engine.EstimateAsync(Model(AudioCatalog.FalMiniMaxHd),
            new AudioRequest(AudioOp.Speak, AudioCatalog.FalMiniMaxHd, Personal, Text: "Привет"), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Unavailable);
        result.Charged.Should().BeFalse();
        await quote.Should().ThrowAsync<AudioEngineUnavailableException>();
        _fal.Requests.Should().BeEmpty();
    }

    // ── Отправка, опрос, скачивание ─────────────────────────────────────────────

    [Fact]
    public async Task Speak_SubmitPollDownload_BodyByOurLayout()
    {
        var endpoint = AudioCatalog.FalMiniMaxHd;
        _fal.Job(endpoint, "r1", ["IN_QUEUE:3", "IN_PROGRESS", "COMPLETED"],
            """{"audio":{"url":"https://cdn.test/a/speech.mp3","content_type":"audio/mpeg"},"duration_ms":1200}""");
        _fal.File("https://cdn.test/a/speech.mp3", [7, 7, 7]);
        var progress = new Recorder();
        var voice = new JsonObject { ["voice_setting"] = new JsonObject { ["voice_id"] = "Calm_Woman" } };

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Speak, endpoint, Personal, Text: "Привет, мир",
            Language: "ru", Params: voice), progress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be("r1");
        var file = result.Files.Should().ContainSingle().Subject;
        file.Role.Should().Be("main");
        file.Bytes.Should().Equal(7, 7, 7);
        file.Extension.Should().Be(".mp3");

        var submit = _fal.Requests.First(r => r.Method == HttpMethod.Post);
        submit.Url.Should().Be($"{Queue}/{endpoint}");
        submit.Auth.Should().Be("Key fal-key");
        var body = JsonNode.Parse(submit.Body!)!.AsObject();
        body["prompt"]!.GetValue<string>().Should().Be("Привет, мир");
        body["language_boost"]!.GetValue<string>().Should().Be("Russian");
        // По умолчанию MiniMax отдаёт hex вместо ссылки
        body["output_format"]!.GetValue<string>().Should().Be("url");
        body["voice_setting"]!["voice_id"]!.GetValue<string>().Should().Be("Calm_Woman");

        progress.Stages.Should().ContainInOrder(AudioStage.Queued, AudioStage.Running, AudioStage.Downloading);
        progress.Values.Should().Contain(p => p.Stage == AudioStage.Queued && p.QueuePosition == 3);
    }

    [Fact]
    public async Task Separate_DownloadsEveryStem_SourceAsDataUri()
    {
        var endpoint = AudioCatalog.FalDemucs;
        _fal.Job(endpoint, "r2", ["COMPLETED"],
            """
            {"vocals":{"url":"https://cdn.test/s/vocals.wav"},"drums":{"url":"https://cdn.test/s/drums.wav"},
             "bass":{"url":"https://cdn.test/s/bass.wav"},"other":{"url":"https://cdn.test/s/other.wav"},
             "guitar":null,"piano":null}
            """);
        foreach (var stem in new[] { "vocals", "drums", "bass", "other" })
            _fal.File($"https://cdn.test/s/{stem}.wav", Encoding.UTF8.GetBytes(stem));

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Separate, endpoint, Personal,
            Source: new AudioBytes([1, 2, 3], "audio/wav"), Params: new JsonObject { ["model"] = "htdemucs_ft" }),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Files.Select(f => f.Role).Should().Equal("stem:vocals", "stem:drums", "stem:bass", "stem:other");
        result.Files.Should().OnlyContain(f => f.Extension == ".wav");
        Encoding.UTF8.GetString(result.Files[1].Bytes).Should().Be("drums");
        var body = JsonNode.Parse(_fal.Requests.First(r => r.Method == HttpMethod.Post).Body!)!;
        body["audio_url"]!.GetValue<string>().Should().Be("data:audio/wav;base64,AQID");
        body["model"]!.GetValue<string>().Should().Be("htdemucs_ft");
    }

    [Fact]
    public async Task Transcribe_TextAndSubtitlesFromChunks()
    {
        var endpoint = AudioCatalog.FalWizper;
        _fal.Job(endpoint, "r3", ["COMPLETED"],
            """
            {"text":" Привет. Как дела?","chunks":[{"timestamp":[0.0,1.5],"text":" Привет."},
             {"timestamp":[1.5,null],"text":" Как дела?"}],"languages":["ru"]}
            """);

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Transcribe, endpoint, Personal, Language: "ru",
            Source: new AudioBytes([1], "audio/mpeg")), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Files.Select(f => (f.Role, f.Extension)).Should().Equal(("text", ".txt"), ("subtitles", ".srt"));
        Encoding.UTF8.GetString(result.Files[0].Bytes).Should().Be("Привет. Как дела?");
        Encoding.UTF8.GetString(result.Files[1].Bytes).Should().Be(
            "1\n00:00:00,000 --> 00:00:01,500\nПривет.\n\n2\n00:00:01,500 --> 00:00:03,500\nКак дела?\n\n");
        JsonNode.Parse(_fal.Requests.First(r => r.Method == HttpMethod.Post).Body!)!["language"]!.GetValue<string>()
            .Should().Be("ru");
    }

    [Fact]
    public async Task QwenClone_TwoRuns_EmbeddingLinkedIntoSpeech()
    {
        _fal.Job(AudioCatalog.FalQwenClone, "c1", ["COMPLETED"],
            """{"speaker_embedding":{"url":"https://cdn.test/e/voice.safetensors"}}""");
        _fal.Job(AudioCatalog.FalQwenTts, "t1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out.mp3"}}""");
        _fal.File("https://cdn.test/e/out.mp3", [9]);

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.CloneVoice, AudioCatalog.FalQwenClone, Personal,
            Text: "Скажи это моим голосом", Language: "ru", Reference: new AudioBytes([5], "audio/wav"),
            Params: new JsonObject { ["temperature"] = 0.7 }), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.RemoteId.Should().Be("t1");
        result.Files.Should().ContainSingle(f => f.Role == "main");
        var posts = _fal.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        posts.Select(p => p.Url).Should().Equal($"{Queue}/{AudioCatalog.FalQwenClone}", $"{Queue}/{AudioCatalog.FalQwenTts}");
        var clone = JsonNode.Parse(posts[0].Body!)!.AsObject();
        clone.Select(p => p.Key).Should().BeEquivalentTo("audio_url");
        var speech = JsonNode.Parse(posts[1].Body!)!;
        speech["speaker_voice_embedding_file_url"]!.GetValue<string>().Should().Be("https://cdn.test/e/voice.safetensors");
        speech["text"]!.GetValue<string>().Should().Be("Скажи это моим голосом");
        speech["language"]!.GetValue<string>().Should().Be("Russian");
        speech["temperature"]!.GetValue<double>().Should().Be(0.7);
    }

    // ── Отказы значением ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_NoBalance_InsufficientCreditsNotCharged()
    {
        _fal.Respond(HttpMethod.Post, $"{Queue}/{AudioCatalog.FalMiniMaxHd}", HttpStatusCode.PaymentRequired,
            """{"detail":"User is locked. Reason: Exhausted balance."}""");

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Speak, AudioCatalog.FalMiniMaxHd, Personal, Text: "a"),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
        result.Error.Should().Be("User is locked. Reason: Exhausted balance.");
    }

    [Fact]
    public async Task Network_Down_UnavailableByValue()
    {
        _fal.Throw = true;

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Speak, AudioCatalog.FalMiniMaxHd, Personal, Text: "a"),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Unavailable);
        result.Charged.Should().BeFalse();
        result.Error.Should().StartWith("fal.ai не ответил");
    }

    [Fact]
    public async Task MissingSource_RefusedBeforeSubmit()
    {
        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Separate, AudioCatalog.FalDemucs, Personal),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Error.Should().Be("Модели нужен исходный звук");
        _fal.Requests.Should().BeEmpty();
    }

    // ── Отмена ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_OutsideWhileQueued_RevokesAtFalAndThrows()
    {
        _fal.Job(AudioCatalog.FalElevenMusic, "r4", ["IN_QUEUE:1"], "{}", repeatLast: true);
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var progress = new Recorder { OnReport = p => { if (p.Stage == AudioStage.Queued && p.QueuePosition == 1) cts.Cancel(); } };

        var run = () => engine.RunAsync(new AudioRequest(AudioOp.Song, AudioCatalog.FalElevenMusic, Personal, Prompt: "lofi"),
            progress, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        _fal.Requests.Should().Contain(r => r.Method == HttpMethod.Put && r.Url == $"{Queue}/elevenlabs/requests/r4/cancel");
    }

    [Fact]
    public async Task CancelRemote_ByRequestIdWhileRunning()
    {
        _fal.Job(AudioCatalog.FalElevenMusic, "r5", ["IN_PROGRESS"], "{}", repeatLast: true);
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Recorder
        {
            OnReport = p =>
            {
                if (p.Stage != AudioStage.Running || cancelled.Task.IsCompleted) return;
                cancelled.TrySetResult(engine.CancelRemoteAsync("r5", CancellationToken.None).GetAwaiter().GetResult());
                cts.Cancel();
            },
        };

        var run = () => engine.RunAsync(new AudioRequest(AudioOp.Song, AudioCatalog.FalElevenMusic, Personal, Prompt: "lofi"),
            progress, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await cancelled.Task).Should().BeTrue();
        (await engine.CancelRemoteAsync("r5", CancellationToken.None)).Should().BeFalse("после конца задачи cancel_url забыт");
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, double, string, double, bool> Units => new()
    {
        // эндпоинт, единица fal, цена, наша единица, сумма, приблизительно
        { AudioCatalog.FalMiniMaxHd, "1000 characters", 0.1, AudioPriceUnits.Chars, 0.1 * 68 / 1000, false },
        { AudioCatalog.FalMiniMaxMusic, "seconds", 0.002, AudioPriceUnits.Sec, 0.002 * 90, false },
        { AudioCatalog.FalElevenMusic, "minutes", 0.6, AudioPriceUnits.Min, 0.6 * 1.5, false },
        { AudioCatalog.FalMiniMaxClone, "units", 1.5, AudioPriceUnits.Run, 1.5, false },
        { AudioCatalog.FalLyria, "generations", 0.1, AudioPriceUnits.Run, 0.1, false },
        { AudioCatalog.FalWizper, "compute seconds", 0.000625, AudioPriceUnits.Sec, 0.000625 * 90, true },
    };

    [Theory]
    [MemberData(nameof(Units))]
    public async Task Estimate_AllFalUnits(string endpoint, string falUnit, double price, string unit, double amount, bool approx)
    {
        _fal.Price(endpoint, price, falUnit);
        var model = Model(endpoint);
        var request = new AudioRequest(model.Caps.Ops[0], endpoint, Personal, Text: new string('я', 68), DurationSec: 90);

        var estimate = await Engine().EstimateAsync(model, request, CancellationToken.None);

        estimate.Unit.Should().Be(unit);
        estimate.Amount.Should().BeApproximately(amount, 1e-12);
        estimate.Approx.Should().Be(approx);
        estimate.Source.Should().Be(AudioEstimateSources.Provider);
    }

    [Fact]
    public async Task Estimate_PriceCached_AndHintWhenPricingDown()
    {
        _fal.Price(AudioCatalog.FalElevenV4, 0.08, "1000 characters");
        var engine = Engine();
        var model = Model(AudioCatalog.FalElevenV4);
        var request = new AudioRequest(AudioOp.Speak, model.Id, Personal, Text: new string('a', 500));

        (await engine.EstimateAsync(model, request, CancellationToken.None)).Amount.Should().BeApproximately(0.04, 1e-12);
        (await engine.EstimateAsync(model, request, CancellationToken.None)).Amount.Should().BeApproximately(0.04, 1e-12);
        _fal.Requests.Count(r => r.Url.Contains("/models/pricing")).Should().Be(1);

        // Прайс лежит — ориентир каталога в той же единице
        var demucs = Model(AudioCatalog.FalDemucs);
        var fallback = await engine.EstimateAsync(demucs,
            new AudioRequest(AudioOp.Separate, demucs.Id, Personal, DurationSec: 100), CancellationToken.None);
        fallback.Source.Should().Be(AudioEstimateSources.Catalog);
        fallback.Unit.Should().Be(AudioPriceUnits.Sec);
        fallback.Amount.Should().BeApproximately(0.07, 1e-12);
        fallback.Approx.Should().BeTrue();
    }

    // Трата — на запустившего, «эндпоинт · единица», доллары котировки
    [Fact]
    public async Task Job_SpendLabelIsEndpointAndUnit_CostUsd()
    {
        var endpoint = AudioCatalog.FalMiniMaxHd;
        _fal.Price(endpoint, 0.1, "1000 characters");
        _fal.Job(endpoint, "r6", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/a/x.mp3"}}""");
        _fal.File("https://cdn.test/a/x.mp3", [1]);
        var spend = new List<SpendRecord>();
        var collector = new Mock<ISpendCollector>();
        collector.Setup(s => s.Record(It.IsAny<SpendRecord>())).Callback<SpendRecord>(r => { lock (spend) spend.Add(r); });
        var root = Path.Combine(Path.GetTempPath(), "fal-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new AudioEditJobService([Engine()], new AudioEditWorkspace(root), NullLogger<AudioEditJobService>.Instance,
                spend: collector.Object);
            var text = new string('ы', 68);
            var quote = await svc.QuoteAsync("owner", Personal,
                new AudioQuoteRequest(AudioModes.Voice, "speak", "fal", endpoint) with { Text = text }, CancellationToken.None);
            quote.Error.Should().BeNull();
            quote.Value!.Price.Unit.Should().Be(AudioPriceUnits.Chars);

            var started = await svc.StartAsync("owner", Personal, new AudioJobInput(quote.Value.QuoteId, Text: text),
                CancellationToken.None);
            await svc.WhenDone(started.Value!.JobId);

            lock (spend)
            {
                var record = spend.Should().ContainSingle().Subject;
                record.Label.Should().Be($"{endpoint} · 68 симв.");
                record.CostUsd.Should().BeApproximately(0.0068, 1e-12);
                record.Provider.Should().Be("fal");
                record.CostCredits.Should().BeNull();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── Скачивание результата ──────────────────────────────────────────────────

    [Fact]
    public async Task Download_InternalUrlInResult_RefusedByValue_WithoutRequest()
    {
        var endpoint = AudioCatalog.FalMiniMaxHd;
        _fal.Job(endpoint, "r7", ["COMPLETED"], """{"audio":{"url":"https://169.254.169.254/latest/meta-data"}}""");

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.Speak, endpoint, Personal, Text: "Привет"),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.Error.Should().Contain("внутренний адрес");
        _fal.Requests.Should().NotContain(r => r.Url.Contains("169.254"));
    }

    [Fact]
    public async Task CloneChain_InternalEmbeddingLink_RefusedBeforeSecondRun()
    {
        _fal.Job(AudioCatalog.FalQwenClone, "c2", ["COMPLETED"],
            """{"speaker_embedding":{"url":"https://10.0.0.1/voice.safetensors"}}""");

        var result = await Engine().RunAsync(new AudioRequest(AudioOp.CloneVoice, AudioCatalog.FalQwenClone, Personal,
                Text: "Привет", Reference: new AudioBytes([1, 2], "audio/wav")),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.Error.Should().Contain("эмбеддинг");
        _fal.Requests.Should().NotContain(r => r.Url.Contains(AudioCatalog.FalQwenTts));
    }

    // ── Подставки ────────────────────────────────────────────────────────────────

    internal sealed class Recorder : IProgress<AudioProgress>
    {
        public List<AudioProgress> Values { get; } = [];
        public List<AudioStage> Stages => [.. Values.Select(v => v.Stage)];
        public Action<AudioProgress>? OnReport { get; init; }

        public void Report(AudioProgress value)
        {
            lock (Values) Values.Add(value);
            OnReport?.Invoke(value);
        }
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // fal по сценарию: очередь (submit → статусы → результат), файлы CDN, прайс; всё прочее — 404
    // ── Схема входа («Дополнительно») ───────────────────────────────────────────

    private readonly Clock _clock = new();

    private static string SchemaUrl(string endpoint) =>
        $"{Api}/models?endpoint_id={Uri.EscapeDataString(endpoint)}&expand=openapi-3.0";

    private static string ModelsBody(string endpoint) =>
        $$"""{"models":[{"endpoint_id":"{{endpoint}}","openapi":{{ClaudeHomeServer.AudioEditor.Tests.Schema.AudioSchemaTests.OpenApi}}}],"has_more":false}""";

    [Fact]
    public async Task Schema_CachedWithinTtl_RefetchedAfter_CommonFieldsReserved()
    {
        var endpoint = AudioCatalog.FalElevenSfx;
        _fal.Respond(HttpMethod.Get, SchemaUrl(endpoint), HttpStatusCode.OK, ModelsBody(endpoint), repeat: true);
        var engine = Engine();
        engine.Time = _clock;

        var first = await engine.SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);
        _clock.Advance(FalAudioEngine.SchemaTtl - TimeSpan.FromMinutes(1));
        await engine.SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);

        _fal.Requests.Count(r => r.Url == SchemaUrl(endpoint)).Should().Be(1);
        _fal.Requests.Single(r => r.Url == SchemaUrl(endpoint)).Auth.Should().Be("Key fal-key");
        var schema = first.Schema!;
        schema.Source.Should().Be(AudioSchemaSources.FalOpenApi);
        schema.Stale.Should().BeFalse();
        // text и duration_seconds — общие поля (описание звука и длительность): в форме и в params их нет
        schema.Reserved.Should().BeEquivalentTo(["text", "duration_seconds"]);
        schema.Fields.Select(f => f.Key).Should().Equal("prompt_influence", "voice_setting", "output_format");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await engine.SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);
        _fal.Requests.Count(r => r.Url == SchemaUrl(endpoint)).Should().Be(2);
    }

    // Поля-ссылки схемы (вебхук, колбэк, *_url) движок кладёт в Reserved: в форме их нет, params их не пустят
    [Fact]
    public async Task Schema_LinkFields_Reserved()
    {
        var endpoint = AudioCatalog.FalElevenSfx;
        _fal.Respond(HttpMethod.Get, SchemaUrl(endpoint), HttpStatusCode.OK,
            $$"""{"models":[{"endpoint_id":"{{endpoint}}","openapi":{{FalLiveSchemaTests.WithLinks()}}}],"has_more":false}""");

        var schema = (await Engine().SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None)).Schema!;

        schema.Reserved.Should().BeEquivalentTo(["text", "duration_seconds", .. FalLiveSchemaTests.LinkKeys]);
        schema.Fields.Select(f => f.Key).Should().Equal("prompt_influence", "output_format", "loop", "extra");
        AudioParamValidator.Validate(schema, new JsonObject { ["webhook_url"] = "https://evil.example/hook" })
            .Should().Be("Параметр «webhook_url» задаётся общим полем запроса, а не в params");
    }

    [Fact]
    public async Task Schema_FalDown_LastGoodStale_OtherwiseRefusalByValue()
    {
        var endpoint = AudioCatalog.FalElevenSfx;
        _fal.Respond(HttpMethod.Get, SchemaUrl(endpoint), HttpStatusCode.OK, ModelsBody(endpoint));
        _fal.Respond(HttpMethod.Get, SchemaUrl(endpoint), HttpStatusCode.InternalServerError, "{}", repeat: true);
        var engine = Engine();
        engine.Time = _clock;

        (await engine.SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None)).Schema!.Stale.Should().BeFalse();
        _clock.Advance(FalAudioEngine.SchemaTtl + TimeSpan.FromMinutes(1));
        var stale = await engine.SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);

        stale.Schema!.Stale.Should().BeTrue();
        stale.Schema.Fields.Should().NotBeEmpty();

        var cold = await Engine().SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);
        cold.Schema.Should().BeNull();
        cold.Error.Should().Be("Схема модели fal.ai недоступна: fal.ai ответил 500");

        _fal.Throw = true;
        var down = await Engine().SchemaAsync(Model(endpoint), AudioOp.Sfx, CancellationToken.None);
        down.Error.Should().StartWith("Схема модели fal.ai недоступна:");
    }

    [Fact]
    public async Task Schema_Chain_TakesSecondEndpoint_NoKey_Refused()
    {
        _fal.Respond(HttpMethod.Get, SchemaUrl(AudioCatalog.FalQwenTts), HttpStatusCode.OK, ModelsBody(AudioCatalog.FalQwenTts));

        var chain = await Engine().SchemaAsync(Model(AudioCatalog.FalQwenClone), AudioOp.CloneVoice, CancellationToken.None);
        var noKey = await Engine(key: null).SchemaAsync(Model(AudioCatalog.FalQwenClone), AudioOp.CloneVoice, CancellationToken.None);

        chain.Schema!.Model.Should().Be(AudioCatalog.FalQwenClone);
        chain.Schema.Reserved.Should().Contain("speaker_voice_embedding_file_url");
        _fal.Requests.Should().ContainSingle(r => r.Url == SchemaUrl(AudioCatalog.FalQwenTts));
        noKey.Error.Should().Be("fal.ai не настроен: нет ключа");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class FakeFal : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<(string, string), Queue<(HttpStatusCode, string, bool)>> _routes = new();
        private readonly ConcurrentDictionary<string, byte[]> _files = new();

        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];
        public bool Throw { get; set; }

        public void Respond(HttpMethod method, string url, HttpStatusCode code, string body, bool repeat = false) =>
            _routes.GetOrAdd((method.Method, url), _ => new Queue<(HttpStatusCode, string, bool)>()).Enqueue((code, body, repeat));

        // Задача очереди: статусы по порядку («IN_QUEUE:3» — с позицией), последний можно повторять вечно
        public void Job(string endpoint, string id, string[] statuses, string result, bool repeatLast = false)
        {
            var app = string.Join('/', endpoint.Split('/').Take(endpoint.StartsWith("fal-ai/") ? 2 : 1));
            var baseUrl = $"{Queue}/{app}/requests/{id}";
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
            Respond(HttpMethod.Get, baseUrl, HttpStatusCode.OK, result);
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
            if (Throw) throw new HttpRequestException("connection refused");

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
