using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Engines;

public class LocalVideoEngineTests
{
    private static readonly Project ServerProject = new() { Id = "p-server", RootPath = "/srv/p" };
    private static readonly Project DeviceProject = new() { Id = "p-device", RootPath = "C:/p", DeviceId = "dev-1" };

    private static VideoRequest Scene(VideoEditScope scope) =>
        new(LocalVideoEngine.MiniMaxH3, scope, "Идёт снег", new VideoFrameBytes([1, 2, 3], "image/png"),
            new VideoFrameBytes([4, 5], "image/png"), 5, "16:9", Sound: true, Seed: 42);

    private static VideoModelInfo H3(LocalVideoEngine engine) => engine.Models.Single(m => m.Id == LocalVideoEngine.MiniMaxH3);

    private static readonly LocalVideoFile Clip = new([9, 9], "video/mp4", ".mp4", HasSound: true);

    // Синхронный приёмник прогресса: Progress<T> шлёт события через пул и теряет порядок
    private sealed class Recorder : IProgress<VideoProgress>
    {
        public List<VideoProgress> Events { get; } = [];
        public void Report(VideoProgress value) => Events.Add(value);
    }

    // Strict-мок без настроек: любое обращение к шву роняет тест
    private static Mock<ILocalVideoMedia> Untouchable() => new(MockBehavior.Strict);

    private static LocalVideoEngine Fast(ILocalVideoMedia media) => new(media) { PollInterval = TimeSpan.FromMilliseconds(1) };

    public static TheoryData<string> RefusedScopes => new() { "personal", "personal-with-project", "device" };

