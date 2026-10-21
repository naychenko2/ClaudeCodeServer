using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты ленты событий фичи (волна 2 «Обзор фич», разрез §2):
// FeatureProjector.BuildEvents — чистая проекция FeatureRun → List<FeatureEvent> без хранилища.
// Покрытие: каждый из 12 видов события, порядок (at desc, при равенстве taskId), sessionId
// (удалённый/чужой чат → null), обрезка reason ≤ 200. Мутация BuildEvents красит кейсы.
public class FeatureEventsTests
{
    // ── Хелперы сборки ──────────────────────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic(string id, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? completedAt = null, DateTime? awaitingSince = null, string? awaitingReason = null) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        Status = status,
        CreatedAt = T0,
        CompletedAt = completedAt,
        AwaitingHumanSince = awaitingSince,
        AwaitingHumanReason = awaitingReason,
    };

    // Дочерняя задача фичи (FeatureId = epicId).
    private static TaskItem Child(string id, string epicId, string role = TaskRoles.Step,
        string? stepRef = null, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? completedAt = null, DateTime? startedAt = null,
        DateTime? stoppedAt = null, string? stopReason = null,
        DateTime? droppedAt = null, string? linkedSessionId = null,
        string? verdict = null, string? notes = null,
        DateTime? awaitingSince = null, string? awaitingReason = null,
        string? personaId = null, List<string>? reviewOf = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = role,
        StepRef = stepRef,
        Status = status,
        CreatedAt = T0,
        CompletedAt = completedAt,
        ClaudeStartedAt = startedAt,
        ExecutorStoppedAt = stoppedAt,
        ExecutorStopReason = stopReason,
        DroppedByHumanAt = droppedAt,
        LinkedSessionId = linkedSessionId,
        Verification = verdict is null ? null : new TaskVerification { Verdict = verdict, Notes = notes },
        AwaitingHumanSince = awaitingSince,
        AwaitingHumanReason = awaitingReason,
        PersonaId = personaId,
        ReviewOf = reviewOf,
    };

    // FeatureRun из эпика + дочерних (чистое ядро BuildCore — ПОЛНЫЙ, необлегщённый run:
    // лента событий считается по полным задачам — reason вердикта = Verification.Notes,
    // который Slim обнуляет; Build = SlimRun(BuildCore) — контракт master для списков/ответа).
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // Снимок сессий владельца: id → FeatureIndexSession.
    private static IReadOnlyCollection<FeatureIndexSession> Sessions(params string[] ids) =>
        ids.Select(id => new FeatureIndexSession(id, null, null, false)).ToList();

    // Событие по (kind, taskId); бросает, если нет ровно одного.
    private static FeatureEvent Find(List<FeatureEvent> events, string kind, string taskId) =>
        events.Single(e => e.Kind == kind && e.TaskId == taskId);

    // ── 1. Каждый вид события ─────────────────────────────────────────────────────────────────

    // Один прогон: все 12 видов. Для каждого — верный taskId и At (момент из поля задачи).
    [Fact]
    public void BuildEvents_КаждыйВидСобытия()
    {
        var epic = Epic("epic", status: TaskItemStatus.Done, completedAt: new DateTime(2026, 10, 5, 12, 0, 0));
        // s1 — InProgress: несёт started + stopped (stopped — только у незакрытой задачи).
        var s1 = Child("s1", "epic", status: TaskItemStatus.InProgress,
            startedAt: new DateTime(2026, 10, 2, 9, 0, 0),
            stoppedAt: new DateTime(2026, 10, 2, 9, 30, 0), stopReason: "auth_failed");
        // s2 — Done без вердикта: несёт completed.
        var s2 = Child("s2", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 10, 0, 0));
        var r1 = Child("r1", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 9, 0, 0), verdict: TaskVerdicts.Accepted, notes: "ok");
        var r2 = Child("r2", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 10, 0, 0), verdict: TaskVerdicts.AcceptedWithRemarks, notes: "правки");
        var r3 = Child("r3", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 11, 0, 0), verdict: TaskVerdicts.Returned, notes: "вернул");
        var r4 = Child("r4", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 12, 0, 0), verdict: TaskVerdicts.Withdrawn, notes: "снял");
        var f1 = Child("f1", "epic", role: TaskRoles.Final,
            awaitingSince: new DateTime(2026, 10, 4, 13, 0, 0), awaitingReason: "decision");
        var m1 = Child("m1", "epic", role: TaskRoles.Merge, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 5, 11, 0, 0));
        var d1 = Child("d1", "epic", droppedAt: new DateTime(2026, 10, 3, 11, 0, 0));

        var run = Run(epic, s1, s2, r1, r2, r3, r4, f1, m1, d1);
        var events = FeatureProjector.BuildEvents(run, Sessions());

        // created — у каждой задачи (10 задач: эпик + 9 дочерних).
        events.Should().Contain(e => e.Kind == "created" && e.TaskId == "epic" && e.At == T0);
        events.Should().Contain(e => e.Kind == "created" && e.TaskId == "s1" && e.At == T0);

        // started/stopped — s1 (InProgress).
        Find(events, "started", "s1").At.Should().Be(new DateTime(2026, 10, 2, 9, 0, 0));
        var stopped = Find(events, "stopped", "s1");
        stopped.At.Should().Be(new DateTime(2026, 10, 2, 9, 30, 0));
        stopped.Reason.Should().Be("auth_failed");

        // вердикты — r1..r4, reason = Verification.Notes.
        Find(events, "accepted", "r1").Reason.Should().Be("ok");
        Find(events, "accepted_with_remarks", "r2").Reason.Should().Be("правки");
        Find(events, "returned", "r3").Reason.Should().Be("вернул");
        Find(events, "withdrawn", "r4").Reason.Should().Be("снял");

        // waiting — f1, reason = AwaitingHumanReason.
        var waiting = Find(events, "waiting", "f1");
        waiting.At.Should().Be(new DateTime(2026, 10, 4, 13, 0, 0));
        waiting.Reason.Should().Be("decision");

        // completed — s2 (Done, без вердикта); merged — m1; closed — эпик; dropped — d1.
        Find(events, "completed", "s2").At.Should().Be(new DateTime(2026, 10, 3, 10, 0, 0));
        Find(events, "merged", "m1").At.Should().Be(new DateTime(2026, 10, 5, 11, 0, 0));
        Find(events, "closed", "epic").At.Should().Be(new DateTime(2026, 10, 5, 12, 0, 0));
        Find(events, "dropped", "d1").At.Should().Be(new DateTime(2026, 10, 3, 11, 0, 0));
    }

    // ── 2. Терминальное событие: вердикт vs merged vs closed vs completed ─────────────────────

    // Задача Done с вердиктом даёт вердикт (не completed); merge Done → merged; эпик Done → closed;
    // обычный шаг Done → completed. Финал принят (Done + accepted) → фича merged, поэтому merge
    // Done даёт «merged» (с событием «merged» только при State == merged, см. тест ниже).
    [Fact]
    public void BuildEvents_Терминальное_ПриоритетВердиктаМержаЗакрытия()
    {
        var epic = Epic("epic");
        var final = Child("fn", "epic", role: TaskRoles.Final, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 8, 0, 0), verdict: TaskVerdicts.Accepted);
        var review = Child("rv", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 9, 0, 0), verdict: TaskVerdicts.Returned);
        var merge = Child("mg", "epic", role: TaskRoles.Merge, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 5, 9, 0, 0));
        var step = Child("st", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 9, 0, 0));

        var events = FeatureProjector.BuildEvents(Run(epic, final, review, merge, step), Sessions());

        // review Done + вердикт → «returned», НЕ «completed».
        events.Should().Contain(e => e.Kind == "returned" && e.TaskId == "rv");
        events.Should().NotContain(e => e.Kind == "completed" && e.TaskId == "rv");
        // merge Done → «merged», не «completed».
        events.Should().Contain(e => e.Kind == "merged" && e.TaskId == "mg");
        events.Should().NotContain(e => e.Kind == "completed" && e.TaskId == "mg");
        // обычный шаг Done → «completed».
        events.Should().Contain(e => e.Kind == "completed" && e.TaskId == "st");
    }

    // Частичный мерж (инцидент 21.10): merge Done, финал не принят (returned), эпик открыт ⇒
    // фича НЕ merged (idle). Событие «merged» не появляется (мутация «merged из любого merge Done»
    // красит); терминальное событие merge-задачи — «completed».
    [Fact]
    public void BuildEvents_ЧастичныйМерж_MergedТолькоЕслиStateMerged()
    {
        var epic = Epic("epic-partial-merge");
        var final = Child("f1", "epic-partial-merge", role: TaskRoles.Final,
            status: TaskItemStatus.Done, completedAt: T0.AddHours(2), verdict: TaskVerdicts.Returned);
        var merge = Child("m1", "epic-partial-merge", role: TaskRoles.Merge,
            status: TaskItemStatus.Done, completedAt: T0.AddHours(3), startedAt: T0);

        var run = Run(epic, final, merge);
        run.State.Should().NotBe(FeatureStates.Merged, "частичный мерж: финал не принят, эпик открыт");

        var events = FeatureProjector.BuildEvents(run, Sessions());

        // «merged» нет (State != merged); терминальное событие merge-задачи — «completed».
        events.Should().NotContain(e => e.Kind == "merged" && e.TaskId == "m1",
            "частичный мерж (State != merged) не даёт события «merged»");
        Find(events, "completed", "m1").At.Should().Be(T0.AddHours(3));
    }

    // ── 3. Порядок: at desc, при равенстве — taskId ───────────────────────────────────────────

    // Две задачи с равным CompletedAt → события упорядочены по taskId (восходя).
    [Fact]
    public void BuildEvents_Порядок_AtDesc_TieBreakTaskId()
    {
        var epic = Epic("epic");
        var same = new DateTime(2026, 10, 3, 9, 0, 0);
        // taskId: "b" > "a" — при равном At порядок восходя по taskId: a, b.
        var a = Child("a", "epic", status: TaskItemStatus.Done, completedAt: same);
        var b = Child("b", "epic", status: TaskItemStatus.Done, completedAt: same);
        // более ранний completed — в конце (at desc).
        var early = Child("early", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 2, 9, 0, 0));

        var events = FeatureProjector.BuildEvents(Run(epic, a, b, early), Sessions());

        // completed-события: сначала равные (a, b по taskId), затем более ранний (early) —
        // но at desc: равные 10-03 раньше 10-02.
        var completed = events.Where(e => e.Kind == "completed").ToList();
        completed.Select(e => e.TaskId).Should().BeEquivalentTo(["a", "b", "early"]);
        // Равные At: a строго перед b.
        completed.IndexOf(completed.Single(e => e.TaskId == "a"))
            .Should().BeLessThan(completed.IndexOf(completed.Single(e => e.TaskId == "b")));
    }

    // ── 4. sessionId: наружу только если в снимке сессий владельца ─────────────────────────────

    // LinkedSessionId задачи в снимке → наружу; не в снимке (удалённый/чужой чат) → null.
    [Fact]
    public void BuildEvents_SessionId_УдалённыйИЧужойЧат_Null()
    {
        var epic = Epic("epic");
        var inSnap = Child("in", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 9, 0, 0), linkedSessionId: "chat-live");
        var notInSnap = Child("out", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 10, 0, 0), linkedSessionId: "chat-deleted");
        var noSession = Child("none", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 11, 0, 0));

        // Снимок содержит только chat-live.
        var events = FeatureProjector.BuildEvents(Run(epic, inSnap, notInSnap, noSession),
            Sessions("chat-live"));

        Find(events, "completed", "in").SessionId.Should().Be("chat-live");
        Find(events, "completed", "out").SessionId.Should().BeNull();
        Find(events, "completed", "none").SessionId.Should().BeNull();
        // created того же чужого чата — тоже null.
        Find(events, "created", "out").SessionId.Should().BeNull();
    }

    // ── 5. reason ≤ 200 символов ───────────────────────────────────────────────────────────────

    // Длинная причина (ExecutorStopReason) обрезается ровно до 200.
    [Fact]
    public void BuildEvents_Reason_ОбрезкаДо200()
    {
        var epic = Epic("epic");
        var longReason = new string('x', 250);
        var stopped = Child("s", "epic", status: TaskItemStatus.InProgress,
            stoppedAt: new DateTime(2026, 10, 2, 9, 30, 0), stopReason: longReason);

        var events = FeatureProjector.BuildEvents(Run(epic, stopped), Sessions());
        var e = Find(events, "stopped", "s");
        e.Reason.Should().NotBeNull();
        e.Reason!.Length.Should().Be(200);
        e.Reason.Should().Be(new string('x', 200));
    }

    // ── 6. Мутация: порядок при равенстве At держит ThenBy(taskId) ────────────────────────────

    // Равные At → порядок по taskId восходя. Equal (order-sensitive, в отличие от BeEquivalentTo):
    // инверсия ThenBy(taskId) на ThenByDescending разъедает порядок → тест краснеет.
    [Fact]
    public void BuildEvents_Мутация_TieBreakTaskId()
    {
        var epic = Epic("epic");
        var same = new DateTime(2026, 10, 3, 9, 0, 0);
        var a = Child("aa", "epic", status: TaskItemStatus.Done, completedAt: same);
        var b = Child("bb", "epic", status: TaskItemStatus.Done, completedAt: same);

        var events = FeatureProjector.BuildEvents(Run(epic, a, b), Sessions());
        var completed = events.Where(e => e.Kind == "completed").ToList();
        // Восходя по taskId: aa, bb (order-sensitive).
        completed.Select(e => e.TaskId).Should().Equal("aa", "bb");
    }

    // ── 7. Новые поля: role / stepRef / personaId (атрибуты задачи, у всех видов) ───────────

    // created несёт role/stepRef/personaId задачи; round — только у вердиктов (у created null).
    [Fact]
    public void BuildEvents_НовыеПоля_RoleStepRefPersona()
    {
        var epic = Epic("epic");
        var step = Child("step1", "epic", role: TaskRoles.Step, stepRef: "B1",
            personaId: "denis", status: TaskItemStatus.InProgress);

        var events = FeatureProjector.BuildEvents(Run(epic, step), Sessions());

        var created = Find(events, "created", "step1");
        created.Role.Should().Be("step");
        created.StepRef.Should().Be("B1");
        created.PersonaId.Should().Be("denis");
        created.Round.Should().BeNull();
    }

    // ── 8. round: номер витка из FeatureRound (стадия qa + цепочка шага) ─────────────────────

    // qa-вердикт → номер витка из стадии qa (N=1); ревью-вердикт (ReviewOf→шаг) → из цепочки
    // шага (N=1). Арифметику N не дублируем — берём из уже вычисленных FeatureRound.
    [Fact]
    public void BuildEvents_Вердикт_Round_ИзFeatureRound()
    {
        var epic = Epic("epic");
        var qa = Child("qa1", "epic", role: TaskRoles.Qa, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 9, 0, 0), verdict: TaskVerdicts.Accepted, notes: "ok");
        var step = Child("step1", "epic", role: TaskRoles.Step, stepRef: "B1",
            status: TaskItemStatus.Done, completedAt: new DateTime(2026, 10, 3, 9, 0, 0));
        var review = Child("rv1", "epic", role: TaskRoles.Review, status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 4, 10, 0, 0), verdict: TaskVerdicts.Returned,
            notes: "вернул", reviewOf: new List<string> { "step1" });

        var events = FeatureProjector.BuildEvents(Run(epic, qa, step, review), Sessions());

        Find(events, "accepted", "qa1").Round.Should().Be(1);
        Find(events, "returned", "rv1").Round.Should().Be(1);
        // created того же ревью — без round (round только у вердиктов).
        Find(events, "created", "rv1").Round.Should().BeNull();
    }

    // ── 9. stopped: только у незакрытой (InProgress) и только когда ExecutorStoppedAt > ClaudeStartedAt ─

    // Закрытая (Done) задача со стопом — stopped НЕ показываем; InProgress со stale-стопом
    // (ExecutorStoppedAt < ClaudeStartedAt) — тоже. Правило то же, что у Attention.
    [Fact]
    public void BuildEvents_Stopped_ТолькоНезакрытая_ИПослеСтарта()
    {
        var epic = Epic("epic");
        // Done + ExecutorStoppedAt > ClaudeStartedAt → stopped отсутствует (закрыта).
        var closed = Child("closed", "epic", status: TaskItemStatus.Done,
            completedAt: new DateTime(2026, 10, 3, 10, 0, 0),
            startedAt: new DateTime(2026, 10, 2, 9, 0, 0),
            stoppedAt: new DateTime(2026, 10, 2, 9, 30, 0), stopReason: "auth_failed");
        // InProgress, но ExecutorStoppedAt < ClaudeStartedAt (stale) → stopped отсутствует.
        var stale = Child("stale", "epic", status: TaskItemStatus.InProgress,
            startedAt: new DateTime(2026, 10, 2, 9, 30, 0),
            stoppedAt: new DateTime(2026, 10, 2, 9, 0, 0), stopReason: "stale");
        // InProgress, ExecutorStoppedAt > ClaudeStartedAt → stopped есть.
        var live = Child("live", "epic", status: TaskItemStatus.InProgress,
            startedAt: new DateTime(2026, 10, 2, 9, 0, 0),
            stoppedAt: new DateTime(2026, 10, 2, 9, 30, 0), stopReason: "auth_failed");

        var events = FeatureProjector.BuildEvents(Run(epic, closed, stale, live), Sessions());

        events.Should().NotContain(e => e.Kind == "stopped" && e.TaskId == "closed");
        events.Should().NotContain(e => e.Kind == "stopped" && e.TaskId == "stale");
        var stopped = Find(events, "stopped", "live");
        stopped.At.Should().Be(new DateTime(2026, 10, 2, 9, 30, 0));
        stopped.Reason.Should().Be("auth_failed");
    }

    // ─── Truncate: кириллица (BMP) и суррогатные пары (эмодзи) ───────────────────────────────

    // null → null; ≤ 200 → как есть; > 200 (BMP, 1 кодовая точка = 1 единица) → ровно 200.
    [Fact]
    public void Truncate_Киллица_ОбрезкаДо200()
    {
        FeatureProjector.Truncate(null).Should().BeNull();
        FeatureProjector.Truncate("короткая").Should().Be("короткая");
        FeatureProjector.Truncate(new string('а', 200)).Should().Be(new string('а', 200));
        FeatureProjector.Truncate(new string('а', 250)).Should().Be(new string('а', 200));
    }

    // Суррогатная пара (эмодзи, 2 UTF-16-единицы) на границе 200 не рвётся: 199 BMP + эмодзи =
    // 201 единица; s[..200] = 199 BMP + старший суррогат → пара разорвана → старший суррогат
    // отбрасывается (199 BMP), висячего суррогата нет. Мутация: вернуть s[..200] без проверки →
    // висячий старший суррогат → тест краснеет.
    [Fact]
    public void Truncate_СуррогатнаяПара_НеРвётся()
    {
        var s = new string('a', 199) + "😀"; // 199 + 2 = 201 единица
        s.Length.Should().Be(201);

        var r = FeatureProjector.Truncate(s);

        r!.Length.Should().Be(199, "старший суррогат эмодзи отброшен — пара не разорвана");
        r.Should().Be(new string('a', 199));
        char.IsHighSurrogate(r[^1]).Should().BeFalse("висячего старшего суррогата нет");
    }

    // Эмодзи НЕ на границе (внутри лимита) — пара целиком сохраняется, обрезки нет.
    [Fact]
    public void Truncate_ЭмодзиВнутриЛимита_Целиком()
    {
        var s = new string('a', 100) + "😀😀😀"; // 100 + 6 = 106 единиц < 200
        FeatureProjector.Truncate(s).Should().Be(s);
    }
}
