using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Engines;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Voices;

// Применение голосов из библиотеки поставщиками (ADR-021 §5): voice:<slug> при запуске превращается в
// образец, эмбеддинг, элемент, custom_voice_id или пару RVC; созданная привязка кешируется один раз;
// протухший клон MiniMax — отказ с котировкой пересоздания без вызова поставщика; пересоздание — только
// через свою котировку и задачу; Яндекс голос из библиотеки не берёт. Поставщики — на фейковом HTTP.
public sealed class VoiceLaunchTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Queue = FalAudioEngineTests.Queue;
    private const string Transcript = "Привет, меня зовут Аня";

    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "voice-launch-" + Guid.NewGuid().ToString("N"));
    private readonly StepTime _time = new();
    private readonly VoiceLibrary _lib;
    private readonly AudioEditScope _scope;
    private readonly AudioEditWorkspace _workspace;
    private readonly FalAudioEngineTests.FakeFal _fal = new();
    private readonly string _slug;

    public VoiceLaunchTests()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        _lib = new VoiceLibrary(_time);
        _scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = project, OwnerId = Owner });
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        _slug = _lib.CreateFromSamples(_scope, "Аня", Transcript, [new VoiceSampleUpload(Wav)]).Value!.Manifest.Slug;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service(params IAudioEngine[] engines) =>
        new(engines, _workspace, NullLogger<AudioEditJobService>.Instance, time: _time, voices: _lib);

    private FalAudioEngine Fal() => new(new FalAudioEngineTests.Factory(_fal), new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fal:ApiKey"] = "fal-key",
            ["Fal:QueueBase"] = Queue,
            ["Fal:ApiBase"] = FalAudioEngineTests.Api,
        }).Build(), NullLogger<FalAudioEngine>.Instance)
    {
        PollInterval = TimeSpan.Zero,
        // Общий загрузчик поверх фейка: cdn.test — «публичный» хост, прочее — боевая проверка SsrfGuard
        Downloader = new SafeMediaDownloader(_fal, (uri, ct) => uri.Host == "cdn.test"
            ? Task.FromResult(SsrfGuard.AddressCheck.Public)
            : SsrfGuard.CheckAsync(uri, ct)),
    };

    private string Voice => AudioVoiceRefs.Prefix + _slug;

    private async Task<AudioEditCallResult<AudioJobCreatedDto>> RunAsync(AudioEditJobService svc, string provider, string model,
        AudioOp op, AudioJobInput? input = null)
    {
        input ??= new AudioJobInput("", Text: "Скажи это моим голосом", Voice: Voice);
        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, AudioEditJobService.OpName(op),
            provider, model, Text: input.Text, Prompt: input.Prompt, Lyrics: input.Lyrics, DurationSec: input.DurationSec),
            CancellationToken.None);
        quote.Error.Should().BeNull();
        var started = await svc.StartAsync(Owner, _scope, input with { QuoteId = quote.Value!.QuoteId }, CancellationToken.None);
        if (started.Value is { } created) await svc.WhenDone(created.JobId);
        return started;
    }

    private VoiceManifest Manifest() => VoiceStore.Get(_scope.Project!.RootPath, _slug)!;

    private void SetMiniMax(string id) => _lib.UpdateProviders(_scope, _slug, (p, now) => VoiceProviders.SetMiniMax(p, id, now));

    private List<(HttpMethod Method, string Url, string? Body, string? Auth)> Posts() =>
        [.. _fal.Requests.Where(r => r.Method == HttpMethod.Post)];

    // ── fal: Qwen-клон ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FalQwenClone_SampleAsDataUri_EmbeddingCachedOnce_SecondRunSkipsClone()
    {
        _fal.Job(AudioCatalog.FalQwenClone, "c1", ["COMPLETED"], """{"speaker_embedding":{"url":"https://cdn.test/e/voice.safetensors"}}""");
        _fal.Job(AudioCatalog.FalQwenTts, "t1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out1.mp3"}}""");
        _fal.Job(AudioCatalog.FalQwenTts, "t2", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out2.mp3"}}""");
        _fal.File("https://cdn.test/e/out1.mp3", [9]);
        _fal.File("https://cdn.test/e/out2.mp3", [8]);
        var svc = Service(Fal());

        (await RunAsync(svc, "fal", AudioCatalog.FalQwenClone, AudioOp.CloneVoice)).Error.Should().BeNull();
        (await RunAsync(svc, "fal", AudioCatalog.FalQwenClone, AudioOp.CloneVoice)).Error.Should().BeNull();

        var posts = Posts();
        posts.Select(p => p.Url).Should().Equal(
            $"{Queue}/{AudioCatalog.FalQwenClone}", $"{Queue}/{AudioCatalog.FalQwenTts}", $"{Queue}/{AudioCatalog.FalQwenTts}");
        JsonNode.Parse(posts[0].Body!)!["audio_url"]!.GetValue<string>()
            .Should().Be("data:audio/wav;base64," + Convert.ToBase64String(Wav));
        JsonNode.Parse(posts[2].Body!)!["speaker_voice_embedding_file_url"]!.GetValue<string>()
            .Should().Be("https://cdn.test/e/voice.safetensors");
        Manifest().Providers[VoiceProviders.FalQwen]!["embeddingUrl"]!.GetValue<string>()
            .Should().Be("https://cdn.test/e/voice.safetensors");
    }

    // ── fal: MiniMax ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FalMiniMaxSpeech_CachedClone_IntoVoiceSetting_AndTouched()
    {
        SetMiniMax("mm-voice-1");
        _time.Advance(TimeSpan.FromDays(3));
        _fal.Job(AudioCatalog.FalMiniMaxHd, "s1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/s.mp3"}}""");
        _fal.File("https://cdn.test/s.mp3", [1]);

        var started = await RunAsync(Service(Fal()), "fal", AudioCatalog.FalMiniMaxHd, AudioOp.Speak);

        started.Error.Should().BeNull();
        JsonNode.Parse(Posts().Single().Body!)!["voice_setting"]!["voice_id"]!.GetValue<string>().Should().Be("mm-voice-1");
        var minimax = _lib.Get(_scope, _slug)!.Providers.Single(p => p.Provider == VoiceProviders.MiniMax);
        minimax.State.Should().Be(VoiceProviderStates.Ok);
        minimax.LastUsedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task FalMiniMaxSpeech_StaleClone_RefusedWithRecreateQuote_ProviderNotCalled()
    {
        SetMiniMax("mm-voice-1");
        _time.Advance(VoiceProviders.MiniMaxTtl + TimeSpan.FromHours(1));

        var started = await RunAsync(Service(Fal()), "fal", AudioCatalog.FalMiniMaxHd, AudioOp.Speak);

        started.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceCloneStale);
        started.Recreate.Should().NotBeNull();
        started.Recreate!.Model.Should().Be(AudioCatalog.FalMiniMaxClone);
        started.Recreate.RecreateVoice.Should().Be(_slug);
        started.Recreate.Price.Amount.Should().BeGreaterThan(0);
        Posts().Should().BeEmpty();
        // Отказ клон не «освежает»: признак протухания остаётся
        _lib.Get(_scope, _slug)!.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public async Task FalMiniMax_NoClone_OrDirectCloneModel_RefusedWithQuote_NothingCreatedSilently()
    {
        var svc = Service(Fal());

        var speak = await RunAsync(svc, "fal", AudioCatalog.FalMiniMaxHd, AudioOp.Speak);
        var clone = await RunAsync(svc, "fal", AudioCatalog.FalMiniMaxClone, AudioOp.CloneVoice);

        speak.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceCloneMissing);
        speak.Recreate.Should().NotBeNull();
        // Клон MiniMax обычной котировкой не создаётся — только кнопкой пересоздания
        clone.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceCloneMissing);
        clone.Error.Should().Be(AudioEditJobService.CloneByButtonText);
        clone.Recreate!.RecreateVoice.Should().Be(_slug);
        Posts().Should().BeEmpty();
    }

    // Модель, создающая клон, обычным запуском не зовётся и при живом клоне: текст ведёт к кнопке и к HD/Turbo
    [Fact]
    public async Task FalMiniMaxClone_WithLiveClone_RefusedWithButtonText()
    {
        SetMiniMax("mm-voice-1");

        var clone = await RunAsync(Service(Fal()), "fal", AudioCatalog.FalMiniMaxClone, AudioOp.CloneVoice);

        clone.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceCloneMissing);
        clone.Error.Should().Be(AudioEditJobService.CloneByButtonText);
        clone.Recreate.Should().NotBeNull();
        Posts().Should().BeEmpty();
    }

    // Образец едет data: URI внутри JSON: больше потолка — отказ с причиной до запроса, а не таймаут
    [Theory]
    [InlineData(AudioCatalog.FalMiniMaxClone)]
    [InlineData(AudioCatalog.FalChatterbox)]
    [InlineData(AudioCatalog.FalQwenClone)]
    public async Task FalSampleClone_SampleOverCap_RejectedBeforeRequest(string model)
    {
        var big = new byte[FalAudioEngine.MaxDataUriSampleBytes + 1];
        Wav.CopyTo(big, 0);
        var voice = new AudioVoiceUse(_slug, new AudioBytes(big, "audio/wav"), null, null, null, new Dictionary<string, string>());

        var result = await Fal().RunAsync(new AudioRequest(AudioOp.CloneVoice, model, _scope, Text: "Привет", Voice: voice),
            new FalAudioEngineTests.Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Charged.Should().BeFalse();
        result.Error.Should().Be(FalAudioEngine.SampleTooLargeText);
        Posts().Should().BeEmpty();
    }

    // С эмбеддингом из кеша образец не едет вовсе — большой образец не повод отказывать
    [Fact]
    public async Task FalQwenClone_SampleOverCap_CachedEmbedding_SkipsCloneAndSpeaks()
    {
        var big = new byte[FalAudioEngine.MaxDataUriSampleBytes + 1];
        Wav.CopyTo(big, 0);
        var voice = new AudioVoiceUse(_slug, new AudioBytes(big, "audio/wav"), null, null, null,
            new Dictionary<string, string> { [VoiceProviders.FalQwen] = "https://cdn.test/e/voice.safetensors" });
        _fal.Job(AudioCatalog.FalQwenTts, "t1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out1.mp3"}}""");
        _fal.File("https://cdn.test/e/out1.mp3", [9]);

        var result = await Fal().RunAsync(new AudioRequest(AudioOp.CloneVoice, AudioCatalog.FalQwenClone, _scope, Text: "Привет", Voice: voice),
            new FalAudioEngineTests.Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        Posts().Select(p => p.Url).Should().Equal($"{Queue}/{AudioCatalog.FalQwenTts}");
    }

    [Fact]
    public async Task FalSampleClone_SampleAtCap_GoesToProvider()
    {
        var atCap = new byte[FalAudioEngine.MaxDataUriSampleBytes];
        Wav.CopyTo(atCap, 0);
        _fal.Job(AudioCatalog.FalChatterbox, "ch1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/ch.mp3"}}""");
        _fal.File("https://cdn.test/ch.mp3", [5]);

        var result = await Fal().RunAsync(new AudioRequest(AudioOp.CloneVoice, AudioCatalog.FalChatterbox, _scope, Text: "Привет",
            Reference: new AudioBytes(atCap, "audio/wav")), new FalAudioEngineTests.Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        Posts().Should().ContainSingle();
    }

    [Fact]
    public async Task Recreate_QuoteThenJob_CreatesCloneAndUpdatesCache()
    {
        SetMiniMax("mm-old");
        _time.Advance(TimeSpan.FromDays(10));
        _fal.Job(AudioCatalog.FalMiniMaxClone, "m1", ["COMPLETED"],
            """{"custom_voice_id":"mm-new","audio":{"url":"https://cdn.test/preview.mp3"}}""");
        _fal.File("https://cdn.test/preview.mp3", [4]);
        var svc = Service(Fal());

        var quote = await svc.QuoteVoiceCloneAsync(Owner, _scope, _slug, "minimax", CancellationToken.None);
        quote.Error.Should().BeNull();
        Posts().Should().BeEmpty("котировка ничего не запускает");
        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId), CancellationToken.None);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);

        var body = JsonNode.Parse(Posts().Single().Body!)!;
        body["audio_url"]!.GetValue<string>().Should().StartWith("data:audio/wav;base64,");
        body.AsObject().ContainsKey("voice_setting").Should().BeFalse("у эндпоинта клона нет настроек голоса");
        Manifest().Providers[VoiceProviders.MiniMax]!["customVoiceId"]!.GetValue<string>().Should().Be("mm-new");
        _lib.Get(_scope, _slug)!.Providers.Single(p => p.Provider == VoiceProviders.MiniMax).State
            .Should().Be(VoiceProviderStates.Ok);
        svc.Get(Owner, _scope.Key, started.Value.JobId)!.Status.Should().Be(AudioEditJobStatus.Completed);
    }

    // Пересоздание не отдаёт поставщику прежний id: клон создаётся заново, а не «освежается»
    [Fact]
    public async Task Recreate_OldCloneIdNotPassedToProvider()
    {
        SetMiniMax("mm-old");
        var engine = new CloneFake();
        var svc = Service(engine);

        var quote = await svc.QuoteVoiceCloneAsync(Owner, _scope, _slug, "minimax", CancellationToken.None);
        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId), CancellationToken.None);
        await svc.WhenDone(started.Value!.JobId);

        engine.Seen.Should().ContainSingle();
        engine.Seen[0]!.CachedId(VoiceProviders.MiniMax).Should().BeNull();
        Manifest().Providers[VoiceProviders.MiniMax]!["customVoiceId"]!.GetValue<string>().Should().Be("mm-fresh");
    }

    // Несколько вариантов одной задачи: эмбеддинг Qwen создаётся первым прогоном и берётся вторым
    [Fact]
    public async Task FalQwenClone_TwoVariants_CloneRunOnce()
    {
        _fal.Job(AudioCatalog.FalQwenClone, "c1", ["COMPLETED"], """{"speaker_embedding":{"url":"https://cdn.test/e/voice.safetensors"}}""");
        _fal.Job(AudioCatalog.FalQwenTts, "t1", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out1.mp3"}}""");
        _fal.Job(AudioCatalog.FalQwenTts, "t2", ["COMPLETED"], """{"audio":{"url":"https://cdn.test/e/out2.mp3"}}""");
        _fal.File("https://cdn.test/e/out1.mp3", [9]);
        _fal.File("https://cdn.test/e/out2.mp3", [8]);
        var svc = Service(Fal());
        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "cloneVoice", "fal",
            AudioCatalog.FalQwenClone, Count: 2, Text: "Привет"), CancellationToken.None);

        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId, Text: "Привет", Voice: Voice),
            CancellationToken.None);
        await svc.WhenDone(started.Value!.JobId);

        svc.Get(Owner, _scope.Key, started.Value.JobId)!.Variants.Should().HaveCount(2);
        Posts().Count(p => p.Url == $"{Queue}/{AudioCatalog.FalQwenClone}").Should().Be(1);
    }

    // Id клона в кеше уходит только в озвучку MiniMax: тело самого клона его не получает, а новый id — в кеш
    [Fact]
    public async Task FalMiniMaxClone_EngineIgnoresCachedId_ReturnsNewIdForCache()
    {
        _fal.Job(AudioCatalog.FalMiniMaxClone, "m2", ["COMPLETED"],
            """{"custom_voice_id":"mm-new","audio":{"url":"https://cdn.test/p2.mp3"}}""");
        _fal.File("https://cdn.test/p2.mp3", [4]);
        var voice = new AudioVoiceUse(_slug, new AudioBytes(Wav, "audio/wav"), null, null, null,
            new Dictionary<string, string> { [VoiceProviders.MiniMax] = "mm-old" });

        var result = await Fal().RunAsync(new AudioRequest(AudioOp.CloneVoice, AudioCatalog.FalMiniMaxClone, _scope,
            Text: "Привет", Voice: voice), new FalAudioEngineTests.Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        Posts().Single().Body.Should().NotContain("mm-old");
        result.VoiceCache.Should().Equal(new AudioVoiceCacheEntry(VoiceProviders.MiniMax, "mm-new"));
    }

    // Поставщик-подставка: модель, которая сама создаёт клон MiniMax; запоминает голос запуска
    private sealed class CloneFake : IAudioEngine
    {
        public List<AudioVoiceUse?> Seen { get; } = [];
        public string Key => "fake";
        public string Label => "fake";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new("fake-mm-clone", "Клон", new AudioCaps([AudioOp.CloneVoice], ["ru"], [AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Commercial, AudioPriceUnits.Free)),
        ];

        public string? LibraryVoicesRefusal => null;
        public string? LibraryVoiceRefusal(AudioModelInfo model, AudioOp op, AudioVoiceUse voice) => null;
        public (string Key, bool Creates)? StoredClone(AudioModelInfo model, AudioOp op) => (VoiceProviders.MiniMax, true);

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            Seen.Add(req.Voice);
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/wav", ".wav")], null, false,
                null, null, [new AudioVoiceCacheEntry(VoiceProviders.MiniMax, "mm-fresh")]));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task Recreate_OnlyMiniMax_AndQuoteBoundToItsVoice()
    {
        var svc = Service(Fal());
        (await svc.QuoteVoiceCloneAsync(Owner, _scope, _slug, "higgsfield", CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);

        var quote = await svc.QuoteVoiceCloneAsync(Owner, _scope, _slug, "minimax", CancellationToken.None);
        var other = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId, Voice: "voice:someone-else"),
            CancellationToken.None);

        other.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        Posts().Should().BeEmpty();
    }

    // ── Higgsfield ───────────────────────────────────────────────────────────────

    private static JsonObject? Launch(FakeHttp.Call c) =>
        FakeHttp.Tool(c) == "generate_audio" && FakeHttp.Arguments(c)?["params"] is JsonObject p && p["get_cost"] is null ? p : null;

    [Fact]
    public async Task Higgsfield_SampleUploadedOnce_MediaCached_ReusedAsAudioReference()
    {
        var http = new FakeHttp(HiggsfieldAudioEngineTests.Happy);
        var svc = Service(new HiggsfieldAudioEngine(FakeHttp.Client(http), _time) { PollInterval = TimeSpan.Zero });

        (await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak)).Error.Should().BeNull();
        (await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak)).Error.Should().BeNull();

        http.Calls.Count(c => FakeHttp.Tool(c) == "media_upload").Should().Be(1);
        var launches = http.Calls.Select(Launch).OfType<JsonObject>().ToList();
        launches.Should().HaveCount(2).And.OnlyContain(a =>
            a["medias"]![0]!["role"]!.GetValue<string>() == "audio_references"
            && a["medias"]![0]!["value"]!.GetValue<string>() == HiggsfieldAudioEngineTests.MediaId);
        Manifest().Providers[VoiceProviders.HiggsfieldMedia]!["mediaId"]!.GetValue<string>()
            .Should().Be(HiggsfieldAudioEngineTests.MediaId);
        _lib.Get(_scope, _slug)!.Providers.Single(p => p.Provider == VoiceProviders.Higgsfield).State
            .Should().Be(VoiceProviderStates.Ok);
    }

    private const string StaleMedia = "stale-media-id";

    private static bool Refers(FakeHttp.Call c, string mediaId) =>
        Launch(c)?["medias"]?[0]?["value"]?.GetValue<string>() == mediaId;

    // Протухший mediaId из кеша: запуск не принят → кеш вычищен, образец загружен заново, повтор один раз
    [Fact]
    public async Task Higgsfield_StaleCachedMedia_ReuploadedAndRetriedOnce()
    {
        _lib.UpdateProviders(_scope, _slug, (p, now) => VoiceProviders.SetHiggsfieldMedia(p, StaleMedia, now));
        var http = new FakeHttp(c => Refers(c, StaleMedia)
            ? FakeHttp.McpText("Media not found: " + StaleMedia, isError: true)
            : HiggsfieldAudioEngineTests.Happy(c));
        var svc = Service(new HiggsfieldAudioEngine(FakeHttp.Client(http), _time) { PollInterval = TimeSpan.Zero });

        var started = await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak);

        started.Error.Should().BeNull();
        var job = svc.Get(Owner, _scope.Key, started.Value!.JobId)!;
        job.Status.Should().Be(AudioEditJobStatus.Completed);
        http.Calls.Count(c => FakeHttp.Tool(c) == "media_upload").Should().Be(1);
        http.Calls.Select(Launch).OfType<JsonObject>().Select(a => a["medias"]![0]!["value"]!.GetValue<string>())
            .Should().Equal(StaleMedia, HiggsfieldAudioEngineTests.MediaId);
        Manifest().Providers[VoiceProviders.HiggsfieldMedia]!["mediaId"]!.GetValue<string>()
            .Should().Be(HiggsfieldAudioEngineTests.MediaId);
    }

    // Заново загруженный образец тоже не найден: честная ошибка, не списано, кеш пуст, третьей попытки нет
    [Fact]
    public async Task Higgsfield_StaleMedia_ReuploadNotHelping_HonestErrorNotCharged()
    {
        _lib.UpdateProviders(_scope, _slug, (p, now) => VoiceProviders.SetHiggsfieldMedia(p, StaleMedia, now));
        var http = new FakeHttp(c => Launch(c) is not null
            ? FakeHttp.McpText("HTTP 404: media does not exist", isError: true)
            : HiggsfieldAudioEngineTests.Happy(c));
        var svc = Service(new HiggsfieldAudioEngine(FakeHttp.Client(http), _time) { PollInterval = TimeSpan.Zero });

        var started = await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak);

        var job = svc.Get(Owner, _scope.Key, started.Value!.JobId)!;
        job.Status.Should().Be(AudioEditJobStatus.Failed);
        job.Error.Should().Be(HiggsfieldAudioEngine.MediaLostText);
        job.Charged.Should().BeFalse();
        http.Calls.Count(c => FakeHttp.Tool(c) == "media_upload").Should().Be(1);
        http.Calls.Select(Launch).OfType<JsonObject>().Should().HaveCount(2);
        Manifest().Providers.ContainsKey(VoiceProviders.HiggsfieldMedia).Should().BeFalse();
    }

    // Отказ, не связанный с образцом, не повторяется и кеш не трогает
    [Fact]
    public async Task Higgsfield_CachedMedia_OtherRefusal_NoRetry()
    {
        _lib.UpdateProviders(_scope, _slug, (p, now) => VoiceProviders.SetHiggsfieldMedia(p, StaleMedia, now));
        var http = new FakeHttp(c => Launch(c) is not null
            ? FakeHttp.McpText("Rate limit exceeded", isError: true)
            : HiggsfieldAudioEngineTests.Happy(c));
        var svc = Service(new HiggsfieldAudioEngine(FakeHttp.Client(http), _time) { PollInterval = TimeSpan.Zero });

        await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak);

        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "media_upload");
        http.Calls.Select(Launch).OfType<JsonObject>().Should().ContainSingle();
        Manifest().Providers[VoiceProviders.HiggsfieldMedia]!["mediaId"]!.GetValue<string>().Should().Be(StaleMedia);
    }

    [Fact]
    public async Task Higgsfield_CachedElement_VoiceTypeElement_NoUpload()
    {
        _lib.UpdateProviders(_scope, _slug, (p, now) => VoiceProviders.SetHiggsfield(p, "el-1", now));
        var http = new FakeHttp(HiggsfieldAudioEngineTests.Happy);
        var svc = Service(new HiggsfieldAudioEngine(FakeHttp.Client(http), _time) { PollInterval = TimeSpan.Zero });

        (await RunAsync(svc, "higgsfield", "seed_audio", AudioOp.Speak)).Error.Should().BeNull();

        var launch = http.Calls.Select(Launch).OfType<JsonObject>().Single();
        launch["voice_type"]!.GetValue<string>().Should().Be("element");
        launch["voice_id"]!.GetValue<string>().Should().Be("el-1");
        launch.ContainsKey("medias").Should().BeFalse();
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "media_upload");
    }

    // ── local ────────────────────────────────────────────────────────────────────

    private (LocalAudioEngine Engine, List<LocalAudioRequest> Submitted) Local()
    {
        var submitted = new List<LocalAudioRequest>();
        var media = new Mock<ILocalAudioMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.SetupGet(m => m.Configured).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalAudioRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LocalAudioRequest, CancellationToken>((r, _) => submitted.Add(r))
            .ReturnsAsync(new LocalAudioSubmitted("t-1", 0, 5, null));
        media.Setup(m => m.PollAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalAudioPoll(LocalAudioState.Completed, null, [new LocalAudioFile("main", [1], "audio/wav", ".wav")], null));
        return (new LocalAudioEngine(media.Object) { PollInterval = TimeSpan.Zero }, submitted);
    }

    [Fact]
    public async Task Local_Qwen_SampleAsReference_TranscriptAsReferenceText()
    {
        var (engine, submitted) = Local();

        (await RunAsync(Service(engine), "local", AudioCatalog.QwenTts, AudioOp.CloneVoice)).Error.Should().BeNull();

        var run = submitted.Should().ContainSingle().Subject;
        run.Reference.Should().Equal(Wav);
        run.Args!["reference_text"]!.GetValue<string>().Should().Be(Transcript);
    }

    [Fact]
    public async Task Local_RvcVoice_ModelAndIndexIntoConvert_RefusedElsewhere()
    {
        var pth = Path.Combine(_root, "voice.pth");
        var index = Path.Combine(_root, "voice.index");
        File.WriteAllBytes(pth, [1, 1]);
        File.WriteAllBytes(index, [2, 2]);
        var rvc = _lib.CreateFromRvc(_scope, "Модель", pth, index).Value!.Manifest.Slug;
        var (engine, submitted) = Local();
        var svc = Service(engine);
        var input = new AudioJobInput("", Voice: AudioVoiceRefs.Prefix + rvc, Source: new AudioBytes(Wav, "audio/wav"));

        (await RunAsync(svc, "local", AudioCatalog.Rvc, AudioOp.ConvertVoice, input)).Error.Should().BeNull();
        var wrong = await RunAsync(svc, "local", AudioCatalog.QwenTts, AudioOp.Speak, input with { Source = null });

        var run = submitted.Should().ContainSingle().Subject;
        run.VoiceModel.Should().Equal(1, 1);
        run.VoiceIndex.Should().Equal(2, 2);
        run.Args!["engine"]!.GetValue<string>().Should().Be("rvc");
        wrong.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceUnavailable);
    }

    // ── Яндекс и общее ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Yandex_LibraryVoiceUnavailableWithReason_GreyInCatalog()
    {
        var tts = new FakeTts();
        var yandex = new YandexAudioEngine(tts);

        var started = await RunAsync(Service(yandex), "yandex", AudioCatalog.AutoModelId, AudioOp.Speak);

        started.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceUnavailable);
        started.Error.Should().Be(YandexAudioEngine.LibraryVoicesReason);
        tts.Calls.Should().BeEmpty();
        var catalog = AudioCatalogView.Build([yandex, Fal()], _scope);
        catalog.Providers.Single(p => p.Key == "yandex").LibraryVoicesReason.Should().Be(YandexAudioEngine.LibraryVoicesReason);
        catalog.Providers.Single(p => p.Key == "fal").LibraryVoicesReason.Should().BeNull();
    }

    [Fact]
    public async Task UnknownVoice_OrNotLibraryValue_RefusedBeforeProvider()
    {
        var (engine, submitted) = Local();
        var svc = Service(engine);

        var missing = await RunAsync(svc, "local", AudioCatalog.QwenTts, AudioOp.Speak, new AudioJobInput("", Voice: "voice:nobody"));
        var preset = await RunAsync(svc, "local", AudioCatalog.QwenTts, AudioOp.Speak, new AudioJobInput("", Voice: "Vivian"));

        missing.ErrorCode.Should().Be(AudioEditErrorCodes.VoiceNotFound);
        preset.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        submitted.Should().BeEmpty();
    }

    // Id поставщиков наружу не уходят: ни в DTO голоса, ни в котировке пересоздания
    [Fact]
    public async Task ProviderIds_NeverLeakToDtos()
    {
        SetMiniMax("mm-secret-id");
        _lib.UpdateProviders(_scope, _slug, (p, now) =>
        {
            VoiceProviders.SetFalQwen(p, "https://cdn.test/secret.safetensors", now);
            VoiceProviders.SetHiggsfield(p, "hf-secret-element", now);
            VoiceProviders.SetHiggsfieldMedia(p, "hf-secret-media", now);
        });
        var quote = await Service(Fal()).QuoteVoiceCloneAsync(Owner, _scope, _slug, "minimax", CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(new object[] { _lib.Get(_scope, _slug)!, quote.Value! });

        json.Should().NotContain("mm-secret-id").And.NotContain("secret.safetensors")
            .And.NotContain("hf-secret-element").And.NotContain("hf-secret-media");
        // Список голосов идёт тем же DTO — и в нём id нет
        System.Text.Json.JsonSerializer.Serialize(_lib.List(_scope)).Should().NotContain("hf-secret");
    }
}
