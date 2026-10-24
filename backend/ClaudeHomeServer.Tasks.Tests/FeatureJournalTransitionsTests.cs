using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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

        // created (эпик) + started + status + waiting + stopped + waiting_cleared (переход
        // в Done снимает ожидание) + status + verdict + dropped — ровно в этом порядке
        // (Equal проверяет и состав, и порядок).
        kinds.Should().Equal(
            "created", "started", "status", "waiting", "stopped", "waiting_cleared", "status", "verdict", "dropped");
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

    // ─── stop_expired: смена причины стопа без движения метки (W4-HOLD-J, R-HOLD-J п.4) ──
    // Мутация: убрать ветку stop_expired в WriteJournalTransitions — первый абзац краснеет
    // (в журнале нет причины); вернуть «stopped» вместо отдельного вида — красен второй.

    [Fact]
    public void СменаПричиныСтопа_ПишетStopExpired_БезДубляStopped()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _sut.MarkExecutorStopped(step.Id, now, ExecutorStopClassifier.FeatureHeldReason);
        _sut.MarkExecutorStopExpired(step.Id);

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "stop_expired"
            && e.Reason == ExecutorStopClassifier.FeatureHeldExpiredReason,
            "терминальная причина смены пометки в журнале");
        // Момент стопа не двигался: второго «stopped» нет — таймлайн не считает стоп дважды.
        result.Entries.Count(e => e.Kind == "stopped").Should().Be(1);
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

    // ─── Каскад: удаление дочерней задачи НЕ сносит журнал живой фичи (critical) ─────
    // Мутация «вернуть task.FeatureId в DeleteJournalFor» краснит: удаление шага снова
    // стирает файл журнала эпика.

    [Fact]
    public void УдалениеШага_НеТрогаетФайлЖурналаФичи()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);

        var path = Path.Combine(_dir, "feature-journal", "u", $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();

        // Удаление ШАГА (Role=step): эпик жив, файл журнала фичи не сносится.
        _sut.Delete(step.Id);

        File.Exists(path).Should().BeTrue("удаление шага не сносит журнал живой фичи");
        _sut.GetById(epic.Id).Should().NotBeNull("эпик (Role=feature) не удалён");
    }

    [Fact]
    public void УдалениеРевью_НеТрогаетФайлЖурналаФичи()
    {
        var (epic, _) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Ревью (Role=review) — дочерняя задача фичи.
        var review = _sut.Create(null, "u", new CreateTaskRequest("ревью",
            FeatureId: epic.Id, Role: TaskRoles.Review));
        _sut.MarkClaudeStarted(review.Id, "sess-2", now);

        var path = Path.Combine(_dir, "feature-journal", "u", $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();

        _sut.Delete(review.Id);

        File.Exists(path).Should().BeTrue("удаление ревью не сносит журнал живой фичи");
    }

    // ─── DeleteByProject: эпик в другом проекте не сносится ─────────────────────────

    [Fact]
    public void DeleteByProject_ЭпикВДругомПроекте_НеСносится()
    {
        // Эпик A + шаг A в proj-A.
        var epicA = _sut.Create("proj-A", "u", new CreateTaskRequest("эпик A", Role: TaskRoles.Feature));
        var stepA = _sut.Create("proj-A", "u", new CreateTaskRequest("шаг A",
            FeatureId: epicA.Id, Role: TaskRoles.Step));
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(stepA.Id, "sess-1", now);

        // Эпик B + шаг B в proj-B (другой проект).
        var epicB = _sut.Create("proj-B", "u", new CreateTaskRequest("эпик B", Role: TaskRoles.Feature));
        var stepB = _sut.Create("proj-B", "u", new CreateTaskRequest("шаг B",
            FeatureId: epicB.Id, Role: TaskRoles.Step));
        _sut.MarkClaudeStarted(stepB.Id, "sess-2", now);

        var pathA = Path.Combine(_dir, "feature-journal", "u", $"{epicA.Id}.jsonl");
        var pathB = Path.Combine(_dir, "feature-journal", "u", $"{epicB.Id}.jsonl");
        File.Exists(pathA).Should().BeTrue();
        File.Exists(pathB).Should().BeTrue();

        // Удаляем proj-A: эпик A (в proj-A) сносит свой журнал; эпик B (в proj-B) — нет.
        _sut.DeleteByProject("proj-A");

        File.Exists(pathA).Should().BeFalse("эпик удалённого проекта сносит свой журнал");
        File.Exists(pathB).Should().BeTrue("эпик в другом проекте не сносится");
    }

    // ─── DeleteByProject: шаг удаляемого проекта при эпике в другом проекте (Проба7) ──
    // Эпик живёт в proj-B, его шаг — в proj-A. DeleteByProject("proj-A") сносит шаг, но
    // НЕ эпик (тот в proj-B) → файл журнала эпика остаётся. Код корректен (Проба7), нужен тест.

    [Fact]
    public void DeleteByProject_ШагУдаляемогоПроекта_ЭпикВДругом_ЖурналЭпикаЖив()
    {
        // Эпик в proj-B.
        var epic = _sut.Create("proj-B", "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        // Шаг той же фичи — в proj-A (другой проект).
        var step = _sut.Create("proj-A", "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step));
        var now = DateTime.UtcNow;
        _sut.MarkClaudeStarted(step.Id, "sess-1", now);

        var path = Path.Combine(_dir, "feature-journal", "u", $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();

        // Удаляем proj-A: шаг (в proj-A) сносится, эпик (в proj-B) — нет.
        _sut.DeleteByProject("proj-A");

        _sut.GetById(step.Id).Should().BeNull("шаг удалённого проекта снесён");
        _sut.GetById(epic.Id).Should().NotBeNull("эпик в другом проекте жив");
        File.Exists(path).Should().BeTrue("журнал эпика в другом проекте не сносится");
    }

    // ─── Снятие ожидания: старт исполнителя и переход в Done пишут waiting_cleared ───
    // Мутация «убрать ClearAwaitingHuman в одной из трёх точек» краснит соответствующий тест.

    [Fact]
    public void Ожидание_СтартИсполнителя_ПишетWaitingCleared()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Ожидание человека (Update ставит AwaitingHumanSince → waiting).
        _sut.Update(step.Id, new UpdateTaskRequest(AwaitingHumanSince: now, AwaitingHumanReason: "merge"));
        // Исполнитель снова запущен → ожидание снято (waiting_cleared).
        _sut.MarkClaudeStarted(step.Id, "sess-1", now.AddMinutes(1));

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "waiting");
        result.Entries.Should().Contain(e => e.Kind == "waiting_cleared",
            "старт исполнителя снимает ожидание");
    }

    [Fact]
    public void Ожидание_ПереходВДone_ПишетWaitingCleared()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Ожидание человека.
        _sut.Update(step.Id, new UpdateTaskRequest(AwaitingHumanSince: now, AwaitingHumanReason: "merge"));
        // Переход в Done → ожидание снято (waiting_cleared).
        _sut.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "waiting");
        result.Entries.Should().Contain(e => e.Kind == "waiting_cleared",
            "переход в Done снимает ожидание");
    }

    // ─── Посторонняя правка человека НЕ снимает ожидание (Проба1) ────────────────────
    // «Ответ человека» ≠ «любая правка»: переименование/приоритет/колонка — не ответ,
    // ожидание «ждёт вас» от них не гаснет. Мутация «вернуть ветку any-Update снимает»
    // красит этот тест.

    [Fact]
    public void Ожидание_ПосторонняяПравкаЧеловека_НеСнимает()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Ожидание человека (Update ставит AwaitingHumanSince → waiting).
        _sut.Update(step.Id, new UpdateTaskRequest(AwaitingHumanSince: now, AwaitingHumanReason: "merge"));

        // Посторонние правки человеком (не агент): переименование, приоритет, колонка.
        _sut.Update(step.Id, new UpdateTaskRequest(Title: "переименовано"));
        _sut.Update(step.Id, new UpdateTaskRequest(Priority: TaskItemPriority.High));
        _sut.Update(step.Id, new UpdateTaskRequest(ColumnId: ""));

        // Ожидание на месте: поле не снято, waiting_cleared в журнале нет.
        _sut.GetById(step.Id)!.AwaitingHumanSince.Should().NotBeNull(
            "посторонняя правка человека не снимает ожидание");
        _journal.Read("u", epic.Id).Entries.Should().NotContain(e => e.Kind == "waiting_cleared",
            "посторонняя правка не пишет waiting_cleared");
    }

    // ─── device_wait / device_wait_cleared ─────────────────────────────────────────

    [Fact]
    public void ОжиданиеУстройства_ПишетDeviceWait_ИСнятие_ПишетCleared()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Ожидание устройства (MarkDeviceWait) → device_wait.
        _sut.MarkDeviceWait(step.Id, now, "device offline");
        // Снятие (ClearDeviceWait) → device_wait_cleared.
        _sut.ClearDeviceWait(step.Id);

        var result = _journal.Read("u", epic.Id);
        result.Entries.Should().Contain(e => e.Kind == "device_wait");
        result.Entries.Should().Contain(e => e.Kind == "device_wait_cleared");
        // Причина переносится.
        result.Entries.Single(e => e.Kind == "device_wait").Reason.Should().Be("device offline");
    }

    // ─── Старт НЕ подметает журналы (Б8-доработка-2, critical) ───────────────────────
    // Автоматическое подметание при старте было опасно: при испорченном/частично
    // загруженном tasks.json (JsonFileStore переносит/пропускает записи) старт сносил
    // каталоги журнала владельцев, чьи задачи не поднялись — тотальная потеря истории.
    // Теперь при старте подметания нет: каталоги целы во всех трёх сценариях.
    // Мутация «вернуть вызов подметания в конструктор» красит все три теста.

    [Fact]
    public void Старт_ИспорченныйTasksJson_ЖурналВладельцаЦел()
    {
        const string owner = "u";
        var epic = _sut.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var path = Path.Combine(_dir, "feature-journal", owner, $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();
        _sut.Flush();

        // tasks.json = не-JSON (ни массив, ни объект): Load переносит файл в .corrupt-*.bak,
        // список null → новый менеджер поднимает 0 задач, но журнал владельца не подметает.
        File.WriteAllText(Path.Combine(_dir, "tasks.json"), "{ не валидный json");

        var reloaded = new TaskManager(BuildConfig(_dir), journal: _journal);
        reloaded.Dispose();

        File.Exists(path).Should().BeTrue("испорченный tasks.json не сносит журнал владельца");
    }

    [Fact]
    public void Старт_ЧастичнаяЗагрузка_ЖурналВладельцаЦел()
    {
        const string owner = "u";
        const string other = "x";
        // Владелец owner: эпик (его задача будет БИТОЙ в tasks.json → не поднимется).
        var epic = _sut.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        // Другой владелец: живая задача (чтобы список после частичной загрузки был non-null).
        var live = _sut.Create(null, other, new CreateTaskRequest("живая"));
        var path = Path.Combine(_dir, "feature-journal", owner, $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();
        _sut.Flush();

        // tasks.json = JSON-массив, где запись владельца owner битая (не TaskItem), а запись
        // other валидная: TryLoadPerItem поднимает only other (non-null), owner не поднялся.
        var json = File.ReadAllText(Path.Combine(_dir, "tasks.json"));
        // Бьём первую запись (эпик owner): заменяем её id на битое значение.
        var corrupted = json.Replace($"\"Id\":\"{epic.Id}\"", "\"Id\":42");
        File.WriteAllText(Path.Combine(_dir, "tasks.json"), corrupted);

        var reloaded = new TaskManager(BuildConfig(_dir), journal: _journal);
        reloaded.Dispose();

        // Задача owner не поднялась (битая), но его журнал цел.
        reloaded.GetById(epic.Id).Should().BeNull("битая запись владельца не поднялась");
        reloaded.GetById(live.Id).Should().NotBeNull("валидная запись другого владельца поднялась");
        File.Exists(path).Should().BeTrue("частичная загрузка не сносит журнал владельца");
    }

    [Fact]
    public void Старт_ПустойВладелец_ЖурналЦел()
    {
        const string owner = "u";
        // Эпик владельца + журнал, затем задача владельца удалена → владелец «без задач»,
        // но каталог журнала на месте.
        var epic = _sut.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var path = Path.Combine(_dir, "feature-journal", owner, $"{epic.Id}.jsonl");
        File.Exists(path).Should().BeTrue();
        _sut.Delete(epic.Id);
        // Delete эпика сносит и файл журнала (каскад) — восстанавливаем файл напрямую,
        // чтобы сценарий был «владелец без задач, но каталог журнала существует».
        _journal.Append(owner, epic.Id, new FeatureJournalEntry
        {
            At = DateTime.UtcNow, Kind = "created", TaskId = epic.Id,
        });
        _sut.Flush();

        var reloaded = new TaskManager(BuildConfig(_dir), journal: _journal);
        reloaded.Dispose();

        // Владелец без задач, но каталог/файл журнала цел (подметания при старте нет).
        File.Exists(path).Should().BeTrue("пустой владелец не сносит журнал при старте");
    }
}
