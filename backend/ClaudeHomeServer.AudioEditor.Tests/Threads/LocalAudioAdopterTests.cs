using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Threads;

// Агент позвал local_* напрямую, мимо audio_generate: результат-звук усыновляется — нить по файлу и якорь
// audio_thread в ленте, то есть та же богатая карточка, что у запуска через audio_* или кнопкой
public sealed class LocalAudioAdopterTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p-1";
    private const string Chat = "chat-1";
    private const string Cover = ".cc-attachments/local-media/2026-10-02/lm_a-1.mp3";
    private const string Score = ".cc-attachments/local-media/2026-10-02/lm_a-score.abc";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-adopt-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly List<StoredModuleRecord> _records = [];
    private readonly Mock<IChatFeed> _feed = new();
    private readonly Mock<ISessionBroadcaster> _broadcaster = new();
    private readonly Mock<IFeatureFlagGate> _flags = new();
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectManager> _projects = new();

    public LocalAudioAdopterTests()
    {
        _store = new AudioThreadStore(_root);
        _feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, StoredModuleRecord, CancellationToken>((_, r, _) => _records.Add(r)).ReturnsAsync(true);
        _flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.AudioEditor)).Returns(true);
        var session = new Session { Id = Chat, OwnerId = Owner, ProjectId = Project };
        _directory.Setup(d => d.GetById(Chat)).Returns(session);
        _directory.Setup(d => d.ResolveOwnerId(session)).Returns(Owner);
        _projects.Setup(p => p.GetById(Project)).Returns(new Project { Id = Project, OwnerId = Owner, RootPath = _root });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private LocalAudioAdopter Adopter()
    {
        var threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance, _directory.Object, _feed.Object, _broadcaster.Object);
        return new LocalAudioAdopter(_store, threads, _directory.Object, _projects.Object, _flags.Object);
    }

    private static LocalMediaAdoption Result(params (string Path, string Type)[] files) =>
        new(Owner, Project, Chat, "music_edit", [.. files.Select(f => new LocalMediaAdoptedFile(f.Path, f.Type))]);

    [Fact]
    public async Task Звук_результата_получает_нить_и_якорь_в_ленте()
    {
        await Adopter().AdoptAsync(Result((Cover, "audio/mpeg"), (Score, "text/plain")), default);

        var thread = _store.Get(Owner, Chat).Threads.Should().ContainSingle("партитура .abc — не звук").Subject;
        thread.File.Should().Be(Cover);
        var record = _records.Should().ContainSingle().Subject;
        record.Module.Should().Be("audioeditor");
        record.RecordType.Should().Be(AudioJobThreads.RecordTypes.Thread);
        record.Data!.Value.GetProperty("threadId").GetString().Should().Be(thread.Id);
        record.Data!.Value.GetProperty("versionId").GetString().Should().Be(thread.CurrentVersionId);
        _broadcaster.Verify(b => b.ToOwner(Owner, It.IsAny<AudioThreadChangedMessage>()), Times.Once);
    }

    [Fact]
    public async Task Повторное_усыновление_того_же_файла_второго_якоря_не_даёт()
    {
        var adopter = Adopter();
        await adopter.AdoptAsync(Result((Cover, "audio/mpeg")), default);
        await adopter.AdoptAsync(Result((Cover, "audio/mpeg")), default);

        _store.Get(Owner, Chat).Threads.Should().ContainSingle();
        _records.Should().ContainSingle();
    }

    [Fact]
    public async Task Выбор_человека_в_полосе_остаётся_за_ним()
    {
        var mine = _store.Open(Owner, Chat, "samples/mine.mp3", null, null).Thread!.Id;

        await Adopter().AdoptAsync(Result((Cover, "audio/mpeg")), default);

        var state = _store.Get(Owner, Chat);
        state.Threads.Should().HaveCount(2);
        state.Focus.Should().Be(mine);
    }

    [Fact]
    public async Task Стемы_ложатся_каждый_своей_нитью()
    {
        await Adopter().AdoptAsync(Result(("a/v.wav", "audio/wav"), ("a/i.wav", "audio/wav")), default);

        _store.Get(Owner, Chat).Threads.Select(t => t.File).Should().Equal("a/v.wav", "a/i.wav");
        _records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Результат_без_звука_ничего_не_заводит()
    {
        await Adopter().AdoptAsync(Result((Score, "text/plain")), default);

        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
        _records.Should().BeEmpty();
    }

    [Fact]
    public async Task Выключенный_флаг_модуля_молчит()
    {
        _flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.AudioEditor)).Returns(false);

        await Adopter().AdoptAsync(Result((Cover, "audio/mpeg")), default);

        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
        _records.Should().BeEmpty();
    }

    [Fact]
    public async Task Чат_другого_проекта_молчит()
    {
        _directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = "other" });

        await Adopter().AdoptAsync(Result((Cover, "audio/mpeg")), default);

        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
    }
}
