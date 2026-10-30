using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// S2a-6c: проводка ChatTurnLogger → журнал событий. К ResultMessage ядро уже обнулило актора
// хода, поэтому в журнале он должен быть тем, кого логгер запомнил внутри хода.
public partial class SessionManagerTests
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
}