    private static VideoEditScope ScopeOf(string kind) => kind switch
    {
        "personal" => new VideoEditScope(VideoEditScope.Personal, null),
        // Ключ личной области с подставленным проектом — гейт смотрит на область, а не на наличие Project
        "personal-with-project" => new VideoEditScope(VideoEditScope.Personal, ServerProject),
        "device" => VideoEditScope.Of(DeviceProject),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(RefusedScopes))]
    public async Task Run_RefusedScope_RejectsWithoutTouchingSeam(string kind)
    {
        var media = Untouchable();
        var engine = new LocalVideoEngine(media.Object);

        var result = await engine.RunAsync(Scene(ScopeOf(kind)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Rejected);
        result.Error.Should().Be(kind == "device" ? LocalVideoEngine.DeviceProjectReason : LocalVideoEngine.PersonalScopeReason);
        result.Charged.Should().BeFalse();
        media.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RefusedScopes))]
    public async Task Estimate_RefusedScope_ThrowsWithoutTouchingSeam(string kind)
    {
        var media = Untouchable();
        var engine = new LocalVideoEngine(media.Object);

        var act = () => engine.EstimateAsync(H3(engine), Scene(ScopeOf(kind)), CancellationToken.None);

        await act.Should().ThrowAsync<VideoEngineUnavailableException>();
        engine.ExpectedSeconds(H3(engine), Scene(ScopeOf(kind))).Should().BeNull();
        media.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void ScopeRefusal_ServerProject_Admits()
    {
        var engine = new LocalVideoEngine(Untouchable().Object);

        engine.ScopeRefusal(VideoEditScope.Of(ServerProject)).Should().BeNull();
        engine.ScopeRefusal(VideoEditScope.Of(DeviceProject)).Should().Be(LocalVideoEngine.DeviceProjectReason);
        engine.ScopeRefusal(new VideoEditScope(VideoEditScope.Personal, null)).Should().Be(LocalVideoEngine.PersonalScopeReason);
    }

    [Fact]
    public void Identity_FreeLocalProviderWithH3Caps()
    {
        var engine = new LocalVideoEngine(null);

        engine.Key.Should().Be("local");
        engine.Label.Should().Be("Локальные модели");
        engine.PriceUnit.Should().Be(VideoPriceUnits.Free);
        var caps = H3(engine).Caps;
        caps.Durations.Should().Equal(Enumerable.Range(1, LocalVideoEngine.MaxSeconds));
        caps.Aspects.Should().Equal("16:9", "9:16");
        caps.Sound.Should().BeTrue();
        caps.LastFrame.Should().BeTrue();
        caps.PriceUnit.Should().Be(VideoPriceUnits.Free);
        engine.ParamNames(H3(engine)).Should().BeEquivalentTo(["size", "fast"]);
    }

    [Fact]
    public async Task NoSeam_DisabledAndNotRegistered_WithoutException()
    {
        var engine = new LocalVideoEngine(null);

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeFalse();
        var result = await engine.RunAsync(Scene(VideoEditScope.Of(ServerProject)), new Recorder(), CancellationToken.None);
        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        (await engine.CancelRemoteAsync("t", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public void SeamDown_RegisteredButDisabled()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Configured).Returns(true);
        media.SetupGet(m => m.Available).Returns(false);

        var engine = new LocalVideoEngine(media.Object);

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeTrue();
    }

    [Fact]
    public async Task Run_Success_AcceptedRightAfterSubmitThenClip()
    {
        var media = new Mock<ILocalVideoMedia>();
        LocalVideoRequest? sent = null;
        var recorder = new Recorder();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LocalVideoRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new LocalVideoSubmitted("t-1", 1, 333, null));
        var polls = 0;
        media.Setup(m => m.PollAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // В момент первого опроса принятие уже должно быть доложено
                if (polls++ == 0) recorder.Events.Should().ContainSingle(e => e.Accepted);
                return polls switch
                {
                    1 => new LocalVideoPoll(LocalVideoState.Queued, 1, null, null),
                    2 => new LocalVideoPoll(LocalVideoState.Running, 0, null, null),
                    _ => new LocalVideoPoll(LocalVideoState.Completed, null, Clip, null),
                };
            });
        var engine = Fast(media.Object);

        var request = Scene(VideoEditScope.Of(ServerProject)) with { Params = new JsonObject { ["size"] = "half", ["fast"] = false } };
        var result = await engine.RunAsync(request, recorder, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.Bytes.Should().Equal(9, 9);
        result.File.Extension.Should().Be(".mp4");
        result.File.HasSound.Should().BeTrue();
        result.ActualCost.Should().Be(new VideoCost(0, VideoPriceUnits.Free));
        result.Charged.Should().BeFalse();
        result.RemoteId.Should().Be("t-1");

        var accepted = recorder.Events[0];
        accepted.Accepted.Should().BeTrue();
        accepted.RemoteId.Should().Be("t-1");
        accepted.Stage.Should().Be(VideoStage.Queued);
        accepted.EtaSeconds.Should().Be(333);
        recorder.Events.Select(e => e.Stage).Should().Contain(VideoStage.Running).And.EndWith(VideoStage.Downloading);

        sent!.Prompt.Should().Be("Идёт снег");
        sent.FirstFrame.Should().Equal(1, 2, 3);
        sent.LastFrame.Should().Equal(4, 5);
        sent.Seconds.Should().Be(5);
        sent.Size.Should().Be("half");
        sent.Fast.Should().BeFalse();
        sent.Seed.Should().Be(42);
    }

    [Fact]
    public async Task Run_Busy_UnavailableWithoutAccept()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalVideoSubmitted.Fail("Очередь локальной видеокарты занята (4 задач).", busy: true));
        var recorder = new Recorder();

        var result = await Fast(media.Object).RunAsync(Scene(VideoEditScope.Of(ServerProject)), recorder, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Unavailable);
        result.Error.Should().Contain("занята");
        result.Charged.Should().BeFalse();
        recorder.Events.Should().BeEmpty("задача не принята — трата не пишется");
        media.Verify(m => m.PollAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_SubmitRefusedNotBusy_Failed()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalVideoSubmitted.Fail("Файл «первый кадр» — не картинка."));

        var result = await Fast(media.Object).RunAsync(Scene(VideoEditScope.Of(ServerProject)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("не картинка");
    }

    [Fact]
    public async Task Run_RunningPoll_PercentFromComfyProgress_NoDataStaysEmpty()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-p", 0, 100, null));
        media.SetupSequence(m => m.PollAsync("t-p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Running, 0, null, null))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Running, 0, null, null, Percent: 0.375))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Running, 0, null, null, Percent: 0.75))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Completed, null, Clip, null));
        var recorder = new Recorder();

        var result = await Fast(media.Object).RunAsync(Scene(VideoEditScope.Of(ServerProject)), recorder, CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        recorder.Events.Where(e => e.Stage == VideoStage.Running).Select(e => e.Percent)
            .Should().Equal(null, 0.38, 0.75);
    }

    [Fact]
    public async Task Run_PollFailed_FailedWithReason()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-2", 0, null, null));
        media.SetupSequence(m => m.PollAsync("t-2", It.IsAny<CancellationToken>()))
            // Временный сбой опроса задачу не валит
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Running, null, null, null, "ComfyUI не ответил"))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Failed, null, null, "CUDA out of memory"));

        var result = await Fast(media.Object).RunAsync(Scene(VideoEditScope.Of(ServerProject)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("CUDA out of memory");
        result.RemoteId.Should().Be("t-2");
        media.Verify(m => m.CancelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_CancelledOutside_CancelsTicketAndThrows()
    {
        var media = new Mock<ILocalVideoMedia>();
        using var cts = new CancellationTokenSource();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-3", 0, null, null));
        media.Setup(m => m.PollAsync("t-3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return new LocalVideoPoll(LocalVideoState.Running, 0, null, null);
            });
        media.Setup(m => m.CancelAsync("t-3", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => Fast(media.Object).RunAsync(Scene(VideoEditScope.Of(ServerProject)), new Recorder(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        media.Verify(m => m.CancelAsync("t-3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_Ceiling_FailedAndCancelsTicket()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-4", 3, null, null));
        media.Setup(m => m.PollAsync("t-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Queued, 3, null, null));
        media.Setup(m => m.CancelAsync("t-4", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var engine = Fast(media.Object);
        engine.Ceiling = TimeSpan.FromMilliseconds(50);

        var result = await engine.RunAsync(Scene(VideoEditScope.Of(ServerProject)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.Error.Should().Contain("не успела");
        media.Verify(m => m.CancelAsync("t-4", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_NoFirstFrame_RejectedBeforeSeam()
    {
        var media = Untouchable();

        var result = await new LocalVideoEngine(media.Object).RunAsync(
            Scene(VideoEditScope.Of(ServerProject)) with { FrameA = null }, new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Rejected);
        media.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelRemote_GoesToSeam()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.CancelAsync("t-9", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await new LocalVideoEngine(media.Object).CancelRemoteAsync("t-9", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Estimate_ServerProject_FreeWithQueueAndEtaFromSeam()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        media.Setup(m => m.EtaSeconds(It.Is<LocalVideoRequest>(r => r.Seconds == 5))).Returns(333);
        var engine = new LocalVideoEngine(media.Object);

        var estimate = await engine.EstimateAsync(H3(engine), Scene(VideoEditScope.Of(ServerProject)), CancellationToken.None);

        estimate.Amount.Should().Be(0);
        estimate.Unit.Should().Be(VideoPriceUnits.Free);
        estimate.Approx.Should().BeFalse();
        estimate.Source.Should().Be(VideoEstimateSources.Provider);
        estimate.EtaSeconds.Should().Be(333);
        estimate.QueueLength.Should().Be(2);
        engine.ExpectedSeconds(H3(engine), Scene(VideoEditScope.Of(ServerProject))).Should().Be(333);
    }

    [Fact]
    public async Task Estimate_ComfyDown_ThrowsUnavailable()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        var engine = new LocalVideoEngine(media.Object);

        var act = () => engine.EstimateAsync(H3(engine), Scene(VideoEditScope.Of(ServerProject)), CancellationToken.None);

        await act.Should().ThrowAsync<VideoEngineUnavailableException>().WithMessage("*не отвечает*");
    }
}
