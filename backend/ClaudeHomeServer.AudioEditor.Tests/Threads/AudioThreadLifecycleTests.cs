using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Threads;

// Нити звука живут и умирают вместе с чатом: session/deleted сносит их, session/branched копирует
// в ветку с теми же id
public sealed class AudioThreadLifecycleTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "session-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;

    public AudioThreadLifecycleTests() => _store = new AudioThreadStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Удаление_чата_на_шине_сносит_нити()
    {
        var bus = await StartedLifecycle();
        _store.Open(Owner, Chat, "voice/intro.mp3", null, null);
        _store.Open(Owner, "session-2", "voice/other.mp3", null, null);

        await bus.PublishAsync(new SessionDeleted(new TurnContext(Chat, Owner, 0, 0, "p1")));

        File.Exists(_store.StatePath(Owner, Chat)).Should().BeFalse();
        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
        _store.Get(Owner, "session-2").Threads.Should().ContainSingle("чужой чат удаление не задевает");
    }

    [Fact]
    public async Task Ветвление_на_шине_копирует_нити_с_теми_же_id()
    {
        var bus = await StartedLifecycle();
        var opened = _store.Open(Owner, Chat, "voice/intro.mp3", null, null);

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        var branch = _store.Get(Owner, "branch-1");
        branch.Should().BeEquivalentTo(opened.State, "якоря в скопированной истории ветки ссылаются на те же threadId и versionId");
        branch.Threads.Single().Id.Should().Be(opened.Thread!.Id);
        _store.Get(Owner, Chat).Should().BeEquivalentTo(opened.State, "источник ветвление не меняет");
    }

    [Fact]
    public async Task Ветвление_чата_без_нитей_ничего_не_создаёт()
    {
        var bus = await StartedLifecycle();

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        File.Exists(_store.StatePath(Owner, "branch-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Повторный_старт_не_удваивает_подписчиков()
    {
        var bus = await StartedLifecycle();

        bus.Subscribers.Should().Be(2);
    }

    private async Task<FakeBus> StartedLifecycle()
    {
        var bus = new FakeBus();
        var lifecycle = new AudioThreadLifecycle(_store, NullLogger<AudioThreadLifecycle>.Instance, bus);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StartAsync(CancellationToken.None);
        return bus;
    }

    // Шина ядра живёт в сборке Turn; модулю нужен только контракт из Core
    private sealed class FakeBus : ITurnEventBus
    {
        private readonly List<(Type Type, Func<object, Task> Handler)> _handlers = [];

        public int Subscribers => _handlers.Count;

        public void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification =>
            _handlers.Add((typeof(T), e => handler((T)e)));

        public void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter =>
            throw new NotSupportedException();

        public async Task PublishAsync<T>(T e) where T : ITurnNotification
        {
            foreach (var (type, handler) in _handlers.Where(h => h.Type == typeof(T)).ToList())
                await handler(e!);
        }

        public Task<T> ApplyAsync<T>(T e) where T : ITurnFilter => throw new NotSupportedException();
    }
}
