using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using ClaudeHomeServer.Services.VideoEditor;
using FluentAssertions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// Инструменты агента для видео (ADR-022 §5): состав не зависит от хода, фокуса и вида чата; делегированный ход —
// отказ fail-closed на всех тратящих и пишущих инструментах; запись агента — только video/** и music/**, только
// CreateNew; траты — с Initiator = Agent; лимита запусков за ход нет
public sealed class VideoEditorToolsetTests
{
    private static readonly string[] Gated = [ToolShoot, ToolCancel, ToolSaveScene, ToolFilmEdit, ToolFilmBuild];

    private static string[] ProjectFiles(AgentWorld w) =>
        [.. Directory.GetFiles(w.F.ProjectRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(w.F.ProjectRoot, f).Replace('\\', '/')).Order()];

    // ── Состав ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Состав_ровно_десять_инструментов_и_ровно_список_автодопуска()
    {
        using var w = new AgentWorld();

        var names = w.Toolset.ToolsFor(w.Ctx()).Select(t => t.Name).ToList();

        names.Should().BeEquivalentTo([ToolState, ToolFocus, ToolNew, ToolSceneSet, ToolSuggestPrompt, ToolShoot, ToolCancel,
            ToolSaveScene, ToolFilmEdit, ToolFilmBuild]);
        names.Select(n => $"mcp__{ServerName}__{n}").Should().BeEquivalentTo(
            ClaudeHomeServer.Services.VideoEditor.VideoEditorAgentTools.AutoAllowTools);
    }

    [Fact]
    public void Без_запуска_агентом_остаются_только_инструменты_без_трат_и_записи_в_проект()
    {
        using var w = new AgentWorld(agentLaunch: false);

        w.Toolset.ToolsFor(w.Ctx()).Select(t => t.Name).Should().BeEquivalentTo(
            [ToolState, ToolFocus, ToolNew, ToolSceneSet, ToolSuggestPrompt]);
    }

