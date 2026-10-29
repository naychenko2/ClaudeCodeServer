using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// «Хвосты после мержа» (GC-B4): у смерженной/закрытой фичи незакрытые дочерние задачи
// (кроме merge) — это ХВОСТЫ, а не «ждёт вас». Правило (решение Софьи, вопрос 1 плана GC-B4):
// хвост — ЛЮБАЯ незакрытая дочерняя задача фичи, кроме merge; порядок — по CreatedAt;
// хвосты не участвуют в waitTask/attention и в state/tone стадий.
//
// Мутации (краснеют):
//   • SlimRun не копирует PostMergeTails → тест «SlimRun» красный;
//   • хвосты идут в waitTask (attention не гасится при merged) → тест «Attention == null» красный;
//   • порядок хвостов не по CreatedAt → тест «порядок» красный.
// Коллекция без параллелизации: тесты этого файла идут отдельным проходом, чтобы не сдвигать
// расписание параллельных коллекций — от этого страдает тайминг-гейт журнала
// (FeatureTimelineJournalTests.Build_Замер_20kЗаписей_Быстрый): он меряет отношение времени
// сборки 20k/5k записей и на параллельной нагрузке уезжает за порог нелинейности.
[CollectionDefinition("FeaturePostMergeTailsSerial", DisableParallelization = true)]
public class FeaturePostMergeTailsSerialCollection;

