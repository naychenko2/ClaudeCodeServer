using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Метрики этапа (D1b, мини-разрез D0): FeatureStageMetrics.Build — доли исполнителя / ревью /
// человека. Время параметром (now) — детерминизм, границы на целых минутах.
public class FeatureStageMetricsTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 8, 0, 0);
    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private static TaskItem Stage(DateTime? completedAt = null, DateTime? startedAt = null) => new()
    {
        Id = "t", FeatureId = "epic", Role = TaskRoles.Step, CreatedAt = T0,
        Status = completedAt is null ? TaskItemStatus.InProgress : TaskItemStatus.Done,
        ClaudeStartedAt = startedAt ?? T0, CompletedAt = completedAt,
    };

    private static TaskItem Review(string id = "r", DateTime? completedAt = null, DateTime? startedAt = null,
        params string[] reviewOf) => new()
    {
        Id = id, FeatureId = "epic", Role = TaskRoles.Review, CreatedAt = At(90),
        Status = completedAt is null ? TaskItemStatus.InProgress : TaskItemStatus.Done,
        ClaudeStartedAt = startedAt, CompletedAt = completedAt,
        ReviewOf = reviewOf.Length > 0 ? [.. reviewOf] : ["t"],
        Verification = completedAt is null ? null : new TaskVerification { Verdict = TaskVerdicts.Returned },
    };

    private static FeatureJournalEntry E(string kind, DateTime at, string? taskId = "t",
        DateTime? from = null, DateTime? to = null, string? outcome = null, string? model = null,
        string? reason = null) => new()
    {
        At = at, Kind = kind, TaskId = taskId, StartedAt = from, EndedAt = to, Outcome = outcome,
        Model = model, Reason = reason,
    };

    private static FeatureJournalEntry Turn(string taskId, int fromMin, int toMin, string? outcome = "success",
        string? model = null) =>
        E("turn", At(toMin), taskId, At(fromMin), At(toMin), outcome, model);

    private static FeatureJournalReadResult Journal(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true, Truncated = false,
        Entries = [E("created", T0, "epic"), .. entries],
    };

    private static FeatureSpendRecord Spend(string taskId, string? initiator = "agent", long input = 100,
        bool daily = false, double? cost = 1.0, string source = SpendSources.ChatTurn, string? model = "claude-opus-5-5") => new()
    {
        Timestamp = At(10), TaskId = taskId, Initiator = initiator, InputTokens = input, OutputTokens = 10,
        CacheReadTokens = 5, CacheCreationTokens = 2, CostUsd = cost, Source = source, Model = model,
        IsDaily = daily, UnreliableCost = model?.Contains("deepseek") == true,
    };

    private static readonly DateTime Now = At(600);

    [Fact]
    public void Build_ДолиИсполнителяРевьюИЧеловекаРаздельно()
    {
        var t = Stage(completedAt: At(120));
        var r = Review(completedAt: At(180));
        var journal = Journal(
            Turn("t", 0, 30, model: "claude-opus-5-5"), Turn("t", 31, 60),         // склейка 1 мин → [0,60] = 60
            E("waiting", At(70)), E("waiting_cleared", At(80)),                      // человек 10 на T
            Turn("r", 100, 120, "failed"),                                           // ревью 20, один упавший
            E("waiting", At(120), "r"), E("waiting_cleared", At(140), "r"));         // человек 20 на R
        var m = FeatureStageMetrics.Build(t, [r], journal,
            [Spend("t", "human"), Spend("t", "agent"), Spend("r", "agent")], Now);

        m.Mode.Should().Be("journal");
        m.Window.Live.Should().BeFalse();
        m.Window.ElapsedMinutes.Should().Be(180);
        m.Executor.Minutes.Should().Be(60);
        m.Executor.Turns.Should().Be(2);
        m.Executor.ErrorTurns.Should().Be(0);
        m.Executor.Tokens!.Input.Should().Be(200);
        m.Executor.CostUsd.Should().Be(2.0);
        m.Review!.Minutes.Should().Be(20);
        m.Review.Turns.Should().Be(1);
        m.Review.ErrorTurns.Should().Be(1);
        m.Review.Tokens!.Input.Should().Be(100);
        m.Review.Tasks.Should().ContainSingle().Which.Should().Be(new StageMetricsReviewTask("r", TaskVerdicts.Returned, 1));
        m.Human.Minutes.Should().Be(30);
        m.Human.Waits.Should().Be(2);
        m.Human.Replies.Should().Be(1, "один ход начат человеком");
        // простой = окно − объединение долей: 180 − (60 + 20 + 30) = 70
        m.IdleMinutes.Should().Be(70);
        m.SpendFrom.Should().Be("2026-11-12");
    }

    [Fact]
    public void Build_ОжиданиеСильнееРаботы()
    {
        var t = Stage(completedAt: At(60));
        var m = FeatureStageMetrics.Build(t, [], Journal(
            Turn("t", 0, 60), E("waiting", At(30)), E("waiting_cleared", At(45))), null, Now);

        m.Executor.Minutes.Should().Be(45, "15 минут ожидания внутри хода — не работа");
        m.Human.Minutes.Should().Be(15);
        m.IdleMinutes.Should().Be(0);
        m.Review.Should().BeNull("задач ревью нет — это не «0 минут»");
    }

    [Fact]
    public void Build_ОчередьСильнееИРаботыИОжидания()
    {
        var t = Stage(completedAt: At(60));
        var m = FeatureStageMetrics.Build(t, [], Journal(
            Turn("t", 0, 60),
            E("waiting", At(10)), E("waiting_cleared", At(30)),
            E("held", At(20), "epic", reason: FeatureHoldReasons.Queue), E("hold_cleared", At(40), "epic")),
            null, Now);

        m.Human.Minutes.Should().Be(10, "ожидание 10…30 минус очередь 20…40");
        m.Executor.Minutes.Should().Be(30, "работа 0…60 минус ожидание 10…30 и очередь 20…40");
    }

    [Fact]
    public void Build_ЖивойХвостДотянутДоNow()
    {
        var t = Stage(startedAt: T0);
        var m = FeatureStageMetrics.Build(t, [], Journal(), null, At(30));

        m.Window.Live.Should().BeTrue();
        m.Executor.Live.Should().BeTrue();
        m.Executor.Minutes.Should().Be(30);
        m.Executor.Turns.Should().Be(0);
    }

    [Fact]
    public void Build_ОбщееРевьюНеДелитсяАПоказываетсяЦеликом()
    {
        var t = Stage(completedAt: At(60));
        var r = Review(completedAt: At(120), reviewOf: ["t", "u"]);
        var m = FeatureStageMetrics.Build(t, [r], Journal(Turn("r", 90, 120)), null, Now);

        m.Review!.Minutes.Should().Be(30);
        m.Review.Tasks!.Single().SharedWith.Should().Be(2);
    }

    [Fact]
    public void Build_ЖурналНеполныйДаётApprox()
    {
        var t = Stage(completedAt: At(60));
        var r = Review(completedAt: At(120));
        var truncated = new FeatureJournalReadResult { StartsWithCreated = true, Truncated = true, Entries = [Turn("t", 0, 5)] };
        foreach (var journal in new FeatureJournalReadResult?[] { null, truncated })
        {
            var m = FeatureStageMetrics.Build(t, [r], journal, [Spend("t", "human"), Spend("r")], Now);

            m.Mode.Should().Be("approx");
            m.Executor.Minutes.Should().Be(60, "[ClaudeStartedAt, CompletedAt]");
            m.Executor.Turns.Should().BeNull();
            m.Executor.ErrorTurns.Should().BeNull();
            m.Review!.Minutes.Should().BeNull();
            m.Review.Turns.Should().BeNull();
            m.Human.Minutes.Should().BeNull();
            m.Human.Waits.Should().BeNull();
            m.IdleMinutes.Should().BeNull();
            m.Executor.Tokens!.Input.Should().Be(100, "токены от журнала не зависят");
            m.Human.Replies.Should().Be(1);
        }
    }

    [Fact]
    public void Build_ОжидаетсяСейчасБерётсяИзПоляЗадачи()
    {
        var t = Stage();
        t.AwaitingHumanSince = At(5);
        t.AwaitingHumanReason = "нужен ответ";
        var m = FeatureStageMetrics.Build(t, [], null, null, At(30));
        m.Human.WaitingNow.Should().Be(new StageMetricsWaitingNow("t", At(5), "нужен ответ"));
    }

    [Fact]
    public void Build_ДневнаяЗаписьДаётRepliesNullТокиЕсть()
    {
        var m = FeatureStageMetrics.Build(Stage(completedAt: At(60)), [], Journal(),
            [Spend("t", "human"), Spend("t", null, daily: true)], Now);

        m.Human.Replies.Should().BeNull();
        m.Executor.Tokens!.Input.Should().Be(200);
    }

    [Fact]
    public void Build_SpendВыключенИлиЗаписейНет_ПоляNullБез500()
    {
        var t = Stage(completedAt: At(60));
        foreach (var records in new IReadOnlyList<FeatureSpendRecord>?[] { null, [] })
        {
            var m = FeatureStageMetrics.Build(t, [], Journal(), records, Now);
            m.Executor.Tokens.Should().BeNull();
            m.Executor.CostUsd.Should().BeNull();
            m.Human.Replies.Should().BeNull();
            m.SpendFrom.Should().BeNull();
        }
    }

    [Fact]
    public void Build_НенадёжнаяЦенаДаётCostPartialИСкрытуюМодель()
    {
        var m = FeatureStageMetrics.Build(Stage(completedAt: At(60)), [], Journal(),
            [Spend("t", model: "deepseek-v4", cost: 5.0), Spend("t", cost: 1.5)], Now);

        m.Executor.CostPartial.Should().BeTrue();
        m.Executor.HiddenCostModels.Should().Equal("deepseek-v4");
        m.Executor.CostUsd.Should().Be(1.5);
        m.Executor.Tokens!.Input.Should().Be(200, "токены ненадёжной цены в сумму идут");
    }

    [Fact]
    public void Build_ХодБезПаспортаСчитаетсяВХодахНоНеВМинутах()
    {
        var m = FeatureStageMetrics.Build(Stage(completedAt: At(60)), [], Journal(
            E("turn", At(20), outcome: "failed"), Turn("t", 0, 10)), null, Now);

        m.Executor.Turns.Should().Be(2);
        m.Executor.ErrorTurns.Should().Be(1, "упавший до паспорта ход — ровно ради errorTurns");
        m.Executor.Minutes.Should().Be(10);
    }

    // И7: errorTurns по каждому значению реального словаря outcome. Мутация «список вместо
    // отрицания» (только cancelled/interrupted/error) → строки failed/crashed/local_* краснеют.
    [Theory]
    [InlineData("failed", 1)]
    [InlineData("egress_down", 1)]
    [InlineData("interrupted", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("crashed", 1)]
    [InlineData("local_down", 1)]
    [InlineData("local_busy", 1)]
    [InlineData("local_file_missing", 1)]
    [InlineData("window_1m_unavailable", 1)]
    [InlineData("success", 0)]
    [InlineData(null, 0)]
    public void Build_ErrorTurns_ПоСловарюИсходов(string? outcome, int expected)
    {
        var m = FeatureStageMetrics.Build(Stage(completedAt: At(60)), [], Journal(Turn("t", 0, 10, outcome)), null, Now);
        m.Executor.ErrorTurns.Should().Be(expected);
        m.Executor.Turns.Should().Be(1);
    }

    // Сторож И6: минуты исполнителя == сумма интервалов стадии «Хронологии» (одна функция склейки).
    // Зазор 1 мин склеен, 5 мин — разрыв в обоих; разрыв уходит в простой.
    [Fact]
    public void Build_И6_МинутыИсполнителяРавныСуммеИнтерваловСтадии()
    {
        var epic = new TaskItem { Id = "epic", Role = TaskRoles.Feature, CreatedAt = T0, Status = TaskItemStatus.Todo };
        var t = Stage(completedAt: At(30));
        var journal = Journal(Turn("t", 0, 10), Turn("t", 11, 20), Turn("t", 25, 30));

        var run = FeatureProjector.BuildCore(epic, [epic, t]);
        var intervals = FeatureStageIntervals.Build(run, journal, Now)!["prod"];
        var m = FeatureStageMetrics.Build(t, [], journal, null, Now);

        intervals.Should().HaveCount(2, "зазор 1 мин склеен, 5 мин — разрыв");
        m.Executor.Minutes.Should().Be(intervals.Sum(i => i.Minutes)).And.Be(25);
        m.IdleMinutes.Should().Be(5);
    }

    // Сторож И4: ожидание человека этапа == waitingMinutes таймлайна. Оговорки фикстуры (иначе
    // таймлайн шире доли «человек» по составу): нет device_wait, нет ожидания на эпике, нет упавших
    // ходов (вид error сильнее waiting), границы на целых минутах (у таймлайна наибольший остаток,
    // у доли — CeilMinutes), у эпика и T одно создание. Различие по составу ошибок остаётся и после
    // исправления a6b5a2d0 (с обеих сторон один предикат IsFailedTurn): таймлайн считает ошибками
    // ещё точки stopped и только ходы внутри окна фичи [from, to), метрики этапа — все ходы стадии.
    // В этой фикстуре ни того, ни другого нет — сравнение по ожиданию честно.
    // Вторая проверка — с FeatureTypical: см. FeatureTypicalTests.Typical_ЭпизодСОчередьюДаётNetМинуты
    // (одна функция WaitingEpisodes и один вычет очереди).
    [Fact]
    public void Build_И4_ОжиданиеЧеловекаРавноWaitingMinutesТаймлайна()
    {
        var epic = new TaskItem { Id = "epic", Role = TaskRoles.Feature, CreatedAt = T0, Status = TaskItemStatus.Todo };
        var t = Stage();
        var journal = Journal(
            Turn("t", 0, 10),
            E("waiting", At(20)), E("waiting_cleared", At(40)),
            E("held", At(30), "epic", reason: FeatureHoldReasons.Queue), E("hold_cleared", At(50), "epic"),
            E("waiting", At(45)), E("waiting_cleared", At(65)));

        var timeline = FeatureTimeline.Build(FeatureProjector.BuildCore(epic, [epic, t]), journal, Now);
        var m = FeatureStageMetrics.Build(t, [], journal, null, Now);

        m.Human.Minutes.Should().Be(25, "10 + 15 минут за вычетом очереди");
        m.Human.Minutes.Should().Be(timeline.Totals.WaitingMinutes);
    }
}
