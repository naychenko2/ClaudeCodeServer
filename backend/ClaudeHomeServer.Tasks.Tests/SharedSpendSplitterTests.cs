using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Чистая функция SharedSpendSplitter (волна 3 «Обзор фич», Б9c): деление расхода ОБЩЕГО
// (оркестраторского) чата поровну (1/N) между фичами, активными на момент хода.
//
// Покрытие (критерии задачи):
//  • 2 активные фичи → по 1/2;
//  • ход ночью без активных → 0 (не распределяется);
//  • одна активная → целиком ей;
//  • три фичи, две активны → 1/2 (знаменатель = активные, не все);
//  • ход ровно на границе интервала: включительно с начала, исключая конец (half-open, фиксация);
//  • пустой вход → 0;
//  • отрицательные/нулевые токены → кламп в ≥ 0 (как в итоге Б9b);
//  • округление денег: decimal, без потери;
//  • сумма долей по ВСЕМ фичам = сумме хода (до округления);
//  • деление без остатка / с остатком (правило остатка: round(ход/N), ToEven);
//  • цена: только с надёжной ценой (free → 0; ненадёжная/без цены → costPartial + hidden).
public class SharedSpendSplitterTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);

    // Запись расхода общего чата: время, токены, цена, источник, признак ненадёжной цены.
    private static FeatureSpendRecord Turn(DateTime at, long input = 0, long output = 0,
        long cacheRead = 0, long cacheCreation = 0, double? cost = null,
        string source = SpendSources.ChatTurn, bool unreliable = false, string? model = null) => new()
    {
        Timestamp = at,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheCreation,
        CostUsd = cost,
        Source = source,
        UnreliableCost = unreliable,
        Model = model,
    };

    // Фича с одним интервалом активности [from, to).
    private static FeatureActivity Active(string id, DateTime from, DateTime to) =>
        new(id, new List<FeatureActivityInterval> { new(from, to) });

    // ── Деление поровну ──────────────────────────────────────────────────────────────────────

    // 2 активные фичи → по 1/2 (100 → 50).
    [Fact]
    public void Split_ДвеАктивные_ПоПоловине()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100, output: 40, cost: 2.0) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(50);
        share.OutputTokens.Should().Be(20);
        share.CostUsd.Should().Be(1.0);
        share.Turns.Should().Be(1);
        share.Method.Should().Be("approx");
    }

    // Ход ночью, без активных фич → 0 (не распределяется, «остаётся нигде»).
    [Fact]
    public void Split_ХодБезАктивных_Ноль()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100) };
        // Обе фичи активны ДО хода (интервалы заканчиваются до T0) → на момент хода никто не активен.
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-3), T0.AddHours(-2)),
            Active("B", T0.AddHours(-3), T0.AddHours(-2)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(0);
        share.Turns.Should().Be(0, "ход без активных фич не распределяется");
    }

    // Одна активная фича → целиком ей (100 → 100).
    [Fact]
    public void Split_ОднаАктивная_Целиком()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100, cost: 3.0) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-3), T0.AddHours(-2)), // B не активна на T0
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(100);
        share.CostUsd.Should().Be(3.0);
        share.Turns.Should().Be(1);
    }

    // Три фичи, две активны → 1/2 (знаменатель = 2 активные, не 3): 100 → 50.
    [Fact]
    public void Split_ТриФичи_ДвеАктивны_Половина()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)), // активна
            Active("B", T0.AddHours(-1), T0.AddHours(1)), // активна
            Active("C", T0.AddHours(-3), T0.AddHours(-2)), // не активна
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(50, "знаменатель = 2 активные, не 3");
    }

    // ── Граница интервала (half-open: включительно с начала, исключая конец) ────────────────

    // Ход ровно на From → внутри (активна).
    [Fact]
    public void Split_Граница_РовноНаStart_Внутри()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0, T0.AddHours(1)), // From = T0 (ровно)
            Active("B", T0, T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(50, "ход ровно на From входит (включительно с начала)");
    }

    // Ход ровно на To → снаружи (не активна): интервал [From, To) исключает To.
    [Fact]
    public void Split_Граница_РовноНаEnd_Снаружи()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0), // To = T0 (ровно) → T0 снаружи
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(0, "ход ровно на To не входит (half-open, исключая конец)");
        share.Turns.Should().Be(0);
    }

    // ── Пустой вход / кламп / округление ─────────────────────────────────────────────────────

    // Пустой вход (нет ходов) → 0.
    [Fact]
    public void Split_ПустойВход_Ноль()
    {
        var (share, costPartial, hidden) = SharedSpendSplitter.Split(
            new List<FeatureSpendRecord>(),
            new List<FeatureActivity> { Active("A", T0, T0.AddHours(1)) },
            "A", "approx");

        share.InputTokens.Should().Be(0);
        share.Turns.Should().Be(0);
        costPartial.Should().BeFalse();
        hidden.Should().BeEmpty();
    }

    // Отрицательные/нулевые токены → кламп в ≥ 0 (как в итоге Б9b): -100 → 0.
    [Fact]
    public void Split_ОтрицательныеТокены_Кламп()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: -100, output: 0) };
        var activities = new List<FeatureActivity> { Active("A", T0.AddHours(-1), T0.AddHours(1)) };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(0, "отрицательные токены клампаются в 0");
    }

    // Округление денег: decimal, без потери. 1.00 / 3 = 0.333… (не 0.33, не 0).
    [Fact]
    public void Split_ОкруглениеДенег_DecimalБезПотери()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, cost: 1.0) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
            Active("C", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        // 1.0 / 3 = 0.333…; double хранит 0.3333… (не 0.33, не 0).
        share.CostUsd.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    // Сумма долей по ВСЕМ фичам = сумме хода (до округления): 100 → 50+50 = 100.
    [Fact]
    public void Split_СуммаДолей_РавнаХоду()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100, output: 30) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (shareA, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");
        var (shareB, _, _) = SharedSpendSplitter.Split(turns, activities, "B", "approx");

        (shareA.InputTokens + shareB.InputTokens).Should().Be(100, "сумма долей = сумме хода");
        (shareA.OutputTokens + shareB.OutputTokens).Should().Be(30);
    }

    // Деление с остатком (наибольший остаток): 100 / 3 → base=33, r=1; A (idx 0) получает 34.
    // Сумма по A+B+C = 34+33+33 = 100 РОВНО.
    [Fact]
    public void Split_ДелениеСОстатком_НаибольшийОстаток()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 100) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
            Active("C", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(34, "100/3: base=33, r=1, A(idx 0) получает 33+1=34");
    }

    // Деление (наибольший остаток): 101 / 2 → base=50, r=1; A (idx 0) получает 51.
    // Сумма A+B = 51+50 = 101 РОВНО.
    [Fact]
    public void Split_Деление_НаибольшийОстаток_ТочноеРавенство()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, input: 101) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, _, _) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.InputTokens.Should().Be(51, "101/2: base=50, r=1, A(idx 0) получает 50+1=51");
    }

    // ── Надёжность цены (единая точка правды SpendCostReliability) ───────────────────────────

    // Надёжная цена (chat-turn, с ценой) → в долю.
    [Fact]
    public void Split_НадёжнаяЦена_ВДолю()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, cost: 2.0) };
        var activities = new List<FeatureActivity>
        {
            Active("A", T0.AddHours(-1), T0.AddHours(1)),
            Active("B", T0.AddHours(-1), T0.AddHours(1)),
        };

        var (share, costPartial, hidden) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.CostUsd.Should().Be(1.0);
        costPartial.Should().BeFalse();
        hidden.Should().BeEmpty();
    }

    // Free (локальная модель) → надёжный 0, не costPartial.
    [Fact]
    public void Split_Free_НадёжныйНоль()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, cost: 5.0, source: SpendSources.Free) };
        var activities = new List<FeatureActivity> { Active("A", T0.AddHours(-1), T0.AddHours(1)) };

        var (share, costPartial, hidden) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.CostUsd.Should().Be(0, "free → честный 0 (даже если сырая цена 5)");
        costPartial.Should().BeFalse();
        hidden.Should().BeEmpty();
    }

    // Ненадёжная модель (UnreliableCost=true) → стоимость не в долю, costPartial, модель в hidden.
    [Fact]
    public void Split_НенадёжнаяЦена_НеВДолю()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, cost: 2.0, unreliable: true, model: "deepseek-chat") };
        var activities = new List<FeatureActivity> { Active("A", T0.AddHours(-1), T0.AddHours(1)) };

        var (share, costPartial, hidden) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.CostUsd.Should().Be(0, "ненадёжная цена не в долю");
        costPartial.Should().BeTrue();
        hidden.Should().Contain("deepseek-chat");
    }

    // Запись без цены (не free, CostUsd=null) → costPartial, модель в hidden.
    [Fact]
    public void Split_ЗаписьБезЦены_CostPartial()
    {
        var turns = new List<FeatureSpendRecord> { Turn(T0, cost: null, model: "gpt-4o") };
        var activities = new List<FeatureActivity> { Active("A", T0.AddHours(-1), T0.AddHours(1)) };

        var (share, costPartial, hidden) = SharedSpendSplitter.Split(turns, activities, "A", "approx");

        share.CostUsd.Should().Be(0);
        costPartial.Should().BeTrue();
        hidden.Should().Contain("gpt-4o");
    }

    // ── Интервалы активности: approx / journal ────────────────────────────────────────────────

    // Approx: задача [start, done] → интервал активности; ход внутри → активна.
    // done < now: завершённая задача — интервал до done (не до now).
    [Fact]
    public void ApproxActivity_Задача_ИнтервалПокрывает()
    {
        var start = T0.AddHours(-2);
        var done = T0.AddHours(-1); // до now
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = start.AddMinutes(-10), // до старта: clip не режет
                ClaudeStartedAt = start,
                CompletedAt = done,
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(start, done));
    }

    // Approx: идущая задача (CompletedAt=null) → интервал до now.
    [Fact]
    public void ApproxActivity_ИдущаяЗадача_ДоNow()
    {
        var start = T0.AddHours(-1);
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = start.AddMinutes(-10),
                ClaudeStartedAt = start,
                CompletedAt = null,
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(start, T0));
    }

    // Approx: текущее ожидание (AwaitingHumanSince) → интервал [since, now].
    [Fact]
    public void ApproxActivity_ТекущееОжидание_Интервал()
    {
        var since = T0.AddHours(-1);
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = since.AddMinutes(-10),
                AwaitingHumanSince = since,
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(since, T0));
    }

    // Approx: run null (фича не найдена) → нет интервалов.
    [Fact]
    public void ApproxActivity_Null_НетИнтервалов()
    {
        SharedSpendSplitter.ApproxActivity(null, T0).Should().BeEmpty();
    }

    // Journal: started → status Done → интервал [started, done] (done до now — нереалистично
    // иначе: EndOf обрезает конец до now, и done>now дал бы [start, now], а не [start, done]).
    [Fact]
    public void JournalActivity_StartedDone_Интервал()
    {
        var start = T0.AddHours(-2); // 10:00
        var done = T0.AddHours(-1);  // 11:00 (до now=12:00)
        var journal = new FeatureJournalReadResult
        {
            Entries = new List<FeatureJournalEntry>
            {
                new() { Kind = "created", At = start.AddMinutes(-10), TaskId = "t1" },
                new() { Kind = "started", At = start, TaskId = "t1" },
                new() { Kind = "status", At = done, TaskId = "t1", From = "InProgress", To = "Done" },
            },
            StartsWithCreated = true,
            Truncated = false,
        };

        var intervals = SharedSpendSplitter.JournalActivity(journal, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(start, done));
    }

    // Journal: started → (без Done, до now) → интервал [started, now].
    [Fact]
    public void JournalActivity_StartedБезDone_ДоNow()
    {
        var start = T0.AddHours(-1);
        var journal = new FeatureJournalReadResult
        {
            Entries = new List<FeatureJournalEntry>
            {
                new() { Kind = "created", At = start.AddMinutes(-10), TaskId = "t1" },
                new() { Kind = "started", At = start, TaskId = "t1" },
            },
            StartsWithCreated = true,
            Truncated = false,
        };

        var intervals = SharedSpendSplitter.JournalActivity(journal, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(start, T0));
    }

    // Journal: waiting → waiting_cleared → интервал [waiting, cleared].
    [Fact]
    public void JournalActivity_Waiting_Интервал()
    {
        var w = T0.AddHours(-1);
        var cleared = T0.AddHours(-0.5);
        var journal = new FeatureJournalReadResult
        {
            Entries = new List<FeatureJournalEntry>
            {
                new() { Kind = "created", At = w.AddMinutes(-10), TaskId = "t1" },
                new() { Kind = "waiting", At = w, TaskId = "t1" },
                new() { Kind = "waiting_cleared", At = cleared, TaskId = "t1" },
            },
            StartsWithCreated = true,
            Truncated = false,
        };

        var intervals = SharedSpendSplitter.JournalActivity(journal, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(w, cleared));
    }
    // Approx: остановленная задача (ExecutorStoppedAt) активна только до остановки, не до now
    // (решение Александра, п.3; мутация М3 — не учитывать ExecutorStoppedAt).
    [Fact]
    public void ApproxActivity_ОстановленнаяЗадача_ДоОстановки_НеДоNow()
    {
        var start = T0.AddHours(-3);
        var stopped = T0.AddHours(-2);
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = start.AddMinutes(-10),
                ClaudeStartedAt = start,
                CompletedAt = null,
                ExecutorStoppedAt = stopped,
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(start, stopped),
                "остановленная задача перестаёт быть активной в момент остановки");
    }

    // Approx: ожидание человека ДОЧЕРНЕЙ задачи фичи (не эпика) тоже активность
    // (мутация М9 — ожидание только у эпика).
    [Fact]
    public void ApproxActivity_ОжиданиеДочернейЗадачи_Интервал()
    {
        var since = T0.AddHours(-1);
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = since.AddMinutes(-10),
            },
            Steps = new List<TaskItem>
            {
                new() { Id = "step", Role = TaskRoles.Step, FeatureId = "epic", AwaitingHumanSince = since },
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(since, T0));
    }

    // Journal с clipFrom: интервал, начавшийся ДО создания фичи, обрезается по CreatedAt
    // (минор 3: знаменатели режимов однородны, как у approx).
    [Fact]
    public void JournalActivity_ClipFrom_ОбрезаетНачалоДоСоздания()
    {
        var start = T0.AddHours(-3);
        var created = T0.AddHours(-2);
        var done = T0.AddHours(-1);
        var journal = new FeatureJournalReadResult
        {
            Entries = new List<FeatureJournalEntry>
            {
                new() { Kind = "created", At = start.AddMinutes(-10), TaskId = "t1" },
                new() { Kind = "started", At = start, TaskId = "t1" },
                new() { Kind = "status", At = done, TaskId = "t1", From = "InProgress", To = "Done" },
            },
            StartsWithCreated = true,
            Truncated = false,
        };

        var intervals = SharedSpendSplitter.JournalActivity(journal, T0, created);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(created, done));
    }
    // Approx: клип по epic.CreatedAt — активность не начинается до создания фичи. Это и есть
    // граница окна (М5): ход ДО создания участницы ей не достаётся, потому что она неактивна
    // (отдельный from=epic.CreatedAt избыточен и ненаблюдаем: клип уже делает фичу неактивной).
    [Fact]
    public void ApproxActivity_Клип_ПоCreatedAt_НеАктивнаДоСоздания()
    {
        var start = T0.AddHours(-3);
        var created = T0.AddHours(-2);
        var done = T0.AddHours(-1);
        var run = new FeatureRun
        {
            Epic = new TaskItem
            {
                Id = "epic",
                Role = TaskRoles.Feature,
                CreatedAt = created,
                ClaudeStartedAt = start,
                CompletedAt = done,
            },
        };

        var intervals = SharedSpendSplitter.ApproxActivity(run, T0);

        intervals.Should().ContainSingle()
            .Which.Should().Be(new FeatureActivityInterval(created, done));

        // Следствие для деления: ход ДО создания B (start..created) достаётся только активной A.
        var turn = Turn(T0.AddHours(-2.5), input: 100);
        var a = Active("A", start, done);
        var b = new FeatureActivity("B", intervals);
        var (shareA, _, _) = SharedSpendSplitter.Split([turn], [a, b], "A", "approx");
        var (shareB, _, _) = SharedSpendSplitter.Split([turn], [a, b], "B", "approx");
        shareA.InputTokens.Should().Be(100);
        shareB.InputTokens.Should().Be(0);
    }
}
