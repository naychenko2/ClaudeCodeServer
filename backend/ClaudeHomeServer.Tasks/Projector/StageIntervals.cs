namespace ClaudeHomeServer.Services.Tasks.Projector;

// Публичные типы интервалов этапов фичи (GC-B6, «Хронология»/Гант).
//
// Раньше типы жили вложенными в internal static class FeatureStageIntervals, а поле
// intervalsByStage — internal-свойством FeatureTimeline: наружу (stats.timeline) они уходили
// JSON-ом, но типы были недоступны потребителям контракта, а параметр публичной записи не мог
// ссылаться на internal-тип (CS0051). Теперь это обычные публичные типы вертикали Tasks.

// Ход журнала внутри интервала: время, исход и модель — как записаны.
public sealed record StageTurn(
    string TaskId, DateTime From, DateTime To, string? Outcome, string? Model);

// Интервал стадии. From/To — границы, Minutes — FeatureTimeMath.Minutes(From, To).
// Live=true — интервал дотянут до «сейчас» (в нём живой хвост идущей задачи).
// LastOutcome — исход ПОСЛЕДНЕГО хода интервала, если он cancelled/interrupted/error,
// иначе null. TaskIds — id задач, чьи ходы вошли (уникальные, по первому появлению).
// Turns — ходы интервала в хронологическом порядке (живой хвост — не ход, его нет).
public sealed record StageInterval(
    DateTime From, DateTime To, int Minutes, bool Live,
    string? LastOutcome, List<string> TaskIds, List<StageTurn> Turns);
