using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Scenes;

public sealed class VideoThreadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vtest-" + Guid.NewGuid().ToString("N"));
    private readonly VideoThreadStore _store;
    private const string Owner = "u1";
    private const string Session = "s1";

    public VideoThreadStoreTests() => _store = new VideoThreadStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static VideoSceneSettingsDto Settings(string text = "т", FrameRef? a = null) =>
        new(a, null, text, "fal", "veo", 5, "16:9", false, 1);

    private static VideoLaunchDto Launch(string job = "job-1", string status = VideoLaunchStatus.Running) =>
        new(job, DateTime.UtcNow, status, false, VideoInitiators.Human, "fal", "veo", 2, "т", "watermark", null);

    private static VideoVariantResult Variant(int n) => new(n, 5, 1000, true, new VideoCostDto("usd", 1.5));

    private string AddScene() => _store.AddScene(Owner, Session, "video/a", Settings(), null).Scene!.SceneId;

    [Fact]
    public void Новая_сцена_называется_по_порядку_и_берётся_в_фокус()
    {
        var first = _store.AddScene(Owner, Session, "video/a", Settings(), null);
        var second = _store.AddScene(Owner, Session, "video/a", Settings(), null);

        first.Scene!.Name.Should().Be("Сцена 1");
        second.Scene!.Name.Should().Be("Сцена 2");
        second.State.Focus.SceneId.Should().Be(second.Scene.SceneId);
        second.State.Revision.Should().Be(2);
    }

    [Fact]
    public void Запись_со_старой_ревизией_конфликт_и_состояние_не_меняется()
    {
        AddScene();

        var write = _store.AddScene(Owner, Session, "video/a", Settings(), revision: 0);

        write.Status.Should().Be(VideoThreadWriteStatus.Conflict);
        write.State.Scenes.Should().HaveCount(1);
        write.State.Revision.Should().Be(1);
    }

    [Fact]
    public void Фокус_на_чужую_сцену_отказ_а_повтор_того_же_фокуса_не_пишет()
    {
        var id = AddScene();
        var rev = _store.Get(Owner, Session).Revision;

        _store.SetFocus(Owner, Session, new VideoFocusDto("нет-такой", null), null).Status
            .Should().Be(VideoThreadWriteStatus.SceneNotFound);
        _store.SetFocus(Owner, Session, new VideoFocusDto(id, null), null).Status.Should().Be(VideoThreadWriteStatus.Ok);
        _store.Get(Owner, Session).Revision.Should().Be(rev, "фокус уже был на этой сцене");
    }

    [Fact]
    public void Запуск_кончается_версиями_и_первая_становится_текущей()
    {
        var id = AddScene();
        _store.AddLaunch(Owner, Session, id, Launch());

        var done = _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0), Variant(1)],
            new VideoInputsSnapshotDto("т", null, null));

        done.NewVersions.Should().HaveCount(2);
        done.NewVersions.Select(v => v.Number).Should().Equal(1, 2);
        done.Scene!.CurrentVersionId.Should().Be(done.NewVersions[0].VersionId);
        done.Scene.Launches.Single().Status.Should().Be(VideoLaunchStatus.Done);
        done.NewVersions[0].Provider.Should().Be("fal");
        done.NewVersions[0].License.Should().Be("watermark");
    }

    [Fact]
    public void Повторное_окончание_запуска_не_дублирует_версии()
    {
        var id = AddScene();
        _store.AddLaunch(Owner, Session, id, Launch());
        var inputs = new VideoInputsSnapshotDto("т", null, null);
        _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0)], inputs);

        _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0)], inputs);

        _store.Get(Owner, Session).Scenes.Single().Versions.Should().HaveCount(1);
    }

    [Fact]
    public void Сцену_с_версиями_или_идущим_запуском_удалить_нельзя()
    {
        var id = AddScene();
        _store.AddLaunch(Owner, Session, id, Launch());
        _store.Remove(Owner, Session, id, _store.Get(Owner, Session).Revision).Status.Should().Be(VideoThreadWriteStatus.Invalid);

        _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0)], new VideoInputsSnapshotDto("т", null, null));
        _store.Remove(Owner, Session, id, _store.Get(Owner, Session).Revision).Status.Should().Be(VideoThreadWriteStatus.Invalid);
    }

    [Fact]
    public void Пустую_сцену_удалить_можно_и_фокус_снимается()
    {
        var id = AddScene();

        var write = _store.Remove(Owner, Session, id, _store.Get(Owner, Session).Revision);

        write.Status.Should().Be(VideoThreadWriteStatus.Ok);
        write.State.Scenes.Should().BeEmpty();
        write.State.Focus.SceneId.Should().BeNull();
    }

    [Fact]
    public void Кадр_изменился_после_съёмки_сцена_помечается_переснять()
    {
        var id = AddScene();
        _store.SetSettings(Owner, Session, id, Settings(a: FrameRef.Image("t1", "v1")), null);
        var inputs = VideoSignatures.Snapshot(Settings(a: FrameRef.Image("t1", "v1")));
        _store.AddLaunch(Owner, Session, id, Launch());
        _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0)], inputs);
        _store.Get(Owner, Session).ToDto().Scenes.Single().Stale.Should().BeNull();

        _store.SetSettings(Owner, Session, id, Settings(a: FrameRef.Image("t1", "v2")), null);

        var stale = _store.Get(Owner, Session).ToDto().Scenes.Single().Stale;
        stale.Should().NotBeNull();
        stale!.FrameA.Should().BeTrue();
        stale.Text.Should().BeFalse();
    }

    [Fact]
    public void Недопустимое_число_вариантов_отклоняется()
    {
        var id = AddScene();

        _store.SetSettings(Owner, Session, id, Settings() with { Count = 9 }, null).Status
            .Should().Be(VideoThreadWriteStatus.Invalid);
    }

    [Fact]
    public void После_рестарта_бегущий_запуск_interrupted_а_готовые_версии_на_месте()
    {
        var id = AddScene();
        var inputs = new VideoInputsSnapshotDto("т", null, null);
        _store.AddLaunch(Owner, Session, id, Launch("job-1"));
        _store.FinishLaunch(Owner, Session, id, "job-1", VideoLaunchStatus.Done, [Variant(0)], inputs);
        _store.AddLaunch(Owner, Session, id, Launch("job-2"));

        // Перезапуск: новый экземпляр на тех же файлах
        var restarted = new VideoThreadStore(_root);
        var recovery = new VideoThreadRecovery(restarted, NullLogger<VideoThreadRecovery>.Instance);
        recovery.Recover(CancellationToken.None).Should().Be(1);

        var scene = restarted.Get(Owner, Session).Scenes.Single();
        scene.Launches.Single(l => l.JobId == "job-2").Status.Should().Be(VideoLaunchStatus.Interrupted);
        scene.Launches.Single(l => l.JobId == "job-2").Interrupted.Should().BeTrue();
        scene.Launches.Single(l => l.JobId == "job-1").Status.Should().Be(VideoLaunchStatus.Done);
        scene.Versions.Should().HaveCount(1, "готовые версии сохраняются");
        restarted.Get(Owner, Session).Events.Should().ContainSingle(e => e.Kind == VideoThreadEventKinds.Interrupted);
        recovery.Recover(CancellationToken.None).Should().Be(0, "повторная сверка ничего не находит");
    }

    [Fact]
    public void Ветка_получает_копию_нитей_а_удаление_чата_их_уносит()
    {
        var id = AddScene();

        _store.Copy(Owner, Session, "s2").Should().BeTrue();
        _store.Get(Owner, "s2").Scenes.Single().SceneId.Should().Be(id);

        _store.Delete(Owner, Session);
        _store.Get(Owner, Session).Scenes.Should().BeEmpty();
        _store.Get(Owner, "s2").Scenes.Should().HaveCount(1, "ветка живёт отдельно");
    }

    [Fact]
    public void Рабочие_задачи_живых_версий_удерживаются()
    {
        var id = AddScene();
        _store.AddLaunch(Owner, Session, id, Launch("job-keep"));
        _store.FinishLaunch(Owner, Session, id, "job-keep", VideoLaunchStatus.Done, [Variant(0)], new VideoInputsSnapshotDto("т", null, null));

        _store.ReferencedJobs(Owner).Should().Contain("job-keep");
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    public void Небезопасный_идентификатор_отвергается(string bad)
    {
        var act = () => _store.Get(Owner, bad);

        act.Should().Throw<ArgumentException>();
    }
}
