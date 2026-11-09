using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// «Повторить увеличение» у агента: тот же путь, что у кнопки человека, но через существующий video_shoot с аргументом
// retryUpscaleOfVersionId — нового инструмента нет, схема статична. Повтор не снимает сцену заново и не тратит
public sealed class VideoEditorToolsetUpscaleRetryTests
{
    private static async Task<(string Scene, string Version)> ShotWithMark(AgentWorld w)
    {
        w.Engine.MarkUpscaleError = true;
        var scene = w.NewScene();
        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();
        var version = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single();
        version.UpscaleError.Should().NotBeNull();
        return (scene.SceneId, version.VersionId);
    }

    [Fact]
    public async Task Retry_argument_starts_only_the_upscale_and_adds_a_1440p_version_without_reshoot_or_spend()
    {
        using var w = new AgentWorld();
        w.Engine.RetryRefusal = null;
        var (scene, versionId) = await ShotWithMark(w);
        var spent = w.Spend.Records.Count;

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene, ["retryUpscaleOfVersionId"] = versionId });

        result.IsError.Should().BeFalse(result.Text);
        var answer = AgentWorld.Parse(result);
        answer["sourceVersionId"]!.GetValue<string>().Should().Be(versionId);
        await w.Jobs.WhenDone(answer["jobId"]!.GetValue<string>());
        w.Engine.Runs.Should().Be(1, "пересъёмки нет");
        w.Engine.Retries.Should().Be(1);
        var versions = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions;
        versions.Should().HaveCount(2);
        var fresh = versions.Single(v => v.VersionId != versionId);
        (fresh.Width, fresh.Height, fresh.UpscaleError).Should().Be((2528, 1440, null));
        fresh.Initiator.Should().Be(VideoInitiators.Agent);
        w.Spend.Records.Should().HaveCount(spent);
    }

    [Fact]
    public async Task Retry_argument_refuses_other_arguments_before_anything_starts()
    {
        using var w = new AgentWorld();
        w.Engine.RetryRefusal = null;
        var (scene, versionId) = await ShotWithMark(w);

        var result = await w.Call(ToolShoot, new JsonObject
            { ["sceneId"] = scene, ["retryUpscaleOfVersionId"] = versionId, ["count"] = 2 });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("count");
        w.Engine.Retries.Should().Be(0);
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_argument_for_a_model_without_upscale_is_refused_with_a_reason()
    {
        using var w = new AgentWorld();
        var (scene, versionId) = await ShotWithMark(w);

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene, ["retryUpscaleOfVersionId"] = versionId });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain(VideoUpscaleRetryTexts.NoUpscale);
    }

    [Fact]
    public async Task Rest_retry_answers_202_with_job_and_409_when_the_shot_ticket_is_gone()
    {
        using var w = new AgentWorld();
        w.Engine.RetryRefusal = null;
        var (scene, versionId) = await ShotWithMark(w);

        var accepted = (ObjectResult)await w.Rest.RetryUpscale(w.ProjectId, w.Chat, scene, versionId, default);
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var result = accepted.Value.Should().BeOfType<VideoUpscaleRetryResult>().Subject;
        result.SourceVersionId.Should().Be(versionId);
        await w.Jobs.WhenDone(result.JobId);

        w.Engine.RetryRefusal = VideoUpscaleRetryTexts.Gone;
        var gone = (ObjectResult)await w.Rest.RetryUpscale(w.ProjectId, w.Chat, scene, versionId, default);
        gone.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        w.Engine.RetryRefusal = VideoUpscaleRetryTexts.NoUpscale;
        var none = (ObjectResult)await w.Rest.RetryUpscale(w.ProjectId, w.Chat, scene, versionId, default);
        none.StatusCode.Should().Be(StatusCodes.Status400BadRequest, "у модели без увеличения — invalid_request");
    }

    [Fact]
    public async Task Retry_argument_goes_through_the_delegated_turn_gate_like_a_shoot()
    {
        using var w = new AgentWorld(delegatedDenied: true);
        w.Engine.RetryRefusal = null;
        var scene = w.NewScene();

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["retryUpscaleOfVersionId"] = "v" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("делегированный ход");
    }

    [Fact]
    public void Retry_argument_is_in_the_static_schema_with_not_without_ask_and_no_new_tool_appears()
    {
        using var w = new AgentWorld();

        var tools = w.Toolset.ToolsFor(w.Ctx());

        tools.Select(t => t.Name).Should().NotContain(n => n.Contains("upscale", StringComparison.OrdinalIgnoreCase));
        tools.Single(t => t.Name == ToolShoot).InputSchema["properties"]!["retryUpscaleOfVersionId"]!["description"]!
            .GetValue<string>().Should().Contain("Не указывай без просьбы человека");
    }
}
