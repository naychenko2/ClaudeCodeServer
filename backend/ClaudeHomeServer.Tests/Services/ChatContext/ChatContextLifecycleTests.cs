using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Контекст чата живёт и умирает вместе с чатом: session/deleted сносит файл, session/branched
// копирует его под ветку
public sealed class ChatContextLifecycleTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "session-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chat-context-lifecycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string PathOf(string owner, string session) => Path.Combine(_root, owner, session + ".json");

    private void Write(string owner, string session, string content)
    {
        var path = PathOf(owner, session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Удаление_чата_на_шине_сносит_только_его_файл()
    {
        var bus = await StartedLifecycle();
        Write(Owner, Chat, "{\"a\":1}");
        Write(Owner, Chat + "-2", "{\"b\":2}");
        Write(Owner, "x" + Chat, "{\"c\":3}");

        await bus.PublishAsync(new SessionDeleted(new TurnContext(Chat, Owner, 0, 0, "p1")));

        File.Exists(PathOf(Owner, Chat)).Should().BeFalse();
        File.Exists(PathOf(Owner, Chat + "-2")).Should().BeTrue("соседний файл удаление по маске не должно задевать");
        File.Exists(PathOf(Owner, "x" + Chat)).Should().BeTrue("файл с чужим префиксом цел");
    }

    [Fact]
    public async Task Ветвление_на_шине_копирует_файл_под_новый_id()
    {
        var bus = await StartedLifecycle();
        Write(Owner, Chat, "{\"revision\":5}");

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        File.ReadAllText(PathOf(Owner, "branch-1")).Should().Be("{\"revision\":5}");
        File.ReadAllText(PathOf(Owner, Chat)).Should().Be("{\"revision\":5}", "источник ветвление не меняет");
    }

    [Fact]
    public async Task Ветвление_чата_без_контекста_ничего_не_создаёт()
    {
        var bus = await StartedLifecycle();

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        File.Exists(PathOf(Owner, "branch-1")).Should().BeFalse();
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
        var lifecycle = new ChatContextLifecycle(_root, NullLogger<ChatContextLifecycle>.Instance, bus);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StartAsync(CancellationToken.None);
        return bus;
    }

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
