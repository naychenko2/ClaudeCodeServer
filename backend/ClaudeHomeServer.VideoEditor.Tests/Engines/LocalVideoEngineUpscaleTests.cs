using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Engines;

// «Макс» у local MiniMax H3 = 1440p (ADR-025, решение 1): съёмка full, затем вторая тяжёлая задача — латентный апскейл.
// Шов — Moq: проверяется, что именно уходит в адаптер и что видит исполнитель (стадии, версия, отмена, сбой)
public class LocalVideoEngineUpscaleTests
{
    private static readonly Project ServerProject = new() { Id = "p-server", RootPath = "/srv/p" };
    private static readonly LocalVideoFile Shot = new([7, 7], "video/mp4", ".mp4", HasSound: true);
    private static readonly LocalVideoFile Big = new([1, 4, 4, 0], "video/mp4", ".mp4", HasSound: true);

    // Отчёты пишутся под замком: порядок важен тестам, а читают их из другого потока после await
    private sealed class Recorder : IProgress<VideoProgress>
    {
        private readonly List<VideoProgress> _events = [];
        public List<VideoProgress> Events { get { lock (_events) return [.. _events]; } }
        public void Report(VideoProgress value) { lock (_events) _events.Add(value); }
    }

    private static ResolvedFormat Tier(FormatTier tier, string aspect = "16:9")
    {
        var caps = new LocalVideoEngine(null).Models[0].Caps.Format;
        return MediaFormatResolver.Resolve(caps,
            new MediaFormat(FormatModes.Aspect, aspect, tier, null, null, ContextActor.Human, DateTime.UtcNow));
    }

    private static VideoRequest Scene(ResolvedFormat? format) =>
        new(LocalVideoEngine.MiniMaxH3, VideoEditScope.Of(ServerProject), "Идёт снег", new VideoFrameBytes([1, 2, 3], "image/png"),
            null, 5, "16:9", Sound: true, Seed: 42, Format: format);

    private static LocalVideoEngine Fast(ILocalVideoMedia media, bool keepShot = true) =>
        new(media) { PollInterval = TimeSpan.FromMilliseconds(1), KeepShotWhenUpscaleFails = keepShot };

    // Съёмка t-shot завершается сразу, апскейл t-up принимается и завершается по сценарию polls
    private static Mock<ILocalVideoMedia> Seam(params LocalVideoPoll[] upscalePolls)
    {
        var media = new Mock<ILocalVideoMedia>();
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-shot", 0, 330, null));
        media.Setup(m => m.PollAsync("t-shot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoPoll(LocalVideoState.Completed, null, Shot, null));
        media.Setup(m => m.UpscaleEtaSeconds(It.IsAny<LocalVideoRequest>())).Returns(915);
        media.Setup(m => m.SubmitUpscaleAsync("t-shot", "1440p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalVideoSubmitted("t-up", 0, 915, null));
        if (upscalePolls.Length > 0)
        {
            // Сценарий опросов — своя очередь, а не SetupSequence: порядок отдачи не зависит от внутреннего состояния Moq,
            // а исчерпанный сценарий не молчит «null», а падает понятной ошибкой
            var script = new Queue<LocalVideoPoll>(upscalePolls);
            media.Setup(m => m.PollAsync("t-up", It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    lock (script)
                        return Task.FromResult(script.Count > 0
                            ? script.Dequeue()
                            : throw new InvalidOperationException("Сценарий опросов t-up исчерпан"));
                });
        }
        return media;
    }

    private static LocalVideoPoll Running(double? percent = null) =>
        new(LocalVideoState.Running, 0, null, null, Percent: percent);

    [Theory]
    [InlineData(FormatTier.Standard, "half")]
    [InlineData(FormatTier.High, "full")]
    public async Task Standard_and_High_shoot_with_size_by_tier_without_upscale(FormatTier tier, string size)
    {
        var media = Seam();
        LocalVideoRequest? sent = null;
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LocalVideoRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new LocalVideoSubmitted("t-shot", 0, 330, null));

        var result = await Fast(media.Object).RunAsync(Scene(Tier(tier)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        sent!.Size.Should().Be(size);
        result.File!.Bytes.Should().Equal(7, 7);
        media.Verify(m => m.SubmitUpscaleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "автоапскейла нет нигде, кроме ступени «Макс»");
    }

    [Fact]
    public async Task Max_shoots_full_then_upscales_to_1440p_and_version_is_only_the_big_clip()
    {
        var media = Seam(Running(0.3), new LocalVideoPoll(LocalVideoState.Completed, null, Big, null));
        LocalVideoRequest? sent = null;
        media.Setup(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LocalVideoRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new LocalVideoSubmitted("t-shot", 0, 330, null));
        var recorder = new Recorder();

        var result = await Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max)), recorder, CancellationToken.None);

        sent!.Size.Should().Be("full", "«Макс» снимается full: апскейл идёт по латенту съёмки 1344×768");
        media.Verify(m => m.SubmitUpscaleAsync("t-shot", "1440p", It.IsAny<CancellationToken>()), Times.Once);
        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.Bytes.Should().Equal(new byte[] { 1, 4, 4, 0 }, "версией становится только 1440p-ролик");
        result.File.UpscaleError.Should().BeNull();
        (result.File.Width, result.File.Height).Should().Be((2528, 1440));
        result.RemoteId.Should().Be("t-up");
    }

    [Fact]
    public async Task Upscaling_stage_has_its_own_eta_and_percent_and_shoot_announces_upscale_eta()
    {
        var media = Seam(Running(0.3), Running(0.6), new LocalVideoPoll(LocalVideoState.Completed, null, Big, null));
        var recorder = new Recorder();

        await Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max)), recorder, CancellationToken.None);

