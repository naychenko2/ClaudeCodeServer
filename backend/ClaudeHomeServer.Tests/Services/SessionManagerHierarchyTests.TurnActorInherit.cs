using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Системная подача без инициатора наследует последнего ЯВНОГО актора чата (Session.LastTurnActorId),
// а не UnknownActor; поле переживает рестарт. Чат без явного актора остаётся узким (регрессия О2).
public partial class SessionManagerHierarchyTests
{
    private sealed class MemberAccess(Project project) : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read)
        {
            if (projectId != project.Id || !ProjectAccess.Can(project, callerUserId, right)) return null;
            return new RequestScope(callerUserId!, project.OwnerId!, ProjectAccess.Check(project, callerUserId)!.Value);
        }

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) => null;
    }

    private static void NoteActorOn(SessionManager manager, string sessionId, string? actor, bool fromHuman)
    {
        var sessions = (System.Collections.IDictionary)typeof(SessionManager)
            .GetProperty("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var entry = sessions[sessionId]!;
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        info.Status = SessionStatus.Working; // ход в полёте — актор подачи виден
        typeof(SessionManager).GetMethod("NoteTurnActor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, [entry, actor, fromHuman]);
    }

    private (ActorRightGate Gate, Project Project) GateFor(SessionManager manager, Session session)
    {
        var project = _projectManager.GetById(session.ProjectId!)!;
        project.Members =
        [
            new ProjectMember { UserId = Участник, Role = ProjectRole.Editor, AddedBy = TestUserId },
            new ProjectMember { UserId = ДругойУчастник, Role = ProjectRole.Reader, AddedBy = TestUserId },
        ];
        return (new ActorRightGate(manager, new MemberAccess(project), manager), project);
    }

    [Fact]
    public async Task СистемнаяПодача_ПослеЯвногоВладельца_НаследуетВладельца_ПишущийИнструментОткрыт()
    {
        var (session, _) = await RunChatAsync("tai-owner");
        var (gate, project) = GateFor(_sut, session);
        NoteActor(session.Id, TestUserId, fromHuman: true);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;

        NoteActor(session.Id, null, fromHuman: false);

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(TestUserId);
        gate.Denied("tasks", "tasks_complete", session.Id, TestUserId, project.Id).Should().BeNull();
    }

    [Fact]
    public async Task СистемнаяПодача_ПослеРестарта_АкторБерётсяИзСессии()
    {
        var (session, _) = await RunChatAsync("tai-restart");
        NoteActor(session.Id, TestUserId, fromHuman: true);
        _sut.GetById(session.Id)!.LastTurnActorId.Should().Be(TestUserId);

        var reloaded = BuildSecondManager();
        reloaded.GetById(session.Id)!.LastTurnActorId.Should().Be(TestUserId, "поле сохранено в sessions.json");
        var (gate, project) = GateFor(reloaded, session);

        NoteActorOn(reloaded, session.Id, null, fromHuman: false);

        reloaded.GetTurnActor(session.Id, TestUserId).Should().Be(TestUserId, "реестр в памяти пуст — актор из Session");
        gate.Denied("tasks", "tasks_complete", session.Id, TestUserId, project.Id).Should().BeNull();
    }

    [Fact]
    public async Task СистемнаяПодача_ПослеЯвногоРедактора_ИдётСПравамиРедактора()
    {
        var (session, _) = await RunChatAsync("tai-editor");
        var (gate, project) = GateFor(_sut, session);
        NoteActor(session.Id, Участник, fromHuman: true);
        NoteActor(session.Id, null, fromHuman: false);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник);
        gate.Denied("tasks", "tasks_complete", session.Id, TestUserId, project.Id).Should().BeNull("редактор пишет задачи");
        gate.Denied("wsp", "files_write", session.Id, TestUserId, project.Id).Should().BeNull();
    }

    [Fact]
    public async Task СистемнаяПодача_ПослеЯвногоЧитателя_ЗаписьОтказана()
    {
        var (session, _) = await RunChatAsync("tai-reader");
        var (gate, project) = GateFor(_sut, session);
        NoteActor(session.Id, ДругойУчастник, fromHuman: true);
        NoteActor(session.Id, null, fromHuman: false);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(ДругойУчастник);
        gate.Denied("tasks", "tasks_complete", session.Id, TestUserId, project.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task ЧатБезЯвногоАктора_СистемнаяПодача_Неизвестный_ЗаписьВОбщемПроектеОтказана()
    {
        var (session, _) = await RunChatAsync("tai-none");
        var (gate, project) = GateFor(_sut, session);
        NoteActor(session.Id, null, fromHuman: false);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;

        _sut.GetById(session.Id)!.LastTurnActorId.Should().BeNull();
        _sut.GetTurnActor(session.Id, TestUserId).Should().BeNull("явного актора не было — UnknownActor");
        gate.Denied("tasks", "tasks_complete", session.Id, TestUserId, project.Id).Should().NotBeNull();
    }
}
