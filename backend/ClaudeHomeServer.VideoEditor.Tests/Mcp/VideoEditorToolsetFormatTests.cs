using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// Формат у агента (ADR-025, Б2): схемы статичны и с оговоркой NotWithoutAsk, явный формат пишется в префы с ✦ и тихой
// строкой, замок формата фильма держит сервер — ручка человека и инструмент получают один и тот же отказ
public sealed class VideoEditorToolsetFormatTests
{
    private const string NotWithoutAsk = "Не указывай без просьбы человека";

    private static string Description(AgentWorld w, string tool, string arg) =>
        w.Toolset.ToolsFor(w.Ctx()).Single(t => t.Name == tool).InputSchema["properties"]![arg]!["description"]!.GetValue<string>();

    // ── Схемы ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ToolNew, "aspect")]
    [InlineData(ToolNew, "durationSec")]
    [InlineData(ToolNew, "quality")]
    [InlineData(ToolNew, "anchor")]
    [InlineData(ToolSceneSet, "aspect")]
    [InlineData(ToolSceneSet, "durationSec")]
    [InlineData(ToolSceneSet, "quality")]
    [InlineData(ToolShoot, "aspect")]
    [InlineData(ToolShoot, "durationSec")]
    [InlineData(ToolShoot, "quality")]
    public void Format_arguments_carry_not_without_ask_in_description(string tool, string arg)
    {
        using var w = new AgentWorld();

        Description(w, tool, arg).Should().Contain(NotWithoutAsk);
    }