        // Пока идёт съёмка, «следом ~15 мин» — время апскейла
        recorder.Events[0].Stage.Should().Be(VideoStage.Queued);
        recorder.Events[0].Accepted.Should().BeTrue();
        recorder.Events[0].UpscaleEtaSeconds.Should().Be(915);
        var up = recorder.Events.Where(e => e.Stage == VideoStage.Upscaling).ToList();
        up.Should().NotBeEmpty("вторая тяжёлая задача видна отдельной стадией");
        up.Select(e => e.RemoteId).Should().Contain("t-up");
        up.Where(e => e.Percent is not null).Select(e => e.Percent).Should().Equal(0.3, 0.6);
        up.Where(e => e.EtaSeconds is not null).Select(e => e.EtaSeconds).Should().OnlyContain(e => e == 915);
        recorder.Events.Select(e => e.Stage).Should().ContainInOrder(
            VideoStage.Queued, VideoStage.Upscaling, VideoStage.Downloading);
    }

    [Fact]
    public async Task Upscale_failure_keeps_768p_shot_with_mark_and_never_pretends_it_is_1440p()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Failed, null, null, "CUDA out of memory"));

        var result = await Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.Bytes.Should().Equal(new byte[] { 7, 7 }, "версия — съёмка 768p, а не подмена увеличенным");
        result.File.UpscaleError.Should().Contain("не удалось").And.Contain("CUDA out of memory");
        result.File.UpscaleCancelled.Should().BeNull("сбой — не отмена");
        (result.File.Width, result.File.Height).Should().Be((1344, 768), "размер съёмки — «Высокое»");
    }

    [Fact]
    public async Task Upscale_rejected_by_seam_keeps_shot_with_mark()
    {
        var media = Seam();
        media.Setup(m => m.SubmitUpscaleAsync("t-shot", "1440p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocalVideoSubmitted.Fail("Съёмка больше не доступна для увеличения", busy: false));

        var result = await Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.UpscaleError.Should().Contain("не доступна");
    }

    [Fact]
    public async Task Upscale_failure_fails_the_job_when_keeping_the_shot_is_switched_off()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Failed, null, null, "oom"));

        var result = await Fast(media.Object, keepShot: false).RunAsync(Scene(Tier(FormatTier.Max)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.File.Should().BeNull("768p не сохраняется, когда решение Андрея — «не сохранять»");
        result.Error.Should().Contain("1440p").And.Contain("не сохранена");
    }

    [Fact]
    public async Task Cancelling_the_upscale_ticket_alone_keeps_the_shot_marked_cancelled()
    {
        var media = Seam();
        LocalVideoEngine? engine = null;
        var polls = 0;
        media.Setup(m => m.PollAsync("t-up", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (polls++ == 0) return Running(0.1);
                // «Отменить увеличение»: исполнитель снимает именно тикет апскейла, ComfyUI прерывает задачу
                await engine!.CancelRemoteAsync("t-up", CancellationToken.None);
                return new LocalVideoPoll(LocalVideoState.Failed, null, null, "Interrupted");
            });
        media.Setup(m => m.CancelAsync("t-up", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        engine = Fast(media.Object);

        var result = await engine.RunAsync(Scene(Tier(FormatTier.Max)), new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.UpscaleError.Should().Be("Увеличение отменено");
        result.File.UpscaleCancelled.Should().BeTrue();
        result.File.Bytes.Should().Equal(7, 7);
        media.Verify(m => m.CancelAsync("t-up", It.IsAny<CancellationToken>()), Times.Once);
        media.Verify(m => m.CancelAsync("t-shot", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancelling_the_whole_job_during_upscale_cancels_the_upscale_ticket_not_the_shot()
    {
        var media = Seam();
        using var cts = new CancellationTokenSource();
        media.Setup(m => m.PollAsync("t-up", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return Running(0.2);
            });
        media.Setup(m => m.CancelAsync("t-up", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max)), new Recorder(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        media.Verify(m => m.CancelAsync("t-up", It.IsAny<CancellationToken>()), Times.Once);
        media.Verify(m => m.CancelAsync("t-shot", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Estimate_of_max_is_shoot_plus_upscale_and_marks_upscale_post_step()
    {
        var media = Seam();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        media.Setup(m => m.EtaSeconds(It.IsAny<LocalVideoRequest>())).Returns(333);
        var engine = Fast(media.Object);

        var max = await engine.EstimateAsync(engine.Models[0], Scene(Tier(FormatTier.Max)), CancellationToken.None);
        var high = await engine.EstimateAsync(engine.Models[0], Scene(Tier(FormatTier.High)), CancellationToken.None);

        max.EtaSeconds.Should().Be(333 + 915);
        max.UpscaleEtaSeconds.Should().Be(915);
        max.ShotEtaSeconds.Should().Be(333, "замер съёмки отдельным полем, а не разностью на клиенте");
        max.PostStep.Should().Be(FormatPostSteps.Upscale);
        high.EtaSeconds.Should().Be(333);
        high.UpscaleEtaSeconds.Should().BeNull();
        high.ShotEtaSeconds.Should().BeNull();
        high.PostStep.Should().BeNull();
    }

    [Fact]
    public async Task Estimate_of_max_without_measured_upscale_gives_no_number()
    {
        var media = Seam();
        media.SetupGet(m => m.Available).Returns(true);
        media.Setup(m => m.QueueLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        media.Setup(m => m.EtaSeconds(It.IsAny<LocalVideoRequest>())).Returns(943);
        media.Setup(m => m.UpscaleEtaSeconds(It.IsAny<LocalVideoRequest>())).Returns((int?)null);
        var engine = Fast(media.Object);

        var max = await engine.EstimateAsync(engine.Models[0], Scene(Tier(FormatTier.Max)), CancellationToken.None);

        max.EtaSeconds.Should().BeNull("ролик длиннее замеренного: «время не замерено», число не выдумываем");
        max.ShotEtaSeconds.Should().Be(943, "съёмка замерена — её время отдаём");
        max.PostStep.Should().Be(FormatPostSteps.Upscale);
    }

    [Fact]
    public async Task Size_in_params_is_refused_because_format_sets_the_size()
    {
        var media = new Mock<ILocalVideoMedia>(MockBehavior.Strict);
        var request = Scene(Tier(FormatTier.High)) with { Params = new JsonObject { ["size"] = "half" } };

        var result = await Fast(media.Object).RunAsync(request, new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Rejected);
        result.Error.Should().Contain("quality");
        media.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Portrait_scene_sizes_swap_sides_for_shot_and_final_clip()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Completed, null, Big, null));

        var result = await Fast(media.Object).RunAsync(Scene(Tier(FormatTier.Max, "9:16")), new Recorder(), CancellationToken.None);

        (result.File!.Width, result.File.Height).Should().Be((1440, 2528));
    }

    // ── Повторить увеличение ─────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_upscales_the_live_shot_without_reshooting_and_gives_the_big_clip_without_mark()
    {
        var media = Seam(Running(0.4), new LocalVideoPoll(LocalVideoState.Completed, null, Big, null));
        media.Setup(m => m.CanUpscale("t-shot")).Returns(true);
        var recorder = new Recorder();

        var result = await Fast(media.Object).RetryUpscaleAsync(new UpscaleRetryRequest("t-shot", Tier(FormatTier.Max)), recorder,
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Ok);
        result.File!.Bytes.Should().Equal(new byte[] { 1, 4, 4, 0 });
        result.File.UpscaleError.Should().BeNull();
        (result.File.Width, result.File.Height).Should().Be((2528, 1440));
        media.Verify(m => m.SubmitAsync(It.IsAny<LocalVideoRequest>(), It.IsAny<CancellationToken>()), Times.Never, "пересъёмки нет");
        media.Verify(m => m.SubmitUpscaleAsync("t-shot", "1440p", It.IsAny<CancellationToken>()), Times.Once);
        recorder.Events.Select(e => e.Stage).Should().ContainInOrder(VideoStage.Upscaling, VideoStage.Downloading);
    }

    [Fact]
    public async Task Retry_failure_returns_failed_without_the_768p_clip()
    {
        var media = Seam(new LocalVideoPoll(LocalVideoState.Failed, null, null, "oom"));
        media.Setup(m => m.CanUpscale("t-shot")).Returns(true);

        var result = await Fast(media.Object).RetryUpscaleAsync(new UpscaleRetryRequest("t-shot", Tier(FormatTier.Max)),
            new Recorder(), CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Failed);
        result.File.Should().BeNull("съёмка 768p уже есть версией — второй раз её не сохраняем");
        result.Error.Should().Contain("не удалось").And.Contain("oom");
    }

    [Fact]
    public async Task Retry_cancelled_by_user_returns_cancelled()
    {
        var media = Seam();
        media.Setup(m => m.CanUpscale("t-shot")).Returns(true);
        LocalVideoEngine? engine = null;
        media.Setup(m => m.PollAsync("t-up", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await engine!.CancelRemoteAsync("t-up", CancellationToken.None);
                return new LocalVideoPoll(LocalVideoState.Failed, null, null, "Interrupted");
            });
        media.Setup(m => m.CancelAsync("t-up", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        engine = Fast(media.Object);

        var result = await engine.RetryUpscaleAsync(new UpscaleRetryRequest("t-shot", Tier(FormatTier.Max)), new Recorder(),
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Cancelled);
        result.File.Should().BeNull();
    }

    [Fact]
    public async Task Retry_is_refused_before_the_seam_when_the_shot_context_is_gone()
    {
        var media = Seam();
        media.Setup(m => m.CanUpscale("t-shot")).Returns(false);
        var engine = Fast(media.Object);

        engine.UpscaleRetryRefusal("t-shot").Should().Be("Повторить увеличение уже нельзя — переснимите сцену");
        var result = await engine.RetryUpscaleAsync(new UpscaleRetryRequest("t-shot", Tier(FormatTier.Max)), new Recorder(),
            CancellationToken.None);

        result.Outcome.Should().Be(VideoOutcome.Rejected);
        media.Verify(m => m.SubmitUpscaleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
