using System.Net;
using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Engines;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Запуск сверяется с котировкой: текст, подводка, слова, длительность и итог params — то, от чего зависит
// цена. Котируют «а», запускают 5000 символов — отказ до поставщика, котировка не сгорает. Правило одно
// для всех поставщиков, включая бесплатный local
public sealed class AudioQuoteMatchTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Quoted = "а";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-quote-match-" + Guid.NewGuid().ToString("N"));
    private readonly AudioEditScope _scope;
    private readonly FalAudioEngineTests.FakeFal _fal = new();
    private readonly FakeTts _tts = new();
    private readonly Mock<ILocalAudioMedia> _local = new();

    public AudioQuoteMatchTests()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        _scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = project, OwnerId = Owner });
        _fal.Price(AudioCatalog.FalMiniMaxHd, 0.1, "1000 characters");
        _local.SetupGet(m => m.Available).Returns(true);
        _local.SetupGet(m => m.Configured).Returns(true);
        _local.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _local.Setup(m => m.SubmitAsync(It.IsAny<LocalAudioRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalAudioSubmitted(null, null, null, "нет"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service() =>
        new([Fal(), new YandexAudioEngine(_tts), new LocalAudioEngine(_local.Object)],
            new AudioEditWorkspace(Path.Combine(_root, "work")), NullLogger<AudioEditJobService>.Instance);

    private FalAudioEngine Fal() => new(new FalAudioEngineTests.Factory(_fal), new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fal:ApiKey"] = "fal-key",
            ["Fal:QueueBase"] = FalAudioEngineTests.Queue,
            ["Fal:ApiBase"] = FalAudioEngineTests.Api,
        }).Build(), NullLogger<FalAudioEngine>.Instance) { PollInterval = TimeSpan.Zero };

    public static TheoryData<string> Providers => new() { "fal", YandexAudioEngine.ProviderKey, "local" };

    public static TheoryData<string, string> Mismatches()
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in new[] { "fal", YandexAudioEngine.ProviderKey, "local" })
            foreach (var change in new[] { "text", "prompt", "lyrics", "duration", "params" })
                data.Add(provider, change);
        return data;
    }

    private static (string Model, JsonObject Fields) ModelOf(string provider) => provider switch
    {
        "fal" => (AudioCatalog.FalMiniMaxHd, new JsonObject()),
        "local" => (AudioCatalog.QwenTts, new JsonObject()),
        _ => (YandexAudioEngine.ModelId, new JsonObject { ["voice"] = "jane" }),
    };

    private async Task<AudioQuoteDto> QuoteAsync(AudioEditJobService svc, string provider)
    {
        var (model, fields) = ModelOf(provider);
        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "speak", provider, model,
            Text: Quoted, DurationSec: 10, Fields: fields), CancellationToken.None);
        quote.Error.Should().BeNull();
        return quote.Value!;
    }

    private static AudioJobInput Launch(string quoteId, string change) => new(quoteId,
        Text: change == "text" ? new string('а', 5000) : Quoted,
        Prompt: change == "prompt" ? "весело" : null,
        Lyrics: change == "lyrics" ? "[Verse]\nслова" : null,
        DurationSec: change == "duration" ? 600 : 10,
        Params: change == "params" ? new JsonObject { ["speed"] = 2.0 } : null);

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task Start_DiffersFromQuote_RefusedBeforeProvider_QuoteKept(string provider, string change)
    {
        var svc = Service();
        var quote = await QuoteAsync(svc, provider);

        var started = await svc.StartAsync(Owner, _scope, Launch(quote.QuoteId, change), CancellationToken.None);

        started.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        started.Error.Should().Be(AudioEditJobService.QuoteMismatchText);
        _fal.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        _tts.Calls.Should().BeEmpty();
        _local.Verify(m => m.SubmitAsync(It.IsAny<LocalAudioRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        svc.FindQuote(Owner, _scope.Key, quote.QuoteId).Should().NotBeNull("отказ сверки котировку не тратит");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Start_SameAsQuote_Accepted_EmptyStringEqualsMissing(string provider)
    {
        var svc = Service();
        var quote = await QuoteAsync(svc, provider);

        // Форма запуска пустых полей не шлёт: "" и null — одно и то же
        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.QuoteId, Text: Quoted, Prompt: "",
            Lyrics: "  ", DurationSec: 10, Params: ModelOf(provider).Fields), CancellationToken.None);

        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);
    }

    [Fact]
    public async Task Quote_Fal_PricesLyricsAndPrompt_WhenNoText()
    {
        var svc = Service();

        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "speak", "fal",
            AudioCatalog.FalMiniMaxHd, Prompt: new string('п', 2000)), CancellationToken.None);

        quote.Value!.Price.Amount.Should().BeApproximately(0.2, 1e-9);
    }
}
