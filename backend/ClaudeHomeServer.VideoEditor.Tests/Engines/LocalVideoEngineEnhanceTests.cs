using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Engines;

// «Увеличить ×2/×4» поверх шва local-media: что уходит в адаптер (метка тяжёлого прогона), стадии, отмена и сбой
public class LocalVideoEngineEnhanceTests
{
    private static readonly byte[] Clip = [1, 2, 3];
    private static readonly LocalVideoFile Big = new([9, 9], "video/mp4", ".mp4", HasSound: true, Width: 1728, Height: 960);

    private sealed class Recorder : IProgress<VideoProgress>
    {
        private readonly List<VideoProgress> _events = [];
        public List<VideoProgress> Events { get { lock (_events) return [.. _events]; } }
        public void Report(VideoProgress value) { lock (_events) _events.Add(value); }
    }

    private static LocalVideoEngine Fast(ILocalVideoMedia media) => new(media) { PollInterval = TimeSpan.FromMilliseconds(1) };

    private static Mock<ILocalVideoMedia> Seam(params LocalVideoPoll[] polls)
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.SubmitEnhanceAsync(It.IsAny<LocalVideoEnhanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-enh", 0, 37, null));
        var script = new Queue<LocalVideoPoll>(polls);
        media.Setup(m => m.PollAsync("t-enh", It.IsAny<CancellationToken>()))
            .Returns(() => { lock (script) return Task.FromResult(script.Dequeue()); });
        return media;
    }

    [Fact]
    public async Task Enhance_sends_a_heavy_origin_reports_upscaling_and_returns_the_big_clip_with_its_size()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Running, 0, null, null, Percent: 0.5),
            new LocalVideoPoll(LocalVideoState.Completed, null, Big, null));
        var progress = new Recorder();

        var result = await Fast(media.Object).EnhanceAsync(new EnhanceRequest(Clip, 2, "owner", "chat", "p-1", "job-1"), progress, default);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        (result.File!.Width, result.File.Height, result.File.HasSound).Should().Be((1728, 960, true));
        media.Verify(m => m.SubmitEnhanceAsync(It.Is<LocalVideoEnhanceRequest>(r =>
            r.Scale == 2 && r.Video == Clip && r.Origin!.Heavy && r.Origin.Op == "видео ×2 · RelayVSR"
            && r.Origin.Source == LocalJobOrigin.SourceVideoEditor && r.Origin.SourceJobId == "job-1"), It.IsAny<CancellationToken>()));
        progress.Events.Should().Contain(e => e.Stage == VideoStage.Upscaling && e.RemoteId == "t-enh");
    }

    [Fact]
    public async Task A_failed_run_and_a_busy_card_are_reported_with_the_reason()
    {
        var failed = await Fast(Seam(new LocalVideoPoll(LocalVideoState.Failed, null, null, "oom")).Object)
            .EnhanceAsync(new EnhanceRequest(Clip, 4), new Recorder(), default);
        failed.Outcome.Should().Be(VideoOutcome.Failed);
        failed.Error.Should().Contain("oom");

        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitEnhanceAsync(It.IsAny<LocalVideoEnhanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalVideoSubmitted.Fail("Очередь локальной видеокарты занята (4 задач).", busy: true));
        var busy = await Fast(media.Object).EnhanceAsync(new EnhanceRequest(Clip, 2), new Recorder(), default);
        busy.Outcome.Should().Be(VideoOutcome.Unavailable);
    }

    [Fact]
    public async Task Cancel_by_the_human_returns_cancelled_without_a_clip()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Running, 0, null, null),
            new LocalVideoPoll(LocalVideoState.Failed, null, null, "interrupted"));
        media.Setup(m => m.CancelAsync("t-enh", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var engine = Fast(media.Object);
        var progress = new Recorder();
        var run = engine.EnhanceAsync(new EnhanceRequest(Clip, 2), progress, default);
        // Человек жмёт «Отменить увеличение» по тикету, который видел в отчёте прогресса
        while (progress.Events.All(e => e.RemoteId is null)) await Task.Delay(1);
        await engine.CancelRemoteAsync("t-enh", default);

        var result = await run;

        result.Outcome.Should().Be(VideoOutcome.Cancelled);
        result.File.Should().BeNull();
    }

    [Fact]
    public void Eta_needs_the_size_of_the_version_and_the_scope_is_checked_like_a_shoot()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.EnhanceEtaSeconds(2, 1344, 768, 5)).Returns(37);
        var engine = new LocalVideoEngine(media.Object);

        engine.EnhanceEtaSeconds(2, 1344, 768, 5).Should().Be(37);
        engine.EnhanceEtaSeconds(2, null, null, 5).Should().BeNull("размер версии не известен — числа не выдумываем");
        engine.EnhanceRefusal(VideoEditScope.Of(new Project { Id = "p", RootPath = "/srv/p" })).Should().BeNull();
        engine.EnhanceRefusal(VideoEditScope.Of(new Session { Id = "chat" })).Should().BeNull("в личном чате «Увеличить» работает");
        engine.EnhanceRefusal(VideoEditScope.Of(new Project { Id = "d", RootPath = "C:/p", DeviceId = "dev-1" })).Should().Be(LocalVideoEngine.DeviceProjectReason);
        new LocalVideoEngine(null).EnhanceRefusal(VideoEditScope.Of(new Project { Id = "p", RootPath = "/srv/p" })).Should().NotBeNull();
    }
}