    [Theory]
    [InlineData(ToolNew)]
    [InlineData(ToolSceneSet)]
    [InlineData(ToolShoot)]
    public void Quality_is_a_static_enum_without_model_specific_values(string tool)
    {
        using var w = new AgentWorld();

        var schema = w.Toolset.ToolsFor(w.Ctx()).Single(t => t.Name == tool).InputSchema["properties"]!["quality"]!;

        schema["enum"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("standard", "high", "max");
    }

    [Fact]
    public void Describe_state_lists_format_rule_and_h3_max_note_without_changing_tool_schemas()
    {
        using var w = new AgentWorld();
        var before = string.Join("\n", w.Toolset.ToolsFor(w.Ctx()).Select(t => t.InputSchema.ToJsonString()));

        var state = AgentWorld.Parse(w.Call(ToolState).Result);
        var rule = state["humanChoice"]!["format"]!.GetValue<string>();

        rule.Should().Contain("MiniMax H3").And.Contain("1440p").And.Contain("увеличение").And.Contain("resolution");
        string.Join("\n", w.Toolset.ToolsFor(w.Ctx()).Select(t => t.InputSchema.ToJsonString())).Should().Be(before, "схемы статичны");
    }

    // ── Запись формата ✦ ───────────────────────────────────────────────────────

    [Fact]
    public async Task Explicit_aspect_and_quality_in_shoot_write_agent_format_to_prefs_with_quiet_line_once()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "16:9", ["quality"] = "high" });

        result.IsError.Should().BeFalse(result.Text);
        var prefs = w.Prefs.Get(w.Owner, w.F.Scope);
        prefs.Format.Should().Match<MediaFormat>(f => f.Mode == FormatModes.Aspect && f.Aspect == "16:9" && f.Tier == FormatTier.High
            && f.By == ContextActor.Agent);
        prefs.Aspect.Should().Be("16:9", "Aspect — зеркало Format.Aspect");
        var lines = w.F.Feed.Records.Select(r => r.Record.Fallback).Where(t => t.Contains("сменил формат")).ToList();
        lines.Should().ContainSingle().Which.Should().Be("✦ Claude сменил формат сцены «Сцена 1» и видео проекта: 16:9 → 16:9 · Высокое");
        // Формат ложится и в саму сцену, а не только в запуск и префы: на чипе и в video_state он виден
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings.Format.Should().Match<MediaFormat>(f =>
            f.Aspect == "16:9" && f.Tier == FormatTier.High && f.By == ContextActor.Agent);

        await w.WaitJobsAsync();
        // Тот же формат повторно — строки нет
        await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "16:9", ["quality"] = "high" });
        await w.WaitJobsAsync();
        w.F.Feed.Records.Count(r => r.Record.Fallback.Contains("сменил формат")).Should().Be(1);
    }

    [Fact]
    public async Task Shoot_writes_format_to_scene_even_when_prefs_already_have_it()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();
        w.Prefs.Save(w.Owner, w.F.Scope, w.Prefs.Get(w.Owner, w.F.Scope) with
        {
            Format = new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.High, null, null, ContextActor.Human, DateTime.UtcNow),
            Aspect = "9:16",
        }, ContextActor.Human);

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16", ["quality"] = "high" });
        await w.WaitJobsAsync();

        result.IsError.Should().BeFalse(result.Text);
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings.Format.Should().Match<MediaFormat>(f =>
            f.Aspect == "9:16" && f.Tier == FormatTier.High && f.By == ContextActor.Agent);
        w.F.Feed.Records.Select(r => r.Record.Fallback).Should().ContainSingle(t => t.Contains("сменил формат сцены"));
    }

    [Fact]
    public async Task New_scene_inherits_prefs_format_without_agent_mark()
    {
        using var w = new AgentWorld();
        var human = new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.High, null, null, ContextActor.Human, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        w.Prefs.Save(w.Owner, w.F.Scope, w.Prefs.Get(w.Owner, w.F.Scope) with { Format = human, Aspect = "9:16" }, ContextActor.Human);

        var result = await w.Call(ToolNew, new JsonObject { ["text"] = "закат" });

        result.IsError.Should().BeFalse(result.Text);
        var format = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings.Format;
        format.Should().NotBeNull();
        format!.By.Should().Be(ContextActor.Human, "формат унаследован из префов человека: ✦ агента здесь ложь");
        format.At.Should().Be(w.Prefs.Get(w.Owner, w.F.Scope).Format!.At, "время тоже из префов, а не штамп этой правки");
    }

    [Fact]
    public async Task Shoot_without_format_arguments_does_not_touch_prefs_and_writes_no_line()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();

        w.Prefs.Get(w.Owner, w.F.Scope).Format.Should().BeNull();
        w.F.Feed.Records.Should().NotContain(r => r.Record.Fallback.Contains("сменил формат"));
    }

    [Fact]
    public async Task New_with_aspect_names_scene_and_project_in_quiet_line()
    {
        using var w = new AgentWorld();

        var result = await w.Call(ToolNew, new JsonObject { ["aspect"] = "16:9" });

        result.IsError.Should().BeFalse(result.Text);
        w.F.Feed.Records.Select(r => r.Record.Fallback).Where(t => t.Contains("сменил формат")).Should().ContainSingle()
            .Which.Should().Match("✦ Claude сменил формат сцены «*» и видео проекта: *");
    }

    [Fact]
    public async Task Scene_set_with_aspect_writes_scene_wish_by_agent_and_mirrors_aspect()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        var result = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16", ["quality"] = "max" });

        result.IsError.Should().BeFalse(result.Text);
        var settings = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings;
        settings.Format.Should().Match<MediaFormat>(f => f.Aspect == "9:16" && f.Tier == FormatTier.Max && f.By == ContextActor.Agent);
        settings.Aspect.Should().Be("9:16");
    }

    // Регрессия круга 3: сцена без выбранной модели (caps == null) показывала агенту и хвосту «как у модели — не задан»
    [Fact]
    public async Task Scene_set_aspect_shows_agent_wish_in_dto_state_tail_and_names_the_scene_in_quiet_line()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();
        w.F.Threads.SetSettings(w.Owner, w.Chat, scene.SceneId, scene.Settings with
        {
            Format = new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.Max, null, null, ContextActor.Human, DateTime.UtcNow), Aspect = "9:16",
        }, null);

        var result = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "16:9" });

        result.IsError.Should().BeFalse(result.Text);
        var settings = w.F.Threads.Get(w.Owner, w.Chat).ToDto().Scenes.Single().Settings;
        settings.Format.Should().Match<MediaFormat>(f => f.Aspect == "16:9" && f.Tier == FormatTier.Max && f.By == ContextActor.Agent);
        settings.Aspect.Should().Be("16:9");

        var sceneJson = AgentWorld.Parse(await w.Call(ToolState))["scenes"]![0]!["settings"]!["format"]!;
        sceneJson["aspect"]!.GetValue<string>().Should().Be("16:9");
        sceneJson["by"]!.ToString().Should().Be("agent", "в video_state формат сцены с пометкой агента");

        var session = new ClaudeHomeServer.Models.Session { Id = w.Chat, OwnerId = w.Owner };
        var item = new ContextItem("i", "video-scene", new JsonObject { ["sceneId"] = scene.SceneId }, null, ContextActor.Human, DateTime.UtcNow);
        var tail = new ClaudeHomeServer.Services.VideoEditor.ChatContext.VideoContextKind(w.F.Threads)
            .DescribeFormat(new ContextScope(w.Owner, session, null), item);
        tail.Should().Contain("16:9").And.Contain("✦ поставил Claude").And.NotContain("не задан")
            .And.Contain("16:9 · Макс (модель не выбрана — формат применится, когда выберете модель) — ✦ поставил Claude");

        w.F.Feed.Records.Select(r => r.Record.Fallback).Where(t => t.Contains("сменил формат")).Should().ContainSingle()
            .Which.Should().Be("✦ Claude сменил формат сцены «Сцена 1» и видео проекта: 9:16 · Макс → 16:9 · Макс");
    }

    [Fact]
    public async Task Quality_without_aspect_and_bad_values_are_refused_with_hint()
    {
        using var w = new AgentWorld();
        var plain = w.F.Threads.AddScene(w.Owner, w.Chat, "video/утро",
            new VideoSceneSettingsDto(null, null, "т", null, null, 5, null, false, 1), null, "Без пропорций").Scene!;

        var noAspect = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = plain.SceneId, ["quality"] = "high" });
        var badQuality = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = plain.SceneId, ["aspect"] = "16:9", ["quality"] = "ultra" });
        var badAspect = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = plain.SceneId, ["aspect"] = "широко" });

        noAspect.IsError.Should().BeTrue();
        noAspect.Text.Should().Contain("aspect");
        badQuality.Text.Should().Contain("standard");
        badAspect.Text.Should().Contain("пропорции");
    }

    // ── Замок формата фильма ───────────────────────────────────────────────────

    private static (VideoSceneDto Scene, VideoFormatLockDto Lock) LockedScene(AgentWorld w)
    {
        var scene = w.NewScene();
        var locked = new VideoFormatLockDto("video/утро/утро.film", "16:9");
        w.F.Threads.SetFilmRef(w.Owner, w.Chat, scene.SceneId, new VideoFilmRefDto(locked.FilmPath, 1), locked);
        return (scene, locked);
    }

    [Fact]
    public async Task Lock_refuses_other_aspect_for_agent_and_rest_alike_and_force_overrides()
    {
        using var w = new AgentWorld();
        var (scene, locked) = LockedScene(w);

        // Агент: отказ с кодом и текстом из записки
        var byAgent = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16" });
        byAgent.IsError.Should().BeTrue();
        var agentJson = AgentWorld.Parse(byAgent);
        agentJson["code"]!.GetValue<string>().Should().Be(VideoEditorErrors.FilmFormatLocked);
        agentJson["error"]!.GetValue<string>().Should().Contain("Фильм «утро» — 16:9").And.Contain("9:16").And.Contain("force");

        // Ручка человека: 409 с тем же кодом и тем же текстом
        var current = w.F.Threads.Get(w.Owner, w.Chat);
        var rest = await w.Rest.Settings(w.ProjectId, w.Chat, scene.SceneId,
            new VideoSceneSettingsRequest(current.Scenes.Single().Settings with { Aspect = "9:16" }, current.Revision));
        var conflict = rest.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        var body = conflict.Value.Should().BeOfType<VideoFormatLockedError>().Subject;
        body.Code.Should().Be(VideoEditorErrors.FilmFormatLocked);
        body.LockedBy.Should().Be(locked);
        agentJson["error"]!.GetValue<string>().Should().StartWith(body.Error, "текст отказа один и тот же");

        // Ничего не записано
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings.Aspect.Should().Be("16:9");

        // force — «Всё равно 9:16»
        var forced = await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16", ["force"] = true });
        forced.IsError.Should().BeFalse(forced.Text);
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings.Aspect.Should().Be("9:16");
        var described = AgentWorld.Parse(await w.Call(ToolState))["scenes"]!.AsArray().Single()!;
        described["formatMismatch"]!.GetValue<bool>().Should().BeTrue();
        described["lockedBy"]!["aspect"]!.GetValue<string>().Should().Be("16:9", "агент видит замок в video_state");
    }

    [Fact]
    public async Task Lock_allows_the_same_aspect_and_changes_that_do_not_touch_aspect()
    {
        using var w = new AgentWorld();
        var (scene, _) = LockedScene(w);

        (await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "16:9", ["quality"] = "high" }))
            .IsError.Should().BeFalse();
        (await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["text"] = "новый текст" })).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Lock_also_stops_shoot_with_other_aspect_before_any_money()
    {
        using var w = new AgentWorld();
        var (scene, _) = LockedScene(w);

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16" });

        result.IsError.Should().BeTrue();
        AgentWorld.Parse(result)["code"]!.GetValue<string>().Should().Be(VideoEditorErrors.FilmFormatLocked);
        w.Engine.Runs.Should().Be(0);
        w.Spend.Records.Should().BeEmpty();
        w.Prefs.Get(w.Owner, w.F.Scope).Format.Should().BeNull("формат проекта при отказе не меняется");
    }

    [Fact]
    public async Task Scene_without_lock_changes_aspect_freely()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        (await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "9:16" })).IsError.Should().BeFalse();
    }
}
