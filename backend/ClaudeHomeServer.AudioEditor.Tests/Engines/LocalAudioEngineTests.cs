using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

public class LocalAudioEngineTests
{
    private static readonly Project ServerProject = new() { Id = "p-server", RootPath = "/srv/p" };
    private static readonly Project DeviceProject = new() { Id = "p-device", RootPath = "C:/p", DeviceId = "dev-1" };

    private static AudioRequest Speak(AudioEditScope scope) =>
        new(AudioOp.Speak, AudioCatalog.QwenTts, scope, Text: "Привет", Source: new AudioBytes([1, 2, 3], "audio/wav"));

    private static readonly IProgress<AudioProgress> NoProgress = new Progress<AudioProgress>();

    // Strict-мок без настроек: любое обращение к шву роняет тест
    private static Mock<ILocalAudioMedia> Untouchable() => new(MockBehavior.Strict);

    public static TheoryData<string> RefusedScopes => new() { "personal", "personal-with-project", "device" };

    private static AudioEditScope ScopeOf(string kind) => kind switch
    {
        "personal" => new AudioEditScope(AudioEditScope.Personal, null),
        // Ключ личной области с подставленным проектом — гейт смотрит на область, а не на наличие Project
        "personal-with-project" => new AudioEditScope(AudioEditScope.Personal, ServerProject),
        "device" => AudioEditScope.Of(DeviceProject),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(RefusedScopes))]
    public async Task Run_RefusedScope_RejectsWithoutTouchingSeam(string kind)
    {
        var media = Untouchable();
        var engine = new LocalAudioEngine(media.Object);

        var result = await engine.RunAsync(Speak(ScopeOf(kind)), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Error.Should().Be(kind == "device" ? LocalAudioEngine.DeviceProjectReason : LocalAudioEngine.PersonalScopeReason);
        result.Charged.Should().BeFalse();
        media.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RefusedScopes))]
    public async Task Estimate_RefusedScope_ThrowsWithoutTouchingSeam(string kind)
    {
        var media = Untouchable();
        var engine = new LocalAudioEngine(media.Object);
        var model = engine.Models.First(m => m.Id == AudioCatalog.QwenTts);

        var act = () => engine.EstimateAsync(model, Speak(ScopeOf(kind)), CancellationToken.None);

        await act.Should().ThrowAsync<AudioEngineUnavailableException>();
        engine.ExpectedSeconds(model, Speak(ScopeOf(kind))).Should().BeNull();
        media.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void ScopeRefusal_ServerProject_Admits()
    {
        var engine = new LocalAudioEngine(Untouchable().Object);

        engine.ScopeRefusal(AudioEditScope.Of(ServerProject)).Should().BeNull();
        engine.ScopeRefusal(AudioEditScope.Of(DeviceProject)).Should().Be(LocalAudioEngine.DeviceProjectReason);
        engine.ScopeRefusal(new AudioEditScope(AudioEditScope.Personal, null)).Should().Be(LocalAudioEngine.PersonalScopeReason);
    }

    [Fact]
    public async Task NoSeam_DisabledAndNotRegistered_WithoutException()
    {
        var engine = new LocalAudioEngine(null);

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeFalse();
        engine.Models.Should().NotBeEmpty();
        var result = await engine.RunAsync(Speak(AudioEditScope.Of(ServerProject)), NoProgress, CancellationToken.None);
        result.Outcome.Should().Be(AudioOutcome.Unavailable);
        (await engine.CancelRemoteAsync("t", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public void SeamDown_RegisteredButDisabled()
    {
        var media = new Mock<ILocalAudioMedia>();
        media.SetupGet(m => m.Configured).Returns(true);
        media.SetupGet(m => m.Available).Returns(false);

        var engine = new LocalAudioEngine(media.Object);

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeTrue();
        AudioCatalog.Available([engine]).Should().BeEmpty();
        AudioCatalog.Registered([engine]).Should().ContainSingle();
    }

    [Fact]
    public async Task Run_ServerProject_SubmitsComposedRequestAndReturnsFiles()
    {
        var media = new Mock<ILocalAudioMedia>();
        LocalAudioRequest? sent = null;
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalAudioRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LocalAudioRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new LocalAudioSubmitted("t-1", 0, 30, null));
        media.Setup(m => m.PollAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalAudioPoll(LocalAudioState.Completed, null,
                [new LocalAudioFile(LocalAudioRoles.Main, [9], "audio/wav", ".wav")], null));
        var engine = new LocalAudioEngine(media.Object);

        var request = Speak(AudioEditScope.Of(ServerProject)) with
        {
            // Params не перебивают фиксированный движок модели
            Params = new JsonObject { ["engine"] = "moss", ["speaker"] = "Ryan" },
        };
        var result = await engine.RunAsync(request, NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Files.Should().ContainSingle(f => f.Role == LocalAudioRoles.Main);
        result.ActualCost.Should().Be(new AudioCost(0, AudioPriceUnits.Free));
        result.RemoteId.Should().Be("t-1");
        sent!.Op.Should().Be(LocalAudioOp.Speech);
        sent.Args!["engine"]!.GetValue<string>().Should().Be("qwen");
        sent.Args["speaker"]!.GetValue<string>().Should().Be("Ryan");
        sent.Args["text"]!.GetValue<string>().Should().Be("Привет");
        sent.Audio.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Estimate_ServerProject_FreeWithQueueAndEtaFromSeam()
    {
        var media = new Mock<ILocalAudioMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        media.Setup(m => m.EtaSeconds(It.IsAny<LocalAudioRequest>())).Returns(42);
        var engine = new LocalAudioEngine(media.Object);
        var model = engine.Models.First(m => m.Id == AudioCatalog.QwenTts);

        var estimate = await engine.EstimateAsync(model, Speak(AudioEditScope.Of(ServerProject)), CancellationToken.None);

        estimate.Should().Be(new AudioEstimate(0, AudioPriceUnits.Free, false, AudioEstimateSources.Provider, 42, 2));
    }

    [Fact]
    public void Compose_OpNotSupportedByModel_Null()
    {
        LocalAudioEngine.Compose(new AudioRequest(AudioOp.Song, AudioCatalog.QwenTts, AudioEditScope.Of(ServerProject)))
            .Should().BeNull();
        LocalAudioEngine.Compose(new AudioRequest(AudioOp.Repaint, AudioCatalog.AceStep, AudioEditScope.Of(ServerProject),
                StartSec: 5, EndSec: 10))!
            .Args!["task"]!.GetValue<string>().Should().Be("repaint");
    }

    // Шаг 29: метка происхождения прогона (владелец, чат, вид=звук, операция, тяжёлый ли) заполняется
    // при постановке, когда запрос несёт владельца; без владельца (тесты драйвера напрямую) — null
    [Fact]
    public void Compose_WithOwner_FillsOriginLabel()
    {
        var request = Speak(AudioEditScope.Of(ServerProject)) with
        {
            OwnerId = "user-a", SessionId = "chat-1", Heavy = true,
        };

        var composed = LocalAudioEngine.Compose(request)!;

        composed.Origin.Should().Be(new LocalJobOrigin("user-a", "chat-1", "audio", "speak", true));
    }

    [Fact]
    public void Compose_WithoutOwner_OriginNull()
    {
        var composed = LocalAudioEngine.Compose(Speak(AudioEditScope.Of(ServerProject)))!;

        composed.Origin.Should().BeNull("без владельца — метка не заполняется");
    }

    // Шаг 29: живой снимок очереди GPU из опроса прокидывается в отчёт прогресса (не в статус/трату)
    [Fact]
    public async Task Run_RunningPoll_PropagatesGpuToProgressReport()
    {
        var gpu = new LocalGpuQueueInfo(Ahead: 1, StartInSeconds: 30, Steps: null, Percent: 40,
            PercentExact: false, RemainingSeconds: 20, Busy: null, DownSince: null);
        var media = new Mock<ILocalAudioMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalAudioRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalAudioSubmitted("t-1", 0, 30, null));
        // Первый опрос — Running с новым положением (0 → 1): срабатывает отчёт с Gpu; второй — Completed
        var polls = new Queue<LocalAudioPoll>(new[]
        {
            new LocalAudioPoll(LocalAudioState.Running, 1, [], null, Gpu: gpu),
            new LocalAudioPoll(LocalAudioState.Completed, null,
                [new LocalAudioFile(LocalAudioRoles.Main, [9], "audio/wav", ".wav")], null),
        });
        media.Setup(m => m.PollAsync("t-1", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => Task.FromResult(polls.Dequeue()));
        var engine = new LocalAudioEngine(media.Object) { PollInterval = TimeSpan.FromMilliseconds(1) };

        var reports = new List<AudioProgress>();
        var result = await engine.RunAsync(Speak(AudioEditScope.Of(ServerProject)), new SyncProgress(reports.Add),
            CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        reports.Should().Contain(p => p.Gpu == gpu, "отчёт несёт живой снимок очереди GPU");
    }

    // Синхронный IProgress для теста: отчёты собираются в список в порядке прихода
    private sealed class SyncProgress(Action<AudioProgress> sink) : IProgress<AudioProgress>
    {
        public void Report(AudioProgress value) => sink(value);
    }
}
