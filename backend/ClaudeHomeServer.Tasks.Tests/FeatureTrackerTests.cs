using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// TR-B1a+TR-B1b: «Сверка трекера» (разрез Александра 26.10) — FeatureTracker.Build: список
// расхождений на том же проходе, что учёт шагов. Шесть видов: unreviewed, returned_no_rework,
// dead_verdict, reported_open, epic_open_after_merge, orphans_in_closed. Эталон каждого теста —
// условие таблицы разреза, а не вывод реализации.
//
// Мутации: TrackerGraceMinutes = 0 — тест порога красный (сданная 29 мин назад тоже попадает);
// убрать ветку «виток ещё открыт» — тест «открытое пачечное ревью покрывает шаг» красный;
// убрать метку из исключения — тест метки красный; убрать проверку статуса эпика в orphans —
// тест «открытый эпик» красный; заменить `> t.CompletedAt` на `!= null` в dead_verdict —
// тест «ревью по свежей доработке» красный; убрать требование merge-задачи в epic_open_after_merge — тест «без мержа» красный; опустить трим/регистр в метке — тест метки красный; убрать проверку chatBusy в
// reported_open — тест «чат занят» красный.
public class FeatureTrackerTests
{
    private const string Epic = "ep";
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
    // «Сейчас» — через 2 часа после T0: всё сданное на T0 старше порога 30 мин.
    private static readonly DateTime NOW = T0 + TimeSpan.FromHours(2);

    private static TaskItem Feature(TaskItemStatus status = TaskItemStatus.InProgress) => new()
    {
        Id = Epic, Role = TaskRoles.Feature, Status = status, CreatedAt = T0,
        CompletedAt = status == TaskItemStatus.Done ? T0 + TimeSpan.FromMinutes(90) : null,
    };

