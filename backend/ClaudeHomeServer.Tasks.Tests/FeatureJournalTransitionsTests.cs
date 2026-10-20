using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Tests.Services;

// Тесты записи переходов задач фичи в журнал (волна 3, Б8b).
// TaskManager пишет в FeatureJournalStore события для 6 полей (started/stopped/waiting/
// status/verdict/dropped) при их смене; created — при создании эпика; каскад удаления —
// при Delete эпика. Платформонезависимые: пути от Path.GetTempPath().
public class FeatureJournalTransitionsTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;
    private readonly FeatureJournalStore _journal;

    public FeatureJournalTransitionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fjt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
        // Журнал — в своём каталоге (dir-конструктор), задачи — в DataPath; оба в _dir.
        _journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _sut = new TaskManager(config, journal: _journal);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IConfiguration BuildConfig(string dir) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json")
        })
        .Build();

    // Эпик фичи (Role=feature) + дочерняя задача-шаг, привязанная к нему.
    private (TaskItem Epic, TaskItem Step) CreateFeatureAndStep(string owner = "u")
    {
        var epic = _sut.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _sut.Create(null, owner, new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));
        return (epic, step);
    }

    // ─── created: эпик ─────────────────────────────────────────────────────────

    [Fact]
    public void Create_ЭпикФичи_ПишетCreated_ПервойЗаписью()
    {
        var (epic, _) = CreateFeatureAndStep();

        var result = _journal.Read("u", epic.Id);

        result.Entries.Should().ContainSingle(e => e.Kind == "created")
            .Which.TaskId.Should().Be(epic.Id);
        // Журнал точный, только если начинается с created.
        result.StartsWithCreated.Should().BeTrue();
    }

    [Fact]
    public void Create_ДочерняяЗадача_НеПишетCreated()
    {
        var (epic, step) = CreateFeatureAndStep();

        // created пишется только для эпика; у шага created нет.
        var result = _journal.Read("u", epic.Id);
        result.Entries.Where(e => e.Kind == "created")
            .Should().ContainSingle().Which.TaskId.Should().Be(epic.Id);
        // Шаг сам по себе не создаёт записи created (он не эпик).
        result.Entries.Should().NotContain(e => e.Kind == "created" && e.TaskId == step.Id);
    }

    // ─── Сценарий: все 6 полей через мутаторы ───────────────────────────────────

    [Fact]
    public void Сценарий_ВсеПереходы_ЖурналСодержитРовноОжидаемыеСобытия()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;

        // 1. Старт исполнителя: started (+ status Todo→InProgress).
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);
        // 2. Ожидание человека: waiting.
        _sut.Update(step.Id, new UpdateTaskRequest(AwaitingHumanSince: now, AwaitingHumanReason: "merge"));
        // 3. Остановка исполнителя: stopped.
        _sut.MarkExecutorStopped(step.Id, now.AddMinutes(1), "auth_failed");
        // 4. Статус → Done + вердикт: status (InProgress→Done) + verdict.
        //    Вердикт и статус — в одном Update: в Update вердикт сбрасывается, если статус ≠ Done,
        //    поэтому Done и Verification должны приходить вместе, иначе verdict потеряется.
        _sut.Update(step.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done,
            Verification: new TaskVerification { Verdict = TaskVerdicts.Accepted, VerifiedAt = now }));
        // 5. Снятие человеком: dropped.
        _sut.MarkDroppedByHuman(step.Id, now.AddMinutes(2));

        var result = _journal.Read("u", epic.Id);
        var kinds = result.Entries.Select(e => e.Kind).ToList();

        // created (эпик) + started + status + waiting + stopped + status + verdict + dropped —
        // ровно в этом порядке (Equal проверяет и состав, и порядок).
        kinds.Should().Equal(
            "created", "started", "status", "waiting", "stopped", "status", "verdict", "dropped");
        // started — время = момент запуска (ClaudeStartedAt).
        result.Entries.Single(e => e.Kind == "started").StartedAt.Should().NotBeNull();
        // waiting — причина переносится.
        result.Entries.Single(e => e.Kind == "waiting").Reason.Should().Be("merge");
        // stopped — причина переносится.
        result.Entries.Single(e => e.Kind == "stopped").Reason.Should().Be("auth_failed");
        // verdict — значение переносится.
        result.Entries.Single(e => e.Kind == "verdict").Verdict.Should().Be(TaskVerdicts.Accepted);
        // status — from/to: первый InProgress (из started), второй Done.
        result.Entries.Where(e => e.Kind == "status").Select(e => e.To).Should()
            .BeEquivalentTo("InProgress", "Done");
    }

    // ─── Снятие: stop_cleared / waiting_cleared ─────────────────────────────────

    [Fact]
    public void Снятие_Остановки_ПишетStopCleared()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;

        // Остановка, затем снятие (ClearExecutorStopped сам гасит ExecutorStoppedAt → stop_cleared).
        _sut.MarkExecutorStopped(step.Id, now, "auth_failed");
        _sut.ClearExecutorStopped(step.Id, "auth_failed");

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "stopped");
        result.Entries.Should().Contain(e => e.Kind == "stop_cleared");
        // Время stop_cleared — момент мутации (не значение снятого поля).
        result.Entries.Single(e => e.Kind == "stop_cleared").At.Should().BeOnOrAfter(now);
    }

    [Fact]
    public void Снятие_Ожидания_ПишетWaitingCleared()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;

        // Ожидание (Update ставит AwaitingHumanSince → waiting).
        _sut.Update(step.Id, new UpdateTaskRequest(AwaitingHumanSince: now, AwaitingHumanReason: "merge"));
        // Снятие через мутатор: Capture фиксирует состояние ДО клира (waiting), сам клир —
        // сразу после Capture, до WriteJournalTransitions → мутатор видит set→null → waiting_cleared.
        // (В Update AwaitingHumanSince=null = «без изменения», явного клира там нет — см. ClearAwaitingHuman.)
        _sut.ClearAwaitingHuman(step.Id);

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "waiting");
        result.Entries.Should().Contain(e => e.Kind == "waiting_cleared");
        // Время waiting_cleared — момент мутации (не значение снятого поля).
        result.Entries.Single(e => e.Kind == "waiting_cleared").At.Should().BeOnOrAfter(now);
    }

    // ─── Идемпотентность: повторная мутация без смены значения ──────────────────

    [Fact]
    public void ПовторнаяМутация_БезСменыЗначения_НеДобавляетСобытий()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;

        // Старт.
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);
        var afterStart = _journal.Read("u", epic.Id).Entries.Count;

        // Повторный старт с ТОЙ же сессией/временем: ClaudeStartedAt не сменился (тот же atUtc)
        // → started не пишится повторно.
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);
        _journal.Read("u", epic.Id).Entries.Count.Should().Be(afterStart,
            "повторный старт без смены ClaudeStartedAt не добавляет started");

        // Повторный статус на тот же InProgress: status не пишится.
        _sut.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress));
        _journal.Read("u", epic.Id).Entries.Count.Should().Be(afterStart,
            "повторный статус без смены не добавляет status");
    }

    // ─── Задача без фичи не пишется ─────────────────────────────────────────────

    [Fact]
    public void ЗадачаБезФичи_НеПишетВЖурнал()
    {
        // Обычная задача без FeatureId/Role — журнал не трогает.
        var task = _sut.Create(null, "u", new CreateTaskRequest("личная"));
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(task.Id, "sess-1", now);
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        // Файл журнала не создан (нет ни эпика, ни фичи).
        var ownerDir = Path.Combine(_dir, "feature-journal", "u");
        (Directory.Exists(ownerDir) ? Directory.GetFiles(ownerDir, "*.jsonl") : Array.Empty<string>())
            .Should().BeEmpty("задача без фичи не создаёт файл журнала");
    }

    // ─── Каскад: удаление эпика удаляет файл журнала ───────────────────────────

    [Fact]
    public void УдалениеЭпика_УдаляетФайлЖурнала()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);

        // Файл журнала существует.
        var path = Path.Combine(_dir, "feature-journal", "u", $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();

        // Удаление эпика → каскад Delete.
        _sut.Delete(epic.Id);

        File.Exists(path).Should().BeFalse("удаление эпика уносит файл её журнала");
    }

    [Fact]
    public void УдалениеПроекта_УдаляетЖурналЭпиков()
    {
        var epic = _sut.Create("proj-1", "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _sut.Create("proj-1", "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step));
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);

        var path = Path.Combine(_dir, "feature-journal", "u", $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();

        _sut.DeleteByProject("proj-1");

        File.Exists(path).Should().BeFalse();
    }

    // ─── SweepOrphans при старте ───────────────────────────────────────────────

    [Fact]
    public void SweepOrphans_ПриСтарте_ПодметаетФайлБезЖивогоЭпика()
    {
        const string owner = "u";
        // Живая задача владельца — чтобы SweepOrphans прошёл по этому владельцу
        // (метод итерирует владельцев из _tasks; без живой задачи владельца нет).
        var live = _sut.Create(null, owner, new CreateTaskRequest("живая"));
        // Сирота: файл журнала фичи, эпик которой уже не в tasks.
        const string orphanFeature = "orphan-feature";
        _journal.Append(owner, orphanFeature, new FeatureJournalEntry
        {
            At = DateTime.UtcNow, Kind = "created", TaskId = orphanFeature,
        });
        var path = Path.Combine(_dir, "feature-journal", owner, $"{orphanFeature}.jsonl");
        File.Exists(path).Should().BeTrue();

        // Персистим tasks (живая задача), чтобы новый менеджер загрузил владельца.
        _sut.Flush();

        // Новый менеджер: видит живую задачу (владелец "u"), SweepOrphans подметает сироту.
        var reloaded = new TaskManager(BuildConfig(_dir), journal: _journal);
        reloaded.Dispose();

        File.Exists(path).Should().BeFalse("SweepOrphans подмёл сироту при старте");
        reloaded.GetById(live.Id).Should().NotBeNull("живая задача не затронута");
    }
}