[Collection("FeaturePostMergeTailsSerial")]
public class FeaturePostMergeTailsTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic(string id = "epic-t") => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
    };

    // Дочерняя задача фичи с произвольным статусом/ролью.
    private static TaskItem Child(string id, string epicId, string? role,
        TaskItemStatus status, DateTime? createdAt = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = role,
        Status = status,
        CreatedAt = createdAt ?? T0,
    };

    // Дефект фичи: kind=defect, Assignee=me, ждёт человека (AwaitingHumanSince задан).
    private static TaskItem TailDefect(string id, string epicId, DateTime createdAt) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = null,
        Kind = TaskKind.Defect,
        Assignee = TaskItemAssignee.Me,
        Status = TaskItemStatus.Todo,
        AwaitingHumanSince = createdAt.AddMinutes(30),
        AwaitingHumanReason = "нужен ответ по границе",
        CreatedAt = createdAt,
    };

    // Смерженная фича: финал принят, merge-задача закрыта + два открытых дефекта-хвоста.
    // Возвращает эпик и полный набор задач фичи.
    private static (TaskItem Epic, List<TaskItem> Tasks) MergedFeatureWithTwoTails()
    {
        var epic = Epic();
        var step = Child("s1", epic.Id, TaskRoles.Step, TaskItemStatus.Done, T0);
        step.CompletedAt = T0.AddHours(2);
        var review = Child("r1", epic.Id, TaskRoles.Review, TaskItemStatus.Done, T0);
        review.CompletedAt = T0.AddHours(3);
        var final = Child("f1", epic.Id, TaskRoles.Final, TaskItemStatus.Done, T0);
        final.CompletedAt = T0.AddHours(4);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var merge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Done, T0);
        merge.CompletedAt = T0.AddHours(5);
        var d1 = TailDefect("d1", epic.Id, T0.AddHours(6));
        var d2 = TailDefect("d2", epic.Id, T0.AddHours(7));
        return (epic, [epic, step, review, final, merge, d1, d2]);
    }

    // Критерий 1 (GC-B4): смерженная фича с двумя открытыми дефектами → хвосты = оба id,
    // тревоги нет, тон этапа не «wait».
    [Fact]
    public void Build_СмерженнаяФича_ОткрытыеДефекты_ХвостыИБезТревоги()
    {
        var (epic, tasks) = MergedFeatureWithTwoTails();

        var run = FeatureProjector.Build(epic, tasks);

        run.State.Should().Be(FeatureStates.Merged, "merge-задача закрыта, финал принят — фича смержена");
        run.PostMergeTails.Should().Equal(new[] { "d1", "d2" }, "хвосты — оба открытых дефекта, по CreatedAt");
        run.Attention.Should().BeNull("хвосты не держат «ждёт вас»");
        run.Stages.Should().NotContain(s => s.Tone == "wait", "ни один этап не тон «wait»");
        run.Stages.Single(s => s.Key == "prod").Tone.Should().NotBe("wait");
    }

    // Критерий 1 (GC-B4), сильная половина: открытый ШАГ после мержа — активная стадия prod,
    // и её тон не «wait» (раньше тревога wait красила активный этап).
    [Fact]
    public void Build_СмерженнаяФича_ОткрытыйШаг_ТонПродаНеWait()
    {
        var epic = Epic();
        var openStep = Child("s1", epic.Id, TaskRoles.Step, TaskItemStatus.Todo, T0);
        var final = Child("f1", epic.Id, TaskRoles.Final, TaskItemStatus.Done, T0);
        final.CompletedAt = T0.AddHours(4);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var merge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Done, T0);
        merge.CompletedAt = T0.AddHours(5);

        var run = FeatureProjector.Build(epic, [epic, openStep, final, merge]);

        run.State.Should().Be(FeatureStates.Merged);
        run.PostMergeTails.Should().Equal(new[] { "s1" }, "открытый шаг — тоже хвост");
        run.Attention.Should().BeNull();
        var prod = run.Stages.Single(s => s.Key == "prod");
        prod.State.Should().Be("active", "открытый шаг — активная стадия");
        prod.Tone.Should().NotBe("wait", "хвост не красит активный этап тоном «ждёт вас»");
    }

    // Критерий 2 (GC-B4): та же фича ДО мержа — хвостов нет, тревога «wait» на месте.
    [Fact]
    public void Build_ДоМержа_ХвостовНет_ТревогаWait()
    {
        var epic = Epic();
        var step = Child("s1", epic.Id, TaskRoles.Step, TaskItemStatus.Done, T0);
        step.CompletedAt = T0.AddHours(2);
        var final = Child("f1", epic.Id, TaskRoles.Final, TaskItemStatus.Done, T0);
        final.CompletedAt = T0.AddHours(4);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        // Мерж ещё не закрыт — фича не смержена.
        var merge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Todo, T0);
        var d1 = TailDefect("d1", epic.Id, T0.AddHours(6));
        var d2 = TailDefect("d2", epic.Id, T0.AddHours(7));

        var run = FeatureProjector.Build(epic, [epic, step, final, merge, d1, d2]);

        run.State.Should().NotBe(FeatureStates.Merged);
        run.PostMergeTails.Should().BeEmpty("до мержа хвостов нет");
        run.Attention.Should().NotBeNull();
        run.Attention!.Kind.Should().Be("wait", "дефект ждёт человека — тревога wait");
    }

    // Остановленный хвост после мержа — тоже не тревога: хвост исключён из stoppedTask.
    [Fact]
    public void Build_ОстановленныйХвост_ТревогиНет()
    {
        var epic = Epic();
        var final = Child("f1", epic.Id, TaskRoles.Final, TaskItemStatus.Done, T0);
        final.CompletedAt = T0.AddHours(4);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var merge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Done, T0);
        merge.CompletedAt = T0.AddHours(5);
        // Роль не step: шаг, заведённый после мержа, открывает волну и снимает merged (P1).
        var stoppedTail = Child("s1", epic.Id, null, TaskItemStatus.InProgress, T0.AddHours(6));
        stoppedTail.ClaudeStartedAt = T0.AddHours(6);
        stoppedTail.ExecutorStoppedAt = T0.AddHours(7);
        stoppedTail.ExecutorStopReason = "auth_failed";

        var run = FeatureProjector.Build(epic, [epic, final, merge, stoppedTail]);

        run.State.Should().Be(FeatureStates.Merged);
        run.PostMergeTails.Should().Equal(new[] { "s1" });
        run.Attention.Should().BeNull("остановленный хвост — не тревога «ждёт вас»");
    }

    // Порядок хвостов — по CreatedAt, не по порядку в наборе.
    [Fact]
    public void Build_Хвосты_ПорядокПоCreatedAt()
    {
        var epic = Epic();
        var final = Child("f1", epic.Id, TaskRoles.Final, TaskItemStatus.Done, T0);
        final.CompletedAt = T0.AddHours(4);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var merge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Done, T0);
        merge.CompletedAt = T0.AddHours(5);
        // id противоположны порядку по времени: «z-tail» создан раньше, «a-tail» позже —
        // иначе сортировка по id дала бы тот же порядок и тест не ловил бы подмену.
        var early = TailDefect("z-tail", epic.Id, T0.AddHours(6));
        var late = TailDefect("a-tail", epic.Id, T0.AddHours(9));

        // В наборе «поздний» идёт первым — порядок хвостов всё равно по CreatedAt.
        var run = FeatureProjector.Build(epic, [epic, final, merge, late, early]);

        run.PostMergeTails.Should().Equal(new[] { "z-tail", "a-tail" }, "порядок хвостов — по CreatedAt");
    }

    // Закрытая фича (эпик Done, мержа нет) — хвосты те же; merge-задача, если она есть и не
    // закрыта, хвостом НЕ является (правило «кроме merge»).
    [Fact]
    public void Build_ЗакрытаяФича_ХвостыБезMergeЗадачи()
    {
        var epic = Epic();
        epic.Status = TaskItemStatus.Done;
        var openStep = Child("s1", epic.Id, TaskRoles.Step, TaskItemStatus.Todo, T0);
        var openMerge = Child("m1", epic.Id, TaskRoles.Merge, TaskItemStatus.Todo, T0);

        var run = FeatureProjector.Build(epic, [epic, openStep, openMerge]);

        run.State.Should().Be(FeatureStates.Closed);
        run.PostMergeTails.Should().Equal(new[] { "s1" }, "merge-задача хвостом не считается");
    }

    // Критерий 3 (GC-B4): SlimRun копирует хвосты — облегчённый run (то, что уходит в ответ)
    // их не теряет. Мутация «SlimRun не копирует PostMergeTails» → тест красный.
    [Fact]
    public void Build_SlimRun_ХвостыНаМесте()
    {
        var (epic, tasks) = MergedFeatureWithTwoTails();

        var full = FeatureProjector.BuildCore(epic, tasks);
        var slim = FeatureProjector.Build(epic, tasks); // = SlimRun(BuildCore)

        full.PostMergeTails.Should().Equal("d1", "d2");
        slim.PostMergeTails.Should().Equal(new[] { "d1", "d2" }, "SlimRun копирует хвосты в ответ");
    }
}
