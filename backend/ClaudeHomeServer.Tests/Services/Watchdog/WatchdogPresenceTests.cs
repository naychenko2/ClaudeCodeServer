using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Watchdog;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Watchdog;

// Присутствие сторожей для фронта: снимок {sessions, projects}, событие Changed стора
// на Create/Cancel/CancelBySession и адресация рассылки watchdogs_changed. Хаб — моком
// с записью пар (группа, сообщение); sends мока синхронны, поэтому fire-and-forget
// рассылка успевает до ассертов без ожиданий (CI Linux).
public class WatchdogPresenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WatchdogStore _store;
    private readonly List<(string Group, ServerMessage Msg)> _sent = [];
    private readonly WatchdogNotifier _notifier;

    public WatchdogPresenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchdog_presence_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new WatchdogStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
        _notifier = new WatchdogNotifier(_store, TestHub(), NullLogger<WatchdogNotifier>.Instance);
    }

    // Мок хаба с записью (группа, сообщение) — образец ChatArchivedEventTests
    private IHubContext<SessionHub> TestHub()
    {
        _sent.Clear();
        string? currentGroup = null;
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
                _sent.Add((currentGroup!, (ServerMessage)args[0]!)))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(g => currentGroup = g)
            .Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private WatchdogRecord Create(string session = "chat-1", string? project = "proj-1",
        string owner = "owner-1") =>
        _store.Create(owner, session, project, "Билд", "true", null, null, out _)!;

    private List<WatchdogsChangedMessage> Broadcasts() => _sent
        .Where(t => t.Msg is WatchdogsChangedMessage)
        .Select(t => (WatchdogsChangedMessage)t.Msg).ToList();

    // --- Снимок ---

    [Fact]
    public void Snapshot_ActiveOnly_ListsSessionsAndProjectsDistinct()
    {
        Create("chat-1", "proj-1");
        Create("chat-2", "proj-1");
        Create("chat-3", null);
        var fired = Create("chat-4", "proj-2");
        fired.Status = WatchdogStatus.Fired;
        fired.FiredAt = DateTime.UtcNow;

        var snap = _notifier.Snapshot("owner-1");

        snap.Sessions.Should().BeEquivalentTo(["chat-1", "chat-2", "chat-3"],
            "терминальный сторож присутствия не создаёт");
        snap.Projects.Should().BeEquivalentTo(["proj-1"],
            "проекты считаются по активным сторожам и без дублей");
    }

    [Fact]
    public void Snapshot_OtherOwner_NotIncluded()
    {
        Create("chat-1", null, owner: "owner-1");
        Create("chat-9", null, owner: "owner-2");

        _notifier.Snapshot("owner-1").Sessions.Should().BeEquivalentTo(["chat-1"]);
    }

    // --- Событие Changed стора ---

    [Fact]
    public void Create_FiresChangedWithOwner()
    {
        var owners = new List<string>();
        _store.Changed += owners.Add;

        Create("chat-1", "proj-1");

        owners.Should().BeEquivalentTo(["owner-1"]);
        _notifier.Snapshot("owner-1").Sessions.Should().Contain("chat-1");
    }

    [Fact]
    public void Cancel_FiresChangedOnce_AndDropsPresence()
    {
        var w = Create("chat-1", null);
        var owners = new List<string>();
        _store.Changed += owners.Add;

        _store.Cancel(w.Id, "owner-1", out var error);

        error.Should().BeNull();
        owners.Should().BeEquivalentTo(["owner-1"]);
        _notifier.Snapshot("owner-1").Sessions.Should().BeEmpty("снятый сторож не активен");
    }

    [Fact]
    public void CancelBySession_FiresChangedOncePerOwner()
    {
        Create("chat-1", "proj-1");
        Create("chat-1", "proj-1");
        var owners = new List<string>();
        _store.Changed += owners.Add;

        _store.CancelBySession("chat-1").Should().Be(2);

        owners.Should().Equal(new[] { "owner-1" },
            "все погашенные сторожа одного чата — владелец один");
    }

    // --- Рассылка watchdogs_changed ---

    [Fact]
    public void Create_BroadcastsToUserSessionAndProjectGroups()
    {
        _sent.Clear();

        Create("chat-1", "proj-1");

        var byGroup = _sent.Where(t => t.Msg is WatchdogsChangedMessage).ToList();
        byGroup.Select(t => t.Group).Should().BeEquivalentTo(
            ["user_owner-1", "chat-1", "project_proj-1"],
            "session-группа каждого затронутого чата (имя группы — сам id) + project- и user-группы");
        // Копия в session-группу несёт SessionId — клиент роутит по сессии
        var sessionCopy = byGroup.Single(t => t.Group == "chat-1");
        sessionCopy.Msg.SessionId.Should().Be("chat-1");
        sessionCopy.Msg.Should().BeOfType<WatchdogsChangedMessage>().Which.Sessions
            .Should().BeEquivalentTo(["chat-1"]);
        byGroup.Single(t => t.Group == "user_owner-1").Msg.SessionId.Should().BeEmpty();
    }

    [Fact]
    public void Cancel_BroadcastsEmptiedSnapshot()
    {
        var w = Create("chat-1", "proj-1");
        _sent.Clear();

        _store.Cancel(w.Id, "owner-1", out _);

        var msgs = Broadcasts();
        msgs.Should().NotBeEmpty();
        msgs.Should().OnlyContain(m => m.Sessions.Count == 0 && m.Projects.Count == 0,
            "снятый сторож убирает чат и проект из снимка");
    }

    // --- Цикл сервиса: терминал снимает присутствие и рассылается ---

    private sealed class FakeEnvironment : IWatchdogEnvironment
    {
        public Dictionary<string, Session> Chats { get; } = new();
        public string? WorkDir { get; set; } = "C:\\work";
#pragma warning disable CS0067 // фейк не эмулирует мгновенное гашение по удалению чата
        public event Action<Session>? ChatDeleted;
#pragma warning restore CS0067
        public Session? FindChat(string sessionId, string ownerId) =>
            Chats.TryGetValue(sessionId, out var s) && s.OwnerId == ownerId ? s : null;
        public string? ResolveWorkDir(WatchdogRecord w) => WorkDir;
    }

    private sealed class FakeRunner : IWatchdogCommandRunner
    {
        public PollOutcome Next { get; set; } = PollOutcome.ExitedZero;
        public Task<PollOutcome> RunAsync(string ownerId, string workDir, string command,
            int timeoutSeconds, CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class FakeAlarm : IWatchdogAlarm
    {
        public Task<bool> DeliverAsync(string ownerId, string sessionId, string text) =>
            Task.FromResult(true);
    }

    [Fact]
    public async Task Tick_Fired_PresenceDroppedAndBroadcast()
    {
        var env = new FakeEnvironment();
        env.Chats["chat-1"] = new Session { Id = "chat-1", OwnerId = "owner-1", ProjectId = "proj-1" };
        var w = Create("chat-1", "proj-1");
        var sut = new WatchdogService(_store, env, new FakeRunner { Next = PollOutcome.ExitedZero },
            new FakeAlarm(), NullLogger<WatchdogService>.Instance, _notifier);
        _sent.Clear();

        await sut.TickAsync(DateTime.UtcNow);

        w.Status.Should().Be(WatchdogStatus.Fired);
        _notifier.Snapshot("owner-1").Sessions.Should().BeEmpty("терминальный сторож убран со значков");
        var msgs = Broadcasts();
        msgs.Should().NotBeEmpty("терминал обязан разослать watchdogs_changed");
        msgs.Should().OnlyContain(m => m.Sessions.Count == 0 && m.Projects.Count == 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временный каталог — мусор не критичен */ }
    }
}
