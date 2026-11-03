using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// S2a-6c: проводка ChatTurnLogger → журнал событий. К ResultMessage ядро уже обнулило актора
// хода, поэтому в журнале он должен быть тем, кого логгер запомнил внутри хода.
public partial class SessionManagerHierarchyTests
{
    private async Task RaiseSessionMessageAsync(Session session, ServerMessage msg)
    {
        var field = typeof(SessionManager).GetField("OnSessionMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Func<Session, ServerMessage, Task>)field.GetValue(_sut)!)(session, msg);
    }

    [Fact]
    public async Task ХодУчастника_ChatTurnВЖурнале_СActorUserIdУчастника()
    {
        var (session, fake) = await RunChatAsync("ctl-actor");
        var dir = Path.Combine(Path.GetTempPath(), "ccs-ctl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
            }).Build();
            using var events = new ProjectEventLogService(config);
            var logger = new ChatTurnLoggerService(_sut, events, _projectManager, _personaManager);
            await logger.StartAsync(CancellationToken.None);
            try
            {
                await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
                await RaiseSessionMessageAsync(session, new TruncatedMessage()); // сообщение внутри хода

                EndTurn(session, fake); // ядро сняло статус хода и актора
                _sut.GetTurnActor(session.Id, TestUserId).Should().BeNull("предусловие: к result актор уже снят");
                await RaiseSessionMessageAsync(session, new ResultMessage("success", 0, 1, null, null));

                var deadline = DateTime.UtcNow.AddSeconds(5);
                IReadOnlyList<ProjectEvent> found = [];
                while (DateTime.UtcNow < deadline && found.Count == 0)
                {
                    found = events.Query(session.ProjectId!, TestUserId, type: ProjectEventTypes.ChatTurn);
                    if (found.Count == 0) await Task.Delay(20);
                }
                found.Should().ContainSingle().Which.ActorUserId.Should().Be(Участник);
            }
            finally { await logger.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    // Внутренний словарь запомненных акторов хода (S2a-6c): у приватного чата в него попадать
    // нечего — выход из OnMsgAsync идёт до этой ветки, словарь не растёт за приватные чаты.
    private static bool LoggerRemembersActor(ChatTurnLoggerService logger, string sessionId)
    {
        var field = typeof(ChatTurnLoggerService).GetField("_turnActors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var actors = (System.Collections.Concurrent.ConcurrentDictionary<string, string>)field.GetValue(logger)!;
        return actors.ContainsKey(sessionId);
    }

    // S2d-6d: ход приватного чата в журнал проекта не пишется вовсе — журнал читают все участники
    // через GET /api/projects/{id}/events без фильтра по видимости. Первый (общий) ход того же чата
    // стоит сентинелом: он обязан дать ровно одно событие — иначе «приватный молчит» было бы
    // пустой проверкой сломанной проводки. После перевода чата в приватные: ни нового события,
    // ни нового актора в словаре (выход из OnMsgAsync идёт до этой ветки).
    [Theory]
    [InlineData(TestUserId)]
    [InlineData("")]
    public async Task ПриватныйХод_НичегоВЖурнале_ИАкторНеЗапоминается(string privateTo)
    {
        var (session, fake) = await RunChatAsync("ctl-priv-" + privateTo);
        var dir = Path.Combine(Path.GetTempPath(), "ccs-ctl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
            }).Build();
            using var events = new ProjectEventLogService(config);
            var logger = new ChatTurnLoggerService(_sut, events, _projectManager, _personaManager);
            await logger.StartAsync(CancellationToken.None);
            try
            {
                // Сентинел: общий ход того же чата пишет событие — проводка жива, окно достаточно
                await RaiseFullTurnAsync(session, fake, "из общего чата");
                var found = await WaitForEventsAsync(events, session.ProjectId!, TestUserId, 5);
                found.Should().ContainSingle("предусловие: общий ход чата пишет chat_turn");

                session.PrivateToUserId = privateTo; // тот же признак приватности, что у SessionAccess.CanSee

                await _sut.SendMessageAndWaitAsync(session.Id, "из приватного чата", TimeSpan.Zero,
                    senderUserId: TestUserId);
                await RaiseSessionMessageAsync(session, new TruncatedMessage()); // сообщение внутри хода
                LoggerRemembersActor(logger, session.Id).Should().BeFalse(
                    "приватный чат не должен попадать в словарь запомненных акторов хода");
                EndTurn(session, fake); // ядро сняло статус хода и актора
                await RaiseSessionMessageAsync(session, new ResultMessage("success", 0, 1, null, null));

                // Окно, за которое append (он идёт Task.Run) точно успел бы появиться:
                // любое новое событие журнала = провал
                var quiet = DateTime.UtcNow.AddSeconds(1.5);
                while (DateTime.UtcNow < quiet)
                {
                    events.Query(session.ProjectId!, TestUserId, type: ProjectEventTypes.ChatTurn)
                        .Should().HaveCount(1, "ход приватного чата не добавил событие в журнал проекта");
                    await Task.Delay(50);
                }
            }
            finally { await logger.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    // Полный ход «до result»: сообщение → сообщение внутри хода → конец хода в ядре → result
    private async Task RaiseFullTurnAsync(Session session, RunFake fake, string text)
    {
        await _sut.SendMessageAndWaitAsync(session.Id, text, TimeSpan.Zero, senderUserId: TestUserId);
        await RaiseSessionMessageAsync(session, new TruncatedMessage());
        EndTurn(session, fake);
        await RaiseSessionMessageAsync(session, new ResultMessage("success", 0, 1, null, null));
    }

    // Снимок журнала по типу chat_turn, ожидание появления — до дедлайна
    private static async Task<IReadOnlyList<ProjectEvent>> WaitForEventsAsync(ProjectEventLogService events,
        string projectId, string ownerId, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var found = events.Query(projectId, ownerId, type: ProjectEventTypes.ChatTurn);
            if (found.Count > 0 || DateTime.UtcNow >= deadline) return found;
            await Task.Delay(20);
        }
    }
}
