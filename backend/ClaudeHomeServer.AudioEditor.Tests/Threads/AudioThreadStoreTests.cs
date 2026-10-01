using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Threads;

public sealed class AudioThreadStoreTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "session-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-threads-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;

    public AudioThreadStoreTests() => _store = new AudioThreadStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AudioThread OpenFile(string file) => _store.Open(Owner, Chat, file, null, null).Thread!;

    private static AudioThreadLaunch Launch(string jobId, string? baseVersion, string? license = null) =>
        new(jobId, baseVersion, DateTime.UtcNow, AudioThreadLaunchStatus.Running, "human", "спой", license);

    [Fact]
    public void Файл_нитей_лежит_по_владельцу_и_чату()
    {
        OpenFile("music/track.mp3");

        File.Exists(Path.Combine(_root, Owner, Chat + ".json")).Should().BeTrue();
        _store.Get(Owner, "другой-чат").Threads.Should().BeEmpty();
    }

    [Fact]
    public void Запись_со_старой_ревизией_конфликт_без_записи()
    {
        var thread = OpenFile("music/track.mp3");
        var stale = _store.Get(Owner, Chat).Revision;
        _store.SetFocus(Owner, Chat, null, stale).Status.Should().Be(AudioThreadWriteStatus.Ok);

        var settings = new AudioThreadSettings(AudioModes.Music, "cover", "local", "ace", null);
        var write = _store.SetSettings(Owner, Chat, thread.Id, settings, stale);

        write.Status.Should().Be(AudioThreadWriteStatus.Conflict);
        write.State.Revision.Should().Be(stale + 1);
        _store.Get(Owner, Chat).Threads.Single().Settings.Should().BeNull();
        _store.SetFocus(Owner, Chat, thread.Id, stale).Status.Should().Be(AudioThreadWriteStatus.Conflict);
        _store.Get(Owner, Chat).Focus.Should().BeNull();
    }

    [Fact]
    public void Каждая_запись_поднимает_ревизию()
    {
        var thread = OpenFile("a.mp3");
        var r1 = _store.Get(Owner, Chat).Revision;

        _store.SetSettings(Owner, Chat, thread.Id, new AudioThreadSettings(AudioModes.Voice, "tts", null, null, null), r1)
            .State.Revision.Should().Be(r1 + 1);
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", "origin")).State.Revision.Should().Be(r1 + 2);
    }

    [Fact]
    public void Журнал_хранит_последние_тридцать_записей()
    {
        var thread = OpenFile("a.mp3");
        for (var i = 0; i < AudioThreadStore.MaxEvents + 5; i++)
            _store.AddLaunch(Owner, Chat, thread.Id, Launch($"job-{i}", "origin"),
                new AudioThreadEvent(DateTime.UtcNow, AudioThreadEventKinds.Launched, $"запуск {i}", thread.Id, $"job-{i}"));

        var events = _store.Get(Owner, Chat).Events;
        events.Should().HaveCount(AudioThreadStore.MaxEvents);
        events[0].Text.Should().Be("запуск 5");
        events[^1].Text.Should().Be($"запуск {AudioThreadStore.MaxEvents + 4}");
    }

    [Fact]
    public void Версия_запуска_хранит_файлы_с_ролями()
    {
        var thread = OpenFile("music/song.mp3");
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", AudioThreadVersion.OriginId));

        IReadOnlyList<AudioVersionFile> files =
        [
            new(AudioFileRoles.Stem("vocals"), "job-1/vocals.mp3"),
            new(AudioFileRoles.Stem("drums"), "job-1/drums.mp3"),
            new(AudioFileRoles.Score, "job-1/song.abc"),
            new(AudioFileRoles.Lyrics, "job-1/song.lrc"),
        ];
        var write = _store.FinishLaunch(Owner, Chat, thread.Id, "job-1", AudioThreadLaunchStatus.Done, [(0, files)]);

        write.Status.Should().Be(AudioThreadWriteStatus.Ok);
        var stored = _store.Get(Owner, Chat).Threads.Single();
        var version = stored.Versions.Should().HaveCount(2).And.Subject.Last();
        version.Number.Should().Be(1);
        version.BaseVersionId.Should().Be(AudioThreadVersion.OriginId);
        version.Files.Should().BeEquivalentTo(files, o => o.WithStrictOrdering());
        version.File("stem:drums")!.Path.Should().Be("job-1/drums.mp3");
        stored.CurrentVersionId.Should().Be(version.Id);
        stored.Launches.Single().Status.Should().Be(AudioThreadLaunchStatus.Done);

        var origin = stored.Version(AudioThreadVersion.OriginId)!;
        origin.Number.Should().Be(0);
        origin.Files.Should().ContainSingle().Which.Should().Be(new AudioVersionFile(AudioFileRoles.Main, "music/song.mp3"));
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("stem:")]
    [InlineData("stem:../x")]
    public void Неизвестная_роль_файла_отказ_без_записи(string role)
    {
        var thread = OpenFile("a.mp3");
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", AudioThreadVersion.OriginId));
        var revision = _store.Get(Owner, Chat).Revision;

        var write = _store.FinishLaunch(Owner, Chat, thread.Id, "job-1", AudioThreadLaunchStatus.Done,
            [(0, [new AudioVersionFile(role, "job-1/x.mp3")])]);

        write.Status.Should().Be(AudioThreadWriteStatus.Invalid);
        _store.Get(Owner, Chat).Revision.Should().Be(revision);
        _store.Get(Owner, Chat).Threads.Single().Versions.Should().ContainSingle();
    }

    [Fact]
    public void Повторная_роль_в_версии_отказ()
    {
        var thread = OpenFile("a.mp3");
        var write = _store.AddEditVersion(Owner, Chat, thread.Id,
            [new(AudioFileRoles.Main, "j/a.mp3"), new(AudioFileRoles.Main, "j/b.mp3")], null);

        write.Status.Should().Be(AudioThreadWriteStatus.Invalid);
    }

    [Fact]
    public void Повтор_итога_запуска_не_дублирует_версии()
    {
        var thread = OpenFile("a.mp3");
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", AudioThreadVersion.OriginId));
        var results = new List<(int, IReadOnlyList<AudioVersionFile>)>
        {
            (0, [new(AudioFileRoles.Main, "job-1/0.mp3")]),
            (1, [new(AudioFileRoles.Main, "job-1/1.mp3")]),
        };
        _store.FinishLaunch(Owner, Chat, thread.Id, "job-1", AudioThreadLaunchStatus.Done, results)
            .NewVersions.Select(v => v.Number).Should().Equal(1, 2);

        _store.FinishLaunch(Owner, Chat, thread.Id, "job-1", AudioThreadLaunchStatus.Done, results)
            .NewVersions.Should().BeEmpty();
        _store.Get(Owner, Chat).Threads.Single().Versions.Should().HaveCount(3);
    }

    [Fact]
    public void Лицензия_запуска_ложится_в_версию_и_переживает_правку_без_ИИ()
    {
        var thread = OpenFile("a.mp3");
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", AudioThreadVersion.OriginId, "CC BY-NC"));
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-2", AudioThreadVersion.OriginId));
        _store.FinishLaunch(Owner, Chat, thread.Id, "job-1", AudioThreadLaunchStatus.Done,
            [(0, [new AudioVersionFile(AudioFileRoles.Main, "job-1/song.mp3")])]);
        _store.FinishLaunch(Owner, Chat, thread.Id, "job-2", AudioThreadLaunchStatus.Done,
            [(0, [new AudioVersionFile(AudioFileRoles.Main, "job-2/song.mp3")])]);

        var stored = _store.Get(Owner, Chat).Threads.Single();
        stored.Versions.Single(v => v.JobId == "job-1").License.Should().Be("CC BY-NC");
        stored.Versions.Single(v => v.JobId == "job-2").License.Should().BeNull();
        stored.Version(AudioThreadVersion.OriginId)!.License.Should().BeNull();

        var yue = stored.Versions.Single(v => v.JobId == "job-1");
        _store.SetCurrentVersion(Owner, Chat, thread.Id, yue.Id, null);
        var edit = _store.AddEditVersion(Owner, Chat, thread.Id, [new(AudioFileRoles.Main, "edit-1/song.mp3")], null);

        edit.NewVersions.Single().License.Should().Be("CC BY-NC");
        edit.NewVersions.Single().BaseVersionId.Should().Be(yue.Id);
        edit.Thread!.CurrentVersionId.Should().Be(edit.NewVersions.Single().Id);
    }

    [Fact]
    public void Настройки_нити_пишутся_и_читаются_с_полями_операции()
    {
        var thread = OpenFile("a.mp3");
        var fields = new JsonObject { ["lyrics"] = "[Verse]\nла-ла", ["start"] = 1.5, ["voice"] = "anna" };
        var settings = new AudioThreadSettings(AudioModes.Music, "repaint", "local", "ace-step", fields);

        _store.SetSettings(Owner, Chat, thread.Id, settings, _store.Get(Owner, Chat).Revision)
            .Status.Should().Be(AudioThreadWriteStatus.Ok);

        var read = new AudioThreadStore(_root).Get(Owner, Chat).Threads.Single().Settings!;
        read.Mode.Should().Be(AudioModes.Music);
        read.Operation.Should().Be("repaint");
        read.Provider.Should().Be("local");
        read.Model.Should().Be("ace-step");
        read.Fields!["lyrics"]!.GetValue<string>().Should().Be("[Verse]\nла-ла");
        read.Fields!["start"]!.GetValue<double>().Should().Be(1.5);
        read.Fields!["voice"]!.GetValue<string>().Should().Be("anna");
    }

    [Fact]
    public void Настройки_у_каждой_нити_свои()
    {
        var voice = OpenFile("speech.wav");
        var music = OpenFile("song.mp3");
        _store.SetSettings(Owner, Chat, voice.Id,
            new AudioThreadSettings(AudioModes.Voice, "tts", "yandex", "alena", new JsonObject { ["text"] = "привет" }), null);
        _store.SetSettings(Owner, Chat, music.Id,
            new AudioThreadSettings(AudioModes.Music, "cover", "local", "yue2", null), null);

        var threads = _store.Get(Owner, Chat).Threads;
        var v = threads.Single(t => t.Id == voice.Id).Settings!;
        var m = threads.Single(t => t.Id == music.Id).Settings!;
        v.Mode.Should().Be(AudioModes.Voice);
        v.Provider.Should().Be("yandex");
        v.Fields!["text"]!.GetValue<string>().Should().Be("привет");
        m.Mode.Should().Be(AudioModes.Music);
        m.Model.Should().Be("yue2");
        m.Fields.Should().BeNull();
    }

    [Fact]
    public void Неизвестный_режим_настроек_отказ()
    {
        var thread = OpenFile("a.mp3");

        _store.SetSettings(Owner, Chat, thread.Id, new AudioThreadSettings("video", null, null, null, null), null)
            .Status.Should().Be(AudioThreadWriteStatus.Invalid);
        _store.Get(Owner, Chat).Threads.Single().Settings.Should().BeNull();
    }

    [Fact]
    public void Несколько_нитей_и_фокус()
    {
        var first = OpenFile("one.mp3");
        _store.Get(Owner, Chat).Focus.Should().Be(first.Id);

        var draft = _store.Open(Owner, Chat, null, "music", null).Thread!;
        var state = _store.Get(Owner, Chat);
        state.Threads.Select(t => t.Id).Should().Equal(first.Id, draft.Id);
        state.Focus.Should().Be(draft.Id);
        draft.Versions.Should().BeEmpty();
        draft.CurrentVersionId.Should().BeNull();

        var again = _store.Open(Owner, Chat, "one.mp3", null, state.Revision);
        again.Existing.Should().BeTrue();
        again.Thread!.Id.Should().Be(first.Id);
        again.State.Threads.Should().HaveCount(2);
        again.State.Focus.Should().Be(first.Id);

        _store.SetFocus(Owner, Chat, "нет-такой", again.State.Revision).Status
            .Should().Be(AudioThreadWriteStatus.ThreadNotFound);
        _store.SetFocus(Owner, Chat, null, again.State.Revision).State.Focus.Should().BeNull();
    }

    [Fact]
    public void Нить_с_версиями_не_удаляется_пустой_черновик_удаляется()
    {
        var thread = OpenFile("a.mp3");
        _store.AddEditVersion(Owner, Chat, thread.Id, [new(AudioFileRoles.Main, "e/a.mp3")], null);
        var draft = _store.Open(Owner, Chat, null, "", null).Thread!;

        _store.Remove(Owner, Chat, thread.Id, _store.Get(Owner, Chat).Revision).Status
            .Should().Be(AudioThreadWriteStatus.Invalid);
        var removed = _store.Remove(Owner, Chat, draft.Id, _store.Get(Owner, Chat).Revision);
        removed.Status.Should().Be(AudioThreadWriteStatus.Ok);
        removed.State.Threads.Should().ContainSingle().Which.Id.Should().Be(thread.Id);
        removed.State.Focus.Should().BeNull();
    }

    [Fact]
    public void Журнал_отдаётся_ходу_один_раз_без_подъёма_ревизии()
    {
        var thread = OpenFile("a.mp3");
        _store.AddLaunch(Owner, Chat, thread.Id, Launch("job-1", "origin"),
            new AudioThreadEvent(DateTime.UtcNow, AudioThreadEventKinds.Launched, "запуск", thread.Id, "job-1"));
        var revision = _store.Get(Owner, Chat).Revision;

        _store.TakeForTurn(Owner, Chat).Fresh.Should().ContainSingle();
        _store.TakeForTurn(Owner, Chat).Fresh.Should().BeEmpty();
        _store.Get(Owner, Chat).Revision.Should().Be(revision);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Недопустимый_сегмент_пути_отказ(string session)
    {
        var act = () => _store.Get(Owner, session);
        act.Should().Throw<ArgumentException>();
    }
}

public sealed class AudioEditScopeTests
{
    [Fact]
    public void Личный_чат_ключуется_константой_на_владельца()
    {
        var a = new Session { Id = "s1", ProjectId = null };
        var b = new Session { Id = "s2", ProjectId = null };

        AudioEditScope.Of(a).Key.Should().Be(AudioEditScope.Personal);
        AudioEditScope.Of(b).Should().Be(AudioEditScope.Of(a));
        AudioEditScope.Of(a).IsPersonal.Should().BeTrue();
        AudioEditScope.ProjectIdOf(AudioEditScope.Personal).Should().BeNull();
    }

    [Fact]
    public void Чат_проекта_ключуется_id_проекта()
    {
        var project = new Project { Id = "p-1" };

        AudioEditScope.Of(project).Should().Be(new AudioEditScope("p-1", project));
        AudioEditScope.Of(new Session { Id = "s", ProjectId = "p-1" }).Key.Should().Be("p-1");
        AudioEditScope.ProjectIdOf("p-1").Should().Be("p-1");
    }
}
