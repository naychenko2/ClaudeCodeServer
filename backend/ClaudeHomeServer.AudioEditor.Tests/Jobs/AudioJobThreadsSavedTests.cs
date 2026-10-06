using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// «Сохранить в проект» оставляет след в нити: нить идёт за сохранённым путём, журнал хода получает
// saved, владельцу уходит audio_thread_changed с новым состоянием
public sealed class AudioJobThreadsSavedTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Session = "chat-1";
    private const string ScopeKey = "project:p-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-saved-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly AudioJobThreads _threads;

    public AudioJobThreadsSavedTests()
    {
        _store = new AudioThreadStore(_root);
        _threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance, null, null, _broadcaster);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Сохранение_переводит_нить_на_файл_проекта_пишет_журнал_и_рассылает()
    {
        var threadId = _store.Open(Owner, Session, "music/intro.mp3", null, null).Thread!.Id;

        await _threads.OnSavedAsync(Owner, ScopeKey, Session, threadId, "music/intro.v2.mp3");

        var state = _store.Get(Owner, Session);
        var thread = state.Threads.Single();
        thread.File.Should().Be("music/intro.v2.mp3");
        thread.Lineage.Should().Equal("music/intro.mp3");
        thread.Version(AudioThreadVersion.OriginId)!.File(AudioFileRoles.Main)!.Path
            .Should().Be("music/intro.mp3", "исходник остаётся тем файлом, от которого начинали");
        state.Events.Should().ContainSingle(e => e.Kind == AudioThreadEventKinds.Saved && e.ThreadId == threadId);
        var sent = _broadcaster.Messages.OfType<AudioThreadChangedMessage>().Should().ContainSingle().Subject;
        sent.SessionId.Should().Be(Session);
        sent.Revision.Should().Be(state.Revision);
    }

    [Fact]
    public async Task Черновик_после_сохранения_перестаёт_быть_черновиком()
    {
        var threadId = _store.Open(Owner, Session, null, "drafts", null).Thread!.Id;

        await _threads.OnSavedAsync(Owner, ScopeKey, Session, threadId, "drafts/audio.mp3");

        var thread = _store.Get(Owner, Session).Threads.Single();
        thread.File.Should().Be("drafts/audio.mp3");
        thread.DraftFolder.Should().BeNull();
        thread.Lineage.Should().BeEmpty();
    }

    // Версия из одних стемов: нить с файлом остаётся при своём звуке, черновик получает имя группы
    [Fact]
    public async Task Стемы_без_звука_не_перепривязывают_нить_а_черновику_дают_имя()
    {
        var fileThread = _store.Open(Owner, Session, "music/song.mp3", null, null).Thread!.Id;
        var draft = _store.Open(Owner, Session, null, "drafts", null).Thread!.Id;

        await _threads.OnSavedAsync(Owner, ScopeKey, Session, fileThread, "music/demo.stems", stemsOnlyName: "demo");
        await _threads.OnSavedAsync(Owner, ScopeKey, Session, draft, "drafts/demo.stems", stemsOnlyName: "demo");

        var state = _store.Get(Owner, Session);
        var file = state.Threads.Single(t => t.Id == fileThread);
        file.File.Should().Be("music/song.mp3");
        file.Lineage.Should().BeEmpty();
        file.Name.Should().BeNull();
        var named = state.Threads.Single(t => t.Id == draft);
        named.File.Should().BeNull();
        named.DraftFolder.Should().Be("drafts");
        named.Name.Should().Be("demo");
        state.Events.Count(e => e.Kind == AudioThreadEventKinds.Saved).Should().Be(2);
    }

    [Fact]
    public async Task Чужая_нить_молча_пропускается()
    {
        await _threads.OnSavedAsync(Owner, ScopeKey, Session, "нет-такой", "intro.v2.mp3");

        _store.Get(Owner, Session).Events.Should().BeEmpty();
        _broadcaster.Messages.Should().BeEmpty();
    }

    private sealed class RecordingBroadcaster : ISessionBroadcaster
    {
        private readonly List<ServerMessage> _sent = [];

        public List<ServerMessage> Messages { get { lock (_sent) return [.. _sent]; } }

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            lock (_sent) _sent.Add(message);
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
