using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Threads;

// После перезапуска сервера реестр задач пуст: запуск в статусе running результата не дождётся.
// Сверка при старте переводит его в interrupted и пишет журнал для хода; остальное не трогает
public sealed class AudioThreadRecoveryTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "session-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Новый процесс над тем же data — свежее хранилище
    private AudioThreadStore Store() => new(_root);

    private static AudioThreadLaunch Launch(string jobId, string? baseVersion = AudioThreadVersion.OriginId) =>
        new(jobId, baseVersion, DateTime.UtcNow, AudioThreadLaunchStatus.Running, "human", "спой", null);

    private static AudioThreadRecovery Recovery(AudioThreadStore store) =>
        new(store, NullLogger<AudioThreadRecovery>.Instance);

    [Fact]
    public async Task После_перезапуска_идущий_запуск_становится_прерванным_с_записью_журнала()
    {
        var before = Store();
        var thread = before.Open(Owner, Chat, "voice/intro.mp3", null, null).Thread!;
        before.AddLaunch(Owner, Chat, thread.Id, Launch("job-1"));
        var revision = before.Get(Owner, Chat).Revision;

        var after = Store();
        await Recovery(after).StartAsync(CancellationToken.None);

        var state = after.Get(Owner, Chat);
        state.Threads.Single().Launches.Single().Status.Should().Be(AudioThreadLaunchStatus.Interrupted);
        state.Threads.Single().HasRunningLaunch.Should().BeFalse();
        state.Revision.Should().Be(revision + 1);
        var log = state.Events.Last();
        log.Kind.Should().Be(AudioThreadEventKinds.Interrupted);
        log.ThreadId.Should().Be(thread.Id);
        log.JobId.Should().Be("job-1");
        log.Text.Should().StartWith(AudioThreadRecovery.InterruptedText).And.Contain("voice/intro.mp3");
    }

    [Fact]
    public void Готовые_версии_и_завершённые_запуски_не_трогаются()
    {
        var store = Store();
        var thread = store.Open(Owner, Chat, "music/song.mp3", null, null).Thread!;
        store.AddLaunch(Owner, Chat, thread.Id, Launch("done-job"));
        store.FinishLaunch(Owner, Chat, thread.Id, "done-job", AudioThreadLaunchStatus.Done,
            [(1, [new AudioVersionFile(AudioFileRoles.Main, "a.mp3")])]);
        store.AddLaunch(Owner, Chat, thread.Id, Launch("live-job", null));
        var beforeVersions = store.Get(Owner, Chat).Threads.Single().Versions;

        Recovery(Store()).Recover(CancellationToken.None).Should().Be(1);

        var after = Store().Get(Owner, Chat).Threads.Single();
        after.Launches.Single(l => l.JobId == "done-job").Status.Should().Be(AudioThreadLaunchStatus.Done);
        after.Launches.Single(l => l.JobId == "live-job").Status.Should().Be(AudioThreadLaunchStatus.Interrupted);
        after.Versions.Should().BeEquivalentTo(beforeVersions);
    }

    [Fact]
    public void Чат_без_идущих_запусков_не_переписывается()
    {
        var store = Store();
        store.Open(Owner, Chat, "voice/intro.mp3", null, null);
        var before = store.Get(Owner, Chat);

        Recovery(Store()).Recover(CancellationToken.None).Should().Be(0);

        Store().Get(Owner, Chat).Should().BeEquivalentTo(before, "ревизия и журнал без изменений");
    }

    [Fact]
    public void Сверка_обходит_все_чаты_всех_владельцев()
    {
        var store = Store();
        foreach (var (owner, chat) in new[] { (Owner, Chat), (Owner, "session-2"), ("owner-2", "session-3") })
        {
            var thread = store.Open(owner, chat, null, "", null).Thread!;
            store.AddLaunch(owner, chat, thread.Id, Launch("job-" + chat, null));
        }

        Recovery(Store()).Recover(CancellationToken.None).Should().Be(3);

        Store().Get("owner-2", "session-3").Threads.Single().Launches.Single().Status
            .Should().Be(AudioThreadLaunchStatus.Interrupted);
    }

    [Fact]
    public async Task Пустой_корень_не_роняет_старт()
    {
        await Recovery(Store()).StartAsync(CancellationToken.None);
        Directory.Exists(_root).Should().BeFalse();
    }
}
