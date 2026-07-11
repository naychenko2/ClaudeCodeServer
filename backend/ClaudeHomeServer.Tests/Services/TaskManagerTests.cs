using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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

        // «Рестарт сервера»: новый менеджер с тем же DataPath
        var reloaded = new TaskManager(BuildConfig(_dir)).GetById(task.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("выживу рестарт");
        reloaded.OwnerId.Should().Be("user-1");
        reloaded.DueDate.Should().Be("2026-08-01");
        reloaded.ReminderMinutes.Should().Be(15);
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
}
