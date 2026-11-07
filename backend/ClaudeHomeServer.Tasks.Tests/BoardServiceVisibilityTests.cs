using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Tasks;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-4c (находка ревью R-S2d-3b §3): доска агентов (GET /api/board/agents) не показывает
/// чужой приватный чат зрителю: для невидимого чата (SessionAccess.CanSee ложно) BoardItem
/// теряет SessionId и PermissionPending — позиция остаётся в своей колонке, без ссылки на чат
/// (allow/deny по чужому чату закрыл S2d-3b). Автор своего приватного, общий чат, чат вне
/// проекта и чат личного проекта — как раньше. Проверка — видимость, а не владение: владельца
/// проектного чата выводит проект, и по владению участник потерял бы ссылку на свой живой
/// агент в общем чате.
/// Настоящий сервис — BoardService с настоящим TaskManager; каталоги сессий и проектов —
/// Core-швы (тот же образец Moq-сидиров, что SharedProjectTaskRoutingTests).
/// </summary>
public class BoardServiceVisibilityTests : IDisposable
{
    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string SharedProjectId = "p-shared";
    private const string PersonalProjectId = "p-personal";

    // Сидированные чаты: чужой приватный (автор — редактор), свой приватный (автор — владелец),
    // общий проектный, чат вне проекта владельца, чат личного проекта.
    private const string ForeignPrivateId = "priv-foreign";
    private const string OwnPrivateId = "priv-own";
    private const string OpenChatId = "open-chat";
    private const string OutsideChatId = "outside-chat";
    private const string PersonalChatId = "personal-chat";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s2d4c_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;

    private readonly Project _shared = new()
    {
        Id = SharedProjectId,
        OwnerId = Owner,
        Members = [new() { UserId = Editor, Role = ProjectRole.Editor }],
    };
    private readonly Project _personal = new() { Id = PersonalProjectId, OwnerId = Owner };

    private readonly List<Session> _sessions =
    [
        new() { Id = ForeignPrivateId, ProjectId = SharedProjectId, PrivateToUserId = Editor, Status = SessionStatus.Waiting },
        new() { Id = OwnPrivateId, ProjectId = SharedProjectId, PrivateToUserId = Owner, Status = SessionStatus.Waiting },
        new() { Id = OpenChatId, ProjectId = SharedProjectId, Status = SessionStatus.Working },
        new() { Id = OutsideChatId, OwnerId = Owner, Status = SessionStatus.Waiting },
        new() { Id = PersonalChatId, ProjectId = PersonalProjectId, Status = SessionStatus.Working },
    ];

    public BoardServiceVisibilityTests()
    {
        Directory.CreateDirectory(_dir);
        _tasks = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build());
    }

    public void Dispose()
    {
        _tasks.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private BoardService Sut()
    {
        var sessions = new Mock<ISessionDirectory>();
        sessions.Setup(s => s.GetById(It.IsAny<string>()))
            .Returns((string id) => _sessions.FirstOrDefault(x => x.Id == id));
        sessions.Setup(s => s.GetAll()).Returns(() => _sessions);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(It.IsAny<string>()))
            .Returns((string id) => id == SharedProjectId ? _shared : id == PersonalProjectId ? _personal : null);
        return new BoardService(_tasks, sessions.Object, projects.Object);
    }

    private TaskItem NewTask(string title, string ownerId, string? projectId, string? sessionId) =>
        _tasks.Create(projectId, ownerId, new CreateTaskRequest(title, Assignee: TaskItemAssignee.Claude, LinkedSessionId: sessionId));

    private BoardItem ItemOf(string viewerId, string taskId) =>
        Sut().GetBoard(viewerId).Single(i => i.TaskId == taskId);

    // Мутация: в BoardService.GetBoard заменить visibleSession на сам session (убрать проверку
    // видимости) → этот тест красный: SessionId = priv-foreign, PermissionPending = true.
    [Fact]
    public void Доска_ЧужойПриватныйЧат_ВладелецНеВидитНиIdНиБейдж_ПозицияОстаётся()
    {
        var task = NewTask("чужой приватный", Owner, SharedProjectId, ForeignPrivateId);

        var item = ItemOf(Owner, task.Id);

        item.SessionId.Should().BeNull("id чужого приватного чата не виден даже владельцу проекта (решение 28.10)");
        item.PermissionPending.Should().BeFalse("бейдж «нужен ответ» по чужому приватному чату не показывается");
        // Позиция — без ссылки на чат: задача и её колонка исполнения остаются
        item.Column.Should().Be("waiting");
    }

    [Fact]
    public void Доска_АвторСвоегоПриватногоЧата_IdИБейджКакРаньше()
    {
        var task = NewTask("своя в своём приватном", Editor, SharedProjectId, ForeignPrivateId);

        var item = ItemOf(Editor, task.Id);

        item.SessionId.Should().Be(ForeignPrivateId);
        item.PermissionPending.Should().BeTrue();
        item.Column.Should().Be("waiting");
    }

    [Fact]
    public void Доска_СвойПриватныйОбщийИВнеПроекта_КакРаньше()
    {
        var ownPrivate = NewTask("свой приватный", Owner, SharedProjectId, OwnPrivateId);
        var open = NewTask("общий проектный", Owner, SharedProjectId, OpenChatId);
        var outside = NewTask("вне проекта", Owner, null, OutsideChatId);

        var own = ItemOf(Owner, ownPrivate.Id);
        own.SessionId.Should().Be(OwnPrivateId);
        own.PermissionPending.Should().BeTrue();

        var common = ItemOf(Owner, open.Id);
        common.SessionId.Should().Be(OpenChatId);
        common.Column.Should().Be("working");
        common.PermissionPending.Should().BeFalse();

        var outItem = ItemOf(Owner, outside.Id);
        outItem.SessionId.Should().Be(OutsideChatId);
        outItem.PermissionPending.Should().BeTrue();
    }

    // Основной массовый случай доски — личные проекты: не затронут маской вовсе.
    [Fact]
    public void Доска_ЛичныйПроект_КакРаньше()
    {
        var task = NewTask("личная", Owner, PersonalProjectId, PersonalChatId);

        var item = ItemOf(Owner, task.Id);

        item.SessionId.Should().Be(PersonalChatId);
        item.Column.Should().Be("working");
    }
}
