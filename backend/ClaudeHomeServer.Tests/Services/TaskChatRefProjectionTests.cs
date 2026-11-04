using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-4b (К5, разрез Александра §3; находка ревью R-S2d-4c): чужой приватный чат в проекции
/// задачи — без id и названия. Решение отдаёт одна функция <see cref="ChatRefs.For"/>; через неё
/// идут список и карточка (TaskView) и событие доски (копия группе и копия владельца в
/// ToTaskBoard). Маска стоит для всех ролей: владелец проекта чужой приватный чат тоже не
/// видит (решение 28.10 о приватности). Автор своего чата, общий чат и чат вне проекта — как раньше.
///
/// Несуществующий чат (его нет в каталоге) и случай «каталог не передан» не маскируются: называть
/// приватным чат, которого нет, — ложь, а его id ничего не открывает.
/// </summary>
public class TaskChatRefProjectionTests
{
    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string Reader = "reader";
    private const string SharedProjectId = "p-shared";

    // Сидированные чаты: чужой приватный (автор — редактор), свой приватный (автор — владелец),
    // общий проектный, чат вне проекта владельца. Названия — отличные от подписи-заглушки,
    // иначе «в JSON нет названия» не отличить от «в JSON есть заглушка».
    private const string ForeignPrivateId = "priv-foreign";
    private const string OwnPrivateId = "priv-own";
    private const string OpenChatId = "open-chat";
    private const string OutsideChatId = "outside-chat";
    private const string ForeignPrivateTitle = "Рабочий разговор редактора";
    private const string OwnPrivateTitle = "Мой разговор владельца";
    private const string OpenChatTitle = "Общий разговор проекта";

    private readonly Project _shared = new()
    {
        Id = SharedProjectId,
        OwnerId = Owner,
        Members =
        [
            new() { UserId = Editor, Role = ProjectRole.Editor },
            new() { UserId = Reader, Role = ProjectRole.Reader },
        ],
    };

    private readonly List<Session> _sessions =
    [
        new() { Id = ForeignPrivateId, ProjectId = SharedProjectId, OwnerId = Editor, PrivateToUserId = Editor, Name = ForeignPrivateTitle, Status = SessionStatus.Waiting },
        new() { Id = OwnPrivateId, ProjectId = SharedProjectId, OwnerId = Owner, PrivateToUserId = Owner, Name = OwnPrivateTitle, Status = SessionStatus.Waiting },
        new() { Id = OpenChatId, ProjectId = SharedProjectId, OwnerId = Owner, Name = OpenChatTitle, Status = SessionStatus.Working },
        new() { Id = OutsideChatId, OwnerId = Owner, Name = "Разговор вне проекта", Status = SessionStatus.Waiting },
    ];

    private static (Mock<ISessionDirectory> Sessions, Mock<IProjectManager> Projects) Catalogs(
        List<Session> sessions, Project? project)
    {
        var s = new Mock<ISessionDirectory>();
        s.Setup(x => x.GetById(It.IsAny<string>())).Returns((string id) => sessions.FirstOrDefault(c => c.Id == id));
        s.Setup(x => x.GetAll()).Returns(() => sessions);
        var p = new Mock<IProjectManager>();
        p.Setup(x => x.GetById(It.IsAny<string>())).Returns((string id) => id == project?.Id ? project : null);
        return (s, p);
    }

    private static TaskItem TaskWithChats() => new()
    {
        Id = "t1",
        ProjectId = SharedProjectId,
        OwnerId = Owner,
        Title = "Задача из чужого приватного чата",
        Description = "Полный текст карточки",
        ResultMarkdown = "Итог карточки",
        SourceSessionId = ForeignPrivateId,
        LinkedSessionId = ForeignPrivateId,
        ExecutorLaunchedBySessionId = ForeignPrivateId,
        ReportToSessionId = ForeignPrivateId,
    };

    // Карточка: участник и владелец.
    private TaskItem CardFor(string viewerId) =>
        TaskView.MaskForeignChats(TaskView.ForMember(TaskWithChats()),
            Catalogs(_sessions, _shared).Sessions.Object, Catalogs(_sessions, _shared).Projects.Object,
            ChatAudience.Viewer(viewerId));

    // Форма ответа как в MVC (camelCase, null не пишется) и relaxed-энкодер: без него кириллица
    // уходит в \u-последовательности, и «в JSON нет названия» стало бы тривиально зелёным.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Json(object obj) => JsonSerializer.Serialize(obj, Wire);

    private static JsonNode WireNode(object obj) => JsonNode.Parse(Json(obj))!;

