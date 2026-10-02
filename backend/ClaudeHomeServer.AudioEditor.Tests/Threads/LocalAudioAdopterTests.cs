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
        new(Owner, Project, Chat, "music_edit", "lm_a", [.. files.Select(f => new LocalMediaAdoptedFile(f.Path, f.Type))]);

    private static LocalMediaAdoption Separated(params string[] stems) =>
        new(Owner, Project, Chat, "audio_separate", "lm_a",
            [.. stems.Select(n => new LocalMediaAdoptedFile($".cc-attachments/local-media/2026-10-02/lm_a-{n}.wav", "audio/wav"))]);

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

    // Как у кнопки separate: ОДНА нить, ОДНА версия со стемами по ролям, запуск и якорь audio_launch_versions
    [Fact]
    public async Task Стемы_одной_задачи_одна_нить_и_одна_версия_по_ролям()
    {
        await Adopter().AdoptAsync(Separated("vocals", "no_vocals"), default);

        var thread = _store.Get(Owner, Chat).Threads.Should().ContainSingle("стемы — не две нити").Subject;
        thread.File.Should().BeNull("нить-черновик: источник задача не знает");
        var version = thread.Versions.Should().ContainSingle().Subject;
        version.JobId.Should().Be("lm_a");
        version.Variant.Should().Be(1);
        version.Files.Select(f => f.Role).Should().Equal("stem:vocals", "stem:no_vocals");
        version.Files[0].Path.Should().EndWith("lm_a-vocals.wav");
        thread.CurrentVersionId.Should().Be(version.Id);
        thread.Launches.Should().ContainSingle().Which.Status.Should().Be(AudioThreadLaunchStatus.Done);
        var record = _records.Should().ContainSingle().Subject;
        record.RecordType.Should().Be(AudioJobThreads.RecordTypes.LaunchVersions);
        record.Data!.Value.GetProperty("threadId").GetString().Should().Be(thread.Id);
        record.Data!.Value.GetProperty("jobId").GetString().Should().Be("lm_a");
        record.Data!.Value.GetProperty("op").GetString().Should().Be("separate");
    }

    [Fact]
    public async Task Стемы_повторное_усыновление_той_же_задачи_ничего_не_дублирует()
    {
        var adopter = Adopter();
        await adopter.AdoptAsync(Separated("vocals", "drums"), default);
        await adopter.AdoptAsync(Separated("vocals", "drums"), default);

        var thread = _store.Get(Owner, Chat).Threads.Should().ContainSingle().Subject;
        thread.Versions.Should().ContainSingle();
        _records.Should().ContainSingle();
    }

    [Fact]
    public void Стемы_без_хвоста_в_имени_получают_номера_и_роли_не_совпадают()
    {
        var files = new LocalMediaAdoptedFile[]
        {
            new("a/odd.wav", "audio/wav"), new("a/odd.wav", "audio/wav"), new("a/lm_a-vocals.wav", "audio/wav"),
        };

        var roles = LocalAudioAdopter.StemFiles("lm_a", files).Select(f => f.Role).ToList();

        roles.Should().Equal("stem:1", "stem:2", "stem:vocals").And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Стемы_выбор_человека_не_двигается()
    {
        var mine = _store.Open(Owner, Chat, "mine.wav", null, null).Thread!.Id;

        await Adopter().AdoptAsync(Separated("vocals", "drums"), default);

        _store.Get(Owner, Chat).Focus.Should().Be(mine);
    }

    [Fact]
    public async Task Стемы_без_звуковых_файлов_и_при_выключенном_флаге_молчат()
    {
        await Adopter().AdoptAsync(new LocalMediaAdoption(Owner, Project, Chat, "audio_separate", "lm_a",
            [new LocalMediaAdoptedFile("a/x.txt", "text/plain")]), default);
        _flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.AudioEditor)).Returns(false);
        await Adopter().AdoptAsync(Separated("vocals"), default);

        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
        _records.Should().BeEmpty();
    }
}
