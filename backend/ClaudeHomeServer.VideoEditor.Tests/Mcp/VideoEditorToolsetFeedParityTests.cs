using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// МАТРИЧНЫЙ ТЕСТ требования Андрея (2026-10-02): каждый инструмент video_* даёт в ленте те же записи, что та же
// операция через REST-ручку человека. Сравнивается набор записей (recordType + data с замаскированными id и
// initiator); отличаться может только initiator — человек против агента. Ручки здесь — настоящие контроллеры
// модуля поверх тех же сервисов, что у тулсета: если инструмент обойдёт сервис и запишет ленту сам (или другим
// recordType), набор разойдётся и тест покраснеет.
public sealed class VideoEditorToolsetFeedParityTests
{
    private sealed record Ctx(string SceneId = "", string VersionId = "", string FilmPath = "", string Revision = "");

    private sealed record Case(
        string Name,
        Func<AgentWorld, Task<Ctx>> Prepare,
        Func<AgentWorld, Ctx, Task> Rest,
        Func<AgentWorld, Ctx, Task> Tool,
        bool Blocking = false);

    private static void Ok(IActionResult result)
    {
        var status = (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode ?? 200;
        status.Should().BeLessThan(300, "ручка человека отработала: " + (result as ObjectResult)?.Value);
    }

    private static async Task ToolOk(AgentWorld w, string tool, JsonObject args)
    {
        var result = await w.Call(tool, args);
        result.IsError.Should().BeFalse($"{tool}: {result.Text}");
    }

    private static readonly FilmPatchOp ExtraAdd = new(FilmPatchOps.Add, "video/утро/extra.mp4", Trim: [0, 3]);

    private static async Task<Ctx> SavedSceneAsync(AgentWorld w, bool extraClip = false)
    {
        var (scene, version) = w.F.SceneWithClip();
        // Сохранённая сцена — это уже файл фильма; для правки и сборки готовим его заранее одинаково в обоих мирах
        var saved = await w.Saver.SaveAsync(w.Owner, w.F.Scope,
            new SaveSceneRequest(w.Chat, scene.SceneId, version.VersionId, null, null), VideoInitiators.Human, default);
        saved.IsOk.Should().BeTrue(saved.Error);
        if (extraClip) w.F.WriteFile("video/утро/extra.mp4");
        var film = "video/утро/утро.film";
        return new Ctx(scene.SceneId, version.VersionId, film, w.F.Service.State(w.Owner, w.F.Scope, film).Value!.Revision);
    }

    private static readonly Case[] Cases =
    [
        new("video_new",
            _ => Task.FromResult(new Ctx()),
            async (w, _) => Ok(await w.Rest.AddScene(w.ProjectId, w.Chat, new VideoSceneCreateRequest("video/утро", null, "Сцена А", 0), default)),
            async (w, _) => await ToolOk(w, VideoEditorToolset.ToolNew, new JsonObject { ["folder"] = "video/утро", ["name"] = "Сцена А" })),

        new("video_focus",
            w =>
            {
                var scene = w.NewScene();
                w.F.Threads.SetFocus(w.Owner, w.Chat, new VideoFocusDto(null, null), null);
                return Task.FromResult(new Ctx(scene.SceneId));
            },
            async (w, c) => Ok(await w.Rest.Focus(w.ProjectId, w.Chat,
                new VideoSceneFocusRequest(new VideoFocusDto(c.SceneId, null), w.F.Threads.Get(w.Owner, w.Chat).Revision))),
            async (w, c) => await ToolOk(w, VideoEditorToolset.ToolFocus, new JsonObject { ["sceneId"] = c.SceneId })),

        new("video_scene_set",
            w => Task.FromResult(new Ctx(w.NewScene().SceneId)),
            async (w, c) =>
            {
                var scene = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single();
                Ok(await w.Rest.Settings(w.ProjectId, w.Chat, c.SceneId,
                    new VideoSceneSettingsRequest(scene.Settings with { Text = "новый текст" }, w.F.Threads.Get(w.Owner, w.Chat).Revision)));
            },
            async (w, c) => await ToolOk(w, VideoEditorToolset.ToolSceneSet, new JsonObject { ["sceneId"] = c.SceneId, ["text"] = "новый текст" })),

        new("video_shoot",
            w => Task.FromResult(new Ctx(w.NewScene().SceneId)),
            async (w, c) =>
            {
                var quote = (await w.Jobs.QuoteAsync(w.Owner, w.F.Scope, new VideoQuoteRequest(w.Chat, c.SceneId, null, null, null, null, null, null), default)).Value!;
                Ok(await w.Rest.Start(w.ProjectId, new VideoLaunchRequest(quote.QuoteId, w.Chat, c.SceneId), default));
                await w.WaitJobsAsync();
            },
            async (w, c) =>
            {
                await ToolOk(w, VideoEditorToolset.ToolShoot, new JsonObject { ["sceneId"] = c.SceneId });
                await w.WaitJobsAsync();
            }),

        new("video_cancel",
            async w =>
            {
                var scene = w.NewScene();
                var quote = (await w.Jobs.QuoteAsync(w.Owner, w.F.Scope, new VideoQuoteRequest(w.Chat, scene.SceneId, null, null, null, null, null, null), default)).Value!;
                var started = await w.Jobs.StartAsync(w.Owner, w.F.Scope, new VideoLaunchRequest(quote.QuoteId, w.Chat, scene.SceneId, VideoInitiators.Human), default);
                return new Ctx(scene.SceneId, VersionId: started.Value!.JobId);
            },
            async (w, c) =>
            {
                Ok(await w.Rest.Cancel(w.ProjectId, c.VersionId, default));
                await w.WaitJobsAsync();
            },
            async (w, c) =>
            {
                await ToolOk(w, VideoEditorToolset.ToolCancel, new JsonObject { ["jobId"] = c.VersionId });
                await w.WaitJobsAsync();
            },
            Blocking: true),

        new("video_save_scene",
            w =>
            {
                var (scene, version) = w.F.SceneWithClip();
                return Task.FromResult(new Ctx(scene.SceneId, version.VersionId));
            },
            async (w, c) => Ok(await w.RestFilms.SaveScene(w.ProjectId, w.Chat, c.SceneId,
                new SaveSceneRequest(w.Chat, c.SceneId, c.VersionId, null, null), default)),
            async (w, c) => await ToolOk(w, VideoEditorToolset.ToolSaveScene,
                new JsonObject { ["sceneId"] = c.SceneId, ["versionId"] = c.VersionId })),

        new("video_film_edit",
            w => SavedSceneAsync(w, extraClip: true),
            async (w, c) => Ok(await w.RestFilms.Patch(w.ProjectId, c.FilmPath, new FilmPatch(c.Revision, [ExtraAdd]), default, w.Chat)),
            async (w, c) => await ToolOk(w, VideoEditorToolset.ToolFilmEdit, new JsonObject
            {
                ["path"] = c.FilmPath,
                ["revision"] = c.Revision,
                ["ops"] = new JsonArray(new JsonObject { ["op"] = "add", ["file"] = "video/утро/extra.mp4", ["trim"] = new JsonArray(0, 3) }),
            })),

        new("video_film_build",
            w => SavedSceneAsync(w),
            async (w, c) =>
            {
                Ok(w.RestFilms.Build(w.ProjectId, c.FilmPath, w.Chat));
                await w.Assembler.WhenIdleAsync();
            },
            async (w, c) =>
            {
                await ToolOk(w, VideoEditorToolset.ToolFilmBuild, new JsonObject { ["path"] = c.FilmPath });
                await w.Assembler.WhenIdleAsync();
            }),
    ];

    public static TheoryData<string> Names => new(Cases.Select(c => c.Name));

    // Записи ленты одного действия: recordType и data, где id и пути стенда заменены метками, а initiator
    // вынесен отдельно
    private sealed record Rec(string RecordType, string Data, string? Initiator, string Fallback);

    private static async Task<(List<Rec> Feed, string State)> RunAsync(Case c, bool agent)
    {
        using var w = new AgentWorld(c.Blocking);
        var ctx = await c.Prepare(w);
        w.F.Feed.Records.Clear();
        await (agent ? c.Tool(w, ctx) : c.Rest(w, ctx));

        var state = w.F.Threads.Get(w.Owner, w.Chat);
        var ids = new Dictionary<string, string>();
        foreach (var scene in state.Scenes)
        {
            ids[scene.SceneId] = "<scene>";
            foreach (var v in scene.Versions) ids[v.VersionId] = "<version>";
            foreach (var l in scene.Launches) ids[l.JobId] = "<job>";
        }
        if (ctx.VersionId.Length > 0) ids[ctx.VersionId] = ids.GetValueOrDefault(ctx.VersionId, "<version>");
        string Mask(string text)
        {
            text = text.Replace(w.F.Dir, "<dir>", StringComparison.Ordinal);
            foreach (var (id, label) in ids) text = text.Replace(id, label, StringComparison.Ordinal);
            return text;
        }

        var feed = w.F.Feed.Records.Select(r =>
        {
            var data = JsonNode.Parse(r.Record.Data!.Value.GetRawText())!.AsObject();
            var initiator = (data["initiator"] as JsonValue)?.GetValue<string>();
            data.Remove("initiator");
            return new Rec(r.Record.RecordType, Mask(data.ToJsonString()), initiator, r.Record.Fallback ?? "");
        }).ToList();
        // Состояние нитей без идентификаторов, времени и инициатора запуска: ручка и инструмент оставляют сцене одно и то же
        var snapshot = Mask(System.Text.Json.JsonSerializer.Serialize(state.ToDto().Scenes.Select(s => new
        {
            s.Name, s.Folder, s.Settings, versions = s.Versions.Select(v => new { v.Number, v.Provider, v.Model }),
            launches = s.Launches.Select(l => new { l.Status, l.Provider, l.Model, l.Count }), s.SavedFiles.Count,
        })) + System.Text.Json.JsonSerializer.Serialize(state.Focus));
        return (feed, snapshot);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Инструмент_пишет_в_ленту_то_же_что_ручка_человека(string name)
    {
        var c = Cases.Single(x => x.Name == name);

        var (human, humanState) = await RunAsync(c, agent: false);
        var (agent, agentState) = await RunAsync(c, agent: true);

        // Фокус, настройки сцены и отмена в ленту не пишут (отмена видна в состоянии нитей — ниже); остальные действия обязаны (иначе сравнение пустого с пустым — вакуум)
        if (name is not (VideoEditorToolset.ToolFocus or VideoEditorToolset.ToolSceneSet or VideoEditorToolset.ToolCancel))
            human.Should().NotBeEmpty($"{name}: ручка человека пишет в ленту");
        agent.Select(r => r.RecordType).Should().Equal(human.Select(r => r.RecordType),
            $"{name}: агент пишет теми же recordType, что человек");
        agent.Select(r => r.Data).Should().Equal(human.Select(r => r.Data),
            $"{name}: данные записей (ссылки на сцену, нить и фильм) совпадают с ручкой");
        agentState.Should().Be(humanState, $"{name}: состояние нитей после действия то же");
        if (name == VideoEditorToolset.ToolCancel) agentState.Should().Contain("\"Status\":\"cancelled\"", "отмена дошла до сцены");
        // Различается только initiator — и только там, где запись его несёт
        foreach (var (h, a) in human.Zip(agent))
        {
            if (h.Initiator is null) a.Initiator.Should().BeNull();
            else
            {
                h.Initiator.Should().Be(VideoInitiators.Human);
                a.Initiator.Should().BeOneOf(VideoInitiators.Agent, VideoInitiators.Human);
            }
        }
    }

    [Theory]
    [InlineData("video_shoot")]
    [InlineData("video_save_scene")]
    [InlineData("video_film_edit")]
    [InlineData("video_film_build")]
    public async Task У_тулсета_запись_агента_несёт_initiator_agent_а_ручки_человека(string name)
    {
        var c = Cases.Single(x => x.Name == name);

        var (human, _) = await RunAsync(c, agent: false);
        var (agent, _) = await RunAsync(c, agent: true);

        human.Where(r => r.Initiator is not null).Should().OnlyContain(r => r.Initiator == VideoInitiators.Human);
        agent.Where(r => r.Initiator is not null).Should().NotBeEmpty($"{name}: хотя бы одна запись несёт initiator");
        agent.Where(r => r.Initiator is not null).Should().OnlyContain(r => r.Initiator == VideoInitiators.Agent);
    }

    [Fact]
    public void Матрица_покрывает_все_пишущие_и_тратящие_инструменты()
    {
        var covered = Cases.Select(c => c.Name).ToHashSet();

        covered.Should().BeEquivalentTo(VideoEditorToolset.Schemas.Select(s => s.Name)
            .Where(n => n != VideoEditorToolset.ToolState && n != VideoEditorToolset.ToolSuggestPrompt && n != VideoEditorToolset.ToolWait),
            "каждый инструмент, который что-то меняет, обязан быть в матрице");
    }
}
