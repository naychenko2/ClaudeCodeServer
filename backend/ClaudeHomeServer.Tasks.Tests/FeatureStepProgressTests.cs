using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tasks.Tests.Fixtures;

namespace ClaudeHomeServer.Tasks.Tests;

// «Честный прогресс» (Б-pr2): единица учёта — шаг (stepRef), «принят» — по ревью, а не по Done;
// знаменатель — план ∪ заведённые шаги; доработки не раздувают Total. Эталон считан руками из
// сценария каждого теста, а не из вывода реализации.
public class FeatureStepProgressTests
{
    private const string Epic = "ep";
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TaskItem Feature() => new() { Id = Epic, Role = TaskRoles.Feature, CreatedAt = T0 };

    private static TaskItem Step(string id, string? stepRef, int minute, TaskItemStatus status = TaskItemStatus.Done,
        string? lane = "back") => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = status, Lane = lane,
        CreatedAt = T0.AddMinutes(minute),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(minute + 1) : null,
    };

    private static TaskItem Rework(string id, string? stepRef, int minute, string[]? reviewOf = null,
        TaskItemStatus status = TaskItemStatus.Done, string? verdict = null) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Rework, StepRef = stepRef, Status = status,
        ReviewOf = reviewOf?.ToList(), CreatedAt = T0.AddMinutes(minute),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(minute + 1) : null,
        Verification = verdict is null ? null : new TaskVerification { Verdict = verdict },
    };

    private static TaskItem Verdict(string id, string role, string? verdict, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = role, Status = verdict is null ? TaskItemStatus.InProgress : TaskItemStatus.Done,
        ReviewOf = reviewOf.ToList(), CreatedAt = T0.AddMinutes(minute),
        CompletedAt = verdict is null ? null : T0.AddMinutes(minute + 1),
        Verification = verdict is null ? null : new TaskVerification { Verdict = verdict },
    };

    private static TaskItem Plan(string id, int minute, params (string Ref, string? Lane)[] steps) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Plan, Status = TaskItemStatus.Done,
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
        PlanSteps = steps.Length == 0 ? null : steps.Select(x => new PlannedStep { StepRef = x.Ref, Title = x.Ref, Lane = x.Lane }).ToList(),
    };

    private static FeatureRun Run(params TaskItem[] tasks) =>
        FeatureProjector.Build(tasks.First(t => t.Role == TaskRoles.Feature), tasks, T0.AddDays(1));

    // ─── Возвраты финала на всех кругах: счётчики и цепочки не расходятся ──────────────────────

    // f1 returned → доработка → f2 accepted: виток f1 виден и в цепочке, и в счётчиках.
    [Fact]
    public void Возвраты_ДваКруга_СчётчикиСовпадаютСЦепочкой()
    {
        var run = Run(Feature(), Step("s1", "A", 0), Verdict("f1", TaskRoles.Final, TaskVerdicts.Returned, 5, "s1"),
            Rework("w1", "A", 8, ["f1"]), Verdict("f2", TaskRoles.Final, TaskVerdicts.Accepted, 12, "s1"));

        run.StepChains.Single(c => c.StepRef == "A").Loops.Should().Be(1);
        run.Verdicts.Loops.Should().Be(1, "возврат первого круга не теряется при принятом втором");
        run.ReturnsBySource.Final.Count.Should().Be(1);
        run.Verdicts.Returned.Should().Be(1);
        run.Verdicts.Accepted.Should().Be(1);
    }

    [Fact]
    public void Возвраты_ТриКруга_СчитаютсяВсеФиналы()
    {
        var run = Run(Feature(), Step("s1", "A", 0),
            Verdict("f1", TaskRoles.Final, TaskVerdicts.Returned, 5, "s1"), Rework("w1", "A", 8, ["f1"]),
            Verdict("f2", TaskRoles.Final, TaskVerdicts.Returned, 12, "s1"), Rework("w2", "A", 15, ["f2"]),
            Verdict("f3", TaskRoles.Final, TaskVerdicts.Accepted, 20, "s1"));

        run.StepChains.Single(c => c.StepRef == "A").Loops.Should().Be(2);
        run.Verdicts.Loops.Should().Be(2);
        run.ReturnsBySource.Final.Count.Should().Be(2);
        run.ReturnsBySource.Final.Open.Should().Be(0, "обе доработки закрыты");
        run.Verdicts.Accepted.Should().Be(1);
    }

    [Fact]
    public void Возвраты_ОдинФинал_ПоведениеПрежнее()
    {
        var run = Run(Feature(), Step("s1", "A", 0), Verdict("f1", TaskRoles.Final, TaskVerdicts.Returned, 5, "s1"));

        run.Verdicts.Loops.Should().Be(1);
        run.ReturnsBySource.Final.Count.Should().Be(1);
        run.StepChains.Single(c => c.StepRef == "A").Loops.Should().Be(1);
    }

    // ─── Принятие: одна правда ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TaskVerdicts.Accepted, true)]
    [InlineData(TaskVerdicts.AcceptedWithRemarks, true)]
    [InlineData(TaskVerdicts.Returned, false)]
    [InlineData(TaskVerdicts.Withdrawn, false)]
    [InlineData(null, false)]
    public void Принятие_Шаг_Ready_МержПендинг_СогласованыПоОдномуПредикату(string? verdict, bool expectAccepted)
    {
        FeatureProjector.IsAcceptance(verdict).Should().Be(expectAccepted);

        // Шаг: виток с этим вердиктом.
        var stepRun = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, verdict, 5, "s"));
        // accepted ⇔ IsAcceptance; withdrawn — отдельная корзина (вне знаменателя), не «принят».
        (stepRun.StepProgress.Accepted == 1).Should().Be(expectAccepted, "шаг принят ровно когда вердикт — принятие");

        // Финал с тем же вердиктом: Ready и merge_pending идут от того же предиката.
        var finalRun = Run(Feature(), Verdict("f", TaskRoles.Final, verdict, 5));
        finalRun.Ready.Should().Be(expectAccepted);
        (finalRun.State == FeatureStates.MergePending).Should().Be(expectAccepted);
    }

    [Fact]
    public void ШагПринятСЗамечаниями_НеВозврат_ПринятиеНеСчитаетсяПетлёй()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, TaskVerdicts.AcceptedWithRemarks, 5, "s"));

        run.StepProgress.Accepted.Should().Be(1);
        run.StepChains.Single().Outcome.Should().Be("accepted");
        // Ось «сколько раз возвращали» awr по-прежнему учитывает.
        run.StepChains.Single().Loops.Should().Be(1);
        run.Verdicts.Loops.Should().Be(1);
    }

    // ─── Корзины ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ШагDoneБезВитков_НаРевью_НеПринят_ВЛинииТоже()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1));

        run.StepProgress.Should().BeEquivalentTo(new
        {
            PlanState = "pending", Total = 1, Accepted = 0, Review = 1, Work = 0, NotCreated = 0,
            Withdrawn = 0, UnlinkedReworks = 0,
        });
        var lane = run.Lanes.Single();
        lane.Accepted.Should().Be(0, "сдан исполнителем — не принят: линия не «1 из 1»");
        lane.Review.Should().Be(1);
        lane.Total.Should().Be(1);
        run.StepsAccepted.Should().Be(0);
        run.StepsTotal.Should().Be(1);
    }

    [Fact]
    public void ПоследнийВитокReturned_ВРаботе_ДажеЕслиВсёЗакрыто()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"));

        run.StepProgress.Work.Should().Be(1);
        run.StepChains.Single().Outcome.Should().Be("returned");
        run.Lanes.Single().Work.Should().Be(1);
    }

    [Fact]
    public void ПринятоНоДоработкаОткрыта_ВРаботе()
    {
        // accepted-виток по доработке при её незакрытом статусе: принятия нет, пока доработка не закрыта.
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"),
            Rework("w", "Ш1", 8, ["r1"], TaskItemStatus.InProgress),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 9, "s"));

        run.StepProgress.Accepted.Should().Be(0);
        run.StepProgress.Work.Should().Be(1);
    }

    [Fact]
    public void ВитокИдёт_НаРевью()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, null, 5, "s"));

        run.StepProgress.Review.Should().Be(1);
        run.StepChains.Single().Outcome.Should().Be("running");
    }

    [Fact]
    public void ШагВРаботе_InProgress()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1, TaskItemStatus.InProgress));

        run.StepProgress.Work.Should().Be(1);
        run.StepChains.Single().Outcome.Should().Be("running");
    }

    // ─── Доработки не раздувают знаменатель ──────────────────────────────────────────────────

    [Fact]
    public void ДоработкаТогоЖеСтепРефа_ЗнаменательНеРастёт_ШагПринят()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"),
            Rework("w", "Ш1", 8),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 12, "w"));

        run.StepProgress.Total.Should().Be(1);
        run.StepProgress.Accepted.Should().Be(1);
        run.StepProgress.UnlinkedReworks.Should().Be(0);
        run.StepChains.Should().ContainSingle().Which.TaskIds.Should().Equal("s", "w");
    }

    [Fact]
    public void ПакетнаяДоработка_ДругойСтепРеф_ЗакрываетОбаШага_ТоталНеРастёт()
    {
        // Ревью returned на два шага → одна доработка «Б9» (stepRef не совпал ни с одним шагом),
        // привязана ТОЛЬКО через reviewOf → повторное ревью принимает работу по обоим шагам.
        var run = Run(Feature(), Step("s1", "Ш1", 1), Step("s2", "Ш2", 2),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s1", "s2"),
            Rework("w", "Б9", 8, ["r1"]),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 12, "w"));

        run.StepProgress.Total.Should().Be(2, "доработка знаменатель не раздувает");
        run.StepProgress.Accepted.Should().Be(2, "доработка закрывает оба шага пакета");
        run.StepProgress.UnlinkedReworks.Should().Be(0);
        run.StepChains.Select(c => c.StepRef).Should().Equal("Ш1", "Ш2");
    }

    [Fact]
    public void ДоработкаЧерезЦепочкуДоработок_ПривязанаТранзитивно()
    {
        // w1 → r2 (returned по w1) → w2 (reviewOf r2): w2 принадлежит шагу через w1.
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"),
            Rework("w1", "Б9", 8, ["r1"]),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Returned, 12, "w1"),
            Rework("w2", "Б9", 15, ["r2"]),
            Verdict("r3", TaskRoles.Review, TaskVerdicts.Accepted, 19, "w2"));

        run.StepProgress.Total.Should().Be(1);
        run.StepProgress.Accepted.Should().Be(1);
        run.StepProgress.UnlinkedReworks.Should().Be(0);
    }

    [Fact]
    public void Сироты_ДоработкаБезПривязки_НеЗаводитЦепочкуИНеРаздуваетТотал()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Rework("w1", "Нет-такого", 5), Rework("w2", null, 6));

        run.StepProgress.Total.Should().Be(1, "сироты в знаменатель не входят");
        run.StepProgress.UnlinkedReworks.Should().Be(2);
        run.StepChains.Should().ContainSingle().Which.StepRef.Should().Be("Ш1");
    }

    // ─── Порядок витков: по завершению (решение 2 ревью Александра) ────────────────────────────

    [Fact]
    public void ВиткиПоЗавершению_ПоследнийВозвратРешает_ШагВРаботе()
    {
        // Зонд 11 Глеба: r1 returned старт 5/завершён 40, r2 accepted старт 9/завершён 20.
        // По старту последним был бы accepted; по завершению — returned ⇒ шаг честно «в работе».
        var r1 = Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s");
        r1.CompletedAt = T0.AddMinutes(40);
        var r2 = Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 9, "s");
        r2.CompletedAt = T0.AddMinutes(20);
        var run = Run(Feature(), Step("s", "Ш1", 1), r1, r2);

        run.StepProgress.Work.Should().Be(1);
        run.StepProgress.Accepted.Should().Be(0);
        // Порядок один на всю цепочку: N, lastVerdict и Source — из одного порядка по завершению.
        var chain = run.StepChains.Single();
        chain.Rounds.Select(r => r.N).Should().Equal(1, 2);
        chain.Rounds[^1].ReviewTaskId.Should().Be("r1");
        chain.Rounds[^1].Verdict.Should().Be(TaskVerdicts.Returned);
        chain.Source.Should().Be("review");
    }

    [Fact]
    public void ИдущийВитокПоследним_ШагНаРевью_АНеПринят()
    {
        // Первый виток закрыт accepted, второй идёт (без CompletedAt): по завершению он последний —
        // шаг числится «на ревью», а не «принят».
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            Verdict("r2", TaskRoles.Review, null, 9, "s"));

        run.StepProgress.Review.Should().Be(1);
        run.StepChains.Single().Outcome.Should().Be("running");
    }

    // ─── Ожидание и принятие (макет A2, плашка карточки) ──────────────────────────────────────

    // WaitingSince = момент закрытия ПОСЛЕДНЕЙ задачи цепочки (шаг становится обозреваемым
    // только когда всё закрыто), а не первого. Мутация «Min вместо Max» краснит этот тест.
    [Fact]
    public void ЖдётРевью_WaitingSince_МоментЗакрытияПоследнейЗадачи()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1), Rework("w", "Ш1", 8));

        var chain = run.StepChains.Single();
        chain.Outcome.Should().Be("pending");
        chain.WaitingSince.Should().Be(T0.AddMinutes(9), "ожидание начинается с закрытия доработки, не шага");
        chain.AcceptedAt.Should().BeNull("принятия нет");
    }

    // Грязные данные: закрытая задача без CompletedAt. Фолбэк «?? nowUtc» подставил бы
    // «сейчас» (плашка «0 мин»); без фолбэка — честный null. Мутация «вернуть ?? nowUtc»
    // (FeatureStepAccounting.cs:226) краснит этот тест.
    [Fact]
    public void ГрязныеДанные_БезДатыЗакрытия_ОжиданияНет_НеСейчас()
    {
        var step = Step("s", "Ш1", 1);
        step.CompletedAt = null;
        var run = Run(Feature(), step);

        var chain = run.StepChains.Single();
        chain.Outcome.Should().Be("pending");
        chain.WaitingSince.Should().BeNull("нет даты — null, а не «сейчас»");
        chain.AcceptedAt.Should().BeNull();
    }

    // AcceptedAt = CompletedAt витка-принятия. Мутация «featureRounds[0] вместо [^1]» краснит
    // этот тест: первый виток здесь returned (закрыт +6), принятие — второй (+13).
    [Fact]
    public void Принят_AcceptedAt_МоментЗакрытияПоследнегоПринятия_ОжиданияНет()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"),
            Rework("w", "Ш1", 8),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.AcceptedWithRemarks, 12, "s"));

        var chain = run.StepChains.Single();
        chain.Outcome.Should().Be("accepted");
        chain.AcceptedAt.Should().Be(T0.AddMinutes(13), "принял последний accepted-виток (r2)");
        chain.WaitingSince.Should().BeNull("шаг не ждёт — принят");
    }

    // «Идёт» (открытый виток) и «закрыт без повторного ревью» (returned) — не ожидание:
    // плашка деградирует честно, без времени.
    [Fact]
    public void ИдущийВитокИлиВозврат_НиОжиданияНиПринятия()
    {
        var running = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, null, 5, "s")).StepChains.Single();
        running.Outcome.Should().Be("running");
        running.WaitingSince.Should().BeNull();
        running.AcceptedAt.Should().BeNull();

        var returned = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, TaskVerdicts.Returned, 5, "s")).StepChains.Single();
        returned.Outcome.Should().Be("returned");
        returned.WaitingSince.Should().BeNull();
        returned.AcceptedAt.Should().BeNull();
    }

    // ─── Снятие шага (решение 3 ревью Александра) ──────────────────────────────────────────────

    [Fact]
    public void СнятаяДоработка_ИсключенаИзЦепочки_ШагСчитаетсяПоОстальному()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 5, "s"),
            Rework("w", "Ш1", 8, null, TaskItemStatus.InProgress, TaskVerdicts.Withdrawn),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 12, "s"));

        run.StepProgress.Total.Should().Be(1, "снятая доработка не выносит шаг из знаменателя");
        run.StepProgress.Accepted.Should().Be(1, "снятая доработка не открывает work и не считается в reworkOpen");
        run.StepProgress.Withdrawn.Should().Be(0);
        run.StepChains.Single().TaskIds.Should().NotContain("w", "снятая доработка не входит в цепочку");
    }

    [Fact]
    public void ПоследнийВитокWithdrawn_ШагСнят()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Withdrawn, 9, "s"));

        run.StepProgress.Withdrawn.Should().Be(1);
        run.StepProgress.Total.Should().Be(0);
        var chain = run.StepChains.Single();
        chain.Outcome.Should().Be("withdrawn");
        // Снят — не ожидание и не принятие: оба поля null, плашка деградирует честно.
        chain.WaitingSince.Should().BeNull();
        chain.AcceptedAt.Should().BeNull();
    }

    [Fact]
    public void СнятыйВитокВСерединеЦепочки_ШагНеСнят()
    {
        var run = Run(Feature(), Step("s", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"),
            Verdict("r2", TaskRoles.Review, TaskVerdicts.Withdrawn, 9, "s"),
            Verdict("r3", TaskRoles.Review, TaskVerdicts.Accepted, 12, "s"));

        run.StepProgress.Accepted.Should().Be(1);
        run.StepProgress.Withdrawn.Should().Be(0);
    }

    [Fact]
    public void СнятаяЗадачаШаг_ШагСнят()
    {
        var step = Step("s", "Ш1", 1);
        step.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn };
        var run = Run(Feature(), step);

        run.StepProgress.Withdrawn.Should().Be(1);
        run.StepProgress.Total.Should().Be(0);
    }

    // ─── Единый сравнитель кодов шагов (решение 4 ревью Александра) ────────────────────────────

    [Fact]
    public void РегистрКодаШага_ПланИШаг_ОдинШаг_ЛишнегоНеСоздаёт()
    {
        var run = Run(Feature(), Plan("p", 0, ("Б1", "back")), Step("s", "б1", 1));

        run.StepProgress.Total.Should().Be(1, "«Б1» в плане и «б1» в шаге — один шаг");
        run.StepProgress.NotCreated.Should().Be(0);
        run.StepChains.Single().TaskIds.Should().Equal("s");
    }

    // Трим кода шага плана (замечание 1 ревью Глеба): KeyOf тримит код шага, а план добавлялся
    // без трима — «Б1 » в плане и «Б1» в шаге дали два ключа (Total=2, NotCreated=1). Мутация
    // «убрать трим у плана» (FeatureStepAccounting.cs:59) краснит только этот тест: соседний
    // проверяет трим у ключа шага (KeyOf) и от плана не зависит.
    [Fact]
    public void ТримКодаШагаПлана_ПробелВПлане_ОдинШаг()
    {
        var run = Run(Feature(), Plan("p", 0, ("Б1 ", "back")), Step("s", "Б1", 1));

        run.StepProgress.Total.Should().Be(1, "«Б1 » в плане и «Б1» в шаге — один шаг");
        run.StepProgress.NotCreated.Should().Be(0);
    }

    // Трим ключа шага: KeyOf (FeatureStepAccounting.cs:36) тримит StepRef — «Б1 » у шага
    // сходится с «Б1» в плане. Мутация «убрать трим в KeyOf» краснит этот тест; «убрать трим
    // у плана» — нет.
    [Fact]
    public void ТримКодаШага_ПробелУШага_ПланБезПробела_ОдинШаг()
    {
        var run = Run(Feature(), Plan("p", 0, ("Б1", "back")), Step("s", "Б1 ", 1));

        run.StepProgress.Total.Should().Be(1, "пробел у шага не создаёт отдельный шаг");
        run.StepProgress.NotCreated.Should().Be(0);
    }

    [Fact]
    public void ДоработкаВДругомРегистре_Привязана_КаноничнымКлючом()
    {
        // Шаг заведён кодом «б1» (каноничный ключ — первое появление), доработка написана «Б1»:
        // в набор кладется каноничный ключ группы, а не написание доработки.
        var run = Run(Feature(), Step("s", "б1", 1), Rework("w", "Б1", 5));

        run.StepProgress.Total.Should().Be(1);
        run.StepProgress.UnlinkedReworks.Should().Be(0);
        run.StepChains.Single().TaskIds.Should().Equal("s", "w");
        run.StepChains.Single().StepRef.Should().Be("б1", "показ — в написании первого появления");
    }

    // ─── Знаменатель: план ∪ заведённые ──────────────────────────────────────────────────────

    [Fact]
    public void ШагиПлана_СРевью_ВЗнаменателеТолькоStep()
    {
        var plan = Plan("p", 0, ("B1", "back"), ("B2", "back"), ("R-B1", "back"), ("QA", null));
        plan.PlanSteps![2].Kind = PlannedStep.KindReview;
        plan.PlanSteps[3].Kind = PlannedStep.KindQa;
        var run = Run(Feature(), plan);

        run.StepProgress.Total.Should().Be(2);
        run.StepProgress.NotCreated.Should().Be(2);
    }

    [Fact]
    public void ШагиПлана_ШагСExplicitKindStep_Считается()
    {
        var plan = Plan("p", 0, ("B1", "back"));
        plan.PlanSteps![0].Kind = PlannedStep.KindStep;

        Run(Feature(), plan).StepProgress.Total.Should().Be(1);
    }

    [Fact]
    public void ШагиПлана_БезЗадач_NotCreated_ВЗнаменателе()
    {
        var run = Run(Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "front"), ("Ш3", "front")), Step("s", "Ш1", 1));

        var p = run.StepProgress;
        p.PlanState.Should().Be("cut");
        p.Total.Should().Be(3);
        p.NotCreated.Should().Be(2);
        p.Review.Should().Be(1);
        run.Lanes.Single(l => l.Lane == "front").NotCreated.Should().Be(2);
        run.Lanes.Single(l => l.Lane == "front").Total.Should().Be(2);
    }

    // B3: последний план вместо объединения. Ранний [A,B,C], поздний [A,B] — C не «не заведён».
    [Fact]
    public void ПланыСПересечением_ПобеждаетПоздний_РаннийВычёркиваетсяЦеликом()
    {
        var run = Run(Feature(),
            Plan("p1", 0, ("A", "back"), ("B", "back"), ("C", "back")),
            Plan("p2", 5, ("A", "back"), ("B", "back")));

        run.StepProgress.Total.Should().Be(2);
        run.StepProgress.NotCreated.Should().Be(2, "C из замещённого плана не считается");
    }

    // Ранний план вычёркивается целиком, даже если у него есть непересекающиеся шаги.
    [Fact]
    public void ПланыСПересечением_НепересекающийсяШагРаннегоТожеВычёркивается()
    {
        var run = Run(Feature(),
            Plan("p1", 0, ("A", "back"), ("X", "back")),
            Plan("p2", 5, ("A", "back"), ("B", "back")));

        run.StepProgress.Total.Should().Be(2, "A и B; X ушёл вместе с ранним планом");
    }

    // Порядок на входе не важен: побеждает (CreatedAt, Id), а не позиция в списке.
    [Fact]
    public void ПланыСПересечением_ПорядокВхода_НеВлияет()
    {
        var early = Plan("p1", 0, ("A", "back"), ("C", "back"));
        var late = Plan("p2", 5, ("A", "back"));

        Run(Feature(), late, early).StepProgress.Total.Should().Be(1);
        Run(Feature(), early, late).StepProgress.Total.Should().Be(1);
    }

    // Одинаковый CreatedAt: побеждает больший Id (детерминизм).
    [Fact]
    public void ПланыСПересечением_ОдинаковыйCreatedAt_ПобеждаетБольшийId()
    {
        var a = Plan("p-a", 0, ("A", "back"), ("X", "back"));
        var b = Plan("p-b", 0, ("A", "back"), ("Y", "back"));

        // Конкретный победитель в обоих порядках входа: без tie-break по Id порядок решал бы исход.
        FeatureStepAccounting.ActivePlans([a, b]).Select(p => p.Id).Should().Equal("p-b");
        FeatureStepAccounting.ActivePlans([b, a]).Select(p => p.Id).Should().Equal("p-b");
        Run(Feature(), a, b, Step("s", "Y", 3)).StepProgress.NotCreated.Should().Be(1, "Y заведён, A нет");
        Run(Feature(), b, a, Step("s", "Y", 3)).StepProgress.NotCreated.Should().Be(1, "Y заведён, A нет");
        Run(Feature(), b, a, Step("s", "X", 3)).StepProgress.Total.Should().Be(3, "X вне плана p-b, но заведён — считается");
    }

    // Вытесненный план не воскрешает шаги через третий: p1[B], p2[A,B], p3[A] → живёт только p3.
    [Fact]
    public void ПланыЦепочкойПересечений_ВытесненныйНеВозвращаетШаги()
    {
        var p1 = Plan("p1", 0, ("B", "back"));
        var p2 = Plan("p2", 1, ("A", "back"), ("B", "back"));
        var p3 = Plan("p3", 2, ("A", "back"));

        Run(Feature(), p1, p2, p3).StepProgress.Total.Should().Be(1);
        Run(Feature(), p3, p1, p2).StepProgress.Total.Should().Be(1);
        FeatureStepAccounting.ActivePlans([p2, p3, p1]).Select(p => p.Id).Should().Equal("p3");
    }

    // B3b: run.ActivePlanIds — ровно действующие планы (тот же ActivePlans): вытесненный и снятый
    // в списке отсутствуют, независимый (без пересечений) присутствует; SlimRun id не теряет.
    [Fact]
    public void ActivePlanIds_ТолькоДействующиеПланы_ПереживаетSlimRun()
    {
        var early = Plan("p1", 0, ("A", "back"));
        var late = Plan("p2", 5, ("A", "back"));
        var other = Plan("p3", 1, ("Z", "front"));
        var withdrawn = Plan("p4", 2, ("W", "back"));
        withdrawn.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn };

        var run = Run(Feature(), early, late, other, withdrawn);

        run.Plan.Should().HaveCount(4);
        run.ActivePlanIds.Should().BeEquivalentTo("p2", "p3");
        FeatureProjector.SlimRun(run).ActivePlanIds.Should().BeEquivalentTo("p2", "p3");
        Run(Feature()).ActivePlanIds.Should().BeEmpty();
    }

    // B3b: цепочка пересечений p1[B], p2[A,B], p3[A] → действующий только p3.
    [Fact]
    public void ActivePlanIds_ЦепочкаПересечений_ТолькоПоследний()
    {
        var run = Run(Feature(), Plan("p1", 0, ("B", "back")), Plan("p2", 1, ("A", "back"), ("B", "back")),
            Plan("p3", 2, ("A", "back")));

        run.ActivePlanIds.Should().Equal("p3");
    }

    // Планы по линиям без пересечений объединяются.
    [Fact]
    public void ПланыБезПересечения_Объединяются()
    {
        var run = Run(Feature(),
            Plan("p1", 0, ("B1", "back"), ("B2", "back")),
            Plan("p2", 2, ("F1", "front")));

        run.StepProgress.Total.Should().Be(3);
        run.StepProgress.NotCreated.Should().Be(3);
        run.StepProgress.PlanState.Should().Be("cut");
    }

    // Снятый вручную план не входит ни в planned, ни в PlanState (находка плана №3).
    [Fact]
    public void СнятыйПлан_НеВходитВШагиИВСостояние()
    {
        var old = Plan("p-old", 0, ("A", "back"), ("Z", "back"));
        old.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn };
        var run = Run(Feature(), old, Plan("p-new", 1, ("B", "back")));

        run.StepProgress.Total.Should().Be(1, "шаги снятого плана не считаются");
        run.StepProgress.PlanState.Should().Be("cut");
    }

    // Замещённый план не держит фичу в partial: PlanState — по тому же набору, что и шаги.
    [Fact]
    public void ЗамещённыйПланБезСписка_НеВходитВPlanState()
    {
        var run = Run(Feature(), Plan("p-empty", 0), Plan("p1", 1, ("A", "back")), Plan("p2", 2, ("A", "back")));

        // p-empty без списка остаётся действующим → partial; замещённый p1 в счёт не идёт.
        run.StepProgress.PlanState.Should().Be("partial");
        run.StepProgress.Total.Should().Be(1);
    }

    [Fact]
    public void ПланыБезПересечения_ПлановШагиВПланеПомечены()
    {
        var plan1 = Plan("p1", 0, ("A", "back"), ("B", "back"));
        var plan2 = Plan("p2", 5, ("B", "back"));
        // A замещён целиком → шаг A вне действующего плана.
        var acc = FeatureStepAccounting.Build(
            [Step("sa", "A", 1), Step("sb", "B", 6)], [], [plan1, plan2], T0.AddDays(1), null);

        acc.Facts["A"].InPlan.Should().BeFalse();
        acc.Facts["B"].InPlan.Should().BeTrue();
    }

    [Fact]
    public void ШагВнеПлана_ТожеВЗнаменателе()
    {
        var run = Run(Feature(), Plan("p", 0, ("Ш1", "back")), Step("s1", "Ш1", 1), Step("s2", "Ш-внеплана", 2));

        run.StepProgress.Total.Should().Be(2, "трекер честнее плана: заведённый шаг вне плана считается");
        run.StepProgress.NotCreated.Should().Be(0);
    }

    [Fact]
    public void ПланРазобранИВсеШагиПринятые_Done_ИначеCut()
    {
        var accepted = Run(Feature(), Plan("p", 0, ("Ш1", "back")), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"));
        accepted.StepProgress.PlanState.Should().Be("done");

        var notAll = Run(Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back")), Step("s", "Ш1", 1),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"));
        notAll.StepProgress.PlanState.Should().Be("cut", "Ш2 не заведён — не всё принято");

        var noPlan = Run(Feature(), Step("s", "Ш1", 1), Verdict("r", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s"));
        noPlan.StepProgress.PlanState.Should().Be("pending", "плана нет — done не выводим");
    }

    // Маппинг по шву Александра (63a69b77 п.2а): состояние плана — по ДОЛЕ планов со списками,
    // снятые (withdrawn) планы не учитываются. Строки контракта прежние, меняется только маппинг.
    [Fact]
    public void ПланБезСписка_Pending_ПланаНет_Pending()
    {
        // План есть, но не разобран ни на один шаг — не «частично», а «не разобран».
        Run(Feature(), Plan("p", 0), Step("s", "Ш1", 1)).StepProgress.PlanState.Should().Be("pending");
        Run(Feature(), Step("s", "Ш1", 1)).StepProgress.PlanState.Should().Be("pending");
    }

    [Fact]
    public void ДваПлана_СписокТолькоУОдного_Partial()
    {
        var run = Run(Feature(), Plan("p1", 0, ("Ш1", "back")), Plan("p2", 1));

        run.StepProgress.PlanState.Should().Be("partial", "списки есть только у части планов");
    }

    [Fact]
    public void СнятыйПланБезСписка_НеМешает_Cut()
    {
        var oldPlan = Plan("p-old", 0);
        oldPlan.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn };
        var run = Run(Feature(), oldPlan, Plan("p-new", 1, ("Ш1", "back")));

        run.StepProgress.PlanState.Should().Be("cut", "снятый старый план не держит фичу в partial");
    }

    // «total == 0 ⇒ не done» (замечание 2 ревью Глеба Б-pr-st): список не пуст, но валидных
    // кодов нет — знаменатель нулевой, done не выводится даже при cut.
    [Fact]
    public void ПланСНевалиднымиШагами_CutНоTotalНоль_НеDone()
    {
        var run = Run(Feature(), Plan("p", 0, (" ", "back")));

        run.StepProgress.PlanState.Should().Be("cut");
        run.StepProgress.Total.Should().Be(0);
        run.StepProgress.PlanState.Should().NotBe("done");
    }

    // B7 (ревью R-B5, находка 5): дефолт PlanState — константа, не литерал. Значения задают
    // константы FeatureStepAccounting.Plan* (подняты в internal в доработке B2b); дефолт литералом
    // разъехался бы с этим определением молча — тот же риск, который закрыл сторож по исходнику
    // в FeatureBrief.EstimateOf. Мутация: вернуть в FeatureRun.cs `= "pending"` — тест красный.
    [Fact]
    public void PlanState_ДефолтКонстантой_ВИсходникеБезЛитерала()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null && path is null)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer.Tasks",
                "Projector", "FeatureRun.cs");
            if (File.Exists(candidate)) path = candidate;
            dir = dir.Parent;
        }
        path.Should().NotBeNull("FeatureRun.cs не найден от дерева сборки — сторож вне дерева не работает");

        var decls = File.ReadAllLines(path!)
            .Select(l => l.Trim())
            .Where(l => l.Contains("PlanState") && l.Contains("get; init;"))
            .ToList();
        decls.Should().HaveCount(1, "дефолт PlanState объявлен в одном месте");
        decls[0].Should().Contain("FeatureStepAccounting.PlanPending",
            "дефолт — константа, которая задаёт значения PlanState");
        decls[0].Should().NotContain("\"pending\"", "в объявлении PlanState нет литерала состояния плана");

        // Поведение: дефолт равен константе (internal виден этой сборке через InternalsVisibleTo).
        new FeatureStepProgress().PlanState.Should().Be(FeatureStepAccounting.PlanPending);
    }

    // R-B11B12 минор 3: видимость констант Plan* — контракт (FeatureStepAccounting.cs:46-56):
    // pending/partial — internal (по ним сверяет FeatureBrief.EstimateOf). С FS-O2b к internal
    // поднят PlanCut: его сравнивает правило базового срока в TaskManager (InitialEtaMinutes
    // пишется только при нарезанном плане) — это не «для удобства», а новый потребитель значения,
    // и он сравнивает ту же константу, а не второй литерал "cut". С FS-PB1 тем же основанием
    // поднят PlanDone: его сравнивает правило базы плана в TaskManager (BaselineSteps пишется при
    // cut ИЛИ done — поправка постановщика 11.11: окно cut закрывается само при accepted == total).
    // Мутации: вернуть PlanCut или PlanDone в private — красный на его строке (для PlanDone
    // проверено прогоном FS-PB1: «Expected IsPrivate to be True … but found False»).
    [Fact]
    public void ПланКонстанты_ВидимостьПоКонтракту_PlanCutInternal_PlanDoneInternal()
    {
        var t = typeof(FeatureStepAccounting);
        foreach (var name in new[]
        {
            nameof(FeatureStepAccounting.PlanPending),
            nameof(FeatureStepAccounting.PlanPartial),
            "PlanCut",
            "PlanDone",
        })
        {
            var field = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            field.Should().NotBeNull($"константа {name} должна существовать");
            field!.IsAssembly.Should().BeTrue(
                $"{name} — internal: её сравнивает потребитель вне класса (FeatureBrief.EstimateOf, "
                + "правило базового срока и правило базы плана в TaskManager), а не второй литерал");
        }
    }

    [Fact]
    public void СтарыеШагиБезСтепРефа_ОтдельныеШаги()
    {
        var run = Run(Feature(), Step("a", null, 1), Step("b", null, 2));

        run.StepProgress.Total.Should().Be(2, "ключ шага без stepRef — id задачи, а не общая цепочка «»");
        run.StepChains.Select(c => c.StepRef).Should().Equal("a", "b");
    }

    [Fact]
    public void СнятыйШаг_ВнеЗнаменателя_СчитаетсяОтдельно()
    {
        var run = Run(Feature(), Step("s1", "Ш1", 1), Step("s2", "Ш2", 2),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Withdrawn, 6, "s2"));

        run.StepProgress.Total.Should().Be(1);
        run.StepProgress.Withdrawn.Should().Be(1);
        run.Lanes.Single().Total.Should().Be(1);
    }

    [Fact]
    public void Корзины_ВзаимоисключающиеИСуммаРавнаЗнаменателю()
    {
        var run = Run(Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back"), ("Ш4", "back"), ("Ш5", "back")),
            Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s1"),
            Step("s2", "Ш2", 2),
            Step("s3", "Ш3", 3, TaskItemStatus.InProgress),
            Step("s4", "Ш4", 4), Verdict("r4", TaskRoles.Review, TaskVerdicts.Returned, 6, "s4"));
        var p = run.StepProgress;

        (p.Accepted, p.Review, p.Work, p.NotCreated).Should().Be((1, 1, 2, 1));
        (p.Accepted + p.Review + p.Work + p.NotCreated).Should().Be(p.Total);
        p.Total.Should().Be(5);
    }

    // ─── Закрытый хвост (§8.3, Х0): ключи отдаёт учёт, риск мержа делит «живое» и «хвост» ──────

    // Тот же проход, что и корзины: ключ — stepRef (а без него id задачи), порядок — тот же, что
    // у цепочек. Вызывается напрямую, чтобы тест видел ключи, а не только счётчик за ними.
    private static FeatureStepAccounting.Result Account(params TaskItem[] tasks) =>
        FeatureStepAccounting.Build(
            tasks.Where(t => t.Role is TaskRoles.Step or TaskRoles.Rework).ToList(),
            tasks.Where(t => t.Role is TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final).ToList(),
            tasks.Where(t => t.Role == TaskRoles.Plan).ToList(),
            T0.AddDays(1), Array.Empty<FeatureIndexSession>());

    // Фикстура всех корзин: хвост — ровно «ждёт ревью» (сдан, витков нет) и «закрыт без повторного
    // ревью» (вернули, всё закрыто). Идущий исполнитель, идущий виток, принятый шаг и снятый — вне.
    [Fact]
    public void ЗакрытыйХвост_КлючиРовноЦепочкиPendingИReturned_ЖивоеПринятыеИСнятыеВне()
    {
        var tasks = new[]
        {
            Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back"), ("Ш4", "back"),
                ("Ш5", "back"), ("Ш6", "back"), ("Ш7", "back"), ("Ш8", "back")),
            Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s1"),          // принят
            Step("s2", "Ш2", 2),                                                                          // ждёт ревью → хвост
            Step("s3", "Ш3", 3), Verdict("r3", TaskRoles.Review, TaskVerdicts.Returned, 6, "s3"),         // вернули, всё закрыто → хвост
            Step("s4", "Ш4", 4, TaskItemStatus.InProgress),                                             // идёт
            Step("s5", "Ш5", 5), Verdict("r5", TaskRoles.Review, null, 7, "s5"),                         // виток идёт
            Step("s6", "Ш6", 6), Verdict("r6", TaskRoles.Review, TaskVerdicts.Withdrawn, 8, "s6"),       // снят
            Step("s7", "Ш7", 7), Verdict("r7", TaskRoles.Review, TaskVerdicts.Accepted, 9, "s7"),
            Rework("w7", "Ш7", 10, status: TaskItemStatus.InProgress),                                   // принят, но доработка открыта
        };

        var acct = Account(tasks);
        var tailFromChains = acct.Chains.Where(c => c.Outcome is "pending" or "returned").Select(c => c.StepRef).ToList();

        tailFromChains.Should().Equal(new[] { "Ш2", "Ш3" }, "фикстура правда: в хвосте только эти две цепочки");
        acct.ClosedTailKeys.Should().Equal(tailFromChains, "ключи хвоста — тот же проход и тот же предикат, что корзины");
        acct.Progress.ClosedTail.Should().Be(2);
        acct.Chains.Select(c => c.Outcome).Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(new[] { "accepted", "pending", "returned", "running", "withdrawn" },
                "фикстура даёт все исходы, иначе равенство выше пустое");

        var run = Run(tasks);
        run.StepProgress.ClosedTail.Should().Be(acct.ClosedTailKeys.Count, "проектор берёт ключи учёта, а не пересчитывает их");
    }

    // Открытая доработка, принятый шаг с доработкой, снятый шаг и идущая задача — не хвост:
    // кнопка мержа их не трогает (§8.3, «что кнопка не трогает»).
    [Fact]
    public void ЗакрытыйХвост_НеТрогаетЖивуюРаботуПринятыеИСнятыеШаги()
    {
        var tasks = new[]
        {
            Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back"), ("Ш4", "back")),
            Step("s1", "Ш1", 1), Rework("w1", "Ш1", 3, status: TaskItemStatus.InProgress),
            Step("s2", "Ш2", 2), Verdict("r2", TaskRoles.Review, TaskVerdicts.Accepted, 6, "s2"),
            Rework("w2", "Ш2", 7, status: TaskItemStatus.InProgress),
            Step("s3", "Ш3", 4), Verdict("r3", TaskRoles.Review, TaskVerdicts.Withdrawn, 8, "s3"),
            Step("s4", "Ш4", 5, TaskItemStatus.InProgress),
        };

        Account(tasks).ClosedTailKeys.Should().BeEmpty();

        var p = Run(tasks).StepProgress;
        p.ClosedTail.Should().Be(0);
        p.Withdrawn.Should().Be(1, "Ш3 снят: он вне знаменателя и не хвост");
    }

    // Идущий виток — живая работа (§8.3): «на ревью, которое идёт», в отличие от «ждёт ревью»
    // (витков нет), в хвост не входит — его никто не снимает без человека.
    [Fact]
    public void ЗакрытыйХвост_ИдущийВитокНеВХвосте()
    {
        var tasks = new[]
        {
            Feature(), Plan("p", 0, ("Ш1", "back")),
            Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, null, 5, "s1"),
        };

        Account(tasks).ClosedTailKeys.Should().BeEmpty();
        Run(tasks).StepProgress.ClosedTail.Should().Be(0);
    }

    // Ключ и порядок — как в учёте: у шага без stepRef ключ — id задачи, порядок — по появлению.
    [Fact]
    public void ЗакрытыйХвост_КлючБезСтепРефаIdЗадачи_ПорядокКакУЦепочек()
    {
        var tasks = new[] { Feature(), Step("b", null, 2), Step("a", null, 1) };

        var acct = Account(tasks);

        acct.ClosedTailKeys.Should().Equal(new[] { "b", "a" }, "тот же порядок появления, что у Chains");
        acct.Chains.Select(c => c.StepRef).Should().Equal(acct.ClosedTailKeys);
    }

    // Снятие «при мерже» (Origin = merge_tail) — часть Withdrawn с тем же источником, что решил
    // снятие: задача-шаг или последний виток-снятие. Без Origin — снятие по существу; снятая
    // доработка шага не касается и в хвост остаётся.
    [Fact]
    public void WithdrawnAtMerge_СчитаетТолькоВердикСOriginMergeTail()
    {
        var s1 = Step("s1", "Ш1", 1);
        s1.Verification = new TaskVerification { Verdict = TaskVerdicts.Withdrawn, Origin = TaskVerificationOrigins.MergeTail };
        var r2 = Verdict("r2", TaskRoles.Review, TaskVerdicts.Withdrawn, 6, "s2");
        r2.Verification!.Origin = TaskVerificationOrigins.MergeTail;
        var s3 = Step("s3", "Ш3", 2);
        var r3 = Verdict("r3", TaskRoles.Review, TaskVerdicts.Withdrawn, 7, "s3"); // без Origin — по существу
        var w4 = Rework("w4", "Ш4", 3, status: TaskItemStatus.Done, verdict: TaskVerdicts.Withdrawn);
        w4.Verification!.Origin = TaskVerificationOrigins.MergeTail;
        var tasks = new[]
        {
            Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back"), ("Ш4", "back")),
            s1, Step("s2", "Ш2", 4), r2, s3, r3, Step("s4", "Ш4", 5), w4,
        };

        var p = Run(tasks).StepProgress;

        p.Withdrawn.Should().Be(3);
        p.WithdrawnAtMerge.Should().Be(2, "только вердикт с Origin = merge_tail: задача-шаг и последний виток");
        p.ClosedTail.Should().Be(1, "снятая доработка шага не касается: Ш4 остаётся закрытым непринятым");
    }

    // Диалог мержа: «живое» и «хвост» — два отдельных риска; их сумма — то же число, что был
    // один «open» (сторож разбиения, §8.3).
    [Fact]
    public void РискиМержа_ЖивоеИХвостОтдельно_СуммаРавнаПрежнемуOpen()
    {
        var tasks = new[]
        {
            Feature(), Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back"), ("Ш4", "back"), ("Ш5", "back")),
            Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s1"),
            Step("s2", "Ш2", 2),
            Step("s3", "Ш3", 3), Verdict("r3", TaskRoles.Review, TaskVerdicts.Returned, 6, "s3"),
            Step("s4", "Ш4", 4, TaskItemStatus.InProgress),
            Step("s5", "Ш5", 5), Verdict("r5", TaskRoles.Review, null, 7, "s5"),
            Verdict("f", TaskRoles.Final, TaskVerdicts.Accepted, 30, "s1"),
        };

        var run = Run(tasks);

        // Живое — Ш4 (идёт) и Ш5 (виток идёт); хвост — Ш2 (ждёт ревью) и Ш3 (вернули без доработки).
        // «tracker» — тот же закрытый хвост глазами сверки (§8.1: unreviewed / returned_no_rework):
        // отдельная ось, её число этим шагом не меняется.
        var risks = run.Attention!.MergeRisks;
        risks.Should().Equal(new[]
        {
            new FeatureMergeRisk("open", 2),
            new FeatureMergeRisk("tail", 2),
            new FeatureMergeRisk("tracker", 2),
        }, "порядок фиксирован: живое → хвост → сверка трекера");
        (risks.Single(r => r.Kind == "open").Count + risks.Single(r => r.Kind == "tail").Count)
            .Should().Be(run.StepProgress.Work + run.StepProgress.Review, "open + tail = прежний open");
        run.StepProgress.ClosedTail.Should().Be(2);
    }

    // ─── Контракт: SlimRun, индекс, StepsOpen ────────────────────────────────────────────────

    // Рефлексией по ВСЕМ свойствам FeatureRun: следующее добавленное поле, не скопированное в
    // SlimRun, краснит тест само (а не только то, что мы вспомнили перечислить).
    [Fact]
    public void SlimRun_ПереноситВсеВычисленныеПоля()
    {
        var облегчаемые = new HashSet<string>
        {
            nameof(FeatureRun.Epic), nameof(FeatureRun.Steps), nameof(FeatureRun.Reviews), nameof(FeatureRun.Qa),
            nameof(FeatureRun.Final), nameof(FeatureRun.Merge), nameof(FeatureRun.Cut), nameof(FeatureRun.Plan),
            nameof(FeatureRun.Design), nameof(FeatureRun.Unroled), // W4-DES-B: тоже облегчаются (SlimAll)
        };
        var src = new FeatureRun { Epic = new TaskItem { Id = "e" } };
        foreach (var prop in typeof(FeatureRun).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (облегчаемые.Contains(prop.Name)) continue;
            prop.SetValue(src, Sentinel(prop.PropertyType, prop.Name));
        }

        var slim = FeatureProjector.SlimRun(src);

        foreach (var prop in typeof(FeatureRun).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (облегчаемые.Contains(prop.Name)) continue;
            var expected = prop.GetValue(src);
            var actual = prop.GetValue(slim);
            if (prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
                actual.Should().Be(expected, $"SlimRun обязан перенести {prop.Name}");
            else
                actual.Should().BeSameAs(expected, $"SlimRun обязан перенести {prop.Name}");
        }
    }

    private static object Sentinel(Type type, string name)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "sentinel-" + name;
        if (t == typeof(int)) return 7;
        if (t == typeof(double)) return 0.5;
        if (t == typeof(bool)) return true;
        if (t == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        // FeatureHold (W4-HOLD-B1) — позиционная запись без конструктора без параметров:
        // образец собирается явно, дальше тест сверяет, что SlimRun перенёс его по ссылке.
        if (t == typeof(FeatureHold)) return new FeatureHold("sentinel-" + name, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "chat-s", true);
        if (t == typeof(FeatureQueueInfo)) return new FeatureQueueInfo(2, 3);
        return Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"Не удалось собрать образец для {name}: {type}");
    }

    [Fact]
    public void ИндексРядок_НесётКорзиныИСостояниеПлана()
    {
        var epic = Feature();
        var tasks = new[] { epic, Plan("p", 0, ("Ш1", "back"), ("Ш2", "back"), ("Ш3", "back")),
            Step("s1", "Ш1", 1), Step("s2", "Ш2", 2, TaskItemStatus.InProgress) };
        var run = FeatureProjector.Build(epic, tasks, T0.AddDays(1));

        var row = FeatureProjector.BuildRow(run, tasks, Array.Empty<FeatureIndexSession>(), 0);

        (row.StepsReview, row.StepsWork, row.StepsNotCreated, row.PlanState).Should().Be((1, 1, 1, "cut"));
        (row.StepsAccepted, row.StepsTotal).Should().Be((0, 3));
    }

    // Индекс отдаёт то же, что страница фичи: снятые шаги и доработки без привязки (вне знаменателя).
    [Fact]
    public void ИндексРядок_НесётСнятыеИДоработкиБезПривязки_РавноСоСтраницей()
    {
        var epic = Feature();
        var tasks = new[] { epic, Step("s1", "Ш1", 1), Step("s2", "Ш2", 2),
            Verdict("r", TaskRoles.Review, TaskVerdicts.Withdrawn, 6, "s2"),
            Rework("w1", "Нет-такого", 8), Rework("w2", null, 9) };
        var run = FeatureProjector.Build(epic, tasks, T0.AddDays(1));

        var row = FeatureProjector.BuildRow(run, tasks, Array.Empty<FeatureIndexSession>(), 0);

        run.StepProgress.Withdrawn.Should().BeGreaterThan(0);
        run.StepProgress.UnlinkedReworks.Should().BeGreaterThan(0);
        row.StepsWithdrawn.Should().Be(run.StepProgress.Withdrawn);
        row.UnlinkedReworks.Should().Be(run.StepProgress.UnlinkedReworks);
    }

    // Имена полей прогресса — контракт Ф-pr (Б-pr2-д, решение 4): переименование поля через
    // [JsonPropertyName] или смена casing краснит тест — фронт читает ровно эти имена.
    [Fact]
    public void КонтрактПрогресса_ИменаПолейCamelCase()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var epicM = Feature();
        var tasks = new List<TaskItem>
        {
            epicM, Plan("p", 0, ("Ш1", "back")), Step("s1", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s1"),
            Verdict("f", TaskRoles.Final, TaskVerdicts.Accepted, 20, "s1"),
            new() { Id = "m", FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
                CreatedAt = T0.AddMinutes(30), CompletedAt = T0.AddMinutes(31) },
        };
        var run = FeatureProjector.Build(epicM, tasks, T0.AddDays(1));
        var json = JsonSerializer.Serialize(run, opts);
        json.Should().Contain("\"stepProgress\"");
        json.Should().Contain("\"planState\"");
        json.Should().Contain("\"stepsOpen\"");
        // Х0 (§8.3): имена новых счётчиков — часть контракта, фронт (Ф1) читает ровно их.
        json.Should().Contain("\"closedTail\"");
        json.Should().Contain("\"withdrawnAtMerge\"");

        var row = FeatureProjector.BuildRow(run, tasks, Array.Empty<FeatureIndexSession>(), 0);
        var rowJson = JsonSerializer.Serialize(row, opts);
        rowJson.Should().Contain("\"stepsReview\"");
        rowJson.Should().Contain("\"stepsWork\"");
        rowJson.Should().Contain("\"stepsNotCreated\"");
        rowJson.Should().Contain("\"planState\"");
    }

    [Fact]
    public void StepsOpen_УMergedИClosed_ПринятоМеньшеТотала()
    {
        // merged: финал принят + merge Done; принят 1 из 2 шагов.
        var epicM = Feature();
        var mergedTasks = new List<TaskItem>
        {
            epicM, Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 5, "s1"), Step("s2", "Ш2", 2),
            Verdict("f", TaskRoles.Final, TaskVerdicts.Accepted, 20, "s1"),
            new() { Id = "m", FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done, CreatedAt = T0.AddMinutes(30), CompletedAt = T0.AddMinutes(31) },
        };
        var merged = FeatureProjector.Build(epicM, mergedTasks, T0.AddDays(1));
        merged.State.Should().Be(FeatureStates.Merged);
        merged.Summary!.StepsOpen.Should().Be(1);
        merged.Summary.Steps.Should().Be(2, "Summary.Steps = знаменатель");
        FeatureProjector.BuildRow(merged, mergedTasks, Array.Empty<FeatureIndexSession>(), 0).Summary!.StepsOpen.Should().Be(1);

        // closed: эпик Done, merge нет; два шага, принято 0.
        var epicC = Feature();
        epicC.Status = TaskItemStatus.Done;
        epicC.CompletedAt = T0.AddMinutes(40);
        var closed = FeatureProjector.Build(epicC, [epicC, Step("s1", "Ш1", 1), Step("s2", "Ш2", 2)], T0.AddDays(1));
        closed.State.Should().Be(FeatureStates.Closed);
        closed.Summary!.StepsOpen.Should().Be(2);

        // открытая фича: Summary нет.
        Run(Feature(), Step("s", "Ш1", 1)).Summary.Should().BeNull();
    }

    // ─── Ш7: инвариант «корзины вкладки Шагов = stepProgress» ──────────────────────────────────

    // Корзина цепочки — то же правило, что у фронта (FeatureProdCards.bucketOf): снят → принят →
    // pending = ревью; running при всех закрытых задачах = ревью; иначе работа.
    private static string BucketOf(FeatureStepChain c, IReadOnlyList<TaskItem> all)
    {
        if (c.Outcome == "withdrawn" || c.Outcome == "accepted") return c.Outcome;
        if (c.Outcome == "pending") return "review";
        var own = all.Where(t => c.TaskIds.Contains(t.Id)).ToList();
        bool allDone = own.Count > 0 && own.All(t => t.Status == TaskItemStatus.Done);
        return c.Outcome == "running" && allDone ? "review" : "work";
    }

    private static void AssertBucketsMatchProgress(FeatureRun run, IReadOnlyList<TaskItem> all)
    {
        var p = run.StepProgress;
        var chains = run.StepChains;

        chains.Select(c => c.StepRef).Should().OnlyHaveUniqueItems("цепочка на шаг одна, доработка корзину не плодит");
        var byBucket = chains.GroupBy(c => BucketOf(c, all)).ToDictionary(g => g.Key, g => g.Count());
        int B(string k) => byBucket.GetValueOrDefault(k);

        B("accepted").Should().Be(p.Accepted);
        B("review").Should().Be(p.Review);
        B("work").Should().Be(p.Work);
        B("withdrawn").Should().Be(p.Withdrawn);
        // Цепочки вне «снят» + шаги плана без задачи (у них цепочки нет) = знаменатель.
        (chains.Count - B("withdrawn") + p.NotCreated).Should().Be(p.Total);
        p.Total.Should().Be(p.Accepted + p.Review + p.Work + p.NotCreated);
        // «В master» — ортогональный признак: в сумму не входит и не больше числа цепочек.
        chains.Count(c => c.InMaster).Should().Be(p.InMaster);
        p.InMaster.Should().BeLessThanOrEqualTo(chains.Count);
    }

    [Fact]
    public void КорзиныЦепочек_Фикстура_СуммаРавнаStepProgress()
    {
        var tasks = FeatureRunFixture.Load("feat-step-chains.json");
        var epic = tasks.Single(t => t.Role == TaskRoles.Feature);
        var run = FeatureProjector.Build(epic, tasks);

        run.StepChains.Should().NotBeEmpty();
        run.StepProgress.Withdrawn.Should().BeGreaterThan(0, "в фикстуре есть снятый шаг");
        AssertBucketsMatchProgress(run, tasks);
    }

    // Все четыре корзины + notCreated + доработка + «в master» одновременно.
    [Fact]
    public void КорзиныЦепочек_ВсеКорзиныИДоработка_СуммаРавнаTotal_InMasterВнеСуммы()
    {
        var tasks = new List<TaskItem>
        {
            Feature(),
            Plan("p", 0, ("A", "back"), ("B", "back"), ("C", "back"), ("D", "back"), ("E", "back"), ("N", "front")),
            Step("sA", "A", 1), Verdict("rA", TaskRoles.Review, TaskVerdicts.Accepted, 3, "sA"),
            Step("sB", "B", 1), Step("sB2", "B", 2, TaskItemStatus.InProgress),
            Step("sC", "C", 1),
            Step("sD", "D", 1), Rework("wD", "D", 10, ["rD"], verdict: TaskVerdicts.Withdrawn),
            Verdict("rD", TaskRoles.Review, TaskVerdicts.Returned, 3, "sD"),
            Step("sE", "E", 1), Verdict("rE", TaskRoles.Review, TaskVerdicts.Returned, 3, "sE"),
            Rework("wE", "E", 5, ["rE"], TaskItemStatus.InProgress),
            new() { Id = "m", FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done, CreatedAt = T0.AddMinutes(20), CompletedAt = T0.AddMinutes(21) },
        };
        var run = FeatureProjector.Build(tasks[0], tasks, T0.AddDays(1));

        run.StepProgress.NotCreated.Should().Be(1);
        run.StepProgress.InMaster.Should().BeGreaterThan(0);
        run.StepChains.Select(c => c.StepRef).Should().OnlyHaveUniqueItems();
        AssertBucketsMatchProgress(run, tasks);
    }

    // План с пересечением (B3): вытесненный план и его шаги не раздувают ни цепочки, ни знаменатель.
    [Fact]
    public void КорзиныЦепочек_ПланСПересечением_СуммаРавнаTotal()
    {
        var tasks = new List<TaskItem>
        {
            Feature(),
            Plan("p1", 0, ("A", "back"), ("B", "back"), ("C", "back")),
            Plan("p2", 5, ("A", "back"), ("B", "back")),
            Step("sA", "A", 6), Step("sC", "C", 6),
        };
        var run = FeatureProjector.Build(tasks[0], tasks, T0.AddDays(1));

        run.StepProgress.Total.Should().Be(3, "действующий p2 [A,B] плюс заведённый C");
        AssertBucketsMatchProgress(run, tasks);
    }
    // ─── FS-PB2: доработки после мержа вне знаменателя ──────────────────────────────────────────

    private static TaskItem MergeDone(string id, int minute) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
    };

    private static FeatureStepAccounting.Result AccountFu(int? baseline, params TaskItem[] tasks)
    {
        var steps = tasks.Where(t => t.Role is TaskRoles.Step or TaskRoles.Rework).ToList();
        var verdicts = tasks.Where(t => t.Role is TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final).ToList();
        var plan = tasks.Where(t => t.Role == TaskRoles.Plan).ToList();
        var merges = tasks.Where(t => t.Role == TaskRoles.Merge).ToList();
        return FeatureStepAccounting.Build(steps, verdicts, plan, T0.AddDays(1), null, merges, baseline);
    }

    [Fact]
    public void Доработка_СозданаПослеПервогоМержаВнеПлана_ВнеTotalИКорзин()
    {
        var acc = AccountFu(7, Plan("p", 0, ("A", "back")), Step("sA", "A", 1),
            Verdict("rA", TaskRoles.Review, TaskVerdicts.Accepted, 5, "sA"), MergeDone("m1", 30),
            Step("sF", "F", 40, TaskItemStatus.InProgress));

        acc.Progress.Total.Should().Be(1);
        acc.Progress.Accepted.Should().Be(1);
        acc.Progress.Work.Should().Be(0);
        acc.Progress.PlanState.Should().Be(FeatureStepAccounting.PlanDone);
        acc.Progress.FollowUpTotal.Should().Be(1);
        acc.Progress.FollowUpAccepted.Should().Be(0);
        acc.Progress.BaselineTotal.Should().Be(7);
        acc.Lanes.Sum(l => l.Total).Should().Be(1);
        acc.Chains.Single(c => c.StepRef == "F").FollowUp.Should().BeTrue();
        acc.Chains.Single(c => c.StepRef == "A").FollowUp.Should().BeFalse();
    }

    [Fact]
    public void Доработка_ПринятаяСчитаетсяВFollowUpAccepted_ТотальНеМеняется()
    {
        var acc = AccountFu(null, Plan("p", 0, ("A", "back")), Step("sA", "A", 1),
            Verdict("rA", TaskRoles.Review, TaskVerdicts.Accepted, 5, "sA"), MergeDone("m1", 30),
            Step("sF", "F", 40), Verdict("rF", TaskRoles.Review, TaskVerdicts.Accepted, 45, "sF"));

        acc.Progress.Total.Should().Be(1);
        acc.Progress.FollowUpTotal.Should().Be(1);
        acc.Progress.FollowUpAccepted.Should().Be(1);
        acc.Progress.BaselineTotal.Should().BeNull();
    }

    [Fact]
    public void Доработка_ПлановыйШагПослеМержа_ОстаётсяБазовым()
    {
        var acc = AccountFu(null, Plan("p", 0, ("A", "back"), ("B", "back")), Step("sA", "A", 1),
            Verdict("rA", TaskRoles.Review, TaskVerdicts.Accepted, 5, "sA"), MergeDone("m1", 30),
            Step("sB", "B", 40, TaskItemStatus.InProgress));

        acc.Progress.Total.Should().Be(2);
        acc.Progress.Work.Should().Be(1);
        acc.Progress.FollowUpTotal.Should().Be(0);
    }

    // Граница: шаг, созданный ровно в момент закрытия мержа, — не «строго позже», значит базовый.
    // Опорный момент — именно ПЕРВЫЙ мерж: от второго (m2) шаг, созданный между ними, сдвинул бы итог.
    [Fact]
    public void Доработка_ГраницаПервогоМержа_СтрогоПозжеИТолькоОтПервого()
    {
        var m1 = MergeDone("m1", 30); // закрыт на минуте 31
        var onBorder = Step("sB", "B", 31, TaskItemStatus.InProgress);
        var between = Step("sC", "C", 40, TaskItemStatus.InProgress);
        var acc = AccountFu(null, Plan("p", 0, ("A", "back")), Step("sA", "A", 1), m1, MergeDone("m2", 50), onBorder, between);

        acc.Progress.Total.Should().Be(2, "A плюс B (создан ровно на границе)");
        acc.Progress.FollowUpTotal.Should().Be(1, "C создан после первого мержа, хоть и до второго");
        acc.Chains.Single(c => c.StepRef == "B").FollowUp.Should().BeFalse();
    }

    [Fact]
    public void Доработка_БезМержа_НеВыделяется_ПустаяКоллекцияНулевыеСчётчики()
    {
        var acc = AccountFu(null, Plan("p", 0, ("A", "back")), Step("sA", "A", 1), Step("sF", "F", 40));
        acc.Progress.Total.Should().Be(2);
        acc.Progress.FollowUpTotal.Should().Be(0);
        acc.Progress.FollowUpAccepted.Should().Be(0);
        AccountFu(null, Feature()).Progress.FollowUpTotal.Should().Be(0);
    }

    [Fact]
    public void РискиМержа_ОткрытаяДоработкаУчитываетсяВOpen()
    {
        var acc = AccountFu(null, Plan("p", 0, ("A", "back")), Step("sA", "A", 1),
            Verdict("rA", TaskRoles.Review, TaskVerdicts.Accepted, 5, "sA"), MergeDone("m1", 30),
            Step("sF", "F", 40, TaskItemStatus.InProgress));

        var risks = FeatureProjector.BuildMergeRisks(acc.Progress, 0);
        risks.Should().ContainSingle(r => r.Kind == FeatureMergeRiskKinds.Open && r.Count == 1);
    }

    [Fact]
    public void КонтрактДоработок_ИменаПолейCamelCase()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var acc = AccountFu(3, Plan("p", 0, ("A", "back")), Step("sA", "A", 1), MergeDone("m1", 30), Step("sF", "F", 40));
        var json = JsonSerializer.Serialize(new { acc.Progress, acc.Chains }, opts);
        json.Should().Contain("\"baselineTotal\":3").And.Contain("\"followUpTotal\":1")
            .And.Contain("\"followUpAccepted\":0").And.Contain("\"followUp\":true");
    }
}
