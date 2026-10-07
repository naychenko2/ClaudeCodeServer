using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.Services.Mcp.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// SessionManager.RecordToolStarted — фактический старт MCP-инструмента с прогрессом
// (run_tests, build, start_stand), пришедший из его tools/call мимо пампа CLI
public class SessionManagerToolStartedTests : IDisposable
{
    private readonly string _dir;

    public SessionManagerToolStartedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tool_started_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_dir);
        GC.SuppressFinalize(this);
    }

    // Карточка уже в ленте — tool_started уходит сразу; ещё не ушла — событие не шлётся
    // (клиент выбросил бы старт неизвестной карточки) и уходит следом за её анонсом
    [Fact]
    public async Task RecordToolStarted_КарточкаВЛенте_ШлётToolStarted_ИначеЖдёт()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
        }).Build();
        var broadcaster = new TestSessionBroadcaster();
        var (sessions, projects, _) = TestsToolsetTests.BuildSessionManager(config, broadcaster);
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj")).FullName;
        var project = projects.Create("Started", projDir, "started-user", "started");
        var session = await sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = sessions.AccumulatorOf(session.Id);
        acc.OnToolUse("t1", "mcp__dev__build", new { }, startedAt: 1_000);
        acc.OnToolUse("t2", "mcp__dev__build", new { }, startedAt: 1_000);
        acc.OnToolAnnounced("t1");

        sessions.RecordToolStarted(session.Id, "t1");
        sessions.RecordToolStarted(session.Id, "t2");

        var started = broadcaster.Session.Select(t => t.Message).OfType<ToolStartedMessage>().ToList();
        started.Should().ContainSingle().Which.ToolUseId.Should().Be("t1");
        started[0].StartedAt.Should().BeGreaterThan(1_000);
        started[0].SessionId.Should().Be(session.Id);
        acc.OnToolAnnounced("t2").Should().NotBeNull("отложенный старт уходит следом за карточкой");
    }
}
