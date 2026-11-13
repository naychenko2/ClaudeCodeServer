using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// «Увеличить ×2/×4» у версии сцены (RelayVSR, local): отдельная задача над клипом версии, не часть «Макс». Итог — НОВАЯ версия
// с пометкой масштаба и размером результата, исходная цела, трат нет, сбой и отмена версий не добавляют
public sealed class VideoEnhanceTests
{
    private sealed class LocalEnhancer : IVideoEngine
    {
        public int Runs;
        public byte[]? SeenVideo;
        public int SeenScale;
        public VideoResult? Result;
        public string? Refusal;

        public string Key => "local";
        public string Label => "Локальные модели";
        public string PriceUnit => VideoPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new VideoModelInfo("minimax-h3", "MiniMax H3",
                new VideoCaps([5], ["16:9"], true, false, VideoLicense.NotStated, VideoPriceUnits.Free, FirstFrame: true)),
        ];

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct) =>
            throw new InvalidOperationException("съёмки у этого поставщика в тесте нет");

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);

        public string? EnhanceRefusal(VideoEditScope scope) => Refusal;

        public int? EnhanceEtaSeconds(int scale, int? width, int? height, double seconds) => width is null ? null : scale * 20;

        public Task<VideoResult> EnhanceAsync(EnhanceRequest request, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            SeenVideo = request.Video;
            SeenScale = request.Scale;
            progress.Report(new VideoProgress(VideoStage.Upscaling, RemoteId: "e1"));
            return Task.FromResult(Result ?? new VideoResult(VideoOutcome.Ok,
                new VideoFile([8, 8], "video/mp4", ".mp4", 5, true, 1728 * request.Scale / 2, 960 * request.Scale / 2),
                null, false, "e1", null));
        }
    }

    private static async Task<(string Scene, string Version)> Shot(AgentWorld w)
    {
        var scene = w.NewScene();
        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();
        return (scene.SceneId, w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single().VersionId);
    }

    [Fact]
    public async Task Enhance_adds_a_marked_version_from_the_clip_of_the_source_and_spends_nothing()
    {
        var local = new LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);
        var spent = w.Spend.Records.Count;

        var accepted = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(2), default);

        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var result = accepted.Value.Should().BeOfType<VideoEnhanceResult>().Subject;
        (result.SourceVersionId, result.Scale).Should().Be((versionId, 2));
        await w.Jobs.WhenDone(result.JobId);
        local.SeenVideo.Should().Equal(new byte[] { 1, 2, 3 });
        local.SeenScale.Should().Be(2);
        var versions = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions;
        versions.Should().HaveCount(2);
        var fresh = versions.Single(v => v.VersionId != versionId);
        (fresh.EnhanceScale, fresh.Provider, fresh.Model, fresh.Width, fresh.Height, fresh.HasSound)
            .Should().Be((2, "local", "relayvsr-x2", 1728, 960, true));
        versions.Single(v => v.VersionId == versionId).EnhanceScale.Should().BeNull();
        w.Spend.Records.Should().HaveCount(spent);
    }

    [Fact]
    public async Task Failure_adds_no_version_and_offers_no_neighbour()
    {
        var local = new LocalEnhancer { Result = VideoResult.Fail(VideoOutcome.Failed, "Увеличение не удалось: oom") };
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);

        var accepted = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(4), default);
        var result = accepted.Value.Should().BeOfType<VideoEnhanceResult>().Subject;
        await w.Jobs.WhenDone(result.JobId);

        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Should().ContainSingle();
        w.Jobs.Get(w.Owner, w.F.Scope.Key, result.JobId)!.Error.Should().Contain("oom");
    }

    [Fact]
    public async Task Quote_lists_both_scales_with_eta_and_result_size()
    {
        var local = new LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);

        var ok = (OkObjectResult)w.Rest.EnhanceQuote(w.ProjectId, w.Chat, scene, versionId);

        var quote = ok.Value.Should().BeOfType<VideoEnhanceQuoteDto>().Subject;
        quote.Options.Select(o => o.Scale).Should().Equal(2, 4);
        quote.Options.Should().OnlyContain(o => o.Available);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task Bad_scale_is_refused_before_anything_starts(int scale)
    {
        var local = new LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);

        var refused = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(scale), default);

        refused.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        local.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Without_the_local_provider_the_action_is_unavailable()
    {
        using var w = new AgentWorld();
        var (scene, versionId) = await Shot(w);

        var refused = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(2), default);

        refused.StatusCode.Should().NotBe(StatusCodes.Status202Accepted);
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Should().ContainSingle();
    }

    [Fact]
    public async Task An_enhanced_version_cannot_be_enhanced_again_and_a_refusal_of_the_scope_is_passed_on()
    {
        var local = new LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);
        var accepted = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(2), default);
        await w.Jobs.WhenDone(((VideoEnhanceResult)accepted.Value!).JobId);
        var fresh = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single(v => v.EnhanceScale is not null);

        var again = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, fresh.VersionId, new VideoEnhanceRequest(2), default);
        again.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        local.Refusal = "Локальные модели работают только в чате проекта";
        var scoped = (ObjectResult)await w.Rest.Enhance(w.ProjectId, w.Chat, scene, versionId, new VideoEnhanceRequest(2), default);
        scoped.StatusCode.Should().NotBe(StatusCodes.Status202Accepted);
    }
}
