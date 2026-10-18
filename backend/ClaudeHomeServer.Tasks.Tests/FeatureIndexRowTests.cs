using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tasks.Tests.Fixtures;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты строки фичи в индексе (контракт «Контракт индекса фич — утверждён», шаг Б2):
// FeatureProjector.BuildRow — чистая функция FeatureRun → FeatureIndexRow. Значения
// bucket/rail/chips/attention/now0/summary/lastActivityAt/loops/steps* берутся из FeatureRun
// (тон выводится из Attention, MAJOR-1) — проектор не пересчитывает; мутация Build/BuildRow
// красит кейсы. Ключевой чат — отдельная цепочка (4 звена) + мутация.
public class FeatureIndexRowTests
{
    // ── Хелперы сборки ──────────────────────────────────────────────────────────────────────

    // Строка из эпика + дочерних: Build → BuildRow (sessions/chatCount — опционально).
    private static FeatureIndexRow Row(
        TaskItem epic, List<TaskItem> tasks,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null, int chatCount = 0)
    {
        sessions ??= Array.Empty<FeatureIndexSession>();
        var run = FeatureProjector.Build(epic, tasks);
        return FeatureProjector.BuildRow(run, tasks, sessions, chatCount);
    }

    private static TaskItem Epic(string id, string? sourceSessionId = null,
        string? executorLaunchedBy = null, string? projectId = null) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        SourceSessionId = sourceSessionId,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
        ProjectId = projectId,
    };

    private static TaskItem Child(string id, string featureId, string role = TaskRoles.Step,
        string? stepRef = null, string? lane = null,
        string? executorLaunchedBy = null, string? sourceSessionId = null,
        TaskItemStatus status = TaskItemStatus.Todo) => new()
    {
        Id = id,
        FeatureId = featureId,
        Role = role,
        StepRef = stepRef,
        Lane = lane,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
        SourceSessionId = sourceSessionId,
        Status = status,
    };

    // ── 1. bucket: каждое из четырёх значений ────────────────────────────────────────────────

    // Строка на каждый bucket: attention (stopped) / running (идёт) / idle (стоит) / closed.
    [Fact]
    public void Строка_Bucket_КаждоеЗначение()
    {
        // attention: исполнитель встал (ExecutorStoppedAt позже ClaudeStartedAt).
        var epicA = Epic("epic-a");
        var stopped = Child("a1", "epic-a", status: TaskItemStatus.InProgress);
        stopped.ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0);
        stopped.ExecutorStoppedAt = new DateTime(2026, 10, 17, 8, 30, 0);
        stopped.ExecutorStopReason = "auth_failed";
        Row(epicA, [epicA, stopped]).Bucket.Should().Be("attention");

        // running: идёт (InProgress, без attention).
        var epicR = Epic("epic-r");
        var running = Child("r1", "epic-r", status: TaskItemStatus.InProgress);
        running.ClaudeStartedAt = new DateTime(2026, 10, 17, 9, 0, 0);
        Row(epicR, [epicR, running]).Bucket.Should().Be("running");

        // idle: только Todo, без attention.
        var epicI = Epic("epic-i");
        var idle = Child("i1", "epic-i");
        Row(epicI, [epicI, idle]).Bucket.Should().Be("idle");

        // closed: эпик Done.
        var epicC = Epic("epic-c");
        epicC.Status = TaskItemStatus.Done;
        Row(epicC, [epicC]).Bucket.Should().Be("closed");
    }

    // ── 2. «готова ≠ закрыта» ─────────────────────────────────────────────────────────────────

    // Ready (финал принят) НЕ делает фичу закрытой: без merge-задачи и Done-эпика bucket ≠ closed.
    [Fact]
    public void Строка_ГотоваНеЗакрыта_BucketAttention()
    {
        const string epicId = "epic-ready-not-closed";
        var epic = Epic(epicId);
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var run = FeatureProjector.Build(epic, [epic, final]);
        run.Ready.Should().BeTrue("финал принят — готова");
        run.Closed.Should().BeFalse("мержа нет, эпик не Done — не закрыта");

        var row = FeatureProjector.BuildRow(run, [epic, final], Array.Empty<FeatureIndexSession>(), 0);
        row.Bucket.Should().Be("attention",
            "готова (final accepted) ≠ закрыта: bucket = attention (merge), не closed");
    }

    // ── 3. chips для «мерж» ───────────────────────────────────────────────────────────────────

    // attention = wait/«мерж» → chips = [open, merge]; attention.kind в строке = merge, reason = null.
    [Fact]
    public void Строка_Chips_Мерж()
    {
        const string epicId = "epic-merge-chips";
        var epic = Epic(epicId);
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var run = FeatureProjector.Build(epic, [epic, final]);
        var row = FeatureProjector.BuildRow(run, [epic, final], Array.Empty<FeatureIndexSession>(), 0);

        row.Chips.Should().Equal(["open", "merge"],
            "финал принят, мержа нет — чипы open + merge");
        row.Attention.Should().NotBeNull();
        row.Attention!.Kind.Should().Be("merge", "маппинг сырой строки «мерж» → kind=merge");
        row.Attention.Reason.Should().BeNull("у merge reason всегда null");
    }

    // Явный wait (AwaitingHumanSince) → attention.kind = wait, но ЧИП = «waiting»
    // (словарь чипов ≠ словарь attention.kind: chip «waiting», kind «wait»).
    [Fact]
    public void Строка_Chips_ЯвныйWait_Waiting()
    {
        const string epicId = "epic-wait-chip";
        var epic = Epic(epicId);
        epic.AwaitingHumanSince = new DateTime(2026, 10, 17, 9, 0, 0);
        epic.AwaitingHumanReason = "ждём решения";
        var step = Child("s1", epicId, status: TaskItemStatus.InProgress);

        var run = FeatureProjector.Build(epic, [epic, step]);
        var row = FeatureProjector.BuildRow(run, [epic, step], Array.Empty<FeatureIndexSession>(), 0);

        row.Attention!.Kind.Should().Be("wait", "явный wait — kind = wait");
        row.Attention.Reason.Should().Be("ждём решения");
        row.Chips.Should().Equal(["open", "waiting"],
            "явный wait → чип «waiting» (не «wait»: словарь чипов ≠ словарь attention.kind)");
    }

    // ── 4. цепочка ключевого чата: все 4 звена ───────────────────────────────────────────────

    // 1) epic.SourceSessionId; 2) epic.ExecutorLaunchedBySessionId (SourceSessionId null);
    // 3) самый частый ExecutorLaunchedBySessionId у дочерних (оба поля эпика null);
    // 4) null (кандидатов нет).
    [Fact]
    public void Строка_КлючевойЧат_ВсеЧетыреЗвена()
    {
        // 1) epic.SourceSessionId.
        var epic1 = Epic("epic1", sourceSessionId: "chat-src");
        Row(epic1, [epic1, Child("t1", "epic1")]).KeySessionId.Should().Be("chat-src");

        // 2) epic.ExecutorLaunchedBySessionId (SourceSessionId null).
        var epic2 = Epic("epic2", executorLaunchedBy: "chat-exec");
        Row(epic2, [epic2, Child("t2", "epic2")]).KeySessionId.Should().Be("chat-exec");

        // 3) самый частый ExecutorLaunchedBySessionId у дочерних (оба поля эпика null).
        //    chat-major — 2 задачи, chat-minor — 1 → ключевой = chat-major.
        var epic3 = Epic("epic3");
        var t3a = Child("t3a", "epic3", executorLaunchedBy: "chat-major");
        var t3b = Child("t3b", "epic3", executorLaunchedBy: "chat-major");
        var t3c = Child("t3c", "epic3", executorLaunchedBy: "chat-minor");
        Row(epic3, [epic3, t3a, t3b, t3c]).KeySessionId.Should().Be("chat-major",
            "самый частый ExecutorLaunchedBySessionId у дочерних");

        // 4) null: ни у эпика, ни у дочерних ExecutorLaunchedBySessionId/SourceSessionId.
        var epic4 = Epic("epic4");
        Row(epic4, [epic4, Child("t4", "epic4")]).KeySessionId.Should().BeNull();
    }

    // ── 5. rail закрытой фичи ─────────────────────────────────────────────────────────────────

    // У смерженной (закрытой) фичи рельса: 7 стадий, active нет → tone = null у всех сегментов.
    // (Закрыта = merge Done; «честный мерж» — epic Done без merge-задачи — merge active, tone ≠ null.)
    [Fact]
    public void Строка_Rail_ЗакрытойФичи()
    {
        const string epicId = "epic-closed-rail";
        var epic = Epic(epicId);
        epic.Status = TaskItemStatus.Done;
        var step = Child("s1", epicId, status: TaskItemStatus.Done);
        var merge = Child("m1", epicId, role: TaskRoles.Merge, status: TaskItemStatus.Done);

        var run = FeatureProjector.Build(epic, [epic, step, merge]);
        var row = FeatureProjector.BuildRow(run, [epic, step, merge], Array.Empty<FeatureIndexSession>(), 0);

        row.Bucket.Should().Be("closed");
        row.Rail.Should().HaveCount(7, "рельса — ровно 7 стадий в порядке жизненного цикла");
        row.Rail.Select(s => s.Key).Should().Equal(
            ["cut", "plan", "prod", "review", "qa", "final", "merge"],
            "порядок стадий жизненного цикла");
        row.Rail.Should().OnlyContain(s => s.Tone == null,
            "у смерженной фичи active нет → tone = null у всех сегментов");
        row.Rail.Single(s => s.Key == "prod").State.Should().Be("done");
        row.Rail.Single(s => s.Key == "merge").State.Should().Be("done");
    }

    // ── 6. фикстура feat-curator-console.json: строка совпадает с FeatureRun ─────────────────

    // На реальной фикстуре строка выводится из FeatureRun: bucket=running, rail из Stages,
    // now0/summary/lastActivityAt/loops/steps* — те же значения, что в run.
    [Fact]
    public void Строка_Фикстура_СовпадаетСFeatureRun()
    {
        var tasks = FeatureRunFixture.Load("feat-curator-console.json");
        var epic = tasks.Single(t => t.Role == TaskRoles.Feature);
        var run = FeatureProjector.Build(epic, tasks);
        var row = FeatureProjector.BuildRow(run, tasks, Array.Empty<FeatureIndexSession>(), 0);

        // bucket: эпик inProgress, attention null, Now не пуст (cc-r3, cc-s4) → running.
        run.Closed.Should().BeFalse();
        run.Attention.Should().BeNull();
        run.Now.Should().NotBeEmpty();
        row.Bucket.Should().Be("running");

        // rail: ровно 7, совпадает с FeatureRun.Stages (key/state/tone).
        row.Rail.Should().HaveCount(7);
        for (var i = 0; i < run.Stages.Count; i++)
        {
            row.Rail[i].Key.Should().Be(run.Stages[i].Key);
            row.Rail[i].State.Should().Be(run.Stages[i].State);
            row.Rail[i].Tone.Should().Be(run.Stages[i].Tone);
        }

        // счётчики/итог/активность — те же, что в run.
        row.StepsAccepted.Should().Be(run.StepsAccepted);
        row.StepsTotal.Should().Be(run.StepsTotal);
        row.Loops.Should().Be(run.Verdicts.Loops);
        row.LastActivityAt.Should().Be(run.LastActivityAt);
        row.Summary.Should().BeNull("фича не закрыта — summary null");

        // now0: первая InProgress-задача (по убыванию ClaudeStartedAt) — те же personaId/stage/stepRef.
        row.Now0.Should().NotBeNull();
        row.Now0!.Stage.Should().Be(run.Now[0].Stage);
        row.Now0.PersonaId.Should().Be(run.Now[0].PersonaId);
        row.Now0.StepRef.Should().Be(run.Now[0].StepRef);

        // ключевой чат: у эпика фикстуры нет SourceSessionId/ExecutorLaunchedBySessionId,
        // у дочерних тоже → null.
        row.KeySessionId.Should().BeNull();
    }

    // ── Мутация: цепочка ключевого чата ──────────────────────────────────────────────────────

    // Ключевое правило: при равенстве частот ExecutorLaunchedBySessionId у дочерних берётся
    // лексикографически меньший id (детерминизм). Мутация: убрать ThenBy(g => g.Key) →
    // выбор при равенстве нестабилен (зависит от порядка GroupBy) → тест краснеет.
    [Fact]
    public void Мутация_КлючевойЧат_РавенствоЧастот_МеньшийId()
    {
        // Оба дочерних запускали исполнителя из разных чатов, по одному разу (равенство частот 1:1).
        // chat-b < chat-a лексикографически? Нет: "chat-a" < "chat-b". Берём меньший = "chat-a".
        var epic = Epic("epic-mut-key");
        var tA = Child("tA", "epic-mut-key", executorLaunchedBy: "chat-b");
        var tB = Child("tB", "epic-mut-key", executorLaunchedBy: "chat-a");

        var row = Row(epic, [epic, tA, tB]);
        row.KeySessionId.Should().Be("chat-a",
            "равенство частот → лексикографически меньший id (детерминизм)");
    }
}
