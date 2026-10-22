using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

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
        run.StepChains.Single().Outcome.Should().Be("withdrawn");
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

    [Fact]
    public void ДополнениеПлана_ДобавляетШагиВЗнаменатель()
    {
        var run = Run(Feature(),
            Plan("p1", 0, ("Ш1", "back"), ("Ш2", "back")),
            Plan("p2", 2, ("Ш2", "back"), ("Ш3", "back")),
            Step("s", "Ш1", 4));

        run.StepProgress.Total.Should().Be(3, "∪ шагов обоих планов: Ш1, Ш2, Ш3");
        run.StepProgress.NotCreated.Should().Be(2);
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

    [Fact]
    public void ПланБезСписка_Partial_ПланаНет_Pending()
    {
        Run(Feature(), Plan("p", 0), Step("s", "Ш1", 1)).StepProgress.PlanState.Should().Be("partial");
        Run(Feature(), Step("s", "Ш1", 1)).StepProgress.PlanState.Should().Be("pending");
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
}
