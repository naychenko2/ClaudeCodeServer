namespace ClaudeHomeServer.Services.Tasks.Projector;

// Публичный контракт блока metrics экрана этапа (D1b, мини-разрез D0 §4): время, ходы, токены и
// ожидание человека раздельно по исполнителю, ревью и человеку. Считает чистая функция
// FeatureStageMetrics.Build (internal), тут — только форма ответа. camelCase, null-поля
// сериализуются явно: фронт отличает «не считали» от нуля.

// Mode: journal — журнал фичи полный; approx — журнал неполный (минуты исполнителя приближённые,
// у ревью и человека минуты — null, turns/errorTurns/waits — null). IdleMinutes — окно минус
// объединение трёх долей (не остаток от суммы: ревью идёт параллельно доработке). SpendFrom —
// с какой даты в срезе есть данные о расходе (yyyy-MM-dd); null — Spend выключен или записей нет.
public sealed record StageMetrics(
    string Mode, StageMetricsWindow Window, StageMetricsShare Executor, StageMetricsShare? Review,
    StageMetricsHuman Human, int? IdleMinutes, string? SpendFrom);

// Окно этапа [from, to]: to — now, пока этап жив (задача или ревью не Done), иначе конец последней из них.
public sealed record StageMetricsWindow(DateTime From, DateTime To, bool Live, int ElapsedMinutes);

// Доля исполнителя или ревью. Minutes — по правилу интервала стадии (склейка зазора ≤ GlueGap),
// без ожидания человека и очереди. Turns — все записи turn группы, ErrorTurns — из них упавшие
// (FeatureTimeMath.IsFailedTurn). Tokens/CostUsd — null, когда Spend выключен или записей нет;
// CostPartial/HiddenCostModels — как у stats.spend. Tasks — только у ревью (иначе null).
public sealed record StageMetricsShare(
    int? Minutes, bool Live, int? Turns, int? ErrorTurns,
    StageMetricsTokens? Tokens, double? CostUsd, bool CostPartial,
    IReadOnlyList<string> HiddenCostModels, IReadOnlyList<string> Models,
    IReadOnlyList<StageMetricsReviewTask>? Tasks);

public sealed record StageMetricsTokens(long Input, long Output, long CacheRead, long CacheCreation);

// Доля человека. Minutes — эпизоды ожидания по задаче этапа и её ревью, объединённые, минус очередь;
// Waits — число эпизодов, начавшихся в окне; Replies — ходы, начатые человеком (по расходу), null —
// Spend выключен, записей нет или в окне дневные агрегаты. WaitingNow — ожидание идёт сейчас.
public sealed record StageMetricsHuman(
    int? Minutes, int? Waits, int? Replies, StageMetricsWaitingNow? WaitingNow);

public sealed record StageMetricsWaitingNow(string TaskId, DateTime Since, string? Reason);

// Задача ревью этапа. SharedWith — на сколько задач смотрит это ревью (общее ревью не делим,
// показываем целиком); Verdict — Verification.Verdict, null — ещё не вынесен.
public sealed record StageMetricsReviewTask(string TaskId, string? Verdict, int SharedWith);
