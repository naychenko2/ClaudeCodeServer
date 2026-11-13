using Xunit;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Tests.Films;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// Шов IVideoSceneSound (Ш4, «Озвучить сцену»): клип версии наружу и новая версия сцены со вклеенным звуком
public class VideoSceneSoundTests : IDisposable
{
    private readonly FilmWorld _w = new();

    public void Dispose() => _w.Dispose();

    private VideoSceneSound Sound(bool muxOk = true, bool available = true)
    {
        var dsp = new Mock<IVideoDsp>();
        dsp.SetupGet(d => d.Available).Returns(available);
        dsp.Setup(d => d.MuxAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CancellationToken>((video, _, output, _) =>
            {
                if (!muxOk) return Task.FromResult(VideoDspResult.Fail("ffmpeg упал"));
                File.WriteAllBytes(output, [.. File.ReadAllBytes(video), 7]);
                return Task.FromResult(VideoDspResult.Success);
            });
        return new VideoSceneSound(_w.Threads, _w.Workspace, _w.JobThreads, NullLogger<VideoSceneSound>.Instance, dsp.Object);
    }

    private VideoSceneRef Ref(string sceneId, string? versionId = null, string session = FilmWorld.Session) =>
        new(FilmWorld.Owner, FilmWorld.ProjectId, session, sceneId, versionId);

    [Fact]
    public async Task GetClip_ТекущаяВерсия_ОтдаётБайтыИДлину()
    {
        var (scene, version) = _w.SceneWithClip();

        var clip = await Sound().GetClipAsync(Ref(scene.SceneId), default);

        clip.Should().NotBeNull();
        clip!.Bytes.Should().Equal(9, 9, 9, 9);
        clip.VersionId.Should().Be(version.VersionId);
        clip.Seconds.Should().Be(5);
    }

    [Fact]
    public async Task GetClip_ЧужаяСценаИлиВерсия_Null()
    {
        var (scene, _) = _w.SceneWithClip();
        var sound = Sound();

        (await sound.GetClipAsync(Ref("нет-такой"), default)).Should().BeNull();
        (await sound.GetClipAsync(Ref(scene.SceneId, "нет-версии"), default)).Should().BeNull();
        (await sound.GetClipAsync(Ref(scene.SceneId, session: "чужой-чат"), default)).Should().BeNull();
    }

    [Fact]
    public async Task AddSoundVersion_ЗаводитНовуюВерсию_СоЗвуком_ОсновуНеТрогает()
    {
        var (scene, version) = _w.SceneWithClip();

        var added = await Sound().AddSoundVersionAsync(Ref(scene.SceneId, version.VersionId), [1, 2], ".flac", "hunyuanvideo-foley-xl", default);

        added.Error.Should().BeNull();
        added.VersionId.Should().NotBeNull();
        var after = _w.Threads.Get(FilmWorld.Owner, FilmWorld.Session).Scenes.Single();
        after.Versions.Should().HaveCount(2);
        after.Versions[0].VersionId.Should().Be(version.VersionId);
        after.Versions[0].HasSound.Should().BeFalse();
        var created = after.Versions[1];
        created.HasSound.Should().BeTrue();
        created.VersionId.Should().Be(added.VersionId);
        created.DurationSec.Should().Be(5);
        after.CurrentVersionId.Should().Be(created.VersionId);
        var clip = _w.Workspace.FindClip(FilmWorld.Owner, created.JobId, created.Variant);
        File.ReadAllBytes(clip!).Should().Equal(9, 9, 9, 9, 7);
    }

    [Fact]
    public async Task AddSoundVersion_СбойFfmpegИлиЕгоНет_ВерсииНеДобавляет()
    {
        var (scene, version) = _w.SceneWithClip();

        var failed = await Sound(muxOk: false).AddSoundVersionAsync(Ref(scene.SceneId, version.VersionId), [1], ".wav", "m", default);
        var missing = await Sound(available: false).AddSoundVersionAsync(Ref(scene.SceneId, version.VersionId), [1], ".wav", "m", default);

        failed.VersionId.Should().BeNull();
        failed.Error.Should().Contain("ffmpeg");
        missing.VersionId.Should().BeNull();
        _w.Threads.Get(FilmWorld.Owner, FilmWorld.Session).Scenes.Single().Versions.Should().HaveCount(1);
    }
}
