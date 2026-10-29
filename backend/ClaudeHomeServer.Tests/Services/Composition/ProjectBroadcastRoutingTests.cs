using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Composition;

// S1-5: проектные рассылки идут группе проекта (участники общего проекта), личные — владельцу.
public class ProjectBroadcastRoutingTests
{
    private sealed class Spy : ISessionBroadcaster
    {
        public List<(string kind, string id)> Calls { get; } = [];
        public Task ToSession(string sessionId, ServerMessage message) { Calls.Add(("session", sessionId)); return Task.CompletedTask; }
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) { Calls.Add(("session", sessionId)); return Task.CompletedTask; }
        public Task ToOwner(string ownerId, ServerMessage message) { Calls.Add(("owner", ownerId)); return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) { Calls.Add(("project", projectId)); return Task.CompletedTask; }
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) { Calls.Add(("log", projectId)); return Task.CompletedTask; }
    }

    private static readonly ServerMessage Msg = new TaskChangedMessage("updated", new TaskItem { Id = "t1" });

    [Fact]
    public async Task ToTaskBoard_ЗадачаПроекта_УчастникамГруппыИВладельцу()
    {
        var spy = new Spy();

        await spy.ToTaskBoard("owner-1", "proj-1", Msg);

        spy.Calls.Should().Equal(("project", "proj-1"), ("owner", "owner-1"));
    }

    [Fact]
    public async Task ToTaskBoard_ЛичнаяЗадача_ТолькоВладельцу_ГруппаПроектаНеТрогается()
    {
        var spy = new Spy();

        await spy.ToTaskBoard("owner-1", null, Msg);
        await spy.ToTaskBoard("owner-1", "", Msg);

        spy.Calls.Should().Equal(("owner", "owner-1"), ("owner", "owner-1"));
    }

    [Fact]
    public async Task ToProjectOrOwner_СПроектом_ТолькоГруппеПроекта_БезПроекта_Владельцу()
    {
        var spy = new Spy();

        await spy.ToProjectOrOwner("owner-1", "proj-1", Msg);
        await spy.ToProjectOrOwner("owner-1", null, Msg);

        spy.Calls.Should().Equal(("project", "proj-1"), ("owner", "owner-1"));
    }
}