    /// <summary>Какие из четырёх id-полей закрыты и какие формы стоят рядом — «отпечаток» маски.
    /// Одна форма во всех путях значит равные отпечатки.</summary>
    private static string[] MaskedOf(JsonNode node)
    {
        var o = node.AsObject();
        var masked = new List<string>();
        foreach (var (idKey, refKey) in new[]
        {
            ("linkedSessionId", "linkedChat"), ("sourceSessionId", "sourceChat"),
            ("executorLaunchedBySessionId", "executorLaunchedByChat"), ("reportToSessionId", "reportToChat"),
        })
        {
            var closed = !o.TryGetPropertyValue(idKey, out var id) || id is null;
            var hasForm = o.TryGetPropertyValue(refKey, out var form) && form is not null;
            if (closed != hasForm) masked.Add($"«{idKey}» закрыт: {closed}, форма «{refKey}»: {hasForm}");
            if (hasForm) masked.Add(refKey);
        }
        return [.. masked];
    }

    // Мутация (С2d-4b, критерий c2): в TaskView.MaskForeignChats не вызывать Apply для
    // SourceSessionId — «маска снята» → этот тест красный: в JSON владельца остаются
    // и id чужого приватного чата, и его название.
    [Fact]
    public void КарточкаЧужогоПриватногоЧата_ВJSONНетНиIdНиНазвания_УчастникИВладелец()
    {
        foreach (var viewer in new[] { Owner, Reader })
        {
            var json = Json(CardFor(viewer));

            json.Should().NotContain(ForeignPrivateId, $"id чужого приватного чата не уходит ни владельцу проекта, ни участнику (viewer={viewer})");
            json.Should().NotContain(ForeignPrivateTitle, $"название чужого приватного чата не уходит ни одной из этих ролей (viewer={viewer})");

            var form = WireNode(CardFor(viewer))["sourceChat"]!.AsObject();
            form["private"]!.GetValue<bool>().Should().BeTrue();
            form["authorUserId"]!.GetValue<string>().Should().Be(Editor);
            form["title"]!.GetValue<string>().Should().Be(ChatRef.PrivateFormTitle);
            // Путь владельца — тот же состав полей, что раньше: маска не срезала ничего другого.
            json.Should().Contain("Полный текст карточки");
        }

        // Автор своего чата — как раньше: id на месте, отдельной формы нет (название чата фронт
        // резолвит по своему снимку сессий — в ответе его не было и раньше).
        var forAuthor = Json(CardFor(Editor));
        forAuthor.Should().Contain(ForeignPrivateId);
        WireNode(CardFor(Editor))["sourceChat"].Should().BeNull();
    }

    [Fact]
    public void АвторСвоегоЧатаОбщийИВнеПроекта_КакРаньше_БезФормыЗаглушки()
    {
        var own = new TaskItem { Id = "t2", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = OwnPrivateId };
        var open = new TaskItem { Id = "t3", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = OpenChatId };
        var outside = new TaskItem { Id = "t4", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = OutsideChatId };
        var (sessions, projects) = Catalogs(_sessions, _shared);

        var forOwner = TaskView.MaskForeignChats(TaskView.CopyOf(own), sessions.Object, projects.Object, ChatAudience.Viewer(Owner));
        var forOpen = TaskView.MaskForeignChats(TaskView.CopyOf(open), sessions.Object, projects.Object, ChatAudience.Viewer(Reader));
        var forOutside = TaskView.MaskForeignChats(TaskView.CopyOf(outside), sessions.Object, projects.Object, ChatAudience.Viewer(Owner));

        forOwner.SourceSessionId.Should().Be(OwnPrivateId, "автор своего приватного чата видит его id");
        forOwner.SourceChat.Should().BeNull("формы нет там, где id на месте");
        forOpen.SourceSessionId.Should().Be(OpenChatId, "общий чат проекта виден всем участникам");
        forOpen.SourceChat.Should().BeNull();
        forOutside.SourceSessionId.Should().Be(OutsideChatId, "свой чат вне проекта — как раньше");
        forOutside.SourceChat.Should().BeNull();

        // Тот же чат для чужого участника — закрыт: приватность держит автора, не проект.
        var foreignForReader = TaskView.MaskForeignChats(TaskView.CopyOf(own), sessions.Object, projects.Object, ChatAudience.Viewer(Reader));
        foreignForReader.SourceSessionId.Should().BeNull();
        foreignForReader.SourceChat.Should().NotBeNull();
    }