    // Задача шага: Done — сдан (CompletedAt = minute+1); InProgress — идёт. minute — минуты от T0.
    private static TaskItem Step(string id, string stepRef, int minute, TaskItemStatus status = TaskItemStatus.Done,
        string[]? labels = null) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = status,
        Labels = labels?.ToList() ?? [], CreatedAt = T0.AddMinutes(minute),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(minute + 1) : null,
    };

    private static TaskItem Rework(string id, string stepRef, int minute, TaskItemStatus status = TaskItemStatus.Done) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Rework, StepRef = stepRef, Status = status,
        CreatedAt = T0.AddMinutes(minute),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(minute + 1) : null,
    };

    // Виток: вердикт null — ещё открыт (InProgress, без CompletedAt).
    private static TaskItem Verdict(string id, string role, string? verdict, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = role, Status = verdict is null ? TaskItemStatus.InProgress : TaskItemStatus.Done,
        ReviewOf = reviewOf.ToList(), CreatedAt = T0.AddMinutes(minute),
        CompletedAt = verdict is null ? null : T0.AddMinutes(minute + 1),
        Verification = verdict is null ? null : new TaskVerification { Verdict = verdict },
    };

    // Задача в Todo с ReviewOf — кандидат на dead_verdict.
    private static TaskItem TodoVerdict(string id, string role, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = role, Status = TaskItemStatus.Todo,
        ReviewOf = reviewOf.ToList(), CreatedAt = T0.AddMinutes(minute),
    };

    private static TaskItem Merge(int minute) => new()
    {
        Id = "m", FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
    };

    // Тот же набор задач, что берёт FeatureProjector.BuildCore, и тот же проход учёта.
    private static List<FeatureTrackerIssue> Track(TaskItem epic, params TaskItem[] children) =>
        Track(epic, null, children);

    private static List<FeatureTrackerIssue> Track(
        TaskItem epic, IReadOnlyCollection<FeatureIndexSession>? sessions, params TaskItem[] children)
    {
        var steps = children.Where(t => t.Role is TaskRoles.Step or TaskRoles.Rework).ToList();
        var verdictTasks = children.Where(t => t.Role is TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final).ToList();
        var plan = children.Where(t => t.Role == TaskRoles.Plan).ToList();
        var accounting = FeatureStepAccounting.Build(steps, verdictTasks, plan, NOW, sessions);
        return FeatureTracker.Build(epic, children.ToList(), accounting, NOW, sessions);
    }

    // Задача с завершившимся ходом исполнителя: отчиталась (ClaudeResult != null), не закрыта.
    // minute — момент доклада: MarkClaudeResult двигает UpdatedAt, с него и считается тишина.
    private static TaskItem Reported(int minute) => new()
    {
        Id = "s", FeatureId = Epic, Role = TaskRoles.Step, StepRef = "Ш1", Status = TaskItemStatus.InProgress,
        LinkedSessionId = "chat-1", CreatedAt = T0.AddMinutes(minute), ClaudeStartedAt = T0.AddMinutes(minute),
        ClaudeResult = "success", UpdatedAt = T0.AddMinutes(minute + 1),
    };

    // Чат-исполнитель этой задачи в снимке владельца. busy — признак занятости (TR-B1b);
    // null — признака нет (снимок составлен без него).
    private static FeatureIndexSession Chat(bool? busy) => new("chat-1", "s", null, false, Busy: busy);

    private static List<FeatureTrackerIssue> OfKind(
        List<FeatureTrackerIssue> issues, string kind) =>
        issues.Where(i => i.Kind == kind).ToList();

    // ─── unreviewed ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unreviewed_ШагСданБезВитков_Расхождение()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1));

        var un = OfKind(issues, FeatureTracker.Unreviewed).Single();
        un.TaskId.Should().Be("s");
        un.StepRef.Should().Be("Ш1");
        un.Since.Should().Be(T0 + TimeSpan.FromMinutes(2), "since — момент последней сдачи");
    }

    [Fact]
    public void Unreviewed_Порог29Нет31ДаГраница30Нет()
    {
        // Сданная 29 мин назад — оркестратор мог как раз заводить ревью; ровно на пороге (30) —
        // «старше порога» строго; 31 — расхождение. Мутация TrackerGraceMinutes = 0 краснит тест.
        // NOW = T0+120; CompletedAt = minute+1 → возраст = 119 − minute.
        var s29 = Step("s29", "A", 90); // CompletedAt = T0+91 → 29 мин
        var s31 = Step("s31", "B", 88); // CompletedAt = T0+89 → 31 мин
        var s30 = Step("s30", "C", 89); // CompletedAt = T0+90 → ровно 30 мин

        var un = OfKind(Track(Feature(), s29, s31, s30), FeatureTracker.Unreviewed);
        un.Should().HaveCount(1);
        un[0].TaskId.Should().Be("s31");
    }

    [Fact]
    public void Unreviewed_РаботаПослеПринятия_НоваяСдачаБезРевью()
    {
        // accepted-виток закрылся ДО сдачи доработки — он её не покрывает: свежая работа без ревью.
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            Rework("w", "Ш1", 10));

        var un = OfKind(issues, FeatureTracker.Unreviewed).Single();
        un.TaskId.Should().Be("w");
        un.StepRef.Should().Be("Ш1");
    }

    [Fact]
    public void Unreviewed_МеткаБезРевью_НеРасхождение()
    {
        // Метка сравнивается с тримом и без учёта регистра: «  БЕЗ-РЕВЬЮ  » — та же метка.
        var issues = Track(Feature(), Step("s", "Ш1", 1, labels: ["  БЕЗ-РЕВЬЮ  "]));

        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();
    }

    [Fact]
    public void Unreviewed_ОткрытоеПачечноеРевью_ПокрываетШаг()
    {
        // Пачечное ревью, заведённое заранее и ещё открытое, покрывает обе цепочки (разрез).
        // Мутация «убрать ветку “виток ещё открыт”» краснит этот тест.
        var issues = Track(Feature(), Step("s1", "Ш1", 1), Step("s2", "Ш2", 2),
            Verdict("r", TaskRoles.Review, null, 5, "s1", "s2"));

        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();
    }

    [Fact]
    public void Unreviewed_РевьюНаИсходныйШаг_ЗакрываетДоработку()
    {
        // Цепочка общая по stepRef: accepted-виток, указывающий на исходный шаг, покрывает
        // и доработку — расхождения нет (разрез, «цепочечный уровень важен»).
        var issues = Track(Feature(), Step("s", "Ш1", 1), Rework("w", "Ш1", 5),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Accepted, 10, "s"));

        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();
    }

    [Fact]
    public void Unreviewed_СнятаяЦепочка_РевьюНеНужно()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Withdrawn, 5, "s"));

        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();
    }

    // ─── returned_no_rework ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ReturnedNoRework_ВозвратБезДоработки_Расхождение()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Returned, 10, "s"));

        var rn = OfKind(issues, FeatureTracker.ReturnedNoRework).Single();
        rn.TaskId.Should().Be("r", "источник — вернувший виток");
        rn.StepRef.Should().Be("Ш1");
        rn.Since.Should().Be(T0 + TimeSpan.FromMinutes(11));
    }

    [Fact]
    public void ReturnedNoRework_ДоработкаПослеВозврата_НеРасхождение()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Returned, 10, "s"),
            Rework("w", "Ш1", 15, TaskItemStatus.InProgress));

        OfKind(issues, FeatureTracker.ReturnedNoRework).Should().BeEmpty();
    }

    [Fact]
    public void ReturnedNoRework_ПовторноеРевьюИдёт_НеРасхождение()
    {
        // После возврата открыт новый виток — повторное ревью уже идёт, чинить нечего.
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 10, "s"),
            Rework("w", "Ш1", 15),
            Verdict("r2", TaskRoles.Review, null, 25, "w"));

        OfKind(issues, FeatureTracker.ReturnedNoRework).Should().BeEmpty();
    }

    // ─── dead_verdict ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeadVerdict_РевьюВTodoЦельУжеПринята_Расхождение()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            TodoVerdict("v", TaskRoles.Review, 20, "s"));

        var dv = OfKind(issues, FeatureTracker.DeadVerdict).Single();
        dv.TaskId.Should().Be("v");
        dv.Since.Should().Be(T0 + TimeSpan.FromMinutes(20), "since — с момента, как задача лежит в Todo");
    }

    [Fact]
    public void DeadVerdict_ПустойReviewOfУQa_НеПризнак()
    {
        // Под role=qa живут дефекты без ReviewOf — их не считаем мёртвыми.
        var issues = Track(Feature(), Step("s", "Ш1", 1, TaskItemStatus.InProgress),
            TodoVerdict("q", TaskRoles.Qa, 5));

        OfKind(issues, FeatureTracker.DeadVerdict).Should().BeEmpty();
    }

    [Fact]
    public void DeadVerdict_РевьюПоСвежейДоработке_Живое()
    {
        // Целая — доработка, сданная ПОСЛЕ принятия: её ещё никто не принимал, ревью живое.
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            Rework("w", "Ш1", 10),
            TodoVerdict("v", TaskRoles.Review, 20, "w"));

        OfKind(issues, FeatureTracker.DeadVerdict).Should().BeEmpty();
    }

    [Fact]
    public void DeadVerdict_ЦельСнятаВердиктом_Расхождение()
    {
        // Снятая цель — сама задача-шаг с вердиктом withdrawn (снятый виток цели не снимает:
        // тот случай проверяет Unreviewed_СнятаяЦепочка).
        var s = Step("s", "Ш1", 1);
        s.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn };

        var issues = Track(Feature(), s, TodoVerdict("v", TaskRoles.Qa, 20, "s"));

        OfKind(issues, FeatureTracker.DeadVerdict).Should().HaveCount(1);
    }

    // ─── reported_open ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReportedOpen_ДокладСтаршеПорогаЧатСвободен_Расхождение()
    {
        // Ход завершился на T0+89 — за 31 мин до NOW (за порогом 30), чат в снимке свободен:
        // доклад пришёл, задачу никто не разобрал.
        var issues = Track(Feature(), new[] { Chat(false) }, Reported(88));

        var ro = OfKind(issues, FeatureTracker.ReportedOpen).Single();
        ro.TaskId.Should().Be("s");
        ro.StepRef.Should().Be("Ш1");
        ro.Since.Should().Be(T0 + TimeSpan.FromMinutes(89), "since — момент доклада (последняя активность задачи)");
    }

    [Fact]
    public void ReportedOpen_ЧатЗанят_НеРасхождение()
    {
        // Та же задача и тот же возраст, но чат занят (ход идёт или ждёт ответа человека) —
        // исполнитель продолжает, расхождения нет. Мутация «убрать проверку chatBusy» краснит тест.
        var issues = Track(Feature(), new[] { Chat(true) }, Reported(88));

        OfKind(issues, FeatureTracker.ReportedOpen).Should().BeEmpty();
    }

    [Fact]
    public void ReportedOpen_НетСнимкаИлиПризнака_НеРасхождение()
    {
        // Пустой снимок — нет записи о чате; запись без признака (Busy == null, снимок составлен
        // без него) — признака нет. Оба случая — молчание (fail-closed): о чате, которого не
        // видим, не утверждаем; ложных срабатываний не будет.
        var t = Reported(88);
        OfKind(Track(Feature(), new FeatureIndexSession[0], t), FeatureTracker.ReportedOpen).Should().BeEmpty();
        OfKind(Track(Feature(), new[] { Chat(null) }, t), FeatureTracker.ReportedOpen).Should().BeEmpty();
    }

    // ─── epic_open_after_merge / orphans_in_closed ────────────────────────────────────────────

    [Fact]
    public void EpicOpenAfterMerge_МержDoneЭпикОткрыт_Расхождение()
    {
        var issues = Track(Feature(), Merge(30));

        var ep = OfKind(issues, FeatureTracker.EpicOpenAfterMerge).Single();
        ep.TaskId.Should().Be("m");
        ep.StepRef.Should().BeNull();
        ep.Since.Should().Be(T0 + TimeSpan.FromMinutes(31));
    }

    [Fact]
    public void EpicOpenAfterMerge_ВсеДочерниеDoneБезМержа_Нет()
    {
        // Без закрытой merge-задачи сигнала нет, даже если всё закрыто.
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"));

        OfKind(issues, FeatureTracker.EpicOpenAfterMerge).Should().BeEmpty();
    }

    [Fact]
    public void EpicOpenAfterMerge_МержDoneНоЕстьОткрытаяЗадача_Нет()
    {
        var open = Step("o", "Ш2", 3, TaskItemStatus.InProgress);

        OfKind(Track(Feature(), Merge(30), open), FeatureTracker.EpicOpenAfterMerge).Should().BeEmpty();
    }

    [Fact]
    public void Unreviewed_ФиналБезReviewOf_ПокрываетЦепочку()
    {
        // Открытый финал с пустым ReviewOf — виток, покрывающий любую цепочку.
        var issues = Track(Feature(), Step("s", "Ш1", 1), Verdict("f", TaskRoles.Final, null, 5));
        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();

        // Закрытый после сдачи шага — тоже покрывает.
        issues = Track(Feature(), Step("s", "Ш1", 1), Verdict("f", TaskRoles.Final, TaskVerdicts.Accepted, 5));
        OfKind(issues, FeatureTracker.Unreviewed).Should().BeEmpty();
    }

    [Fact]
    public void Unreviewed_ФиналБезReviewOfЗакрытДоСдачи_НеПокрывает()
    {
        // Финал закрылся на минуте 2, шаг сдан на минуте 11 — работа после финала без ревью.
        var issues = Track(Feature(), Verdict("f", TaskRoles.Final, TaskVerdicts.Accepted, 1),
            Step("s", "Ш1", 10));

        OfKind(issues, FeatureTracker.Unreviewed).Should().ContainSingle();
    }

    [Fact]
    public void Unreviewed_РевьюИQaБезReviewOf_НеПокрывают()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, null, 5), Verdict("q", TaskRoles.Qa, null, 6));

        OfKind(issues, FeatureTracker.Unreviewed).Should().ContainSingle();
    }

    [Fact]
    public void EpicOpenAfterMerge_ЭпикЗакрытИЛИМержНеЗакрыт_Нет()
    {
        // Эпик закрыт — расхождения нет.
        OfKind(Track(Feature(TaskItemStatus.Done), Merge(30)), FeatureTracker.EpicOpenAfterMerge).Should().BeEmpty();
        // Мерж ещё не закрыт — мерж идёт, это не «смержили и не закрыли».
        var openMerge = new TaskItem
        {
            Id = "m", FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.InProgress,
            CreatedAt = T0.AddMinutes(30),
        };
        OfKind(Track(Feature(), openMerge), FeatureTracker.EpicOpenAfterMerge).Should().BeEmpty();
    }

    [Fact]
    public void OrphansInClosed_ЭпикDoneОткрытаяЗадача_Расхождение()
    {
        var issues = Track(Feature(TaskItemStatus.Done), Step("s", "Ш1", 1, TaskItemStatus.InProgress));

        var or = OfKind(issues, FeatureTracker.OrphansInClosed).Single();
        or.TaskId.Should().Be("s");
        or.StepRef.Should().Be("Ш1");
        or.Since.Should().Be(T0 + TimeSpan.FromMinutes(90), "since — с закрытия эпика");
    }

    [Fact]
    public void OrphansInClosed_ЭпикОткрыт_Нет()
    {
        var issues = Track(Feature(), Step("s", "Ш1", 1, TaskItemStatus.InProgress));

        OfKind(issues, FeatureTracker.OrphansInClosed).Should().BeEmpty();
    }
}
