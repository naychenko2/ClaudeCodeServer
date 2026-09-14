using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Tests.Services.Composition;

// Прямой unit-тест адаптера SessionHubBroadcaster (Этап 5, Ф4).
//
// До Ф4 потребители мокали IHubContext<SessionHub> сами, и склейку префиксов
// («user_» + ownerId и т.п.) косвенно проверял каждый такой тест. После перевода
// вертикалей на шов их тесты наблюдают фейк ISessionBroadcaster и видят АДРЕС
// (ownerId), а не ИМЯ ГРУППЫ — то есть правильность склейки не проверяет больше
// никто. Опечатка в префиксе даёт молчаливую недоставку: сообщение уходит в
// несуществующую группу, исключения нет, в логах пусто.
//
// Эти тесты — единственное место, где склейка остаётся под контролем.
public class SessionHubBroadcasterTests
{
    [Fact]
    public async Task ToSession_ШлётВГруппуСИменемРавнымSessionId()
    {
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        var msg = new TextDeltaMessage("hi");
        await broadcaster.ToSession("sess-123", msg);

        recorder.GroupNames.Should().ContainSingle(g => g == "sess-123",
            "адрес сессии — голый sessionId, без префикса");
        recorder.Sent.Should().ContainSingle(t =>
            t.method == "message" && t.group == "sess-123" && ReferenceEquals(t.payload, msg));
    }

    [Fact]
    public async Task ToOwner_ШлётВГруппуСПрефиксомUser()
    {
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        await broadcaster.ToOwner("owner-abc", new TextDeltaMessage("hi"));

        recorder.GroupNames.Should().ContainSingle(g => g == "user_owner-abc",
            "SessionHub.JoinUser кладёт соединение ровно в \"user_\" + userId");
    }

    [Fact]
    public async Task ToProject_ШлётВГруппуСПрефиксомProject()
    {
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        await broadcaster.ToProject("proj-xyz", new TextDeltaMessage("hi"));

        recorder.GroupNames.Should().ContainSingle(g => g == "project_proj-xyz",
            "SessionHub.JoinProject кладёт соединение ровно в \"project_\" + projectId");
    }

    [Fact]
    public async Task ToPreviewLog_ШлётВГруппуФорматаPreviewПроектДвоеточиеСервис()
    {
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        await broadcaster.ToPreviewLog("proj-xyz", "svc-1", new TextDeltaMessage("hi"));

        recorder.GroupNames.Should().ContainSingle(g => g == "preview_proj-xyz:svc-1",
            "формат зеркалится из DevServerService.LogGroup — её же зовёт SessionHub.JoinPreviewLog");
    }

    [Fact]
    public async Task ФорматГруппыPreviewСовпадаетСDevServerServiceLogGroup()
    {
        // Парный тест к предыдущему: имя группы собирают ДВЕ стороны — адаптер (рассылка)
        // и SessionHub через DevServerService.LogGroup (членство). Расхождение = подписчик
        // сидит в одной группе, сообщения летят в другую.
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        await broadcaster.ToPreviewLog("p1", "s1", new TextDeltaMessage("hi"));

        recorder.GroupNames.Should().ContainSingle(
            g => g == ClaudeHomeServer.Services.ProjectServices.DevServerService.LogGroup("p1", "s1"));
    }

    [Fact]
    public async Task ВсеМетодыШлютОднимИменемМетода_Message()
    {
        // Имя метода SignalR — контракт с фронтом (docs/architecture/api.md §SignalR Hub).
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        var msg = new TextDeltaMessage("hi");
        await broadcaster.ToSession("s", msg);
        await broadcaster.ToOwner("o", msg);
        await broadcaster.ToProject("p", msg);
        await broadcaster.ToPreviewLog("p", "svc", msg);

        recorder.Sent.Should().HaveCount(4);
        recorder.Sent.Should().OnlyContain(t => t.method == "message");
    }

    [Fact]
    public async Task СообщениеУходитБезОбёртки()
    {
        // Клиент роутит по ServerMessage.Type; обёртка сломала бы диспетчеризацию.
        var recorder = new Recorder();
        var broadcaster = new SessionHubBroadcaster(recorder.HubContext);

        var msg = new TextDeltaMessage("hi");
        await broadcaster.ToOwner("o", msg);

        recorder.Sent.Should().ContainSingle(t => ReferenceEquals(t.payload, msg));
    }

    // Ручная запись вызовов Group/SendCoreAsync: IHubClients отдаёт IClientProxy, и Moq
    // с этой парой ведёт себя капризно — свой stub проще и нагляднее.
    private sealed class Recorder
    {
        private readonly List<string> _groups = new();
        private readonly List<(string method, string group, object? payload)> _sent = new();

        public IReadOnlyList<string> GroupNames => _groups;
        public IReadOnlyList<(string method, string group, object? payload)> Sent => _sent;

        public IHubContext<SessionHub> HubContext { get; }

        public Recorder() => HubContext = new StubContext(new StubClients(_groups, _sent));
    }

    private sealed class StubContext(StubClients clients) : IHubContext<SessionHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class StubClients(
        List<string> groups,
        List<(string method, string group, object? payload)> sent) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
            throw new NotSupportedException();
        public IClientProxy Group(string groupName)
        {
            groups.Add(groupName);
            return new StubProxy(sent, groupName);
        }
        public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
            throw new NotSupportedException();
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class StubProxy(
        List<(string method, string group, object? payload)> sent, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args,
            CancellationToken cancellationToken = default)
        {
            sent.Add((method, group, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }
}
