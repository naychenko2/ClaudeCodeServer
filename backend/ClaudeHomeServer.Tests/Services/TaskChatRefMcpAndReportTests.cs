using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-4b2 (К5): те же правила маски чужого приватного чата в трёх местах, которых S2d-4b не касался:
/// MCP tasks_get (ForActorMasked), событие доски у вызывающих без явного резолвера (вещатель сам
/// несёт каталог — IChatVisibilitySource) и название чата-исполнителя в карточке доклада
/// (ReportCardAudience + ChatRefs.For). Проверка — по сериализованной строке JSON, не по полю.
/// </summary>
public class TaskChatRefMcpAndReportTests
{
    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string ProjectId = "p-shared";
    private const string ForeignId = "priv-foreign";
    private const string ForeignTitle = "Рабочий разговор редактора";
    private const string OpenId = "open-chat";
    private const string OpenTitle = "Общий разговор проекта";

    private readonly Project _project = new()
    {
        Id = ProjectId, OwnerId = Owner,
        Members = [new() { UserId = Editor, Role = ProjectRole.Editor }],
    };

    private readonly List<Session> _chats;

    public TaskChatRefMcpAndReportTests()
    {
        _chats =
        [
            new() { Id = ForeignId, ProjectId = ProjectId, OwnerId = Editor, PrivateToUserId = Editor, Name = ForeignTitle },
            new() { Id = OpenId, ProjectId = ProjectId, OwnerId = Owner, Name = OpenTitle },
        ];
    }

    private (ISessionDirectory, IProjectManager) Catalogs()
    {
        var s = new Mock<ISessionDirectory>();
        s.Setup(x => x.GetById(It.IsAny<string>())).Returns((string id) => _chats.FirstOrDefault(c => c.Id == id));
        var p = new Mock<IProjectManager>();
        p.Setup(x => x.GetById(It.IsAny<string>())).Returns((string id) => id == ProjectId ? _project : null);
        return (s.Object, p.Object);
    }

    private static TaskItem MakeTask(string chatId) => new()
    {
        Id = "t1", ProjectId = ProjectId, OwnerId = Owner, Title = "Задача",
        SourceSessionId = chatId, LinkedSessionId = chatId,
        ExecutorLaunchedBySessionId = chatId, ReportToSessionId = chatId,
    };

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── (1) MCP tasks_get ───────────────────────────────────────────────────────────────

    /// <summary>Мутация: в ForActorMasked убрать вызов MaskForeignChats (вернуть view) → красный.</summary>
    [Theory]
    [InlineData(Owner)]   // арендатор: маска и для него
    [InlineData(Editor)]  // участник, для которого чат принадлежит другому (ниже чат переписывается на владельца)
    [InlineData(null)]    // актор неизвестен — приватное закрыто и для него (общий чат — отдельный тест ниже)
    public void Mcp_ЧужойПриватныйЧат_БезIdИНазванияВСтроке(string? actor)
    {
        var (s, p) = Catalogs();
        // Редактор — автор ForeignId, поэтому для него чат свой; чужой чат для редактора — чат владельца.
        if (actor == Editor) { _chats[0].PrivateToUserId = Owner; _chats[0].OwnerId = Owner; }
        var json = JsonSerializer.Serialize(TasksToolset.ForActorMasked(MakeTask(ForeignId), Owner, actor, s, p), Wire);

        json.Should().NotContain(ForeignId).And.NotContain(ForeignTitle);
        json.Should().Contain("linkedChat").And.Contain("\"private\":true");
    }

    [Fact]
    public void Mcp_АвторСвоегоПриватногоЧатаИОбщийЧат_КакРаньше()
    {
        var (s, p) = Catalogs();
        var own = JsonSerializer.Serialize(TasksToolset.ForActorMasked(MakeTask(ForeignId), Owner, Editor, s, p), Wire);
        own.Should().Contain(ForeignId, "автор видит свой приватный чат");

        var open = JsonSerializer.Serialize(TasksToolset.ForActorMasked(MakeTask(OpenId), Owner, Editor, s, p), Wire);
        open.Should().Contain(OpenId).And.NotContain("linkedChat");
    }

    [Fact]
    public void Mcp_ИсходныйОбъектСтораНеМеняется()
    {
        var (s, p) = Catalogs();
        var task = MakeTask(ForeignId);
        TasksToolset.ForActorMasked(task, Owner, Owner, s, p);
        task.LinkedSessionId.Should().Be(ForeignId);
        task.LinkedChat.Should().BeNull();
    }

