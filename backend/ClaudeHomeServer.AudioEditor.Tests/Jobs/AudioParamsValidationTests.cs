using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Engines;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// params проверяются по схеме модели и в котировке, и в запуске — до денег и до очереди (ADR-021 §5)
public sealed class AudioParamsValidationTests : IDisposable
{
    private const string Owner = "owner-1";
    private static readonly AudioEditScope Scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = "/srv/p" });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-params-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service(IAudioEngine engine) =>
        new([engine], new AudioEditWorkspace(_root), NullLogger<AudioEditJobService>.Instance);

    private static AudioQuoteRequest Speak(JsonObject? fields = null) =>
        new(AudioModes.Voice, "speak", "fal", SchemaEngine.SpeakModel, Fields: fields);

    [Fact]
    public async Task Quote_UnknownKey_RefusedWithName_BeforePrice()
    {
        var engine = new SchemaEngine();
        var quote = await Service(engine).QuoteAsync(Owner, Scope, Speak(new JsonObject { ["temperature"] = 1 }),
            CancellationToken.None);

        quote.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        quote.Error.Should().Be($"Неизвестный параметр «temperature» у модели {SchemaEngine.SpeakModel}");
    }

    // Поставщики без схемы (Higgsfield, Яндекс) сверяют имена по ParamNames: неизвестный ключ — отказ с
    // именем поля до цены, а не молчаливая потеря в драйвере; известный — проходит
    [Fact]
    public async Task Quote_Yandex_UnknownKey_RefusedWithName_KnownPasses()
    {
        var svc = Service(new YandexAudioEngine(new FakeTts()));
        AudioQuoteRequest Request(JsonObject fields) =>
            new(AudioModes.Voice, "speak", YandexAudioEngine.ProviderKey, YandexAudioEngine.ModelId, Text: "Привет", Fields: fields);

        var bad = await svc.QuoteAsync(Owner, Scope, Request(new JsonObject { ["pitch"] = 2 }), CancellationToken.None);
        var ok = await svc.QuoteAsync(Owner, Scope, Request(new JsonObject { ["speed"] = 1.2 }), CancellationToken.None);

        bad.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        bad.Error.Should().StartWith($"Неизвестный параметр «pitch» у модели {YandexAudioEngine.ModelId}");
        ok.Error.Should().BeNull();
    }

    [Fact]
    public async Task Quote_Higgsfield_UnknownKey_RefusedWithName_BeforeCost_KnownPasses()
    {
        var http = new FakeHttp(HiggsfieldAudioEngineTests.Happy);
        var engine = new HiggsfieldAudioEngine(FakeHttp.Client(http)) { PollInterval = TimeSpan.Zero };
        await engine.RefreshModelsAsync(CancellationToken.None);
        var svc = Service(engine);
        AudioQuoteRequest Request(JsonObject fields) =>
            new(AudioModes.Voice, "speak", HiggsfieldAudioEngine.ProviderKey, "seed_audio", Text: "Привет", Fields: fields);

        var bad = await svc.QuoteAsync(Owner, Scope, Request(new JsonObject { ["temperature"] = 1 }), CancellationToken.None);
        var costCalls = http.Calls.Count(c => FakeHttp.Tool(c) == "generate_audio");
        var ok = await svc.QuoteAsync(Owner, Scope, Request(new JsonObject { ["speech_rate"] = 10 }), CancellationToken.None);

        bad.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        bad.Error.Should().StartWith("Неизвестный параметр «temperature» у модели seed_audio");
        costCalls.Should().Be(0, "отказ — до запроса цены");
        ok.Error.Should().BeNull();
    }

    [Fact]
    public async Task Quote_CommonFieldInParams_Refused()
    {
        var quote = await Service(new SchemaEngine()).QuoteAsync(Owner, Scope, Speak(new JsonObject { ["text"] = "привет" }),
            CancellationToken.None);

        quote.Error.Should().Be("Параметр «text» задаётся общим полем запроса, а не в params");
    }

    [Fact]
    public async Task Params_OutOfRange_RefusedAtQuote_LaunchCannotOverrideQuoted()
    {
        var engine = new SchemaEngine();
        var svc = Service(engine);
        var bad = await svc.QuoteAsync(Owner, Scope, Speak(new JsonObject { ["speed"] = 3 }), CancellationToken.None);
        var quote = await svc.QuoteAsync(Owner, Scope, Speak(new JsonObject { ["speed"] = 1.5 }), CancellationToken.None);

        // Цена считана с 1.5: запуск поверх неё — отказ сверки, с теми же params — запуск
        var refused = await svc.StartAsync(Owner, Scope,
            new AudioJobInput(quote.Value!.QuoteId, Params: new JsonObject { ["speed"] = 1.0 }), CancellationToken.None);
        var ok = await svc.StartAsync(Owner, Scope,
            new AudioJobInput(quote.Value!.QuoteId, Params: new JsonObject { ["speed"] = 1.5 }), CancellationToken.None);

        bad.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        bad.Error.Should().Be("Параметр «speed» — от 0.5 до 2");
        refused.Error.Should().Be(AudioEditJobService.QuoteMismatchText);
        ok.Error.Should().BeNull();
        await engine.Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.LastParams!["speed"]!.GetValue<double>().Should().Be(1.5);
    }

    // Входы операции из настроек нити (язык, голос, кусок) в params модели не попадают: со схемой из
    // одного speed они дали бы «неизвестный параметр». Запуск пишет настройки нити, но входы не затирает
    [Fact]
    public async Task ThreadInputs_NotInParams_AndSurviveLaunch()
    {
        const string session = "chat-1";
        var store = new AudioThreadStore(Path.Combine(_root, "threads"));
        var threads = new AudioJobThreads(store, NullLogger<AudioJobThreads>.Instance);
        var threadId = store.Open(Owner, session, null, "", null).Thread!.Id;
        var inputs = new JsonObject { ["language"] = "ru", ["voice"] = "voice:anya", ["startSec"] = 1.5 };
        store.SetSettings(Owner, session, threadId,
            new AudioThreadSettings(AudioModes.Voice, "speak", "fal", SchemaEngine.SpeakModel, new JsonObject { ["speed"] = 1.2 })
            {
                Inputs = inputs,
            }, null).Status.Should().Be(AudioThreadWriteStatus.Ok);
        var engine = new SchemaEngine();
        var svc = new AudioEditJobService([engine], new AudioEditWorkspace(Path.Combine(_root, "work")),
            NullLogger<AudioEditJobService>.Instance, threads);

        var quote = await svc.QuoteAsync(Owner, Scope,
            new AudioQuoteRequest(AudioModes.Voice, SessionId: session, ThreadId: threadId, Text: "привет"), CancellationToken.None);
        quote.Error.Should().BeNull();
        var started = await svc.StartAsync(Owner, Scope,
            new AudioJobInput(quote.Value!.QuoteId, SessionId: session, ThreadId: threadId, Text: "привет"), CancellationToken.None);
        started.Error.Should().BeNull();
        await engine.Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));

        engine.LastParams!.Select(p => p.Key).Should().Equal("speed");
        var settings = store.Get(Owner, session).Threads.Single(t => t.Id == threadId).Settings!;
        settings.Inputs!.ToJsonString().Should().Be(inputs.ToJsonString());
    }

    [Fact]
    public async Task SchemaDown_EmptyParamsPass_NonEmptyRefused()
    {
        var engine = new SchemaEngine { Down = true };
        var svc = Service(engine);

        (await svc.QuoteAsync(Owner, Scope, Speak(), CancellationToken.None)).Error.Should().BeNull();
        var withParams = await svc.QuoteAsync(Owner, Scope, Speak(new JsonObject { ["speed"] = 1 }), CancellationToken.None);

        withParams.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        withParams.Error.Should().Be("схема лежит — params проверить нечем");
    }

    [Fact]
    public async Task SchemaForUi_OnlyCatalogModels()
    {
        var svc = Service(new SchemaEngine());

        var ok = await svc.SchemaAsync("fal", SchemaEngine.SpeakModel, "speak", CancellationToken.None);
        var foreign = await svc.SchemaAsync("fal", "fal-ai/not-in-catalog", "speak", CancellationToken.None);
        var auto = await svc.SchemaAsync("fal", "auto", "speak", CancellationToken.None);
        var noProvider = await svc.SchemaAsync("yandex", SchemaEngine.SpeakModel, "speak", CancellationToken.None);

        ok.Value!.Fields.Should().ContainSingle(f => f.Key == "speed");
        foreign.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        auto.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        noProvider.ErrorCode.Should().Be(AudioEditErrorCodes.ProviderUnavailable);
    }

    private sealed class SchemaEngine : IAudioEngine, IAudioParamSchemas
    {
        public const string SpeakModel = "schema-speech";

        public bool Down { get; init; }
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonObject? LastParams { get; private set; }

        public string Key => "fal";
        public string Label => "fal";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new(SpeakModel, "Речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
                AudioLicenses.Apache2, AudioPriceUnits.Free), new AudioPriceHint(0, AudioPriceUnits.Free, "run")),
        ];

        public Task<AudioSchemaLookup> SchemaAsync(AudioModelInfo model, AudioOp op, CancellationToken ct) =>
            Task.FromResult(Down
                ? AudioSchemaLookup.Fail("схема лежит")
                : AudioSchemaLookup.Ok(new AudioParamSchema(Key, model.Id, AudioSchemaSources.FalOpenApi,
                    [new AudioParamField("speed", AudioParamTypes.Number, Min: 0.5, Max: 2)], ["text"])));

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            LastParams = req.Params;
            Ran.TrySetResult();
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/wav", ".wav")],
                null, false, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
