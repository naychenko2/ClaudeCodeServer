using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Tests.Films;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Moq;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// Агентские «Увеличить ×2/×4» (video_enhance) и «Озвучить сцену» (video_voice): тот же сервис, что у кнопки человека,
// те же отказы, итог — новая версия сцены; гейты как у video_shoot
public sealed class VideoEnhanceVoiceToolsTests
{
    private sealed class FakeVoice : IVideoSceneVoice
    {
        public VideoVoiceRequest? Seen;
        public VideoVoiceStart Result = new(new VideoVoiceStarted("voice-job", "thread-1", "local", "hunyuanvideo-foley-xl", "tencent", 40), null, null);
        public VideoVoiceJob? Job;
        public int Cancels;

        public Task<VideoVoiceStart> StartAsync(VideoVoiceRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(Result);
        }

        public VideoVoiceJob? Get(string ownerId, string scopeKey, string jobId) => Job is { } j && j.JobId == jobId ? j : null;

        public Task<VideoVoiceJob?> CancelAsync(string ownerId, string scopeKey, string jobId, CancellationToken ct)
        {
            Cancels++;
            return Task.FromResult(Get(ownerId, scopeKey, jobId));
        }
    }

    private static VideoVoiceJob VoiceJob(string chat, string status, bool terminal) =>
        new("voice-job", "personal", chat, status, "local", "hunyuanvideo-foley-xl", terminal, terminal ? 1 : 0, null, 30, null);

