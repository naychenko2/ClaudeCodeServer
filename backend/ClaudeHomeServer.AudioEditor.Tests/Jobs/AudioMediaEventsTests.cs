using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Шов «Видео → Звук» (ADR-022 §3): версия, завершившая запуск, объявляется событием AudioVersionAdded ПОСЛЕ
// записи в нить, а шов IAudioTrackSource заводит черновик музыки и отдаёт основной файл версии.
public sealed class AudioMediaEventsTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Session = "chat-1";
    private const string Scope = "p-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-events-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;
    private readonly MediaEventHub _hub = new();
    private readonly AudioJobThreads _threads;
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectManager> _projects = new();

    public AudioMediaEventsTests()
    {
        _store = new AudioThreadStore(Path.Combine(_root, "threads"));
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        var session = new Session { Id = Session, OwnerId = Owner, ProjectId = Scope };
        _directory.Setup(d => d.GetById(Session)).Returns(session);
        _directory.Setup(d => d.ResolveOwnerId(session)).Returns(Owner);
        _threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance, _directory.Object, null, null, _hub);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioTrackSource Source() =>
        new(_store, _threads, _workspace, _projects.Object, _directory.Object);

    [Fact]
    public async Task Событие_версии_приходит_после_записи_в_нить()
    {
        var threadId = _store.Open(Owner, Session, null, "music", null).Thread!.Id;
        _store.AddLaunch(Owner, Session, threadId,
            new AudioThreadLaunch("job-1", null, _store.Now(), AudioThreadLaunchStatus.Running, SpendInitiators.Agent, "джаз", null));
        var seen = new List<(AudioVersionAdded Evt, bool InThread)>();
        _hub.Subscribe<AudioVersionAdded>(e =>
        {
            // Подписчик читает нить в момент события: версия обязана там уже лежать
            seen.Add((e, _store.Get(e.OwnerId, e.SessionId).Threads.Single().Version(e.VersionId) is not null));
            return Task.CompletedTask;
        });

        var path = _workspace.SaveFile(Owner, "job-1", 1, AudioFileRoles.Main, [1, 2, 3], ".mp3");
        await _threads.OnFinishedAsync(Owner, Scope, Session, threadId, "job-1", AudioEditJobStatus.Completed,
            [new AudioJobVariantDto(1, [new AudioVersionFile(AudioFileRoles.Main, path)])], null);

        var only = seen.Should().ContainSingle().Subject;
        only.InThread.Should().BeTrue("событие идёт после записи версии, а не до неё");
        only.Evt.Should().Match<AudioVersionAdded>(e => e.OwnerId == Owner && e.SessionId == Session
            && e.ProjectId == Scope && e.ThreadId == threadId && e.Initiator == SpendInitiators.Agent);
    }

    [Fact]
    public async Task Упавший_подписчик_не_ломает_запись_версии()
    {
        var threadId = _store.Open(Owner, Session, null, "music", null).Thread!.Id;
        _store.AddLaunch(Owner, Session, threadId,
            new AudioThreadLaunch("job-1", null, _store.Now(), AudioThreadLaunchStatus.Running, SpendInitiators.Human, null, null));
        _hub.Subscribe<AudioVersionAdded>(_ => throw new InvalidOperationException("подписчик упал"));

        var act = () => _threads.OnFinishedAsync(Owner, Scope, Session, threadId, "job-1", AudioEditJobStatus.Completed,
            [new AudioJobVariantDto(1, [new AudioVersionFile(AudioFileRoles.Main, "1/main.mp3")])], null);

        await act.Should().NotThrowAsync();
        _store.Get(Owner, Session).Threads.Single().Versions.Should().ContainSingle();
    }

    [Fact]
    public async Task Черновик_музыки_заводится_в_режиме_music_только_в_своём_чате()
    {
        var draft = await Source().CreateDraftAsync(Owner, Scope, Session, "music", default);

        draft.Should().NotBeNull();
        var thread = _store.Get(Owner, Session).Threads.Single();
        thread.Id.Should().Be(draft!.ThreadId);
        thread.File.Should().BeNull();
        thread.DraftFolder.Should().Be("music");
        thread.Settings!.Mode.Should().Be(AudioModes.Music);

        (await Source().CreateDraftAsync("чужой", Scope, Session, "music", default)).Should().BeNull();
        (await Source().CreateDraftAsync(Owner, "чужая-область", Session, "music", default)).Should().BeNull();
    }

    [Fact]
    public async Task Основной_файл_версии_отдаётся_байтами_а_чужая_нить_и_лишнее_не_отдаются()
    {
        var threadId = _store.Open(Owner, Session, null, "music", null).Thread!.Id;
        _store.AddLaunch(Owner, Session, threadId,
            new AudioThreadLaunch("job-1", null, _store.Now(), AudioThreadLaunchStatus.Running, SpendInitiators.Human, null, null));
        var path = _workspace.SaveFile(Owner, "job-1", 1, AudioFileRoles.Main, [7, 8, 9], ".mp3");
        var written = _store.FinishLaunch(Owner, Session, threadId, "job-1", AudioThreadLaunchStatus.Done,
            [(1, [new AudioVersionFile(AudioFileRoles.Main, path)])]);
        var versionId = written.NewVersions.Single().Id;

        var file = await Source().GetMainFileAsync(Owner, threadId, versionId, default);

        file.Should().NotBeNull();
        file!.Bytes.Should().Equal(7, 8, 9);
        file.Extension.Should().Be(".mp3");
        (await Source().GetMainFileAsync("чужой", threadId, versionId, default)).Should().BeNull();
        (await Source().GetMainFileAsync(Owner, threadId, "нет-такой", default)).Should().BeNull();
    }
}
