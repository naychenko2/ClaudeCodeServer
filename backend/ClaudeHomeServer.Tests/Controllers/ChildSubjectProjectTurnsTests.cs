using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б8d (ADR-041 И9′, решение 85): куратор проекта-предмета читает, но не пишет.
/// Куратор — участник Editor общего проекта-предмета (тот же класс участника, что в
/// SharedProject* тестах; членство пишет синхронизация профиля, Б14b — здесь оно записано
/// прямо в стор, ручки членства вне шага). Его право Read даёт чтение уроков штатной
/// SessionAccess.CanSee (уроки без PrivateToUserId, Б9a); право RunTurn закрывает профиль
/// проекта-предмета ProjectCapabilities.ChildSubjectProfile.TurnsOnlyForChildChats (Б8a):
/// POST создания чата — 409 child_subject_project (SessionsController.Create), ход в уроке
/// от не-владельца — отказ ядра приёма (TurnIntake.ChildTurnRefusal → RefuseTurnAsync:
/// причина в ленту, процесс не тронут). Ход самого ребёнка-владельца и обычный общий проект
/// с Editor — без изменений.
///
/// Ход проверяется на ядре приёма прявым вызовом SendMessageAsync — ровно тем, что зовёт хаб
/// (SessionHub.cs:307-309 передаёт senderUserId из JWT sub); транспорт хаба проверяет
/// ChildHubFilterTests. Под процесса — считющий мокадтер (тот же приём, что
/// ChatsSendAwaitingHumanClearTests.SetTurnAdapter): по нему видно, что ход не состоялся,
/// а сверка Compose не подменяет отказ шага (EnsureProcessCoreAsync выходит при
/// entry.Process is not null, TurnProcessHost.cs:189).
///
/// Мутации (каждая краснит ровно названное):
/// • «TurnsOnlyForChildChats=false в профиле» — краснеют КураторPOSTСозданияЧата (201 вместо
///   409) и КураторСообщениеВУроке (ход дошёл до мокадтера);
/// • снять гейт в SessionsController.Create — краснеет КураторPOSTСозданияЧата;
/// • снять ChildTurnRefusal из SendDirectAsync — краснеет КураторСообщениеВУроке;
/// • снять сверку отправителя (оставить только признак ChildSpace) — краснеет
///   РебёнокВладелецЗапускаетХодСвоегоУрока (отказался бы и ход владельца);
/// • снять ChildTurnRefusal из SendMessageAndWaitAsync (Х-W4-73, дефект 358bfe10) — краснеет
///   КураторRESTСообщениеВУрок: 202 running вместо 409, реплика куратора и session_started
///   в ленте урока; поставить тот запрет без сверки владельца — краснеет
///   РебёнокRESTСообщениеВСвоёмУроке (отказался бы и ход самого ребёнка).
/// </summary>
[Trait("Category", "Host")]
public class ChildSubjectProjectTurnsTests : IClassFixture<ChildSubjectProjectTurnsTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            // Куратор — участник общего проекта: без рубильника доступ участника к чужому
            // проекту закрыт (SharedProjectsGate, тот же порог, что в ChildChatReadTests).
            ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        }
    }

    private const string SubjectId = "russian";
    private const string KidPassword = "bsp-kid-pw-123456";
    private const string CuratorPassword = "bsp-cur-pw-123456";

    private readonly Hosts _hosts;

    public ChildSubjectProjectTurnsTests(Hosts hosts)
    {
        _hosts = hosts;
    }

    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _hosts.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _hosts.Services.GetRequiredService<UserStore>();
    private PersonaManager Personas => _hosts.Services.GetRequiredService<PersonaManager>();

    // ── фикстура: ребёнок-владелец, проект-предмет, куратор Editor, урок ──────────

    // Проект-предмет (маркер ChildSubjectId, форма Б8b/ADR-041 ред. 5) + куратор с ролью
    // Editor (тот же состав участника, что записывает SyncChildCuratorsAsync) + флаг
    // общих проектов куратора. Профиль ребёнка (ChildProfile) здесь не заводится: ни одна
    // проверяемая точка его не читает — детская сверка хода (LaunchMismatch) за живым
    // маршрутом принадлежит другим шагам волны.
    private (string ChildId, string CuratorId, Project Project) SubjectProjectWithCurator(string tag)
    {
        var child = Users.Add("bsp-kid-" + tag, KidPassword, "user");
        var curator = Users.Add("bsp-cur-" + tag, CuratorPassword, "user");
        var dir = Path.Combine(_hosts.TempDir, "bsp_" + tag);
        var project = Projects.Create("Предмет-" + tag, dir, child.Id, child.Username,
            createDirectory: true, childSubjectId: SubjectId);
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = curator.Id, Role = ProjectRole.Editor, AddedBy = child.Id }]);
        Users.SetFeatureFlag(curator.Id, FeatureFlagKeys.SharedProjects, true);
        return (child.Id, curator.Id, project);
    }

    // Урок — чат проекта-предмета с детской меткой (ChildSpace/ChildSubject, Session.cs:541,551).
    // Детская точка создания урока (B9b) вне шага — метка ставится прямо, как в
    // ChildChatReadTests. Собеседник — руководитель проекта (реальная форма ADR-041 ред. 7).
    private async Task<string> LessonAsync(string projectId, string childId, bool withChildMark = true)
    {
        var tutor = Personas.Create(childId, "Репетитор-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Project, projectId,
            null, null, false);
        Projects.SetDefaultPersona(projectId, tutor.Id);
        var chat = await Sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "Урок",
            personaId: tutor.Id, createdByUserId: childId);
        if (withChildMark)
        {
            chat.ChildSpace = ChildSpaces.Lessons;
            chat.ChildSubject = SubjectId;
        }
        Sessions.SaveSessions();
        return chat.Id;
    }

    private HttpClient As(string username, string password) =>
        _hosts.CreateAuthenticatedClient(username, password);

    private HttpClient Curator(string tag) => As("bsp-cur-" + tag, CuratorPassword);

    // ── мок-адаптер на месте процесса: считает подачи, живого CLI нет ────────────────

    // Тот же приём, что ChatsSendAwaitingHumanClearTests.SetTurnAdapter/GetEntry (white-box
    // к SessionEntry через рефлексию — публичного доступа к ядру нет). HasLiveTurn=false:
    // чат свободен, сообщение идёт прямым путём, не в очередь.
    private Mock<ILlmSessionAdapter> SetCountingAdapter(string sessionId)
    {
        var map = (System.Collections.IDictionary)typeof(SessionManager)
            .GetProperty("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Sessions)!;
        var entry = map[sessionId]!;
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.SubmittedTurnSeq).Returns(0);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        return adapter;
    }

    private static void AssertTurnNotSent(Mock<ILlmSessionAdapter> adapter, string what) =>
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never, what);

    private static void AssertTurnSent(Mock<ILlmSessionAdapter> adapter, string what) =>
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.AtLeastOnce, what);

    // ── c1.1: POST создания чата куратором в проект-предмет — 409 child_subject_project ──

    [Fact]
    public async Task КураторPOSTСозданияЧата_409_ChildSubjectProject()
    {
        var (_, _, project) = SubjectProjectWithCurator("create409");

        var resp = await Curator("create409").PostAsJsonAsync($"/api/projects/{project.Id}/sessions",
            new { mode = "acceptEdits" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_subject_project");
        // Отказанное создание не оставило следа: в проекте-предмете ни одного чата
        Sessions.GetByProject(project.Id).Should().BeEmpty();
    }

    // ── c1.2: сообщение куратора в урок — ход не запущен, отказ в ленту ───────────

    [Fact]
    public async Task КураторСообщениеВУроке_ХодНеЗапущен_ОтказВЛенте()
    {
        var (childId, curatorId, project) = SubjectProjectWithCurator("msgrefuse");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        // Тот же вызов, что делает хаб SendMessage (SessionHub.cs:307-309) с sub куратора
        await Sessions.SendMessageAsync(lessonId, "проверю-ка я этот урок", [], senderUserId: curatorId);

        AssertTurnNotSent(adapter, "отказанный ход не дошёл до процесса");
        Sessions.GetById(lessonId)!.Status.Should().NotBe(SessionStatus.Working,
            "ход не стартовал");
        // Причина отказа — в ленте (RefuseTurnAsync пишет её через аккумулятор):
        // куратор видит не молчание, а отказ
        var history = await Sessions.GetHistoryAsync(lessonId);
        // Предикат FluentAssertions — expression tree: «is» в нём разбирается только как чистая
        // сверка типа (без последующего обращения к полям), поэтому тип выбирается OfType,
        // а предикат простой (тот же приём, что SessionManagerBranchTests.cs:446)
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text.Contains("владелец-ребёнок"), "отказ назван в ленте");
        // Реплика куратора в историю урока не легла: он не участник переписки
        history.OfType<StoredUserMessage>().Should().NotContain(m => m.Text == "проверю-ка я этот урок");
    }

    // ── Х-W4-73 (дефект 358bfe10): тот же отказ держит и REST-канал агентов ─────────

    // Рево дефекта: POST /api/sessions/{lessonId}/messages токеном куратора давал 202 running,
    // а в ленте урока поднимались его реплика и session_started — проверка ChildTurnRefusal
    // стояла только в SendDirectAsync (хаб-путь). Проверка на боевой ручке канала, с её
    // оркестрацией (SessionMessagingService → SendMessageAndWaitAsync), а не на ядре напрямую.
    [Fact]
    public async Task КураторRESTСообщениеВУрок_409_БезРепликиИБезХода()
    {
        var (childId, _, project) = SubjectProjectWithCurator("restrefuse");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        var resp = await Curator("restrefuse").PostAsJsonAsync($"/api/sessions/{lessonId}/messages",
            new { text = "проверю-ка я этот урок", wait = "none" });

        // 409 с текстом ядра, а не 202 running: ход не начался и не начнётся — «попробуй
        // позже» здесь было бы ложью
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "отказ ядра доходит до вызывающего");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Ход детского чата запускает только его владелец-ребёнок",
            "тот же текст, что у хаб-пути: он рождается в ChildTurnRefusal, здесь только переносится");
        body.Should().Contain("child_turn_owner_only", "машинный код этой двери — ряд соседних child_* отказов");

        AssertTurnNotSent(adapter, "отказанный ход не дошёл до процесса");
        Sessions.GetById(lessonId)!.Status.Should().NotBe(SessionStatus.Working, "ход не стартовал");

        // В ленте урока — ничего: ни реплики куратора, ни начала хода, ни отказа в ленту
        // (текст принадлежит вызывающему: в chats_send ход просил другой чат)
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredUserMessage>().Should().NotContain(m => m.Text == "проверю-ка я этот урок",
            "реплика куратора в историю урока не легла");
        history.OfType<StoredSessionStartedMessage>().Should().BeEmpty("хода не было");
        history.OfType<StoredErrorMessage>().Should().BeEmpty("отказ — вызывающему, не в ленту ребёнка");
    }

    // ── Х-W4-73: отказ не закрывает этот же канал самому владельцу-ребёнку ──────────

    // Обратная сторона той же границы: если бы запрет в REST-канале был поставлен широче
    // (без сверки отправителя с владельцем), красный был бы этот тест, а не только соседний.
    [Fact]
    public async Task РебёнокRESTСообщениеВСвоёмУроке_ХодИдёт()
    {
        var (childId, _, project) = SubjectProjectWithCurator("restkid");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        var resp = await As("bsp-kid-restkid", KidPassword).PostAsJsonAsync(
            $"/api/sessions/{lessonId}/messages", new { text = "объясни мне дроби", wait = "none" });

        ((int)resp.StatusCode).Should().BeOneOf([200, 202],
            $"подача владельца-ребёнка: {await resp.Content.ReadAsStringAsync()}");
        AssertTurnSent(adapter, "ход владельца-ребёнка не закрыт запретом REST-канала");
    }

    // ── c1.3: куратор читает историю урока — 200 с сообщениями ────────────────────

    [Fact]
    public async Task КураторЧитаетИсториюУрока_200_ССообщениями()
    {
        var (childId, _, project) = SubjectProjectWithCurator("readhist");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        // Содержание урока делает сам ребёнок-владелец (с его стороны отказа нет)
        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);
        AssertTurnSent(adapter, "ход ребёнка дошёл до процесса");

        var resp = await Curator("readhist").GetAsync($"/api/projects/{project.Id}/sessions/{lessonId}/history");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "чтение уроков куратору — штатное Read (И9′)");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("объясни мне дроби", "куратор видит переписку урока дословно");
    }

    // ── Б8d, доработка: серверная подача без отправителя в детском уроке проходит ──

    [Fact]
    public async Task СервернаяПодачаБезОтправителя_ВУрокеДоходитДоЗапуска()
    {
        var (childId, _, project) = SubjectProjectWithCurator("noSender");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        // Цикл «до готово», добивание сабагента, возобновление после рестарта — без человека
        await Sessions.SendMessageAsync(lessonId, "служебная подача", [], senderUserId: null);

        AssertTurnSent(adapter, "подача без отправителя человека не представляет — не отказ");
    }

    // ── Б8d, доработка: чат проекта-предмета без метки ChildSpace закрыт признаком проекта ──

    [Fact]
    public async Task КураторСообщениеВЧатеПредметаБезМетки_ХодНеЗапущен()
    {
        var (childId, curatorId, project) = SubjectProjectWithCurator("nomark");
        var chatId = await LessonAsync(project.Id, childId, withChildMark: false);
        Sessions.GetById(chatId)!.ChildSpace.Should().BeNull("предпосылка: метки нет");
        var adapter = SetCountingAdapter(chatId);

        await Sessions.SendMessageAsync(chatId, "куратор пишет в предмет", [], senderUserId: curatorId);

        AssertTurnNotSent(adapter, "гейт по признаку проекта, а не только по метке чата");
    }

    // ── c2: ребёнок-владелец запускает ход своего урока ────────────────────────────

    [Fact]
    public async Task РебёнокВладелецЗапускаетХодСвоегоУрока()
    {
        var (childId, _, project) = SubjectProjectWithCurator("kidturn");
        var lessonId = await LessonAsync(project.Id, childId);
        var adapter = SetCountingAdapter(lessonId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        AssertTurnSent(adapter, "ход владельца-ребёнка не закрыт гейтом Б8d");
    }

    // ── c2: те же действия в обычном общем проекте с Editor — работают как раньше ──

    [Fact]
    public async Task ОбычныйОбщийПроект_CураторEditor_СоздаётЧатИВедётХод()
    {
        var adminId = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var curator = Users.Add("bsp-cur-plain", CuratorPassword, "user");
        var dir = Path.Combine(_hosts.TempDir, "bsp_plain");
        var project = Projects.Create("Обычный-" + Guid.NewGuid().ToString("N")[..8], dir,
            adminId, TestWebApplicationFactory.TestUsername, createDirectory: true);
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = curator.Id, Role = ProjectRole.Editor, AddedBy = adminId }]);
        Users.SetFeatureFlag(curator.Id, FeatureFlagKeys.SharedProjects, true);
        // Руководитель проекта — обычный путь создания чата участником (W3-B4b)
        var lead = Personas.Create(adminId, "Ведущий-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Project, project.Id,
            null, null, false);
        Projects.SetDefaultPersona(project.Id, lead.Id);

        var resp = await As("bsp-cur-plain", CuratorPassword)
            .PostAsJsonAsync($"/api/projects/{project.Id}/sessions", new { mode = "acceptEdits" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "обычный общий проект не затронут гейтом проекта-предмета");
        var chatId = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var adapter = SetCountingAdapter(chatId);
        await Sessions.SendMessageAsync(chatId, "ход редактора в общем проекте", [], senderUserId: curator.Id);
        AssertTurnSent(adapter, "ход Editor в обычном проекте — без изменений");
    }
}
