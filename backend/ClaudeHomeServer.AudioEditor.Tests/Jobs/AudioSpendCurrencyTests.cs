using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Трата звука в общем учёте: кредиты Higgsfield, рубли Яндекса и доллары остальных — каждая в своём
// поле, не складываются; источник Яндекса — tts. Серые модели не котируются, живой каталог
// подтягивается до подбора модели
public sealed class AudioSpendCurrencyTests : IDisposable
{
    private const string Owner = "owner-1";
    private static readonly AudioEditScope Scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = "/srv/p" });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-spend-" + Guid.NewGuid().ToString("N"));
    private readonly List<SpendRecord> _spend = [];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service(params IAudioEngine[] engines)
    {
        var spend = new Mock<ISpendCollector>();
        spend.Setup(s => s.Record(It.IsAny<SpendRecord>())).Callback<SpendRecord>(r => { lock (_spend) _spend.Add(r); });
        return new AudioEditJobService(engines, new AudioEditWorkspace(Path.Combine(_root, "work")),
            NullLogger<AudioEditJobService>.Instance, spend: spend.Object);
    }

    private static async Task RunAsync(AudioEditJobService svc, string provider, string? model = null, JsonObject? fields = null)
    {
        var quote = await svc.QuoteAsync(Owner, Scope,
            new AudioQuoteRequest(AudioModes.Voice, "speak", provider, model, Text: "Привет", Fields: fields), CancellationToken.None);
        quote.Error.Should().BeNull();
        var started = await svc.StartAsync(Owner, Scope, new AudioJobInput(quote.Value!.QuoteId, Text: "Привет"), CancellationToken.None);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);
    }

    [Fact]
    public async Task Spend_CreditsRublesDollars_NeverMixed()
    {
        var svc = Service(
            new PricedEngine("higgsfield", AudioPriceUnits.Credits, 0.2),
            new PricedEngine("yandex", AudioPriceUnits.Rub, 0.33),
            new PricedEngine("fal", AudioPriceUnits.Chars, 0.0015));

        await RunAsync(svc, "higgsfield");
        await RunAsync(svc, "yandex");
        await RunAsync(svc, "fal");

        List<SpendRecord> records;
        lock (_spend) records = [.. _spend];
        var credits = records.Single(r => r.Provider == "higgsfield");
        credits.CostCredits.Should().Be(0.2);
        credits.CostRub.Should().BeNull();
        credits.CostUsd.Should().BeNull();

        var rub = records.Single(r => r.Provider == "yandex");
        rub.CostRub.Should().Be(0.33);
        rub.CostCredits.Should().BeNull();
        rub.CostUsd.Should().BeNull();

        var usd = records.Single(r => r.Provider == "fal");
        usd.CostUsd.Should().Be(0.0015 * "Привет".Length);
        usd.CostCredits.Should().BeNull();
        usd.CostRub.Should().BeNull();
    }

    [Fact]
    public async Task Spend_Yandex_TtsSourceInRubles()
    {
        var svc = Service(new YandexAudioEngine(new FakeTts()));

        await RunAsync(svc, YandexAudioEngine.ProviderKey, fields: new JsonObject { ["voice"] = "jane" });

        List<SpendRecord> records;
        lock (_spend) records = [.. _spend];
        records.Should().NotBeEmpty().And.OnlyContain(r => r.Source == SpendSources.Tts && r.Provider == "yandex"
                                                           && r.CostUsd == null && r.CostCredits == null);
        records.Sum(r => r.CostRub ?? 0).Should().BeApproximately(0.3252, 1e-9);
        records.Sum(r => r.Generations).Should().Be(1);
    }

    [Fact]
    public async Task Quote_GreyModel_NotQuotedExplicitlyNorPickedByAuto()
    {
        var engine = new PricedEngine("higgsfield", AudioPriceUnits.Credits, 0.2) { GreyFirst = true };
        var svc = Service(engine);

        var explicitQuote = await svc.QuoteAsync(Owner, Scope,
            new AudioQuoteRequest(AudioModes.Voice, "speak", "higgsfield", PricedEngine.GreyModel), CancellationToken.None);
        explicitQuote.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);

        var auto = await svc.QuoteAsync(Owner, Scope, new AudioQuoteRequest(AudioModes.Voice, "speak"), CancellationToken.None);
        auto.Value!.Model.Should().Be(PricedEngine.SpeakModel);
    }

    [Fact]
    public async Task Quote_RefreshesLiveCatalogBeforePicking()
    {
        var engine = new PricedEngine("higgsfield", AudioPriceUnits.Credits, 0.2) { Lazy = true };
        var svc = Service(engine);

        var quote = await svc.QuoteAsync(Owner, Scope, new AudioQuoteRequest(AudioModes.Voice, "speak", "higgsfield"),
            CancellationToken.None);

        quote.Error.Should().BeNull();
        quote.Value!.Model.Should().Be(PricedEngine.SpeakModel);
    }

    // Поставщик с ценой в своей единице: ориентир каталога и та же сумма фактом
    private sealed class PricedEngine(string key, string unit, double price) : IAudioEngine
    {
        public const string SpeakModel = "priced-speech";
        public const string GreyModel = "priced-grey";

        private bool _loaded;

        public bool GreyFirst { get; init; }
        public bool Lazy { get; init; }

        public string Key => key;
        public string Label => key;
        public string PriceUnit => unit;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models => Lazy && !_loaded ? [] : GreyFirst ? [Grey, Speak] : [Speak];

        private AudioModelInfo Speak => new(SpeakModel, "Речь",
            new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio], AudioLicenses.Apache2, unit),
            new AudioPriceHint(price, unit, "unit"));

        private AudioModelInfo Grey => Speak with { Id = GreyModel, DisabledReason = "серая" };

        public ValueTask RefreshModelsAsync(CancellationToken ct)
        {
            _loaded = true;
            return ValueTask.CompletedTask;
        }

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            progress.Report(new AudioProgress(AudioStage.Running));
            var units = unit == AudioPriceUnits.Chars ? req.Text!.Length : 1;
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/mpeg", ".mp3")],
                new AudioCost(price * units, unit), true, null, null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
