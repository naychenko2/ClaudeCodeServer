using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Moq;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Tests.Films.FilmWorld;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// ADR-040, Ф2-3: «Сохранить сцену» личной области — клип в Personal/video/scene-NN.mp4 (повтор → .v2, CreateNew),
// без фильма и кадров; выход из корня и ссылка наружу — отказ; папку «Личного» создаёт сам пишущий путь
public sealed class PersonalSceneSaverTests : IDisposable
{
    private const string PersonalSession = "pc1";

    private readonly FilmWorld _w = new();
    private readonly Mock<IPersonalProjects> _store = new();
    private readonly string _personalRoot;
    private readonly VideoEditScope _scope = new(VideoEditScope.Personal, null) { TenantOwnerId = Owner };

    public PersonalSceneSaverTests()
    {
        _personalRoot = Path.Combine(_w.Dir, "Personal");
        var chat = new ClaudeHomeServer.Models.Session { Id = PersonalSession, OwnerId = Owner };
        _w.Directory.Setup(d => d.GetById(PersonalSession)).Returns(chat);
        _w.Directory.Setup(d => d.ResolveOwnerId(chat)).Returns(Owner);
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Directory.CreateDirectory(_personalRoot);
            return new Project { Id = "personal-id", OwnerId = Owner, RootPath = _personalRoot, Kind = ProjectKind.Personal };
        });
    }

    public void Dispose() => _w.Dispose();

    private PersonalSceneSaver Saver() => new(_w.JobThreads, _w.Workspace, new PersonalFolderResolver(_store.Object));

    // Сцена личного чата с готовой версией: клип лежит в рабочей папке задачи
    private (VideoSceneDto Scene, VideoClipVersionDto Version) PersonalSceneWithClip()
    {
        var settings = new VideoSceneSettingsDto(null, null, "Камера у окна", "fal", "veo", 5, "16:9", false, 1);
        var scene = _w.Threads.AddScene(Owner, PersonalSession, "", settings, null, "Сцена 1").Scene!;
        _w.Threads.AddLaunch(Owner, PersonalSession, scene.SceneId, new VideoLaunchDto("pjob", DateTime.UtcNow.AddSeconds(-30),
            VideoLaunchStatus.Running, false, VideoInitiators.Human, "fal", "veo", 1, "т", null, null));
        var finished = _w.Threads.FinishLaunch(Owner, PersonalSession, scene.SceneId, "pjob", VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, new VideoCostDto("usd", 1.5))], VideoSignatures.Snapshot(settings));
        _w.Workspace.SaveClip(Owner, "pjob", 1, [9, 9, 9, 9], ".mp4");
        return (_w.Threads.Get(Owner, PersonalSession).Scenes.Single(s => s.SceneId == scene.SceneId), finished.NewVersions.Single());
    }

    private Task<FilmCallResult<SaveSceneResult>> Save(PersonalSceneSaver saver, VideoSceneDto scene, VideoClipVersionDto version,
        string? folder = null, string? fileName = null, string? filmPath = null) =>
        saver.SaveAsync(Owner, _scope,
            new SaveSceneRequest(PersonalSession, scene.SceneId, version.VersionId, folder, fileName, filmPath),
            VideoInitiators.Human, default);

    [Fact]
    public async Task Клип_ложится_в_Personal_video_папка_создаётся_здесь_и_нить_помнит_файл()
    {
        var (scene, version) = PersonalSceneWithClip();
        Directory.Exists(_personalRoot).Should().BeFalse("до сохранения папки «Личного» нет");

        var result = await Save(Saver(), scene, version);

        result.IsOk.Should().BeTrue(result.Error);
        result.Value!.Path.Should().Be("video/scene-01.mp4");
        result.Value.AddedToFilm.Should().BeFalse();
        File.ReadAllBytes(Path.Combine(_personalRoot, "video", "scene-01.mp4")).Should().Equal(9, 9, 9, 9);
        _w.Threads.Get(Owner, PersonalSession).Scenes.Single().SavedFiles
            .Should().ContainSingle(f => f.Path == "video/scene-01.mp4" && f.VersionId == version.VersionId);
        _store.Verify(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Повторное_сохранение_идёт_в_v2_и_ничего_не_затирает()
    {
        var (scene, version) = PersonalSceneWithClip();
        var saver = Saver();
        await Save(saver, scene, version);
        File.WriteAllBytes(Path.Combine(_personalRoot, "video", "scene-01.mp4"), [7, 7]);

        var again = await Save(saver, scene, version);

        again.Value!.Path.Should().Be("video/scene-01.v2.mp4");
        File.ReadAllBytes(Path.Combine(_personalRoot, "video", "scene-01.mp4")).Should().Equal(7, 7);
    }

    [Fact]
    public async Task Занятое_имя_названное_человеком_даёт_name_taken_без_перезаписи()
    {
        var (scene, version) = PersonalSceneWithClip();
        Directory.CreateDirectory(Path.Combine(_personalRoot, "video"));
        File.WriteAllBytes(Path.Combine(_personalRoot, "video", "final.mp4"), [5, 5]);

        var result = await Save(Saver(), scene, version, fileName: "final.mp4");

        result.ErrorCode.Should().Be(VideoEditorErrors.NameTaken);
        File.ReadAllBytes(Path.Combine(_personalRoot, "video", "final.mp4")).Should().Equal(5, 5);
    }

    [Theory]
    [InlineData("../evil.mp4")]
    [InlineData("a/b.mp4")]
    [InlineData("clip.mov")]
    public async Task Имя_файла_только_простое_с_mp4(string name)
    {
        var (scene, version) = PersonalSceneWithClip();

        (await Save(Saver(), scene, version, fileName: name)).ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("../x")]
    [InlineData("video/../x")]
    [InlineData("/etc")]
    public async Task Папка_вне_video_Личного_отказ_и_ничего_не_пишется(string folder)
    {
        var (scene, version) = PersonalSceneWithClip();

        var result = await Save(Saver(), scene, version, folder: folder);

        result.IsOk.Should().BeFalse();
        result.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        Directory.Exists(Path.Combine(_w.Dir, "x")).Should().BeFalse();
        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "отказ по пути — до создания папки");
    }

    [Fact]
    public async Task Подпапка_video_создаётся_внутри_корня()
    {
        var (scene, version) = PersonalSceneWithClip();

        var result = await Save(Saver(), scene, version, folder: "video/утро");

        result.Value!.Path.Should().Be("video/утро/scene-01.mp4");
        File.Exists(Path.Combine(_personalRoot, "video", "утро", "scene-01.mp4")).Should().BeTrue();
    }

    [Fact]
    public async Task Символическая_ссылка_наружу_вместо_video_отказывает()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_w.Dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(_personalRoot);
        Directory.CreateSymbolicLink(Path.Combine(_personalRoot, "video"), outside);
        var (scene, version) = PersonalSceneWithClip();

        var result = await Save(Saver(), scene, version);

        result.ErrorCode.Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        Directory.GetFiles(outside).Should().BeEmpty("за ссылку наружу ничего не записано");
    }

    [Fact]
    public async Task Фильм_в_Личном_пока_отказ_и_проектная_область_не_принимается()
    {
        var (scene, version) = PersonalSceneWithClip();

        (await Save(Saver(), scene, version, filmPath: "video/x/x.film")).ErrorCode
            .Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        (await Saver().SaveAsync(Owner, _w.Scope,
                new SaveSceneRequest(PersonalSession, scene.SceneId, version.VersionId, null, null), VideoInitiators.Human, default))
            .ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest, "для проекта путь — FilmSceneSaver");
    }

    [Fact]
    public async Task Чужая_сцена_и_сбой_папки_отказ()
    {
        var (scene, version) = PersonalSceneWithClip();
        var broken = new Mock<IPersonalProjects>();
        broken.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("нет"));
        var saver = new PersonalSceneSaver(_w.JobThreads, _w.Workspace, new PersonalFolderResolver(broken.Object));

        (await saver.SaveAsync(Owner, _scope, new SaveSceneRequest(PersonalSession, "нет-такой", null, null, null),
            VideoInitiators.Human, default)).ErrorCode.Should().Be(VideoEditorErrors.SceneNotFound);
        (await Save(saver, scene, version)).IsOk.Should().BeFalse("папку «Личного» создать нельзя");
    }
}
