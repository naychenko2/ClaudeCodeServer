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
    // Снимок — и в Build (fail-closed полей run: now/attention/исполнители), и в BuildRow
    // (строка) — как в продуктовом маршруте BuildIndexCore: один снимок, одна семантика.
    private static FeatureIndexRow Row(
        TaskItem epic, List<TaskItem> tasks,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null, int chatCount = 0)
    {
        sessions ??= Array.Empty<FeatureIndexSession>();
        var run = FeatureProjector.Build(epic, tasks, null, sessions);
        return FeatureProjector.BuildRow(run, tasks, sessions, chatCount);
    }

    private static TaskItem Epic(string id, string? sourceSessionId = null,
        string? executorLaunchedBy = null, string? projectId = null,
        DateTime? createdAt = null) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        SourceSessionId = sourceSessionId,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
        ProjectId = projectId,
        CreatedAt = createdAt ?? DateTime.UtcNow,
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

    // Живой чат владельца (снимок FeatureIndexSession): id + опционально taskId/projectId/archived/personaId.
    private static FeatureIndexSession Session(string id, string? taskId = null,
        string? projectId = null, bool archived = false, string? personaId = null) =>
        new(id, taskId, projectId, archived, null, personaId);

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

    // ── 1b. state: поле строки индекса (единая точка правда bucket) ───────────────────────────

    // Строка несёт state (FeatureRun.State); bucket выводится из него. Проверяем: state
    // присутствует в строке и согласован с bucket (merging → running, merge_pending → attention,
    // merged/closed → closed, running → running, not_started/idle → idle).
    [Fact]
    public void Строка_State_ПрисутствуетИСогласованСBucket()
    {
        var t = new DateTime(2026, 10, 17);

        // merging: merge-задача InProgress → state=merging, bucket=running.
        var epicM = Epic("epic-state-merging");
        var merge = Child("m1", "epic-state-merging", role: TaskRoles.Merge, status: TaskItemStatus.InProgress);
        var rowM = Row(epicM, [epicM, merge]);
        rowM.State.Should().Be(FeatureStates.Merging, "merge InProgress → state=merging");
        rowM.Bucket.Should().Be("running", "merging → bucket=running (не «ждёт вас»)");

        // merge_pending: финал принят, мержа нет → state=merge_pending, bucket=attention.
        var epicP = Epic("epic-state-pending");
        var final = Child("f1", "epic-state-pending", role: TaskRoles.Final, status: TaskItemStatus.Done);
        final.CompletedAt = t;
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var rowP = Row(epicP, [epicP, final]);
        rowP.State.Should().Be(FeatureStates.MergePending, "финал принят, мержа нет → state=merge_pending");
        rowP.Bucket.Should().Be("attention", "merge_pending → bucket=attention (ждёт мержа)");

        // merged: merge Done + финал принят → state=merged, bucket=closed.
        var epicMg = Epic("epic-state-merged");
        var finalMg = Child("f1", "epic-state-merged", role: TaskRoles.Final, status: TaskItemStatus.Done);
        finalMg.CompletedAt = t;
        finalMg.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var mergeDone = Child("m1", "epic-state-merged", role: TaskRoles.Merge, status: TaskItemStatus.Done);
        var rowMg = Row(epicMg, [epicMg, finalMg, mergeDone]);
        rowMg.State.Should().Be(FeatureStates.Merged, "merge Done + финал принят → state=merged");
        rowMg.Bucket.Should().Be("closed", "merged → bucket=closed");

        // closed: эпик Done, мержа нет → state=closed, bucket=closed.
        var epicC = Epic("epic-state-closed");
        epicC.Status = TaskItemStatus.Done;
        var rowC = Row(epicC, [epicC]);
        rowC.State.Should().Be(FeatureStates.Closed, "эпик Done, мержа нет → state=closed");
        rowC.Bucket.Should().Be("closed", "closed → bucket=closed");

        // running: идёт задача, без тревоги → state=running, bucket=running.
        var epicR = Epic("epic-state-running");
        var running = Child("r1", "epic-state-running", status: TaskItemStatus.InProgress);
        running.ClaudeStartedAt = t;
        var rowR = Row(epicR, [epicR, running]);
        rowR.State.Should().Be(FeatureStates.Running, "идёт задача → state=running");
        rowR.Bucket.Should().Be("running", "running → bucket=running");

        // not_started: ни одна задача не стартовала → state=not_started, bucket=idle.
        var epicN = Epic("epic-state-notstarted");
        var todo = Child("s1", "epic-state-notstarted");
        var rowN = Row(epicN, [epicN, todo]);
        rowN.State.Should().Be(FeatureStates.NotStarted, "ничего не стартовало → state=not_started");
        rowN.Bucket.Should().Be("idle", "not_started → bucket=idle");
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

    // ── 4. цепочка ключевого чата: первое ЖИВОЕ звено ────────────────────────────────────────

    // Ключевой чат — первое живое звено цепочки: 1) epic.SourceSessionId;
    // 2) epic.ExecutorLaunchedBySessionId (SourceSessionId null);
    // 3) самый частый ExecutorLaunchedBySessionId у дочерних (оба поля эпика null);
    // 4) null (кандидатов нет). Живым считается чат из снимка владельца (sessions).
    [Fact]
    public void Строка_КлючевойЧат_ВсеЧетыреЗвена()
    {
        // 1) epic.SourceSessionId (живой чат в снимке).
        var epic1 = Epic("epic1", sourceSessionId: "chat-src");
        Row(epic1, [epic1, Child("t1", "epic1")],
            [Session("chat-src")]).KeySessionId.Should().Be("chat-src");

        // 2) epic.ExecutorLaunchedBySessionId (SourceSessionId null; живой чат в снимке).
        var epic2 = Epic("epic2", executorLaunchedBy: "chat-exec");
        Row(epic2, [epic2, Child("t2", "epic2")],
            [Session("chat-exec")]).KeySessionId.Should().Be("chat-exec");

        // 3) самый частый ExecutorLaunchedBySessionId у дочерних (оба поля эпика null).
        //    chat-major — 2 задачи, chat-minor — 1 → ключевой = chat-major (живой в снимке).
        var epic3 = Epic("epic3");
        var t3a = Child("t3a", "epic3", executorLaunchedBy: "chat-major");
        var t3b = Child("t3b", "epic3", executorLaunchedBy: "chat-major");
        var t3c = Child("t3c", "epic3", executorLaunchedBy: "chat-minor");
        Row(epic3, [epic3, t3a, t3b, t3c],
            [Session("chat-major"), Session("chat-minor")]).KeySessionId
            .Should().Be("chat-major", "самый частый ExecutorLaunchedBySessionId у дочерних");

        // 4) null: ни у эпика, ни у дочерних ExecutorLaunchedBySessionId/SourceSessionId.
        var epic4 = Epic("epic4");
        Row(epic4, [epic4, Child("t4", "epic4")]).KeySessionId.Should().BeNull();
    }

    // ── 5. rail закрытой фичи ─────────────────────────────────────────────────────────────────

    // У смерженной (закрытой) фичи рельса: 8 стадий, active нет → tone = null у всех сегментов.
    // Закрыта = merge Done (или epic Done): нормализация RailOf приводит все стадии, кроме absent,
    // к done (см. тест 5b для «честного мержа» без merge-задачи).
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
        row.Rail.Should().HaveCount(8, "рельса — 8 стадий: пустая необязательная «design» добавляется (W4-DES-B)");
        row.Rail.Select(s => s.Key).Should().Equal(
            ["cut", "design", "plan", "prod", "review", "qa", "final", "merge"],
            "порядок стадий жизненного цикла");
        row.Rail.Should().OnlyContain(s => s.Tone == null,
            "у смерженной фичи active нет → tone = null у всех сегментов");
        row.Rail.Single(s => s.Key == "prod").State.Should().Be("done");
        row.Rail.Single(s => s.Key == "merge").State.Should().Be("done");
        row.Rail.Single(s => s.Key == "design").State.Should().Be("skipped",
            "«design» без задач — skipped, не «done»: необязательный этап не пройден (W4-DES-B)");
    }

    // ── 5b. rail «честного мержа»: epic Done, merge-задачи нет ─────────────────────────────────

    // Закрытая эпиком фича БЕЗ merge-задачи (эпик Done, merge-задачи нет): в Build merge-стадия
    // получает active (tone=run) — противоречие контракту С0 (у closed active нет, tone=null).
    // Нормализация в RailOf: все стадии, кроме absent, → done, tone=null.
    // Мутация: убрать нормализацию (RailOf копирует state/tone как есть) → merge=active, tone=run
    // → NotContain(active) и Single(merge).State=done краснеют.
    [Fact]
    public void Строка_Rail_ЧестныйМержБезMergeЗадачи_ВсеDone()
    {
        const string epicId = "epic-honest-merge";
        var epic = Epic(epicId);
        epic.Status = TaskItemStatus.Done;
        var step = Child("s1", epicId, status: TaskItemStatus.Done);
        // merge-задачи НЕТ: только эпик (Done) + шаг (Done).

        var run = FeatureProjector.Build(epic, [epic, step]);
        var row = FeatureProjector.BuildRow(run, [epic, step], Array.Empty<FeatureIndexSession>(), 0);

        row.Bucket.Should().Be("closed", "эпик Done — фича закрыта");
        row.Rail.Should().OnlyContain(s => s.Tone == null,
            "у закрытой фичи tone=null у всех сегментов (active нет)");
        row.Rail.Should().NotContain(s => s.State == "active",
            "у закрытой фичи active нет: merge не active, а done");
        row.Rail.Single(s => s.Key == "merge").State.Should().Be("done",
            "«честный мерж» (epic Done, merge-задачи нет) → merge done, не active");
        row.Rail.Single(s => s.Key == "prod").State.Should().Be("done");
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

        // rail: ровно 8 стадий (с необязательной «design», W4-DES-B), совпадает с FeatureRun.Stages
        // (key/state/tone).
        row.Rail.Should().HaveCount(8);
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
    // выбор при равенстве по первому появлению (порядок GroupBy) → тест краснеет.
    //
    // Фикстура: первое появление — НЕ-минимальный ключ (tA: chat-b, tB: chat-a, порядок
    // добавления tA → tB). Без ThenBy GroupBy-порядок [chat-b, chat-a] стабилен (равные
    // частоты) → FirstOrDefault = chat-b ≠ chat-a → краснеет. С ThenBy → chat-a (меньший).
    [Fact]
    public void Мутация_КлючевойЧат_РавенствоЧастот_МеньшийId()
    {
        // Оба дочерних запускали исполнителя из разных чатов, по одному разу (равенство частот 1:1).
        // "chat-a" < "chat-b" лексикографически — меньший = "chat-a".
        // Первое появление — chat-b (tA добавлен первым): без ThenBy выбор упёрся бы в chat-b.
        var epic = Epic("epic-mut-key");
        var tA = Child("tA", "epic-mut-key", executorLaunchedBy: "chat-b");
        var tB = Child("tB", "epic-mut-key", executorLaunchedBy: "chat-a");

        // Оба чата живы в снимке; цепочка несёт только самый частый (равенство 1:1 → chat-a).
        var row = Row(epic, [epic, tA, tB], [Session("chat-a"), Session("chat-b")]);
        row.KeySessionId.Should().Be("chat-a",
            "равенство частот → лексикографически меньший id (детерминизм)");
    }

    // ── 7. attention.sessionId: снимок живых сессий владельца ─────────────────────────────────

    // attention.sessionId — LinkedSessionId задачи, прошедший по снимку живых сессий владельца
    // (как KeySession: мёртвый/чужой чат наружу не уходит).
    // (1) Удалённый/чужой LinkedSessionId (не в снимке) → attention.sessionId = null.
    // (2) Живой LinkedSessionId (в снимке) → attention.sessionId = id.
    // Мутация: убрать проверку по live-set (AttentionOf возвращает LinkedSessionId как есть)
    // → тест (1) краснеет (sessionId ≠ null).
    [Fact]
    public void Attention_SessionId_Снимок_ЖивыхСессий()
    {
        const string epicId = "epic-att-sess";

        // (1) Мёртвый чат: LinkedSessionId = "chat-dead", но в снимке только "chat-live".
        var epic1 = Epic(epicId);
        var stopped1 = Child("a1", epicId, status: TaskItemStatus.InProgress);
        stopped1.ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0);
        stopped1.ExecutorStoppedAt = new DateTime(2026, 10, 17, 8, 30, 0);
        stopped1.ExecutorStopReason = "auth_failed";
        stopped1.LinkedSessionId = "chat-dead";
        var row1 = Row(epic1, [epic1, stopped1], [Session("chat-live")]);
        row1.Attention.Should().NotBeNull("исполнитель встал — тревога есть");
        row1.Attention!.Kind.Should().Be("stopped");
        row1.Attention.SessionId.Should().BeNull(
            "мёртвый чат (не в снимке) → sessionId = null");

        // (2) Живой чат: LinkedSessionId = "chat-live", и он в снимке.
        var epic2 = Epic("epic-att-sess-2");
        var stopped2 = Child("a2", "epic-att-sess-2", status: TaskItemStatus.InProgress);
        stopped2.ClaudeStartedAt = new DateTime(2026, 10, 17, 9, 0, 0);
        stopped2.ExecutorStoppedAt = new DateTime(2026, 10, 17, 9, 30, 0);
        stopped2.ExecutorStopReason = "timeout";
        stopped2.LinkedSessionId = "chat-live";
        var row2 = Row(epic2, [epic2, stopped2], [Session("chat-live")]);
        row2.Attention.Should().NotBeNull();
        row2.Attention!.SessionId.Should().Be("chat-live",
            "живой чат (в снимке) → sessionId = id");
    }

    // Б7: id задачи тревоги (FeatureIndexAttention.TaskId) — из FeatureRun.Attention.TaskId
    // (та же задача, к которой относится тревога). Мутация: AttentionOf не копирует TaskId →
    // taskId = null → тест краснеет.
    [Fact]
    public void Attention_TaskId_ИзRun()
    {
        const string epicId = "epic-att-taskid";
        var epic = Epic(epicId);
        var stopped = Child("a1", epicId, status: TaskItemStatus.InProgress);
        stopped.ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0);
        stopped.ExecutorStoppedAt = new DateTime(2026, 10, 17, 8, 30, 0);
        stopped.ExecutorStopReason = "auth_failed";

        var row = Row(epic, [epic, stopped], Array.Empty<FeatureIndexSession>());
        row.Attention.Should().NotBeNull("исполнитель встал — тревога есть");
        row.Attention!.Kind.Should().Be("stopped");
        row.Attention.TaskId.Should().Be("a1", "id задачи тревоги = id остановленной задачи");
    }

    // ── 8. аддитивные поля (волна 2, без хранения) ───────────────────────────────────────────

    // branch/createdAt — из полей эпика (WorktreeBranch/CreatedAt); null — эпик без worktree.
    [Fact]
    public void Строка_Аддитивные_ВеткаИСоздание()
    {
        var createdAt = new DateTime(2026, 10, 19, 7, 48, 50);
        var epic = Epic("epic-ad", createdAt: createdAt);
        epic.WorktreeBranch = "feat/features-overview-2";
        var row = Row(epic, [epic]);
        row.Branch.Should().Be("feat/features-overview-2", "ветка = epic.WorktreeBranch");
        row.CreatedAt.Should().Be(createdAt, "createdAt = epic.CreatedAt");

        // Эпик без worktree → branch null (createdAt всегда есть: DateTime по умолчанию).
        var epic2 = Epic("epic-ad2");
        var row2 = Row(epic2, [epic2]);
        row2.Branch.Should().BeNull("эпик без worktree → branch null");
        row2.CreatedAt.Should().NotBeNull();
    }

    // taskCount = эпик + дочерние (число задач фичи, как их дал вызывающий).
    [Fact]
    public void Строка_Аддитивные_ЧислоЗадач()
    {
        var epic = Epic("epic-tc");
        var c1 = Child("c1", "epic-tc");
        var c2 = Child("c2", "epic-tc");
        Row(epic, [epic, c1, c2]).TaskCount.Should().Be(3, "эпик + 2 дочерних");
        Row(epic, [epic]).TaskCount.Should().Be(1, "только эпик");
    }

    // nowPersonaIds: уникальные PersonaId идущих (Now, по убыванию ClaudeStartedAt) в порядке
    // старта; дубль персоны схлопывается (первое, т.е. самое позднее, появление определяет место).
    [Fact]
    public void Строка_Аддитивные_СейчасПерсоны_ПорядокИУникальность()
    {
        var epic = Epic("epic-now");
        // alpha стартует позже (10:00) → раньше в списке; beta (09:00) → позже;
        // alpha снова (08:00) — дубль персоны, схлопывается.
        var a = Child("a", "epic-now", status: TaskItemStatus.InProgress);
        a.ClaudeStartedAt = new DateTime(2026, 10, 19, 10, 0, 0);
        a.PersonaId = "alpha";
        var b = Child("b", "epic-now", status: TaskItemStatus.InProgress);
        b.ClaudeStartedAt = new DateTime(2026, 10, 19, 9, 0, 0);
        b.PersonaId = "beta";
        var c = Child("c", "epic-now", status: TaskItemStatus.InProgress);
        c.ClaudeStartedAt = new DateTime(2026, 10, 19, 8, 0, 0);
        c.PersonaId = "alpha";

        var row = Row(epic, [epic, a, b, c]);
        row.NowPersonaIds.Should().Equal(["alpha", "beta"],
            "уникальные персоны идущих, по убыванию старта; дубль alpha схлопнут");
    }

    // Чип «running»: появляется ровно когда bucket = running (идёт, без тревоги);
    // у закрытой и у тревожной фичи не появляется.
    [Fact]
    public void Строка_Аддитивные_ЧипRunning_ТолькоКогдаИдёт()
    {
        // Идёт (InProgress, без тревоги) → bucket = running, чипы [open, running].
        var epicR = Epic("epic-run");
        var running = Child("r1", "epic-run", status: TaskItemStatus.InProgress);
        running.ClaudeStartedAt = new DateTime(2026, 10, 19, 9, 0, 0);
        var rowR = Row(epicR, [epicR, running]);
        rowR.Bucket.Should().Be("running");
        rowR.Chips.Should().Equal(["open", "running"], "идёт без тревоги → чип running");

        // Закрытая (epic Done) → bucket = closed, чип [closed]; running не появляется.
        var epicC = Epic("epic-closed");
        epicC.Status = TaskItemStatus.Done;
        var rowC = Row(epicC, [epicC]);
        rowC.Bucket.Should().Be("closed");
        rowC.Chips.Should().Equal(["closed"], "у закрытой фичи running не появляется");

        // Тревога (stopped) → bucket = attention, чип [open, stopped]; running не появляется.
        var epicA = Epic("epic-att");
        var stopped = Child("s1", "epic-att", status: TaskItemStatus.InProgress);
        stopped.ClaudeStartedAt = new DateTime(2026, 10, 19, 8, 0, 0);
        stopped.ExecutorStoppedAt = new DateTime(2026, 10, 19, 8, 30, 0);
        stopped.ExecutorStopReason = "auth_failed";
        var rowA = Row(epicA, [epicA, stopped]);
        rowA.Bucket.Should().Be("attention");
        rowA.Chips.Should().Equal(["open", "stopped"], "у тревоги (stopped) running не появляется");
    }

    // ── 9. оркестратор фичи (W4-1B) ────────────────────────────────────────────────────────

    // Цепочка: персона ключевого чата (если он в снимке владельца) → персона, запустившая
    // исполнителя эпика → персона, создавшая эпик; ничего нет → null.
    // (a) живой ключевой чат с персоной — именно она, поля эпика ниже по приоритету;
    // (b) ключевой чат жив, но без персоны — следующее звено;
    // (c) ни звеньев, ни полей эпика — null.
    [Fact]
    public void OrchestratorOf_KeySessionPersona_ThenEpicFields()
    {
        // (a) персона ключевого чата приоритетнее полей эпика.
        var epicA = Epic("epic-orch-a", sourceSessionId: "chat-key");
        epicA.ExecutorLaunchedByPersonaId = "p-launched";
        epicA.CreatedByPersonaId = "p-created";
        var rowA = Row(epicA, [epicA], [Session("chat-key", personaId: "p-key")]);
        rowA.KeySessionId.Should().Be("chat-key");
        rowA.OrchestratorPersonaId.Should().Be("p-key", "персона ключевого чата — первое звено");

        // (b) ключевой чат жив, но персоны нет — цепочка идёт к запустившей.
        var epicB = Epic("epic-orch-b", sourceSessionId: "chat-key");
        epicB.ExecutorLaunchedByPersonaId = "p-launched";
        epicB.CreatedByPersonaId = "p-created";
        var rowB = Row(epicB, [epicB], [Session("chat-key")]);
        rowB.OrchestratorPersonaId.Should().Be("p-launched", "у ключевого чата нет персоны → следующее звено");

        // (c) ни ключевого чата, ни полей эпика — null.
        var epicC = Epic("epic-orch-c");
        Row(epicC, [epicC]).OrchestratorPersonaId.Should().BeNull("цепочка пуста → null");
    }

    // Чужой/удалённый ключевой чат — не в снимке владельца: персона из него не берётся,
    // работает запасная цепочка (запустившая исполнителя эпика). Мутация: убрать сверку
    // ключевого чата с снимком (брать первого живого чата) → "p-live" ≠ "p-launched" → краснеет.
    [Fact]
    public void OrchestratorOf_ForeignKeySession_FallsBackToLaunchedByPersona()
    {
        var epic = Epic("epic-orch-foreign", sourceSessionId: "chat-foreign");
        epic.ExecutorLaunchedByPersonaId = "p-launched";
        epic.CreatedByPersonaId = "p-created";
        // В снимке другой чат владельца (с персоной) — он не ключевой.
        var row = Row(epic, [epic], [Session("chat-live", personaId: "p-live")]);
        row.KeySessionId.Should().BeNull("ключевой чат не в снимке → ключа нет");
        row.OrchestratorPersonaId.Should().Be("p-launched",
            "чужой ключевой чат → запасная цепочка, не персона первого чата снимка");
    }

    // Последний запас: никто не запускал исполнителя — создатель эпика.
    [Fact]
    public void OrchestratorOf_NoLaunchedBy_CreatedByPersona()
    {
        var epic = Epic("epic-orch-created", sourceSessionId: "chat-foreign");
        epic.CreatedByPersonaId = "p-created";
        var row = Row(epic, [epic], [Session("chat-live", personaId: "p-live")]);
        row.OrchestratorPersonaId.Should().Be("p-created", "нет запустившей → создатель эпика");
    }
}
