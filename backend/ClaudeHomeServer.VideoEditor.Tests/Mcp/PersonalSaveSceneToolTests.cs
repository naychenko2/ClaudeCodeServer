using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using ClaudeHomeServer.Services.VideoEditor.Tests.Films;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// ADR-040, Ф2-3: video_save_scene в личном чате — клип в Personal/video/ (тот же сервис, что у ручки человека),
// без фильма; без сохранителя на сервере — честный отказ, а не «фильмы только в проекте»
public sealed class PersonalSaveSceneToolTests
{
    private static (VideoSceneDto Scene, VideoClipVersionDto Version) PersonalScene(AgentWorld w)
    {
        w.F.Directory.Setup(d => d.GetById(AgentWorld.PersonalChat)).Returns(w.Sessions[AgentWorld.PersonalChat]);
        w.F.Directory.Setup(d => d.ResolveOwnerId(w.Sessions[AgentWorld.PersonalChat])).Returns(w.Owner);
        var settings = new VideoSceneSettingsDto(null, null, "Камера", "fal", "veo", 5, "16:9", false, 1);
        var scene = w.F.Threads.AddScene(w.Owner, AgentWorld.PersonalChat, "", settings, null, "Сцена 1").Scene!;
        w.F.Threads.AddLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, new VideoLaunchDto("pj", DateTime.UtcNow.AddSeconds(-30),
            VideoLaunchStatus.Running, false, VideoInitiators.Agent, "fal", "veo", 1, "т", null, null));
        var finished = w.F.Threads.FinishLaunch(w.Owner, AgentWorld.PersonalChat, scene.SceneId, "pj", VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 1.5))], VideoSignatures.Snapshot(settings));
        w.F.Workspace.SaveClip(w.Owner, "pj", 1, [9, 9, 9, 9], ".mp4");
        return (scene, finished.NewVersions.Single());
    }

    [Fact]
    public async Task Агент_сохраняет_сцену_личного_чата_в_Личное()
    {
        var personalRoot = "";
        var store = new Mock<IPersonalProjects>();
        using var w = new AgentWorld(personalSaver: f =>
        {
            personalRoot = Path.Combine(f.Dir, "Personal");
            store.Setup(s => s.EnsurePersonalAsync(FilmWorld.Owner, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                Directory.CreateDirectory(personalRoot);
                return new Project { Id = "personal-id", OwnerId = FilmWorld.Owner, RootPath = personalRoot, Kind = ProjectKind.Personal };
            });
            return new PersonalSceneSaver(f.JobThreads, f.Workspace, new PersonalFolderResolver(store.Object));
        });
        var (scene, version) = PersonalScene(w);

        var result = await w.Call(VideoEditorToolset.ToolSaveScene,
            new JsonObject { ["sceneId"] = scene.SceneId, ["versionId"] = version.VersionId }, AgentWorld.PersonalChat);

        result.IsError.Should().BeFalse(result.Text);
        var json = AgentWorld.Parse(result);
        json["path"]!.GetValue<string>().Should().Be("video/scene-01.mp4");
        json["addedToFilm"]!.GetValue<bool>().Should().BeFalse();
        json["note"]!.GetValue<string>().Should().Contain("«Личное»");
        File.ReadAllBytes(Path.Combine(personalRoot, "video", "scene-01.mp4")).Should().Equal(9, 9, 9, 9);
    }

    [Fact]
    public async Task Без_сохранителя_на_сервере_отказ_про_Личное_а_не_про_фильмы()
    {
        using var w = new AgentWorld();

        var result = await w.Call(VideoEditorToolset.ToolSaveScene, new JsonObject { ["sceneId"] = "s" }, AgentWorld.PersonalChat);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«Личное»").And.NotContain("Фильмы — только в чате проекта");
    }
}