    [Fact]
    public async Task Состав_не_зависит_от_сцен_фокуса_запусков_и_вида_чата()
    {
        using var w = new AgentWorld();
        string Snapshot(string chat) => string.Join("\n",
            w.Toolset.ToolsFor(w.Ctx(chat)).Select(t => t.Name + t.Description + t.InputSchema.ToJsonString()));
        var before = Snapshot(w.Chat);

        var scene = w.NewScene();
        (await w.Call(ToolFocus, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();

        Snapshot(w.Chat).Should().Be(before, "сцены, фокус и запуски состав не меняют");
        Snapshot(AgentWorld.PersonalChat).Should().Be(before, "личный и проектный чат видят один и тот же состав");
    }

    [Fact]
    public async Task Чужой_чат_чужой_владелец_и_выключенный_флаг_дают_пустой_состав_и_отказ()
    {
        using var w = new AgentWorld();
        w.Toolset.ToolsFor(w.Ctx(owner: "stranger")).Should().BeEmpty();
        w.Toolset.ToolsFor(w.Ctx(chat: "no-such-chat")).Should().BeEmpty();
        (await w.Toolset.CallAsync(ToolState, new JsonObject(), w.Ctx(owner: "stranger"), default)).IsError.Should().BeTrue();

        using var off = new AgentWorld(flagOn: false);
        off.Toolset.ToolsFor(off.Ctx()).Should().BeEmpty("флаг video-editor выключен у владельца");
    }

    // ── Делегированный ход ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ToolShoot)]
    [InlineData(ToolCancel)]
    [InlineData(ToolSaveScene)]
    [InlineData(ToolFilmEdit)]
    [InlineData(ToolFilmBuild)]
    public async Task Делегированный_ход_получает_отказ_на_пишущих_и_тратящих_и_ничего_не_меняется(string tool)
    {
        using var w = new AgentWorld(delegatedDenied: true);
        var (scene, version) = w.F.SceneWithClip();
        var args = new JsonObject
        {
            ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId, ["jobId"] = "j1",
            ["path"] = "video/утро/утро.film", ["revision"] = "r", ["ops"] = new JsonArray(new JsonObject { ["op"] = "remove", ["index"] = 0 }),
        };
        var filesBefore = ProjectFiles(w);
        w.F.Feed.Records.Clear();

        var result = await w.Call(tool, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("делегированный ход");
        w.Engine.Runs.Should().Be(0, "съёмка не стартовала");
        ProjectFiles(w).Should().Equal(filesBefore, "ничего не записано в проект");
        w.F.Feed.Records.Should().BeEmpty("в ленту ничего не легло");
    }

    [Theory]
    [InlineData(ToolShoot)]
    [InlineData(ToolCancel)]
    [InlineData(ToolSaveScene)]
    [InlineData(ToolFilmEdit)]
    [InlineData(ToolFilmBuild)]
    public async Task Нет_шва_гейта_хода_значит_отказ_по_построению(string tool)
    {
        using var w = new AgentWorld(withGate: false);
        var scene = w.NewScene();

        var result = await w.Call(tool, new JsonObject { ["sceneId"] = scene.SceneId, ["jobId"] = "j", ["path"] = "video/a.film" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("отказ по построению");
        w.Engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Гейт_стоит_на_всех_пяти_инструментах_и_ровно_на_них()
    {
        using var w = new AgentWorld(delegatedDenied: true);
        var scene = w.NewScene();

        foreach (var tool in Gated)
            (await w.Call(tool, new JsonObject { ["sceneId"] = scene.SceneId, ["jobId"] = "j", ["path"] = "video/a.film" }))
                .Text.Should().Contain("делегированный ход", $"{tool} обязан идти через IDelegatedTurnGate");
        foreach (var tool in new[] { ToolState, ToolFocus, ToolNew, ToolSceneSet, ToolSuggestPrompt })
            (await w.Call(tool, new JsonObject { ["sceneId"] = scene.SceneId, ["prompt"] = "x" }))
                .Text.Should().NotContain("делегированный ход", $"{tool} ничего не тратит и не пишет в проект");
    }

    // ── Съёмка ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Съёмка_без_sceneId_отказывает_до_гейта_и_запуска()
    {
        using var w = new AgentWorld();
        w.NewScene();

        var result = await w.Call(ToolShoot);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("sceneId");
        w.Engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Съёмка_идёт_строго_в_указанную_сцену_и_даёт_версию()
    {
        using var w = new AgentWorld();
        var first = w.NewScene(name: "Первая");
        var second = w.NewScene(name: "Вторая");   // она в фокусе, но снимаем первую

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = first.SceneId });
        await w.WaitJobsAsync();

        result.IsError.Should().BeFalse(result.Text);
        var scenes = w.F.Threads.Get(w.Owner, w.Chat).Scenes;
        scenes.Single(s => s.SceneId == first.SceneId).Versions.Should().HaveCount(1);
        scenes.Single(s => s.SceneId == second.SceneId).Versions.Should().BeEmpty();
    }

    [Fact]
    public async Task Траты_агента_идут_с_Initiator_Agent_и_меткой_с_эндпоинтом_и_секундами()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();

        var spend = w.Spend.Records.Should().ContainSingle().Subject;
        spend.Initiator.Should().Be(SpendInitiators.Agent);
        spend.OwnerId.Should().Be(w.Owner);
        spend.Label.Should().Be("fal-model · 5 с");
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single().Initiator.Should().Be(VideoInitiators.Agent);
    }

    [Fact]
    public async Task Лимита_запусков_за_ход_нет_три_съёмки_подряд_проходят()
    {
        using var w = new AgentWorld();
        for (var i = 0; i < 3; i++)
        {
            var scene = w.NewScene(name: "Сцена " + i);
            var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId });
            result.IsError.Should().BeFalse($"съёмка {i + 1}: потолка трат и лимита хода у видео нет — {result.Text}");
            await w.WaitJobsAsync();
        }

        w.Engine.Runs.Should().Be(3);
        w.Spend.Records.Should().HaveCount(3);
    }

    [Fact]
    public async Task Неизвестный_ключ_params_отказывает_с_именем_поля_до_запуска()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        var result = await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["params"] = new JsonObject { ["fancy"] = 1 } });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("fancy");
        w.Engine.Runs.Should().Be(0);
        w.Spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Чужая_сцена_и_чужая_задача_неотличимы_от_несуществующих()
    {
        using var w = new AgentWorld();

        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = "нет-такой" })).Text.Should().Contain("нет в этом чате");
        (await w.Call(ToolCancel, new JsonObject { ["jobId"] = "нет-такой" })).Text.Should().Contain("не найдена");
    }

    // ── Сохранение и фильмы ────────────────────────────────────────────────────

    [Fact]
    public async Task Сохранение_агентом_кладёт_клип_в_video_только_CreateNew_и_помечает_Claude()
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        var args = new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId };

        var first = await w.Call(ToolSaveScene, args);
        var second = await w.Call(ToolSaveScene, args);

        first.IsError.Should().BeFalse(first.Text);
        AgentWorld.Parse(first)["path"]!.GetValue<string>().Should().Be("video/утро/scene-01.mp4");
        AgentWorld.Parse(second)["path"]!.GetValue<string>().Should().Be("video/утро/scene-01.v2.mp4", "перезаписи нет");
        ProjectFiles(w).Should().Contain(["video/утро/scene-01.mp4", "video/утро/scene-01.v2.mp4", "video/утро/утро.film"]);
        ProjectFiles(w).Should().OnlyContain(f => f.StartsWith("video/") || f.StartsWith("music/"));
        w.F.Service.State(w.Owner, w.F.Scope, "video/утро/утро.film").Value!.Marks.Should().Contain(m => m.Claude,
            "строка фильма, добавленная агентом, помечена «✦ Claude»");
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("src/video")]
    [InlineData("../outside")]
    [InlineData("video/../docs")]
    public async Task Запись_агента_вне_video_и_music_отказывает_и_ничего_не_пишет(string folder)
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        var before = ProjectFiles(w);

        var result = await w.Call(ToolSaveScene,
            new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId, ["folder"] = folder });

        result.IsError.Should().BeTrue(result.Text);
        result.Text.Should().Contain(VideoEditorErrors.OutsideAllowedFolders);
        ProjectFiles(w).Should().Equal(before);
        Directory.GetFiles(Path.GetDirectoryName(w.F.ProjectRoot)!, "*.mp4", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public async Task Правка_фильма_без_ревизии_отказывает_а_устаревшая_ревизия_даёт_конфликт_со_свежим_состоянием()
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        await w.Call(ToolSaveScene, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId });
        w.F.WriteFile("video/утро/extra.mp4");
        var ops = new JsonArray(new JsonObject { ["op"] = "add", ["file"] = "video/утро/extra.mp4", ["trim"] = new JsonArray(0, 3) });

        var noRevision = await w.Call(ToolFilmEdit, new JsonObject { ["path"] = "video/утро/утро.film", ["ops"] = ops.DeepClone() });
        var stale = await w.Call(ToolFilmEdit,
            new JsonObject { ["path"] = "video/утро/утро.film", ["revision"] = "устарела", ["ops"] = ops.DeepClone() });

        noRevision.IsError.Should().BeTrue();
        noRevision.Text.Should().Contain("revision");
        stale.IsError.Should().BeTrue();
        AgentWorld.Parse(stale)["code"]!.GetValue<string>().Should().Be(VideoEditorErrors.RevisionConflict);
        AgentWorld.Parse(stale)["state"].Should().NotBeNull("свежее состояние в ответе");
    }

    [Fact]
    public async Task Неизвестное_поле_операции_патча_отказывает_с_именем_поля()
    {
        using var w = new AgentWorld();

        var result = await w.Call(ToolFilmEdit, new JsonObject
        {
            ["path"] = "video/a/a.film", ["revision"] = "r",
            ["ops"] = new JsonArray(new JsonObject { ["op"] = "remove", ["index"] = 0, ["wat"] = true }),
        });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("wat");
    }

    [Theory]
    [InlineData(ToolSaveScene)]
    [InlineData(ToolFilmEdit)]
    [InlineData(ToolFilmBuild)]
    public async Task В_личном_чате_фильмовые_инструменты_есть_но_отвечают_что_фильмы_только_в_проекте(string tool)
    {
        using var w = new AgentWorld();

        w.Toolset.ToolsFor(w.Ctx(AgentWorld.PersonalChat)).Select(t => t.Name).Should().Contain(tool);
        var result = await w.Call(tool, new JsonObject { ["sceneId"] = "s", ["path"] = "video/a.film", ["revision"] = "r",
            ["ops"] = new JsonArray(new JsonObject { ["op"] = "remove", ["index"] = 0 }) }, AgentWorld.PersonalChat);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Фильмы — только в чате проекта");
    }

    [Fact]
    public async Task Сборка_фильма_агентом_принимается_и_собирает_файл()
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        await w.Call(ToolSaveScene, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId });

        var result = await w.Call(ToolFilmBuild, new JsonObject { ["path"] = "video/утро/утро.film" });
        await w.Assembler.WhenIdleAsync();

        result.IsError.Should().BeFalse(result.Text);
        ProjectFiles(w).Should().Contain("video/утро/film.mp4");
    }

    // ── Сцены, фокус, состояние ────────────────────────────────────────────────

    [Fact]
    public async Task Новая_сцена_берёт_префы_настройки_и_встаёт_в_фокус()
    {
        using var w = new AgentWorld();
        w.Prefs.Save(w.Owner, w.F.Scope, new VideoPrefsDto("fal", "fal-model", 8, null, null, 2));

        var result = await w.Call(ToolNew, new JsonObject { ["text"] = "кот на подоконнике", ["name"] = "Кот" });

        result.IsError.Should().BeFalse(result.Text);
        var scene = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single();
        scene.Name.Should().Be("Кот");
        scene.Settings.Text.Should().Be("кот на подоконнике");
        scene.Settings.DurationSec.Should().Be(8);
        scene.Settings.Count.Should().Be(2);
        w.F.Threads.Get(w.Owner, w.Chat).Focus.SceneId.Should().Be(scene.SceneId);
    }

    [Fact]
    public async Task Папка_сцены_вне_video_и_в_личном_чате_отказывает()
    {
        using var w = new AgentWorld();

        (await w.Call(ToolNew, new JsonObject { ["folder"] = "docs" })).Text.Should().Contain(VideoEditorErrors.OutsideAllowedFolders);
        (await w.Call(ToolNew, new JsonObject { ["folder"] = "video/a" }, AgentWorld.PersonalChat)).IsError.Should().BeTrue();
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Should().BeEmpty();
    }

    [Fact]
    public async Task Настройки_сцены_заменяются_частично_а_кадр_снимается_через_null()
    {
        using var w = new AgentWorld();
        w.F.WriteFile("video/утро/a.png");
        var scene = w.NewScene();

        await w.Call(ToolSceneSet, new JsonObject
        {
            ["sceneId"] = scene.SceneId, ["text"] = "новое",
            ["frameA"] = new JsonObject { ["kind"] = "file", ["path"] = "video/утро/a.png" },
        });
        var withFrame = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings;
        await w.Call(ToolSceneSet, new JsonObject { ["sceneId"] = scene.SceneId, ["frameA"] = null });
        var cleared = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Settings;

        withFrame.FrameA!.Path.Should().Be("video/утро/a.png");
        withFrame.Text.Should().Be("новое");
        withFrame.DurationSec.Should().Be(5, "неуказанное осталось как было");
        cleared.FrameA.Should().BeNull();
        cleared.Text.Should().Be("новое");
    }

    [Fact]
    public async Task Фокус_ставит_сцену_и_версию_а_неизвестное_отказывает()
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        w.NewScene(name: "Другая");

        var ok = await w.Call(ToolFocus, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId });
        var noScene = await w.Call(ToolFocus, new JsonObject { ["sceneId"] = "нет" });
        var noVersion = await w.Call(ToolFocus, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = "нет" });

        ok.IsError.Should().BeFalse(ok.Text);
        w.F.Threads.Get(w.Owner, w.Chat).Focus.SceneId.Should().Be(scene.SceneId);
        noScene.IsError.Should().BeTrue();
        noVersion.IsError.Should().BeTrue();
        noVersion.Text.Should().Contain(version.VersionId, "отказ называет версии сцены");
    }

    [Fact]
    public async Task Состояние_показывает_сцены_каталог_открытый_фильм_и_потрачено()
    {
        using var w = new AgentWorld();
        var (scene, version) = w.F.SceneWithClip();
        await w.Call(ToolSaveScene, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId });
        await w.Call(ToolFocus, new JsonObject { ["filmPath"] = "video/утро/утро.film" });

        var state = AgentWorld.Parse(await w.Call(ToolState));

        state["scenes"]!.AsArray().Should().HaveCount(1);
        state["films"]!.AsArray().Should().HaveCount(1);
        state["film"]!["revision"].Should().NotBeNull("ревизию для video_film_edit агент берёт отсюда");
        state["film"]!["spent"].Should().NotBeNull("«Потрачено на фильм»");
        state["catalog"]!["providers"]!.AsArray().Should().NotBeEmpty();
        state["agentLaunch"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task В_личном_чате_состояние_без_фильмов()
    {
        using var w = new AgentWorld();

        var state = AgentWorld.Parse(await w.Call(ToolState, chat: AgentWorld.PersonalChat));

        state["films"].Should().BeNull();
        state["note"]!.GetValue<string>().Should().Contain("фильмов и файлов проекта тут нет");
    }

    [Fact]
    public async Task Предложение_текста_ничего_не_меняет()
    {
        using var w = new AgentWorld();

        var result = await w.Call(ToolSuggestPrompt, new JsonObject { ["prompt"] = "закат" });

        result.IsError.Should().BeFalse();
        AgentWorld.Parse(result)["prompt"]!.GetValue<string>().Should().Be("закат");
        w.F.Threads.Get(w.Owner, w.Chat).Scenes.Should().BeEmpty();
        (await w.Call(ToolSuggestPrompt, new JsonObject())).IsError.Should().BeTrue();
    }
}