    /// <summary>Критерий c2: список, карточка и копия группы в ToTaskBoard дают одну форму —
    /// равные отпечатки маски (одна функция на все три пути).</summary>
    [Fact]
    public async Task СписокКарточкаИСобытиеДоски_ОднаФормаМаски()
    {
        var task = TaskWithChats();
        var (sessions, projects) = Catalogs(_sessions, _shared);

        var list = TaskView.MaskForeignChats(TaskView.ForMemberListItem(task), sessions.Object, projects.Object, ChatAudience.Viewer(Reader));
        var card = TaskView.MaskForeignChats(TaskView.ForMember(task), sessions.Object, projects.Object, ChatAudience.Viewer(Reader));

        var cap = new Capture();
        await cap.ToTaskBoard(Owner, SharedProjectId, new TaskChangedMessage("updated", task), sessions.Object, projects.Object);
        var groupCopy = ((TaskChangedMessage)cap.ToProjectMsg!).Task;

        var fromList = MaskedOf(WireNode(list));
        var fromCard = MaskedOf(WireNode(card));
        var fromBoard = MaskedOf(WireNode(groupCopy));

        fromCard.Should().Equal(fromList);
        fromBoard.Should().Equal(fromList);
        fromBoard.Should().Contain(["sourceChat", "linkedChat", "executorLaunchedByChat", "reportToChat"],
            "все четыре id-поля чужого приватного чата закрыты во всех трёх путях");
    }

    /// <summary>Мутация: в ISessionBroadcaster.ToTaskBoard в ForOwner вернуть message без маски
    /// («маска у владельца снята») → этот тест красный: ToOwnerMsg несёт priv-foreign.</summary>
    [Fact]
    public async Task СобытиеДоски_ВладельцуПолнаяКарточкаНоБезЧужогоПриватного_ИсходникНеМеняется()
    {
        var task = TaskWithChats();
        var (sessions, projects) = Catalogs(_sessions, _shared);
        var cap = new Capture();

        await cap.ToTaskBoard(Owner, SharedProjectId, new TaskChangedMessage("updated", task), sessions.Object, projects.Object);

        var forOwner = ((TaskChangedMessage)cap.ToOwnerMsg!).Task;
        forOwner.Description.Should().Be("Полный текст карточки", "карточка владельца — полная, как раньше");
        forOwner.ResultMarkdown.Should().Be("Итог карточки");
        forOwner.SourceSessionId.Should().BeNull("владелец проекта чужой приватный чат не видит");
        forOwner.SourceChat!.Private.Should().BeTrue();

        task.SourceSessionId.Should().Be(ForeignPrivateId, "исходный TaskItem (запись стора) не правится");
        task.SourceChat.Should().BeNull();
    }

    /// <summary>Одна копия на всю группу: приватный чат закрыт и для его автора — свой чат он
    /// видит точковыми путями (список и карточка REST), группе отдаётся только общий чат проекта.</summary>
    [Fact]
    public async Task СобытиеДоскиГруппе_ПриватныйЧатЗакрытИДляАвтора_ОбщийОстаётся()
    {
        var privateTask = new TaskItem { Id = "t5", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = OwnPrivateId };
        var openTask = new TaskItem { Id = "t6", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = OpenChatId };
        var (sessions, projects) = Catalogs(_sessions, _shared);
        var cap = new Capture();

        await cap.ToTaskBoard(Owner, SharedProjectId, new TaskChangedMessage("updated", privateTask), sessions.Object, projects.Object);
        ((TaskChangedMessage)cap.ToProjectMsg!).Task.SourceSessionId.Should().BeNull(
            "приватный чат владельца закрыт и группе, и его автору в группе: один payload на всех");

        await cap.ToTaskBoard(Owner, SharedProjectId, new TaskChangedMessage("updated", openTask), sessions.Object, projects.Object);
        ((TaskChangedMessage)cap.ToProjectMsg!).Task.SourceSessionId.Should().Be(OpenChatId);
        ((TaskChangedMessage)cap.ToProjectMsg!).Task.SourceChat.Should().BeNull();
    }

    [Fact]
    public void НесуществующийЧатИОтсутствующийКаталог_НеМаскируются()
    {
        var task = new TaskItem { Id = "t7", ProjectId = SharedProjectId, OwnerId = Owner, SourceSessionId = "gone-chat" };
        var (sessions, projects) = Catalogs(_sessions, _shared);

        var deleted = TaskView.MaskForeignChats(TaskView.CopyOf(task), sessions.Object, projects.Object, ChatAudience.Viewer(Owner));
        deleted.SourceSessionId.Should().Be("gone-chat", "чата нет: называть его приватным — ложь, и его id ничего не открывает");
        deleted.SourceChat.Should().BeNull();

        var noCatalog = TaskView.MaskForeignChats(TaskView.CopyOf(task), null, null, ChatAudience.Viewer(Owner));
        noCatalog.SourceSessionId.Should().Be("gone-chat", "резолвера нет — решения нет: поведение прежнее");
        noCatalog.SourceChat.Should().BeNull();

        var foreignNoCatalog = TaskView.MaskForeignChats(TaskView.CopyOf(TaskWithChats()), null, null, ChatAudience.Viewer(Reader));
        foreignNoCatalog.SourceChat.Should().BeNull();
    }

    private sealed class Capture : ISessionBroadcaster
    {
        public ServerMessage? ToProjectMsg, ToOwnerMsg;
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { ToOwnerMsg = message; return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) { ToProjectMsg = message; return Task.CompletedTask; }
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
