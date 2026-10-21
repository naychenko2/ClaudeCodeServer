using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

public class TaskManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;

    public TaskManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tasks_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = new TaskManager(BuildConfig(_dir));
    }

    public void Dispose()
    {
        // Гасим таймер дебаунса записи: иначе отложенный сброс уже удалённого набора
        // задач воссоздал бы временный каталог после теста.
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IConfiguration BuildConfig(string dir) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json")
        })
        .Build();

    // ─── CRUD ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ЗаполняетПоляИВозвращаетПоId()
    {
        var task = _sut.Create("proj-1", "user-1", new CreateTaskRequest(
            Title: "Задача",
            Description: "Описание",
            Priority: TaskItemPriority.High,
            DueDate: "2026-07-10",
            DueTime: "14:00",
            ReminderMinutes: 30,
            Subtasks: [new CreateSubtaskRequest("Подзадача")]));

        task.ProjectId.Should().Be("proj-1");
        task.OwnerId.Should().Be("user-1");
        task.Status.Should().Be(TaskItemStatus.Todo);
        task.Priority.Should().Be(TaskItemPriority.High);
        task.ReminderMinutes.Should().Be(30);
        task.Subtasks.Should().ContainSingle(s => s.Title == "Подзадача" && !s.IsDone);

        _sut.GetById(task.Id).Should().BeSameAs(task);
    }

    [Fact]
    public void Create_СКлиентскимId_ИспользуетЕго()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t", Id: "client-uuid-1"));
        task.Id.Should().Be("client-uuid-1");
        _sut.GetById("client-uuid-1").Should().BeSameAs(task);
    }

    [Fact]
    public void Create_ПовторСТемЖеId_ВозвращаетСуществующуюБезДубля()
    {
        var first = _sut.Create(null, "u", new CreateTaskRequest("t", Id: "client-uuid-2"));
        // Повтор POST при потерянном ack — тот же id, тот же владелец
        var second = _sut.Create(null, "u", new CreateTaskRequest("t-изменённое", Id: "client-uuid-2"));

        second.Should().BeSameAs(first);
        _sut.GetByOwner("u").Should().ContainSingle(t => t.Id == "client-uuid-2");
    }

    [Fact]
    public void Create_ЧужойЗанятыйId_ГенерируетНовый()
    {
        var mine = _sut.Create(null, "user-1", new CreateTaskRequest("моя", Id: "shared-id"));
        var other = _sut.Create(null, "user-2", new CreateTaskRequest("чужая", Id: "shared-id"));

        other.Id.Should().NotBe("shared-id");
        mine.Id.Should().Be("shared-id");
        _sut.GetById("shared-id").Should().BeSameAs(mine);
    }

    [Fact]
    public void Create_СПовторением_СтавитSeriesIdРавнымId()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily }));

        task.SeriesId.Should().Be(task.Id);
    }

    [Fact]
    public void Create_ОтрицательныйReminder_СтановитсяNull()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t", ReminderMinutes: -1));
        task.ReminderMinutes.Should().BeNull();
    }

    [Fact]
    public void Update_МеняетПоляИОчищаетПустойСтрокой()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            DueDate: "2026-07-10", DueTime: "14:00"));

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(
            Title: "новое", Status: TaskItemStatus.InProgress, DueTime: ""))!;

        updated.Title.Should().Be("новое");
        updated.Status.Should().Be(TaskItemStatus.InProgress);
        updated.DueDate.Should().Be("2026-07-10"); // null в запросе = не менять
        updated.DueTime.Should().BeNull();          // "" = очистить
    }

    [Fact]
    public void Update_СменаСрока_СбрасываетReminderSentAt()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            DueDate: "2026-07-10", ReminderMinutes: 30));
        _sut.MarkReminderSent(task.Id, DateTime.UtcNow);

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(DueDate: "2026-07-11"))!;

        updated.ReminderSentAt.Should().BeNull();
    }

    [Fact]
    public void Update_БезСменыСрока_НеТрогаетReminderSentAt()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            DueDate: "2026-07-10", ReminderMinutes: 30));
        var sentAt = DateTime.UtcNow;
        _sut.MarkReminderSent(task.Id, sentAt);

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(Title: "переименовали"))!;

        updated.ReminderSentAt.Should().Be(sentAt);
    }

    [Fact]
    public void Update_НесуществующаяЗадача_Null()
    {
        _sut.Update("ghost", new UpdateTaskRequest(Title: "x")).Should().BeNull();
    }

    [Fact]
    public void Delete_УдаляетЗадачу()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        _sut.Delete(task.Id).Should().BeTrue();
        _sut.GetById(task.Id).Should().BeNull();
        _sut.Delete(task.Id).Should().BeFalse(); // повторное удаление
    }

    [Fact]
    public void DeleteByProject_УдаляетТолькоЗадачиПроекта()
    {
        var p1 = _sut.Create("proj-1", "u", new CreateTaskRequest("a"));
        var p2 = _sut.Create("proj-2", "u", new CreateTaskRequest("b"));

        var deleted = _sut.DeleteByProject("proj-1");

        deleted.Should().BeEquivalentTo([p1.Id]);
        _sut.GetById(p1.Id).Should().BeNull();
        _sut.GetById(p2.Id).Should().NotBeNull();
    }

    // ─── Изоляция по владельцу ───────────────────────────────────────────────

    [Fact]
    public void GetByOwner_НеВозвращаетЧужиеЗадачи()
    {
        var mine = _sut.Create(null, "user-1", new CreateTaskRequest("моя"));
        _sut.Create(null, "user-2", new CreateTaskRequest("чужая"));

        var tasks = _sut.GetByOwner("user-1");

        tasks.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public void GetByProject_ВозвращаетТолькоЗадачиПроекта()
    {
        var inProject = _sut.Create("proj-1", "u", new CreateTaskRequest("в проекте"));
        _sut.Create(null, "u", new CreateTaskRequest("личная"));

        _sut.GetByProject("proj-1").Should().ContainSingle().Which.Id.Should().Be(inProject.Id);
    }

    // ─── Персистентность ─────────────────────────────────────────────────────

    [Fact]
    public void Персистентность_НовыйЭкземпляр_ЧитаетЗадачиИзФайла()
    {
        var task = _sut.Create("proj-1", "user-1", new CreateTaskRequest("выживу рестарт",
            DueDate: "2026-08-01", ReminderMinutes: 15));

        // Запись стора отложена дебаунсом — досбрасываем явно, чтобы «рестарт» увидел
        // задачу детерминированно (ровно это делает остановка приложения)
        _sut.Flush();

        // «Рестарт сервера»: новый менеджер с тем же DataPath
        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(task.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("выживу рестарт");
        reloaded.OwnerId.Should().Be("user-1");
        reloaded.DueDate.Should().Be("2026-08-01");
        reloaded.ReminderMinutes.Should().Be(15);
    }

    [Fact]
    public void Персистентность_СтараяЗадачаБезПолейФичи_ЧитаетсяСNull()
    {
        var task = _sut.Create("proj-1", "user-1", new CreateTaskRequest("старая задача"));
        _sut.Flush();

        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(task.Id)!;

        reloaded.FeatureId.Should().BeNull();
        reloaded.Role.Should().BeNull();
        reloaded.StepRef.Should().BeNull();
        reloaded.Lane.Should().BeNull();
        reloaded.ReviewOf.Should().BeNull();
        reloaded.AwaitingHumanSince.Should().BeNull();
        reloaded.AwaitingHumanReason.Should().BeNull();
        reloaded.Verification.Should().BeNull();
    }

    [Fact]
    public void Персистентность_ЗаполненныеПоляФичи_ПереживаютСохранениеИЧтение()
    {
        var task = _sut.Create("proj-1", "user-1", new CreateTaskRequest("задача фичи"));
        task.FeatureId = "epic-1";
        task.Role = TaskRoles.Step;
        task.StepRef = "B1";
        task.Lane = "бэк";
        task.ReviewOf = ["t-1", "t-2"];
        task.AwaitingHumanSince = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        task.AwaitingHumanReason = "merge";
        task.Verification = new TaskVerification
        {
            VerifiedAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
            Verdict = TaskVerdicts.AcceptedWithRemarks,
            Notes = "с замечаниями"
        };
        _sut.Flush();

        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(task.Id)!;

        reloaded.FeatureId.Should().Be("epic-1");
        reloaded.Role.Should().Be(TaskRoles.Step);
        reloaded.StepRef.Should().Be("B1");
        reloaded.Lane.Should().Be("бэк");
        reloaded.ReviewOf.Should().BeEquivalentTo(["t-1", "t-2"]);
        reloaded.AwaitingHumanSince.Should().Be(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));
        reloaded.AwaitingHumanReason.Should().Be("merge");
        reloaded.Verification.Should().NotBeNull();
        reloaded.Verification!.Verdict.Should().Be(TaskVerdicts.AcceptedWithRemarks);
        reloaded.Verification.Notes.Should().Be("с замечаниями");
    }

    // ─── SpawnNextOccurrence ─────────────────────────────────────────────────

    [Fact]
    public void SpawnNext_Ежедневная_СоздаётЭкземплярНаСледующийДень()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("регулярная",
            DueDate: "2026-07-01", DueTime: "10:00", ReminderMinutes: 30,
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily },
            Subtasks: [new CreateSubtaskRequest("шаг")]));
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done,
            Subtasks: [new UpdateSubtaskRequest(task.Subtasks[0].Id, "шаг", IsDone: true)]));

        var next = _sut.SpawnNextOccurrence(_sut.GetById(task.Id)!);

        next.Should().NotBeNull();
        next!.Id.Should().NotBe(task.Id);
        next.DueDate.Should().Be("2026-07-02");
        next.DueTime.Should().Be("10:00");
        next.ReminderMinutes.Should().Be(30);
        next.Status.Should().Be(TaskItemStatus.Todo);
        next.SeriesId.Should().Be(task.SeriesId);
        // Подзадачи копируются со сброшенными галочками
        next.Subtasks.Should().ContainSingle(s => s.Title == "шаг" && !s.IsDone);
    }

    [Fact]
    public void SpawnNext_БезПовторенияИлиСрока_Null()
    {
        var noRecurrence = _sut.Create(null, "u", new CreateTaskRequest("t", DueDate: "2026-07-01"));
        _sut.SpawnNextOccurrence(noRecurrence).Should().BeNull();

        var noDue = _sut.Create(null, "u", new CreateTaskRequest("t",
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily }));
        _sut.SpawnNextOccurrence(noDue).Should().BeNull();
    }

    [Fact]
    public void SpawnNext_СерияЗакончиласьПоUntil_Null()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            DueDate: "2026-07-01",
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily, Until = "2026-07-01" }));

        _sut.SpawnNextOccurrence(task).Should().BeNull();
    }

    [Fact]
    public void SpawnNext_СПерсонойИсполнителем_ПереноситPersonaId()
    {
        var task = _sut.Create("proj-1", "u", new CreateTaskRequest("регулярная от персоны",
            DueDate: "2026-07-01",
            Assignee: TaskItemAssignee.Claude,
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily },
            PersonaId: "prs-1"));

        var next = _sut.SpawnNextOccurrence(task);

        next.Should().NotBeNull();
        next!.PersonaId.Should().Be("prs-1");           // исполнитель-персона не теряется
        next.Assignee.Should().Be(TaskItemAssignee.Claude);
        // Отметки исполнителя у нового экземпляра сброшены — отработает заново
        next.ClaudeStartedAt.Should().BeNull();
        next.ClaudeResult.Should().BeNull();
        next.LinkedSessionId.Should().BeNull();
    }

    [Fact]
    public void SpawnNext_Делегированная_ПереноситПостановщикаНоНеСессию()
    {
        var task = _sut.Create("proj-1", "u", new CreateTaskRequest("регулярная делегированная",
            DueDate: "2026-07-01",
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily },
            PersonaId: "prs-executor",
            CreatedByPersonaId: "prs-delegator",
            SourceSessionId: "sess-1"));

        var next = _sut.SpawnNextOccurrence(task);

        next.Should().NotBeNull();
        next!.CreatedByPersonaId.Should().Be("prs-delegator"); // постановщик — атрибут серии
        next.SourceSessionId.Should().BeNull();                // сессия у нового экземпляра своя
    }

    // ─── Инвариант «персона ⇒ Claude» ────────────────────────────────────────

    [Fact]
    public void Create_СПерсоной_ПринудительноСтавитAssigneeClaude()
    {
        // Даже с Assignee=Me назначение персоны переводит исполнителя в Claude
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            Assignee: TaskItemAssignee.Me, PersonaId: "prs-1"));

        task.Assignee.Should().Be(TaskItemAssignee.Claude);
        task.PersonaId.Should().Be("prs-1");
    }

    [Fact]
    public void Update_НазначениеПерсоны_СтавитAssigneeClaude()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t", Assignee: TaskItemAssignee.Me));

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(PersonaId: "prs-1"))!;

        updated.Assignee.Should().Be(TaskItemAssignee.Claude);
        updated.PersonaId.Should().Be("prs-1");
    }

    [Fact]
    public void Update_СнятиеПерсоны_ОставляетAssigneeБезИзменений()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t", PersonaId: "prs-1"));

        // "" — убрать персону; инвариант больше не форсит Claude
        var updated = _sut.Update(task.Id, new UpdateTaskRequest(PersonaId: ""))!;

        updated.PersonaId.Should().BeNull();
        updated.Assignee.Should().Be(TaskItemAssignee.Claude); // явно не сбрасываем — задача уже была на Claude
    }

    // ─── Отметки планировщика и исполнителя ──────────────────────────────────

    [Fact]
    public void MarkReminderSent_СтавитОтметку()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        var at = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

        _sut.MarkReminderSent(task.Id, at)!.ReminderSentAt.Should().Be(at);
    }

    [Fact]
    public void MarkClaudeStarted_СвязываетСессиюИПереводитВРаботу()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        var updated = _sut.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow)!;

        updated.LinkedSessionId.Should().Be("sess-1");
        updated.ClaudeStartedAt.Should().NotBeNull();
        updated.Status.Should().Be(TaskItemStatus.InProgress);
        _sut.GetBySession("sess-1")!.Id.Should().Be(task.Id);
    }

    [Fact]
    public void MarkClaudeResult_ФиксируетИтог()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        _sut.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);

        _sut.MarkClaudeResult(task.Id, "success")!.ClaudeResult.Should().Be("success");
    }

    // ─── CT-8: join двух сигналов завершения (R/D) ───────────────────────────

    [Fact]
    public void TryMarkCompletionDelivered_ПерваяДоставка_ЗанимаетФлагИВозвращаетTrue()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        _sut.TryMarkCompletionDelivered(task.Id).Should().BeTrue();
        _sut.GetById(task.Id)!.CompletionDelivered.Should().BeTrue();
    }

    [Fact]
    public void TryMarkCompletionDelivered_ПовторныйВызов_False()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        _sut.TryMarkCompletionDelivered(task.Id).Should().BeTrue();

        // Второй триггер join-а (гонка R/D) — флаг уже занят, доставки не будет
        _sut.TryMarkCompletionDelivered(task.Id).Should().BeFalse();
    }

    [Fact]
    public void TryMarkCompletionDelivered_НесуществующаяЗадача_False()
    {
        _sut.TryMarkCompletionDelivered("ghost").Should().BeFalse();
    }

    [Fact]
    public void Update_ПереходВDone_ПоднимаетTaskCompleted()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        var fired = new List<string>();
        _sut.TaskCompleted += t => fired.Add(t.Id);

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        fired.Should().ContainSingle().Which.Should().Be(task.Id);
    }

    [Fact]
    public void Update_УжеБылDone_НеПоднимаетTaskCompletedПовторно()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var fired = new List<string>();
        _sut.TaskCompleted += t => fired.Add(t.Id);

        // Повторный PUT с тем же статусом (напр. tasks_update без смены статуса) — не переход
        _sut.Update(task.Id, new UpdateTaskRequest(Title: "переименовали"));

        fired.Should().BeEmpty();
    }

    [Fact]
    public void Update_НеДошёлДоDone_НеПоднимаетTaskCompleted()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        var fired = new List<string>();
        _sut.TaskCompleted += t => fired.Add(t.Id);

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress));

        fired.Should().BeEmpty();
    }

    // ─── Глубина делегирования ────────────────────────────────────────────────

    [Fact]
    public void Create_БезSourceSessionId_ГлубинаНоль()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));
        task.DelegationDepth.Should().Be(0);
    }

    [Fact]
    public void Create_SourceSessionIdНеЯвляетсяЧатомИсполнителя_ГлубинаНоль()
    {
        // Обычный чат (не LinkedSessionId ни одной задачи) — не считается делегированием
        var task = _sut.Create(null, "u", new CreateTaskRequest("t", SourceSessionId: "some-chat"));
        task.DelegationDepth.Should().Be(0);
    }

    [Fact]
    public void Create_ИзЧатаИсполнителя_ГлубинаРодителяПлюсОдин()
    {
        var parent = _sut.Create(null, "u", new CreateTaskRequest("родитель"));
        _sut.MarkClaudeStarted(parent.Id, "exec-sess-1", DateTime.UtcNow); // parent.LinkedSessionId = exec-sess-1

        var child = _sut.Create(null, "u",
            new CreateTaskRequest("ребёнок", SourceSessionId: "exec-sess-1"));

        child.DelegationDepth.Should().Be(1);
    }

    [Fact]
    public void Create_ЦепочкаДелегирования_ГлубинаНарастает()
    {
        var depth0 = _sut.Create(null, "u", new CreateTaskRequest("d0"));
        _sut.MarkClaudeStarted(depth0.Id, "sess-0", DateTime.UtcNow);

        var depth1 = _sut.Create(null, "u", new CreateTaskRequest("d1", SourceSessionId: "sess-0"));
        _sut.MarkClaudeStarted(depth1.Id, "sess-1", DateTime.UtcNow);

        var depth2 = _sut.Create(null, "u", new CreateTaskRequest("d2", SourceSessionId: "sess-1"));

        depth1.DelegationDepth.Should().Be(1);
        depth2.DelegationDepth.Should().Be(2);
    }

    [Fact]
    public void SpawnNextOccurrence_ПереноситГлубинуДелегирования()
    {
        var parent = _sut.Create(null, "u", new CreateTaskRequest("родитель"));
        _sut.MarkClaudeStarted(parent.Id, "exec-sess-1", DateTime.UtcNow);
        var task = _sut.Create(null, "u", new CreateTaskRequest("регулярная",
            SourceSessionId: "exec-sess-1", DueDate: "2026-07-01",
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily }));
        task.DelegationDepth.Should().Be(1);
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var next = _sut.SpawnNextOccurrence(_sut.GetById(task.Id)!);

        next!.DelegationDepth.Should().Be(1);
    }

    // ─── Дерево чата-исполнителя (worktree на задаче) ────────────────────────
    // Путь строим от temp: Windows-литерал на Linux считался бы относительным (см. CLAUDE.md)

    private static readonly string Wt = Path.Combine(Path.GetTempPath(), "wt", "feature-x");

    [Fact]
    public void Create_ПроектнаяЗадача_ХранитДеревоИсполнителя()
    {
        var task = _sut.Create("proj-1", "user-1", new CreateTaskRequest("в дереве",
            WorktreePath: Wt + " ", WorktreeBranch: " wt/feature-x"));

        task.WorktreePath.Should().Be(Wt);
        task.WorktreeBranch.Should().Be("wt/feature-x");

        // Переживает рестарт (поле в data/tasks.json); Flush — из-за дебаунса записи
        _sut.Flush();
        new TaskManager(BuildConfig(_dir)).GetById(task.Id)!.WorktreePath.Should().Be(Wt);
    }

    [Fact]
    public void Create_ЛичнаяЗадача_ДеревоИгнорируется()
    {
        // Worktree — дерево репы проекта; у личной задачи проекта нет
        var task = _sut.Create(null, "user-1", new CreateTaskRequest("личная",
            WorktreePath: Wt, WorktreeBranch: "wt/feature-x"));

        task.WorktreePath.Should().BeNull();
        task.WorktreeBranch.Should().BeNull();
    }

    [Fact]
    public void Update_ПустаяСтрока_СбрасываетДерево()
    {
        var task = _sut.Create("proj-1", "u", new CreateTaskRequest("t",
            WorktreePath: Wt, WorktreeBranch: "wt/a"));

        // null — не менять
        _sut.Update(task.Id, new UpdateTaskRequest(Title: "t2"))!.WorktreePath.Should().Be(Wt);

        var cleared = _sut.Update(task.Id, new UpdateTaskRequest(WorktreePath: "", WorktreeBranch: ""))!;
        cleared.WorktreePath.Should().BeNull();
        cleared.WorktreeBranch.Should().BeNull();
    }

    [Fact]
    public void Update_ЗадачаСталаЛичной_ДеревоСбрасывается()
    {
        var task = _sut.Create("proj-1", "u", new CreateTaskRequest("t",
            WorktreePath: Wt, WorktreeBranch: "wt/a"));

        var personal = _sut.Update(task.Id, new UpdateTaskRequest(ProjectId: ""))!;

        personal.WorktreePath.Should().BeNull();
        personal.WorktreeBranch.Should().BeNull();
    }

    // ─── Карточки дефектов (DefectRules через TaskManager) ────────────────────

    [Fact]
    public void Create_ДефектСОдниЗаголовком_Создаётся()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        task.Kind.Should().Be(TaskKind.Defect);
        task.Status.Should().Be(TaskItemStatus.Todo);
    }

    [Fact]
    public void Create_ДефектСразуВDone_Отклонена()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("баг",
            Kind: TaskKind.Defect, Status: TaskItemStatus.Done));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*нельзя создавать сразу в Done*");
    }

    [Fact]
    public void Create_ДефектВReviewКолонкеБезШагов_Отклонена()
    {
        var reviewCol = new BoardColumn { Role = "review" };
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect),
            targetColumn: reviewCol);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Repro.Steps*");
    }

    [Fact]
    public void Create_ДефектВReviewКолонкеСШагами_Проходит()
    {
        var reviewCol = new BoardColumn { Role = "review" };
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг",
            Kind: TaskKind.Defect, Repro: new DefectRepro { Steps = "1. Открыть" }),
            targetColumn: reviewCol);

        task.Repro!.Steps.Should().Be("1. Открыть");
    }

    [Fact]
    public void Create_ОбычнаяЗадачаВReviewБезШагов_НеЗадета()
    {
        // Правило EnsureReproOnReview касается только Kind == Defect
        var reviewCol = new BoardColumn { Role = "review" };
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"), targetColumn: reviewCol);

        task.Kind.Should().Be(TaskKind.Task);
    }

    [Fact]
    public void Update_ЗакрытиеДефектаБезВердикта_ОтклоненоНаВнешнихПутях()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*заполните Verification*ClosedWithoutCheck*");
    }

    [Fact]
    public void Update_ЗакрытиеДефектаСВердиктом_Проходит()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done,
            Verification: new TaskVerification { VerifiedAt = DateTime.UtcNow, Notes = "проверено" }))!;

        updated.Status.Should().Be(TaskItemStatus.Done);
        updated.Verification.Should().NotBeNull();
    }

    [Fact]
    public void Update_ЗакрытиеДефектаСClosedWithoutCheck_Проходит()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck))!;

        updated.Status.Should().Be(TaskItemStatus.Done);
        updated.Outcome.Should().Be(DefectOutcome.ClosedWithoutCheck);
    }

    [Fact]
    public void Update_ЗакрытиеОбычнойЗадачиБезВердикта_НеЗадета()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        act.Should().NotThrow();
    }

    [Fact]
    public void Update_УходИзDone_СбрасываетVerificationИOutcome()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));
        _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck));

        var reopened = _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress))!;

        reopened.Verification.Should().BeNull();
        reopened.Outcome.Should().BeNull();
    }

    [Fact]
    public void Update_ПереходВReviewБезШагов_Отклонена()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        var reviewCol = new BoardColumn { Role = "review" };
        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Title: "переименовали"),
            effectiveColumn: reviewCol);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Repro.Steps*");
    }

    [Fact]
    public void SpawnNext_РегулярныйДефект_ПереноситВид()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("регулярный баг",
            DueDate: "2026-07-01", Kind: TaskKind.Defect,
            Repro: new DefectRepro { Steps = "шаги" },
            Recurrence: new TaskRecurrence { Type = TaskRecurrenceType.Daily }));
        _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck));

        var next = _sut.SpawnNextOccurrence(_sut.GetById(task.Id)!);

        next.Should().NotBeNull();
        next!.Kind.Should().Be(TaskKind.Defect);
        next.Repro!.Steps.Should().Be("шаги");
        // Verification/Outcome — разовый вердикт закрытого экземпляра, не переносится
        next.Verification.Should().BeNull();
        next.Outcome.Should().BeNull();
    }

    // Сторож: TaskItemStatus — ровно три значения (см. также DefectRulesTests, тот же контракт)
    [Fact]
    public void TaskItemStatus_РовноТриЗначения()
    {
        Enum.GetValues(typeof(TaskItemStatus)).Length.Should().Be(3);
    }

    // Сторож: инвариант закрытого дефекта — Done ⇒ Verification != null ИЛИ Outcome == ClosedWithoutCheck
    [Fact]
    public void ЗакрытыйДефект_БезВердиктаИБезИсхода_НевозможенЧерезUpdate()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("баг", Kind: TaskKind.Defect));

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        act.Should().Throw<InvalidOperationException>();
        _sut.GetById(task.Id)!.Status.Should().NotBe(TaskItemStatus.Done);
    }

    // ─── DroppedByHumanAt (волна 1 team-blocker-honest, дефект f3965801) ─────────
    // После снятия человеком по карточке блокера поздний tasks_complete не должен
    // переписывать Status/Outcome — иначе задача выглядит штатно выполненной с
    // отчётом исполнителя (находка живой приёмки: штатный tasks_complete затирал
    // пометку снятия через пять минут после клика «снять»). ResultMarkdown исполнителя
    // дописывается к пометке снятия отдельной строкой, чтобы не потерять его работу.
    // Мутация для проверки: убрать защиту в TaskManager.Update (ветка DroppedByHumanAt)
    // — тест Update_ПослеСнятияНеТрогаетStatusИOutcomeДокладываетИсполнителяОтдельнойСтрокой
    // сразу падает на StatusChanged и OutcomeChanged.

    [Fact]
    public void MarkDroppedByHuman_СтавитМеткуИСдвигаетUpdatedAt()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("drop"));
        var before = task.UpdatedAt;

        var marked = _sut.MarkDroppedByHuman(task.Id, DateTime.UtcNow);

        marked.Should().NotBeNull();
        marked!.DroppedByHumanAt.Should().NotBeNull();
        marked.UpdatedAt.Should().BeOnOrAfter(before);
        _sut.GetById(task.Id)!.DroppedByHumanAt.Should().NotBeNull();
    }

    [Fact]
    public void Update_ПослеСнятияНеТрогаетStatusИOutcomeДокладываетИсполнителяОтдельнойСтрокой()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("dropped"));
        // Штаб снял задачу: Update(Status=Done, ResultMarkdown=reason, Outcome=ClosedWithoutCheck)
        // + MarkDroppedByHuman — имитируем реальный DropSubtaskAsync.
        _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done,
            ResultMarkdown: "Снято решением человека по карточке остановки (drop).",
            Outcome: DefectOutcome.ClosedWithoutCheck));
        _sut.MarkDroppedByHuman(task.Id, DateTime.UtcNow);
        var snapshot = _sut.GetById(task.Id)!;
        snapshot.Status.Should().Be(TaskItemStatus.Done);
        snapshot.Outcome.Should().Be(DefectOutcome.ClosedWithoutCheck);
        var originalResultMarkdown = snapshot.ResultMarkdown;

        // Поздний tasks_complete от исполнителя: Status=Done, свой ResultMarkdown,
        // свой Outcome. По ТЗ: Status/Outcome не меняются, ResultMarkdown дописывается.
        _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done,
            ResultMarkdown: "Исполнитель успел сделать A, B, C.",
            Outcome: DefectOutcome.ClosedWithoutCheck));

        var after = _sut.GetById(task.Id)!;
        after.Status.Should().Be(TaskItemStatus.Done, "пометка снятия сильнее позднего доклада");
        after.Outcome.Should().Be(DefectOutcome.ClosedWithoutCheck, "Outcome тоже не переписывается");
        after.ResultMarkdown.Should().Contain(originalResultMarkdown);
        after.ResultMarkdown.Should().Contain("Исполнитель успел сделать A, B, C.");
        after.ResultMarkdown.Should().Contain("Доклад исполнителя после снятия");
    }

    [Fact]
    public void Update_ПослеСнятияАгентскийПутьОтказываетВнятно()
    {
        // Агентский путь (MCP tasks_complete/tasks_update от исполнителя): защита от
        // затирания снятой задачи — внятный InvalidOperationException вместо молчаливого
        // 200 OK (находка фикс-волны 4: человек не понимал, почему задача не закрылась
        // по tasks_complete после клика «снять»). Мутация для проверки: убрать
        // throw в TaskManager.Update (ветка isAgentCall && DroppedByHumanAt != null) —
        // метод отдаст Done без ошибки, тест упадёт на ShouldThrow<InvalidOperationException>().
        var task = _sut.Create(null, "u", new CreateTaskRequest("dropped"));
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck));
        _sut.MarkDroppedByHuman(task.Id, DateTime.UtcNow);

        var act = () => _sut.Update(task.Id,
            new UpdateTaskRequest(Status: TaskItemStatus.Done, ResultMarkdown: "Исполнитель хочет закрыть"),
            isAgentCall: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*снята человеком*");
        _sut.GetById(task.Id)!.Status.Should().Be(TaskItemStatus.Done,
            "после отказа статус остаётся от штаба");
    }

    [Fact]
    public void Update_ПослеСнятияЧеловекМожетВернутьВРаботу()
    {
        // Человеческий путь (HTTP PUT): человек перетащил карточку обратно в «В работе».
        // Пометка DroppedByHumanAt снимается, статус применяется штатно — иначе человек
        // не мог бы реабилитировать задачу после снятия (находка фикс-волны 4).
        var task = _sut.Create(null, "u", new CreateTaskRequest("dropped"));
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck));
        _sut.MarkDroppedByHuman(task.Id, DateTime.UtcNow);

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress),
            isAgentCall: false);

        var after = _sut.GetById(task.Id)!;
        after.Status.Should().Be(TaskItemStatus.InProgress,
            "человек вправе вернуть снятую задачу в работу — иначе она навечно в Done");
        after.DroppedByHumanAt.Should().BeNull(
            "пометка снята — задача снова активна, защита не нужна");
    }

    [Fact]
    public void Update_ПослеСнятияЧеловекОставившийВDoneНеСнимаетПометку()
    {
        // Граница: человек закрывает снятую задачу в Done ещё раз (редкий кейс — обычно
        // человек возвращает в работу). Пометка DroppedByHumanAt остаётся — задача
        // закрыта человеком, защита от позднего tasks_complete нужна.
        var task = _sut.Create(null, "u", new CreateTaskRequest("dropped"));
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Outcome: DefectOutcome.ClosedWithoutCheck));
        _sut.MarkDroppedByHuman(task.Id, DateTime.UtcNow);

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done),
            isAgentCall: false);

        var after = _sut.GetById(task.Id)!;
        after.Status.Should().Be(TaskItemStatus.Done);
        after.DroppedByHumanAt.Should().NotBeNull(
            "пометка остаётся — задача закрыта человеком, защита от позднего tasks_complete нужна");
    }

    [Fact]
    public void Update_БезСнятияПоведениеПрежнее()
    {
        // Контроль: на обычной задаче защита не мешает — Status меняется штатно.
        var task = _sut.Create(null, "u", new CreateTaskRequest("normal"));
        task.DroppedByHumanAt.Should().BeNull();

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        _sut.GetById(task.Id)!.Status.Should().Be(TaskItemStatus.Done);
    }

    // ─── Фича: валидация полей (режим «Исполнение фичи») ─────────────────────
    // Эпик фичи — задача с Role=feature; задачи фичи ссылаются на него через FeatureId.
    // Валидация fail-closed в TaskManager.ValidateFeatureFields: role/verdict только из
    // списков, featureId — на живой эпик того же владельца, reviewOf — на задачи той же
    // фичи и того же владельца, role=feature не совмещается с personaId.

    private TaskItem CreateEpic(string ownerId, string title = "эпик фичи") =>
        _sut.Create(null, ownerId, new CreateTaskRequest(title, Role: TaskRoles.Feature));

    [Fact]
    public void Create_ФичаСуществует_FeatureIdУказываетНаНеё_Принята()
    {
        var epic = CreateEpic("u");

        var task = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));

        task.FeatureId.Should().Be(epic.Id);
        task.Role.Should().Be(TaskRoles.Step);
        task.StepRef.Should().Be("B1");
        task.Lane.Should().Be("бэк");
    }

    [Fact]
    public void Create_ЧужойВладелецЭпика_Отказ()
    {
        var epic = CreateEpic("u1");

        var act = () => _sut.Create(null, "u2", new CreateTaskRequest("шаг", FeatureId: epic.Id));

        act.Should().Throw<InvalidOperationException>().WithMessage("*другому владельцу*");
    }

    [Fact]
    public void Create_FeatureIdНаЗадачуБезРолиFeature_Отказ()
    {
        _sut.Create(null, "u", new CreateTaskRequest("обычная", Id: "not-epic"));

        var act = () => _sut.Create(null, "u", new CreateTaskRequest("шаг", FeatureId: "not-epic"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*не эпик фичи*");
    }

    [Fact]
    public void Create_FeatureIdНаНесуществующуюЗадачу_Отказ()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("шаг", FeatureId: "ghost-epic"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Фича не найдена*");
    }

    [Fact]
    public void Create_RoleFeatureСПерсоной_Отказ()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("эпик",
            Role: TaskRoles.Feature, PersonaId: "prs-1"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*не запускается исполнителем*");
    }

    [Fact]
    public void Create_RoleНеИзСписка_ОтказСоСписком()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("t", Role: "bogus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Недопустимая роль*")
            .WithMessage("*" + TaskRoles.Feature + "*");
    }

    // Минор 2: пара к Create_RoleНеИзСписка. Валидация role ловит НЕВАЛИДНОЕ значение и при
    // UPDATE — через effective-state (req ?? task) в ValidateFeatureFields, а не только при
    // создании. Создаём валидную задачу (без роли), обновляем с role="banana" (вне TaskRoles.All)
    // → отказ с тем же сообщением, что и Create.
    [Fact]
    public void Update_RoleНеИзСписка_ОтказСоСписком()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Role: "banana"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Недопустимая роль*")
            .WithMessage("*" + TaskRoles.Feature + "*");
    }

    [Fact]
    public void Create_VerdictНеИзСписка_Отказ()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("t",
            Verification: new TaskVerification { Verdict = "bogus" }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Недопустимый вердикт*");
    }

    [Fact]
    public void Create_ReviewOfНаЗадакуДругойФичи_Отказ()
    {
        var epicA = CreateEpic("u", "фича A");
        var epicB = CreateEpic("u", "фича B");
        var inB = _sut.Create(null, "u", new CreateTaskRequest("шаг B",
            FeatureId: epicB.Id, Role: TaskRoles.Step));

        var act = () => _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epicA.Id, Role: TaskRoles.Review, ReviewOf: [inB.Id]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*из другой фичи*");
    }

    [Fact]
    public void Create_ReviewOfНаЗадакуЧужогоВладельца_Отказ()
    {
        var epicA = CreateEpic("u1", "фича A");
        var inA = _sut.Create(null, "u1", new CreateTaskRequest("шаг A",
            FeatureId: epicA.Id, Role: TaskRoles.Step));

        var act = () => _sut.Create(null, "u2", new CreateTaskRequest("ревью", ReviewOf: [inA.Id]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*другому владельцу*");
    }

    [Fact]
    public void Create_ПоляФичиВалидны_Принята()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));

        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id],
            Verification: new TaskVerification { Verdict = TaskVerdicts.Accepted, Notes = "принято" }));

        review.FeatureId.Should().Be(epic.Id);
        review.Role.Should().Be(TaskRoles.Review);
        review.ReviewOf.Should().BeEquivalentTo([step.Id]);
        review.Verification!.Verdict.Should().Be(TaskVerdicts.Accepted);
    }

    // Роли, привязанные к фиче (featureId обязателен): всё, кроме feature/cut/plan.
    // Выводим из TaskRoles, чтобы при добавлении роли список не расползался по тестам.
    private static string[] BoundRoles =>
        TaskRoles.All.Where(r => !TaskRoles.FeatureIdOptional.Contains(r)).ToArray();

    // ─── Дефект d0b2ff78: привязанная к фиче роль без featureId = «висяк» ─────────
    // Каждая роль из {step, rework, review, qa, final, merge, consult} требует featureId
    // (live эпик); featureId=null для не-привязанных ролей (feature/cut/plan) — валидно.

    [Fact]
    public void Create_ПривязаннаяРольБезFeatureId_Отказ()
    {
        foreach (var role in BoundRoles)
        {
            var act = () => _sut.Create(null, "u", new CreateTaskRequest("t", Role: role));
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*Роль {role} требует featureId*",
                    $"роль {role} без featureId обязана быть отклонена fail-closed");
        }
    }

    [Fact]
    public void Create_ПривязаннаяРольСFeatureId_Принята()
    {
        var epic = CreateEpic("u");
        // rework требует reviewOf (fail-closed, §1): создаём ревью той же фичи, на которое
        // доработка будет ссылаться; прочие роли reviewOf не требуют (null).
        var review = _sut.Create(null, "u", new CreateTaskRequest("t",
            FeatureId: epic.Id, Role: TaskRoles.Review));
        foreach (var role in BoundRoles)
        {
            List<string>? reviewOf = role == TaskRoles.Rework ? [review.Id] : null;
            var task = _sut.Create(null, "u", new CreateTaskRequest("t",
                FeatureId: epic.Id, Role: role, ReviewOf: reviewOf));
            task.FeatureId.Should().Be(epic.Id);
            task.Role.Should().Be(role);
        }
    }

    [Fact]
    public void Create_ПривязаннаяРольСНесуществующимFeatureId_Отказ()
    {
        foreach (var role in BoundRoles)
        {
            var act = () => _sut.Create(null, "u",
                new CreateTaskRequest("t", FeatureId: "ghost-epic", Role: role));
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Фича не найдена*",
                    $"роль {role} с несуществующим featureId — эпик не найден");
        }
    }

    [Fact]
    public void Create_НеПривязаннаяРольБезFeatureId_Принята()
    {
        foreach (var role in TaskRoles.FeatureIdOptional)
        {
            var task = _sut.Create(null, "u", new CreateTaskRequest("t", Role: role));
            task.Role.Should().Be(role);
            task.FeatureId.Should().BeNull();
        }
    }

    [Fact]
    public void Update_ПоляФичи_ПрименяютсяИСбрасываются()
    {
        var epic = CreateEpic("u");
        var task = _sut.Create(null, "u", new CreateTaskRequest("t"));

        var set = _sut.Update(task.Id, new UpdateTaskRequest(
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"))!;
        set.FeatureId.Should().Be(epic.Id);
        set.Role.Should().Be(TaskRoles.Step);
        set.StepRef.Should().Be("B1");
        set.Lane.Should().Be("бэк");

        // "" = сбросить строковые поля (null = не менять)
        var cleared = _sut.Update(task.Id, new UpdateTaskRequest(
            FeatureId: "", Role: "", StepRef: "", Lane: ""))!;
        cleared.FeatureId.Should().BeNull();
        cleared.Role.Should().BeNull();
        cleared.StepRef.Should().BeNull();
        cleared.Lane.Should().BeNull();
    }

    // ─── Наследование featureId (шаг 1.3) ─────────────────────────────────────
    // Если задачу создаёт чат-исполнитель задачи-фичи (Role=feature), сервер
    // наследует featureId от эпика. Дефект внутри фичи дополнительно получает role=qa.
    // Update наследования не делает: агент к тому моменту знает, что редактирует.

    [Fact]
    public void Create_ДефектИзЧатаИсполнителяФичи_НаследуетFeatureIdИRoleQa()
    {
        var epic = CreateEpic("u");
        _sut.MarkClaudeStarted(epic.Id, "sess-exec", DateTime.UtcNow);

        var defect = _sut.Create(null, "u", new CreateTaskRequest("баг",
            Kind: TaskKind.Defect, SourceSessionId: "sess-exec"));

        defect.FeatureId.Should().Be(epic.Id, "featureId наследуется от эпика");
        defect.Role.Should().Be(TaskRoles.Qa, "дефект внутри фичи получает роль qa");
    }

    [Fact]
    public void Create_ОбычнаяЗадачаИзЧатаИсполнителяФичи_НаследуетFeatureId_RoleNull()
    {
        var epic = CreateEpic("u");
        _sut.MarkClaudeStarted(epic.Id, "sess-exec", DateTime.UtcNow);

        var task = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            SourceSessionId: "sess-exec"));

        task.FeatureId.Should().Be(epic.Id, "featureId наследуется");
        task.Role.Should().BeNull("обычная задача не получает роль автоматически");
    }

    [Fact]
    public void Create_БезРодителяФичиВСтекке_ПоляОстаютсяNull()
    {
        // Сессия не привязана ни к одной задаче — контекста нет
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            SourceSessionId: "orphan-sess"));

        task.FeatureId.Should().BeNull();
        task.Role.Should().BeNull();
    }

    [Fact]
    public void Create_ИзЧатаГдеИсполнительИмеетЗадачуНеFeature_НаследованияНет()
    {
        // Родитель — задача, не привязанная к эпика (Role=cut, FeatureId=null):
        // ни эпик фичи, ни шаг фичи — наследовать нечего.
        var cut = _sut.Create(null, "u", new CreateTaskRequest("разрез",
            Role: TaskRoles.Cut));
        _sut.MarkClaudeStarted(cut.Id, "sess-cut", DateTime.UtcNow);

        var child = _sut.Create(null, "u", new CreateTaskRequest("ребёнок",
            SourceSessionId: "sess-cut"));

        child.FeatureId.Should().BeNull("не фича и не шаг фичи — наследуем нечего");
        child.Role.Should().BeNull();
    }

    // ─── Регрессия: живое наследование через шаг фичи (не эпик) ───────────────
    // Живой сценарий: сессия исполняет ШАГ фичи (эпик исполнителем не запускается —
    // гейт 1.4), исполнитель создаёт свою задачу без явного featureId — сервер
    // наследует featureId от эпика, к которому шаг привязан.
    [Fact]
    public void Create_ИзЧатаИсполнителяШагаФичи_НаследуетFeatureId_Regression()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step));
        _sut.MarkClaudeStarted(step.Id, "sess-step", DateTime.UtcNow);

        var child = _sut.Create(null, "u", new CreateTaskRequest("своя задача",
            SourceSessionId: "sess-step"));

        child.FeatureId.Should().Be(epic.Id, "featureId наследуется от эпика через шаг");
    }

    [Fact]
    public void Update_НаследованияFeatureIdНеБывает_Regression()
    {
        var epic = CreateEpic("u");
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            FeatureId: epic.Id, Role: TaskRoles.Step));

        // Update не должен пересчитывать featureId: агент знает, что редактирует
        var updated = _sut.Update(task.Id, new UpdateTaskRequest(Title: "переименовали"))!;

        updated.FeatureId.Should().Be(epic.Id, "featureId не пересчитывается при Update");
        updated.Role.Should().Be(TaskRoles.Step, "role тоже не меняется");
    }

    // ─── PlanSteps: структурный список шагов плана (честный прогресс фичи) ─────────
    // PlanSteps — только у role=plan; stepRef непуст/уникален, лимиты числа шагов и длины.
    // null = не менять, список = задать целиком (как у reviewOf).

    [Fact]
    public void Create_PlanStepsУНеПлана_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "шаг" }]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*только у задачи-плана*");
    }

    [Fact]
    public void Create_PlanStepsУПлана_Принята()
    {
        var epic = CreateEpic("u");
        var plan = _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps:
            [
                new PlannedStep { StepRef = "B1", Title = "бэк-шаг", Lane = "бэк" },
                new PlannedStep { StepRef = "F1", Title = "фронт-шаг", Lane = "фронт" },
            ]));

        plan.PlanSteps.Should().HaveCount(2);
        plan.PlanSteps![0].StepRef.Should().Be("B1");
        plan.PlanSteps[0].Title.Should().Be("бэк-шаг");
        plan.PlanSteps[0].Lane.Should().Be("бэк");
        plan.PlanSteps[1].StepRef.Should().Be("F1");
        plan.PlanSteps[1].Lane.Should().Be("фронт");
    }

    [Fact]
    public void Create_PlanStepsДубльStepRef_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps:
            [
                new PlannedStep { StepRef = "B1", Title = "а" },
                new PlannedStep { StepRef = "B1", Title = "б" },
            ]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Дублируется код шага*");
    }

    [Fact]
    public void Create_PlanStepsСлишкомМного_Отказ()
    {
        var epic = CreateEpic("u");
        var steps = Enumerable.Range(0, PlannedStep.MaxSteps + 1)
            .Select(i => new PlannedStep { StepRef = $"S{i}", Title = "t" }).ToList();
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan, PlanSteps: steps));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Слишком много шагов*");
    }

    [Fact]
    public void Create_PlanStepsДлинныйStepRef_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = new string('x', PlannedStep.MaxStepRefLength + 1), Title = "t" }]));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*длиннее {PlannedStep.MaxStepRefLength}*");
    }

    [Fact]
    public void Create_PlanStepsДлинныйTitle_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = new string('x', PlannedStep.MaxTitleLength + 1) }]));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*длиннее {PlannedStep.MaxTitleLength}*");
    }

    [Fact]
    public void Create_PlanStepsПустойStepRef_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "", Title = "t" }]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*stepRef) не может быть пустым*");
    }

    [Fact]
    public void Update_PlanStepsNullНеМеняет_СписокЗадаёт()
    {
        var epic = CreateEpic("u");
        var plan = _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "а" }]));

        // null = не менять: список остаётся
        var unchanged = _sut.Update(plan.Id, new UpdateTaskRequest(Title: "переименовали"))!;
        unchanged.PlanSteps.Should().HaveCount(1);
        unchanged.PlanSteps![0].StepRef.Should().Be("B1");

        // список = задать целиком
        var set = _sut.Update(plan.Id, new UpdateTaskRequest(
            PlanSteps: [new PlannedStep { StepRef = "F1", Title = "б", Lane = "фронт" }]))!;
        set.PlanSteps.Should().HaveCount(1);
        set.PlanSteps![0].StepRef.Should().Be("F1");
        set.PlanSteps[0].Title.Should().Be("б");
        set.PlanSteps[0].Lane.Should().Be("фронт");
    }

    [Fact]
    public void Персистентность_PlanSteps_ПереживаетСохранениеИЧтение()
    {
        var epic = _sut.Create(null, "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var plan = _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps:
            [
                new PlannedStep { StepRef = "B1", Title = "бэк", Lane = "бэк" },
                new PlannedStep { StepRef = "F1", Title = "фронт", Lane = "фронт" },
            ]));
        _sut.Flush();

        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(plan.Id)!;

        reloaded.PlanSteps.Should().HaveCount(2);
        reloaded.PlanSteps![0].StepRef.Should().Be("B1");
        reloaded.PlanSteps[0].Title.Should().Be("бэк");
        reloaded.PlanSteps[0].Lane.Should().Be("бэк");
        reloaded.PlanSteps[1].StepRef.Should().Be("F1");
        reloaded.PlanSteps[1].Lane.Should().Be("фронт");
    }

    [Fact]
    public void Персистентность_СтараяЗадачаБезPlanSteps_ЧитаетсяСNull()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("старая"));
        _sut.Flush();

        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(task.Id)!;
        reloaded.PlanSteps.Should().BeNull();
    }

    // ─── Rework ⇒ reviewOf: обязательная связь доработки с review/qa/final той же фичи ──
    // Fail-closed: при создании — всегда; при update — только если поле reviewOf меняют
    // (старые доработки без reviewOf можно править, не трогая связь, и можно привязывать).

    [Fact]
    public void Create_ReworkБезReviewOf_Отказ()
    {
        var epic = CreateEpic("u");
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("доработка",
            FeatureId: epic.Id, Role: TaskRoles.Rework));

        act.Should().Throw<InvalidOperationException>().WithMessage("*reviewOf*");
    }

    [Fact]
    public void Create_ReworkСReviewOfНаReviewТойЖеФичи_Принята()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));
        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));

        var rework = _sut.Create(null, "u", new CreateTaskRequest("доработка",
            FeatureId: epic.Id, Role: TaskRoles.Rework, ReviewOf: [review.Id]));

        rework.ReviewOf.Should().BeEquivalentTo([review.Id]);
    }

    [Fact]
    public void Create_ReworkСReviewOfНаStep_Отказ()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));

        var act = () => _sut.Create(null, "u", new CreateTaskRequest("доработка",
            FeatureId: epic.Id, Role: TaskRoles.Rework, ReviewOf: [step.Id]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*review/qa/final*");
    }

    [Fact]
    public void Create_ReworkСReviewOfДругойФичи_Отказ()
    {
        var epicA = CreateEpic("u", "фича A");
        var epicB = CreateEpic("u", "фича B");
        var reviewB = _sut.Create(null, "u", new CreateTaskRequest("ревью B",
            FeatureId: epicB.Id, Role: TaskRoles.Review));

        var act = () => _sut.Create(null, "u", new CreateTaskRequest("доработка",
            FeatureId: epicA.Id, Role: TaskRoles.Rework, ReviewOf: [reviewB.Id]));

        // reviewOf из другой фичи — 400 (общая проверка reviewOf, не только роль цели)
        act.Should().Throw<InvalidOperationException>().WithMessage("*из другой фичи*");
    }

    [Fact]
    public void Update_ReworkБезИзмененияReviewOf_Ок()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));
        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));
        var rework = _sut.Create(null, "u", new CreateTaskRequest("доработка",
            FeatureId: epic.Id, Role: TaskRoles.Rework, ReviewOf: [review.Id]));

        // Правим только заголовок, reviewOf не трогаем — 400 быть не должно
        var updated = _sut.Update(rework.Id, new UpdateTaskRequest(Title: "доработка v2"))!;
        updated.ReviewOf.Should().BeEquivalentTo([review.Id]);
    }

    [Fact]
    public void Update_ReworkСПривязкойReviewOf_Ок()
    {
        var epic = CreateEpic("u");
        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review));
        // Старая доработка без reviewOf: создаём как step (role не требует связи) и меняем
        // роль на rework, не трогая reviewOf — имитация задачи, заведённой до правила.
        var orphan = _sut.Create(null, "u", new CreateTaskRequest("сирота",
            FeatureId: epic.Id, Role: TaskRoles.Step));
        orphan.Role = TaskRoles.Rework;

        // Привязываем reviewOf — связь валидна, 400 быть не должно
        var linked = _sut.Update(orphan.Id, new UpdateTaskRequest(ReviewOf: [review.Id]))!;
        linked.Role.Should().Be(TaskRoles.Rework);
        linked.ReviewOf.Should().BeEquivalentTo([review.Id]);
    }

    // ─── Major 1: обход fail-closed через смену роли ──────────────────────────────
    // Агент создаёт step, затем tasks_update {role:"rework"} — доработка без связи. Гейт
    // срабатывает, когда эффективная роль ТОЛЬКО СЕЙЧАС стала rework (задача раньше не была
    // rework) ИЛИ меняют reviewOf. Старая rework (role уже rework, reviewOf не трогаем) — ок.

    [Fact]
    public void Update_ReworkЧерезСменуРоли_БезReviewOf_Отказ()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));

        // step → rework без reviewOf: раньше гейт не срабатывал (role менялся свободно,
        // enforce зависел только от явного reviewOf) — теперь 400
        var act = () => _sut.Update(step.Id, new UpdateTaskRequest(Role: TaskRoles.Rework));

        act.Should().Throw<InvalidOperationException>().WithMessage("*reviewOf*");
    }

    [Fact]
    public void Update_ReworkЧерезСменуРоли_СReviewOf_Ок()
    {
        var epic = CreateEpic("u");
        var step = _sut.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));
        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));

        // step → rework с reviewOf на review той же фичи: связь валидна, 400 быть не должно
        var rework = _sut.Update(step.Id, new UpdateTaskRequest(
            Role: TaskRoles.Rework, ReviewOf: [review.Id]))!;
        rework.Role.Should().Be(TaskRoles.Rework);
        rework.ReviewOf.Should().BeEquivalentTo([review.Id]);
    }

    [Fact]
    public void Update_СтараяRework_ПравкаДругогоПоля_Ок()
    {
        var epic = CreateEpic("u");
        // Старая доработка без reviewOf (создана до правила): имитируем прямым полем
        var orphan = _sut.Create(null, "u", new CreateTaskRequest("сирота",
            FeatureId: epic.Id, Role: TaskRoles.Step));
        orphan.Role = TaskRoles.Rework;

        // Правим только заголовок: role уже rework, reviewOf не трогаем → 400 быть не должно
        var updated = _sut.Update(orphan.Id, new UpdateTaskRequest(Title: "доработка v2"))!;
        updated.Role.Should().Be(TaskRoles.Rework);
        updated.ReviewOf.Should().BeNull();
    }

    // ─── Major 3: тупик роли у плана ────────────────────────────────────────────────
    // План с planSteps: (а) перевод на не-план разрешён и очищает список; (б) явный []
    // снимает список при сохранении роли plan.

    [Fact]
    public void Update_ПланНаStep_СписокОчищается_Ок()
    {
        var epic = CreateEpic("u");
        var plan = _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "бэк" }]));

        // план → step: раньше 400 «PlanSteps только у плана» (список ≠ null); теперь ок,
        // список очищается (иначе план с planSteps застревал бы навсегда)
        var step = _sut.Update(plan.Id, new UpdateTaskRequest(Role: TaskRoles.Step))!;
        step.Role.Should().Be(TaskRoles.Step);
        step.PlanSteps.Should().BeNull();
    }

    [Fact]
    public void Update_ПланСЯвнымПустымСписком_СнимаетСписок_Ок()
    {
        var epic = CreateEpic("u");
        var plan = _sut.Create(null, "u", new CreateTaskRequest("план",
            FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "бэк" }]));

        // Явный [] (не null) при сохранении роли plan — «снять список»: 400 быть не должно
        var cleared = _sut.Update(plan.Id, new UpdateTaskRequest(PlanSteps: []))!;
        cleared.Role.Should().Be(TaskRoles.Plan);
        cleared.PlanSteps.Should().BeEmpty();
    }
}