    /// <summary>DEF-83e3 (дефект 83e3701b): неизвестный актор — маска судит только приватность.
    /// Общий чат проекта — как есть: id на месте, формы нет (раньше пустой зритель закрывал и его,
    /// и обычный чат исполнителя уходил в ответе как «Приватный чат участника»). Чужой приватный —
    /// закрыт, как раньше: fail-closed ровно для приватных.
    /// Мутации: убрать пустую ветку в ChatRefs.For (вернуть прежнее «пустой зритель не видит
    /// ничего») — первая проверка красная; заменить в ней Hidden на Visible — вторая красная.</summary>
    [Fact]
    public void Mcp_АкторНеизвестен_ОбщийЧатКакЕсть_ПриватныйЗамаскирован()
    {
        var (s, p) = Catalogs();

        var open = JsonSerializer.Serialize(TasksToolset.ForActorMasked(MakeTask(OpenId), Owner, null, s, p), Wire);
        open.Should().Contain(OpenId, "общий чат не приватный: неизвестный актор видит его, как любой").And.NotContain("linkedChat");

        var priv = JsonSerializer.Serialize(TasksToolset.ForActorMasked(MakeTask(ForeignId), Owner, null, s, p), Wire);
        priv.Should().NotContain(ForeignId).And.NotContain(ForeignTitle);
        priv.Should().Contain("linkedChat").And.Contain("\"private\":true");
    }

    // ── (2) событие доски без явного резолвера ──────────────────────────────────────────

    /// <summary>Мутация: в ToTaskBoard убрать «sessions ??= source.Sessions» → красный.</summary>
    [Fact]
    public async Task ToTaskBoard_ВещательНесётКаталог_МаскаУВызывающихБезРезолвера()
    {
        var (s, p) = Catalogs();
        var cap = new SourceCapture(s, p);

        // Вызов как у TaskExecutionService/TasksToolset: без sessions/projects.
        await cap.ToTaskBoard(Owner, ProjectId, new TaskChangedMessage("updated", MakeTask(ForeignId)));

        foreach (var msg in new[] { cap.ToProjectMsg, cap.ToOwnerMsg })
        {
            var json = JsonSerializer.Serialize(((TaskChangedMessage)msg!).Task, Wire);
            json.Should().NotContain(ForeignId).And.NotContain(ForeignTitle);
        }
    }

    // ── (3) карточка доклада ────────────────────────────────────────────────────────────

    [Fact]
    public void Доклад_АудиторияАдресата_ОбщийПриватныйИВнеПроекта()
    {
        TaskExecutionService.ReportCardAudience(new Session { ProjectId = ProjectId, OwnerId = Owner })
            .GroupProjectId.Should().Be(ProjectId);
        TaskExecutionService.ReportCardAudience(new Session { ProjectId = ProjectId, OwnerId = Owner, PrivateToUserId = Editor })
            .ViewerUserId.Should().Be(Editor);
        TaskExecutionService.ReportCardAudience(new Session { OwnerId = Owner })
            .ViewerUserId.Should().Be(Owner);
    }

    /// <summary>Мутация: в ReportToDelegatorAsync убрать подстановку PrivateFormTitle → красный
    /// (здесь — через те же ChatRefs.For и ReportCardAudience: чужой приватный чат исполнителя
    /// для общего чата-адресата закрыт, для автора этого чата — открыт).</summary>
    [Fact]
    public void Доклад_ЧатИсполнителя_ЗакрытДляОбщегоАдресата_ОткрытАвтору()
    {
        var (s, p) = Catalogs();
        var shared = new Session { ProjectId = ProjectId, OwnerId = Owner };
        var authorsChat = new Session { ProjectId = ProjectId, OwnerId = Editor, PrivateToUserId = Editor };

        ChatRefs.For(s, p, ForeignId, TaskExecutionService.ReportCardAudience(shared))
            .Should().Match<ChatRef>(r => r.Private && r.Title == ChatRef.PrivateFormTitle && r.SessionId == null);
        ChatRefs.For(s, p, ForeignId, TaskExecutionService.ReportCardAudience(authorsChat))
            .Should().Match<ChatRef>(r => !r.Private && r.Title == ForeignTitle);
    }

    private sealed class SourceCapture(ISessionDirectory s, IProjectManager p) : ISessionBroadcaster, IChatVisibilitySource
    {
        public ServerMessage? ToProjectMsg, ToOwnerMsg;
        public ISessionDirectory? Sessions => s;
        public IProjectManager? Projects => p;
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { ToOwnerMsg = message; return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) { ToProjectMsg = message; return Task.CompletedTask; }
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