    private static async Task<(string Scene, string Version)> Shot(AgentWorld w)
    {
        var scene = w.NewScene();
        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId })).IsError.Should().BeFalse();
        await w.WaitJobsAsync();
        return (scene.SceneId, w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single().VersionId);
    }

    // ── video_enhance ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 2)]
    [InlineData(4, 4)]
    public async Task Enhance_запускает_увеличение_и_итог_это_новая_версия_local_relayvsr(int? scale, int expected)
    {
        var local = new VideoEnhanceTests.LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, versionId) = await Shot(w);
        var args = new JsonObject { ["sceneId"] = scene };
        if (scale is { } s) args["scale"] = s;

        var result = await w.Call(ToolEnhance, args);

        result.IsError.Should().BeFalse(result.Text);
        var json = AgentWorld.Parse(result);
        json["sceneId"]!.GetValue<string>().Should().Be(scene);
        json["quote"]!["scale"]!.GetValue<int>().Should().Be(expected);
        json["quote"]!["sourceVersionId"]!.GetValue<string>().Should().Be(versionId, "версия не указана — текущая");
        await w.Jobs.WhenDone(json["jobId"]!.GetValue<string>());
        local.SeenScale.Should().Be(expected);
        var versions = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions;
        versions.Should().HaveCount(2);
        versions.Single(v => v.VersionId != versionId).Model.Should().Be($"relayvsr-x{expected}");
    }

    [Fact]
    public async Task Enhance_уже_увеличенной_версии_отказывает_тем_же_текстом_что_кнопка()
    {
        var local = new VideoEnhanceTests.LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, _) = await Shot(w);
        var first = AgentWorld.Parse(await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene }));
        await w.Jobs.WhenDone(first["jobId"]!.GetValue<string>());
        var enhanced = w.F.Threads.Get(w.Owner, w.Chat).Scenes.Single().Versions.Single(v => v.EnhanceScale is not null).VersionId;

        var again = await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene, ["versionId"] = enhanced });

        again.IsError.Should().BeTrue();
        again.Text.Should().Contain(VideoEnhanceTexts.AlreadyEnhanced);
        local.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Enhance_отказывает_без_sceneId_для_неизвестной_сцены_и_плохого_масштаба()
    {
        var local = new VideoEnhanceTests.LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        var (scene, _) = await Shot(w);

        (await w.Call(ToolEnhance, new JsonObject())).Text.Should().Contain("sceneId");
        (await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = "no-such" })).Text.Should().Contain("нет в этом чате");
        (await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene, ["scale"] = 3 })).Text.Should().Contain("scale");
        (await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene, ["versionId"] = "no-such" })).IsError.Should().BeTrue();
        local.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Enhance_без_локального_поставщика_и_при_отказе_области_возвращает_ошибку()
    {
        using var noLocal = new AgentWorld();
        var (scene, _) = await Shot(noLocal);
        (await noLocal.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene })).IsError.Should().BeTrue();

        var local = new VideoEnhanceTests.LocalEnhancer { Refusal = "Файлы локального проекта лежат на устройстве" };
        using var refused = new AgentWorld(extraEngine: local);
        var (scene2, _) = await Shot(refused);
        var result = await refused.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene2 });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("на устройстве");
        local.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Enhance_работает_в_личном_чате()
    {
        var local = new VideoEnhanceTests.LocalEnhancer();
        using var w = new AgentWorld(extraEngine: local);
        w.F.Directory.Setup(d => d.GetById(AgentWorld.PersonalChat)).Returns(w.Sessions[AgentWorld.PersonalChat]);
        var (scene, version) = PersonalSceneWithClip(w);

        var result = await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene, ["versionId"] = version }, AgentWorld.PersonalChat);

        result.IsError.Should().BeFalse(result.Text);
        await w.Jobs.WhenDone(AgentWorld.Parse(result)["jobId"]!.GetValue<string>());
        local.Runs.Should().Be(1);
    }

    private static (string Scene, string Version) PersonalSceneWithClip(AgentWorld w)
    {
        var settings = new VideoSceneSettingsDto(null, null, "кадр", "fal", "fal-model", 5, "16:9", false, 1);
        var scene = w.F.Threads.AddScene(w.Owner, AgentWorld.PersonalChat, "", settings, null, "Личная").Scene!;
        w.F.Threads.AddLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, new VideoLaunchDto("job-personal", DateTime.UtcNow.AddSeconds(-30),
            VideoLaunchStatus.Running, false, VideoInitiators.Human, "fal", "fal-model", 1, "т", null, null));
        var finished = w.F.Threads.FinishLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, "job-personal", VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 0))], VideoSignatures.Snapshot(settings));
        w.F.Workspace.SaveClip(w.Owner, "job-personal", 1, [1, 2, 3], ".mp4");
        return (scene.SceneId, finished.NewVersions.Single().VersionId);
    }

    // ── video_voice ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Voice_запускает_шов_с_версией_подсказкой_и_моделью_и_отвечает_jobId()
    {
        var voice = new FakeVoice();
        using var w = new AgentWorld(voice: voice);
        var (scene, version) = w.F.SceneWithClip();

        var result = await w.Call(ToolVoice, new JsonObject
        {
            ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId, ["text"] = "шум прибоя", ["model"] = "foley-xxl",
        });

        result.IsError.Should().BeFalse(result.Text);
        var json = AgentWorld.Parse(result);
        json["jobId"]!.GetValue<string>().Should().Be("voice-job");
        json["threadId"]!.GetValue<string>().Should().Be("thread-1");
        json["quote"]!["price"]!.GetValue<int>().Should().Be(0);
        voice.Seen.Should().NotBeNull();
        (voice.Seen!.SceneId, voice.Seen.VersionId, voice.Seen.Text, voice.Seen.Model, voice.Seen.ProjectId, voice.Seen.SessionId)
            .Should().Be((scene.SceneId, version.VersionId, "шум прибоя", "foley-xxl", w.ProjectId, w.Chat));
    }

    [Fact]
    public async Task Voice_без_model_и_версии_берёт_авто_и_текущую_а_в_личном_чате_без_проекта()
    {
        var voice = new FakeVoice();
        using var w = new AgentWorld(voice: voice);
        var scene = w.F.Threads.AddScene(w.Owner, AgentWorld.PersonalChat, "",
            new VideoSceneSettingsDto(null, null, "кадр", "fal", "fal-model", 5, "16:9", false, 1), null, "Личная").Scene!;
        w.F.Threads.AddLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, new VideoLaunchDto("jp", DateTime.UtcNow, VideoLaunchStatus.Running,
            false, VideoInitiators.Human, "fal", "fal-model", 1, "т", null, null));
        w.F.Threads.FinishLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, "jp", VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 0))], VideoSignatures.Snapshot(scene.Settings));

        var result = await w.Call(ToolVoice, new JsonObject { ["sceneId"] = scene.SceneId }, AgentWorld.PersonalChat);

        result.IsError.Should().BeFalse(result.Text);
        (voice.Seen!.Model, voice.Seen.VersionId, voice.Seen.ProjectId).Should().Be((null, null, null));
    }

    [Fact]
    public async Task Voice_в_личном_чате_с_папкой_Личное_передаёт_шву_null_а_не_id_Личного()
    {
        var store = new Mock<ClaudeHomeServer.Services.Composition.IPersonalProjects>();
        var folder = Path.Combine(Path.GetTempPath(), "voice-personal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            store.Setup(s => s.GetPersonalOf(FilmWorld.Owner)).Returns(new ClaudeHomeServer.Models.Project
            {
                Id = "personal-id", OwnerId = FilmWorld.Owner, RootPath = folder, Kind = ClaudeHomeServer.Models.ProjectKind.Personal,
            });
            var voice = new FakeVoice();
            using var w = new AgentWorld(voice: voice, personalFolder: new ClaudeHomeServer.Services.Composition.PersonalFolderResolver(store.Object));
            var scene = w.F.Threads.AddScene(w.Owner, AgentWorld.PersonalChat, "",
                new VideoSceneSettingsDto(null, null, "кадр", "fal", "fal-model", 5, "16:9", false, 1), null, "Личная").Scene!;
            w.F.Threads.AddLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, new VideoLaunchDto("jf", DateTime.UtcNow, VideoLaunchStatus.Running,
                false, VideoInitiators.Human, "fal", "fal-model", 1, "т", null, null));
            w.F.Threads.FinishLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, "jf", VideoLaunchStatus.Done,
                [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 0))], VideoSignatures.Snapshot(scene.Settings));

            var result = await w.Call(ToolVoice, new JsonObject { ["sceneId"] = scene.SceneId }, AgentWorld.PersonalChat);

            result.IsError.Should().BeFalse(result.Text);
            voice.Seen!.ProjectId.Should().BeNull();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Voice_отказывает_для_неизвестной_сцены_версии_и_без_sceneId()
    {
        var voice = new FakeVoice();
        using var w = new AgentWorld(voice: voice);
        var (scene, _) = w.F.SceneWithClip();

        (await w.Call(ToolVoice, new JsonObject())).Text.Should().Contain("sceneId");
        (await w.Call(ToolVoice, new JsonObject { ["sceneId"] = "no-such" })).Text.Should().Contain("нет в этом чате");
        (await w.Call(ToolVoice, new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = "no-such" })).Text.Should().Contain("нет версии");
        voice.Seen.Should().BeNull();
    }

    [Fact]
    public async Task Voice_отказ_шва_доходит_до_агента_с_кодом_а_без_шва_инструмент_честно_отказывает()
    {
        var voice = new FakeVoice { Result = VideoVoiceStart.Fail("invalid_request", "Сцена 12.0 с, а модель озвучивает клипы до 10 с") };
        using var refused = new AgentWorld(voice: voice);
        var (scene, _) = refused.F.SceneWithClip();
        var result = await refused.Call(ToolVoice, new JsonObject { ["sceneId"] = scene.SceneId });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("до 10 с").And.Contain("invalid_request");

        using var noSeam = new AgentWorld();
        var (scene2, _) = noSeam.F.SceneWithClip();
        var none = await noSeam.Call(ToolVoice, new JsonObject { ["sceneId"] = scene2.SceneId });
        none.IsError.Should().BeTrue();
        none.Text.Should().Contain("Модуль «Звук»");
    }

    [Fact]
    public async Task Video_cancel_и_video_wait_берут_задачу_озвучки_а_чужая_неотличима_от_несуществующей()
    {
        var voice = new FakeVoice { Job = VoiceJob("chat-project", "running", false) };
        using var w = new AgentWorld(voice: voice);
        voice.Job = VoiceJob(w.Chat, "running", false);

        var waiting = AgentWorld.Parse(await w.Call(ToolWait, new JsonObject { ["jobIds"] = new JsonArray("voice-job"), ["timeoutSeconds"] = 0 }));
        waiting["allDone"]!.GetValue<bool>().Should().BeFalse();

        voice.Job = VoiceJob(w.Chat, "completed", true);
        var done = AgentWorld.Parse(await w.Call(ToolWait, new JsonObject { ["jobIds"] = new JsonArray("voice-job"), ["timeoutSeconds"] = 0 }));
        done["allDone"]!.GetValue<bool>().Should().BeTrue();

        (await w.Call(ToolCancel, new JsonObject { ["jobId"] = "voice-job" })).IsError.Should().BeFalse();
        voice.Cancels.Should().Be(1);

        voice.Job = VoiceJob("other-chat", "running", false);
        (await w.Call(ToolCancel, new JsonObject { ["jobId"] = "voice-job" })).IsError.Should().BeTrue();
        (await w.Call(ToolWait, new JsonObject { ["jobIds"] = new JsonArray("voice-job") })).IsError.Should().BeTrue();
        voice.Cancels.Should().Be(1);
    }

    // ── Гейты и состав ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Делегированный_ход_получает_отказ_и_ничего_не_запускается()
    {
        var voice = new FakeVoice();
        var local = new VideoEnhanceTests.LocalEnhancer();
        using var w = new AgentWorld(delegatedDenied: true, extraEngine: local, voice: voice);
        var (scene, _) = w.F.SceneWithClip();

        var enhance = await w.Call(ToolEnhance, new JsonObject { ["sceneId"] = scene.SceneId });
        var speak = await w.Call(ToolVoice, new JsonObject { ["sceneId"] = scene.SceneId });

        enhance.IsError.Should().BeTrue();
        speak.IsError.Should().BeTrue();
        enhance.Text.Should().Contain("делегированный ход");
        speak.Text.Should().Contain("делегированный ход");
        (local.Runs, voice.Seen).Should().Be((0, null));
    }

    [Fact]
    public void Оба_инструмента_есть_в_составе_и_пропадают_без_запуска_агентом()
    {
        using var on = new AgentWorld();
        on.Toolset.ToolsFor(on.Ctx()).Select(t => t.Name).Should().Contain([ToolEnhance, ToolVoice]);
        on.Toolset.ToolsFor(on.Ctx(AgentWorld.PersonalChat)).Select(t => t.Name).Should().Contain([ToolEnhance, ToolVoice]);

        using var off = new AgentWorld(agentLaunch: false);
        off.Toolset.ToolsFor(off.Ctx()).Select(t => t.Name).Should().NotContain([ToolEnhance, ToolVoice]);
    }

    [Fact]
    public void Схемы_требуют_sceneId_а_scale_это_2_или_4()
    {
        var enhance = Schemas.Single(s => s.Name == ToolEnhance).InputSchema;
        var voice = Schemas.Single(s => s.Name == ToolVoice).InputSchema;

        enhance["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("sceneId");
        voice["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("sceneId");
        enhance["properties"]!["scale"]!["enum"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(2, 4);
        voice["properties"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo("sceneId", "versionId", "text", "model");
    }

    [Fact]
    public async Task Video_state_напоминает_про_оба_действия()
    {
        using var w = new AgentWorld();

        var state = AgentWorld.Parse(await w.Call(ToolState));

        state["humanChoice"]!["rule"]!.GetValue<string>().Should().Contain("video_enhance").And.Contain("video_voice");
    }

    [Fact]
    public void Автодопуск_и_права_участника_знают_оба_инструмента()
    {
        ClaudeHomeServer.Services.VideoEditor.VideoEditorAgentTools.AutoAllowTools
            .Should().Contain(["mcp__video-editor__video_enhance", "mcp__video-editor__video_voice"]);
        ClaudeHomeServer.Services.VideoEditor.VideoEditorAgentTools.AutoAllowTools
            .Select(t => t["mcp__video-editor__".Length..])
            .Should().BeEquivalentTo(Schemas.Select(s => s.Name), "список автодопуска совпадает со схемами тулсета");
    }
}
