using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Волна 6: «Сводка состояния фичи» (GET features/{id} → brief), шаги B1a+B2b ────────────────
// Чистая функция над УЖЕ посчитанными объектами страницы фичи (FeatureRun, FeatureTimeline и
// выборкой FeatureTypical): ни чтения стора, ни журнала, ни собственного вывода состояния.
// kind — словарь от run.State (+ Hold.Reason), now — run.Now[0] либо Attention, done — счётчики
// run, elapsed — работа по часам стены из timeline.Totals.
//
// B2b: estimate — формула разреза п.3 над чужой выборкой: вызывающий передаёт тот же typical,
// что пошёл в stats; своего прохода по смерженным фичам здесь нет (EstimateOf).
//
// B1a: без «голоса» — note всегда null (шаг Б-3); queue всегда null (за скобками этой волны).
public static class FeatureBrief
{
    // Категории сводки (brief.kind, JSON camelCase-совместимые строки как в контракте разреза п. 4).
    public static class Kinds
    {
        public const string Running = "running";
        public const string WaitingYou = "waiting_you";
        public const string Error = "error";
        public const string Paused = "paused";
        public const string Queued = "queued";
        public const string Draft = "draft";
        public const string Idle = "idle";
        public const string Closed = "closed";
    }

    // Максимум элементов brief.next.
    public const int MaxNext = 3;

    // Коэффициент «на ревью» (разрез п.3): виток ревью стоит половины работы шага. Это константа,
    // не настройка: тест считает числа фикстуры по 0.5 (мутация 0.5 → 1 даёт красный).
    public const double ReviewCoefficient = 0.5;

    // estimate.source: единственный источник v1 — история проекта (разрез п.3).
    public const string EstimateSource = "history";

    // «Сейчас»: = run.Now[0] либо Attention (без Now).
    public sealed record BriefNow(
        string Stage, string? TaskId, string? PersonaId, string? StepRef, string? Lane, DateTime? Since);

    // «Сделано»: счётчики run как есть.
    public sealed record BriefDone(int StepsAccepted, int StepsTotal, int Loops, string PlanState);

    // «Дальше»: ближайшие не начатые стадии.
    public sealed record BriefNext(string Stage, string? PersonaId);

    // estimate (B2b): «осталось», минуты — формула EstimateOf; p25–p75 — та же формула по тем же
    // квантилям выборки; sampleSize — n шаговой выборки (FeatureTypical.StepCount).
    public sealed record BriefEstimate(
        int RemainingMinutes, int P25, int P75, string Source, int SampleSize);
    // Заглушки: queue — шов очереди (за скобками этой волны), note — голос оркестратора (шаг Б-3).
    public sealed record BriefQueue(int Position, int Total);
    public sealed record BriefNote(
        string Text, DateTime At, string? ByPersonaId, bool ByUser, string StageAtWrite, bool Stale);

    // brief: контракт ответа (разрез п. 6).
    public sealed record Brief(
        string Kind,
        BriefNow? Now,
        string? Reason,
        BriefDone Done,
        List<BriefNext> Next,
        int? ElapsedMinutes,
        BriefEstimate? Estimate,
        BriefQueue? Queue,
        BriefNote? Note,
        DateTime ComputedAt);

    // Таблица kind: FeatureStates → категория. paused расщепляется по Hold.Reason (см. KindOf).
    // Единая точка правды; тест перебирает константы FeatureStates рефлексией, поэтому новое
    // состояние без строки здесь даёт красный тест (и исключение в Build).
    internal static readonly IReadOnlyDictionary<string, string> KindByState = new Dictionary<string, string>
    {
        [FeatureStates.Running] = Kinds.Running,
        [FeatureStates.Merging] = Kinds.Running,
        [FeatureStates.Waiting] = Kinds.WaitingYou,
        [FeatureStates.MergePending] = Kinds.WaitingYou,
        [FeatureStates.Stopped] = Kinds.Error,
        [FeatureStates.Paused] = Kinds.Paused,
        [FeatureStates.Draft] = Kinds.Draft,
        [FeatureStates.NotStarted] = Kinds.Idle,
        [FeatureStates.Idle] = Kinds.Idle,
        [FeatureStates.Merged] = Kinds.Closed,
        [FeatureStates.Closed] = Kinds.Closed,
    };

    // timeline — null, если статистики нет (elapsedMinutes тогда null). typical — чужая выборка,
    // тот же экземпляр, что пошёл в stats; null или без шаговых метрик — estimate null. Состояние
    // вне таблицы — InvalidOperationException: молча подставлять категорию нельзя (фронт сам не маппит).
    public static Brief Build(FeatureRun run, FeatureTimeline? timeline,
        FeatureTypical.TypicalFeatureStats? typical, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(run);

        var kind = KindOf(run);

        return new Brief(
            kind,
            NowOf(run),
            ReasonOf(run, kind),
            new BriefDone(run.StepsAccepted, run.StepsTotal, run.Verdicts.Loops, run.StepProgress.PlanState),
            NextOf(run),
            timeline?.Totals.WorkWallMinutes,
            EstimateOf(run, typical, kind),
            Queue: null,
            Note: null,
            now);
    }

    // paused + Hold.Reason = queue → queued («в очереди»), иначе paused («на паузе»).
    private static string KindOf(FeatureRun run)
    {
        if (!KindByState.TryGetValue(run.State, out var kind))
            throw new InvalidOperationException(
                $"Состояние фичи «{run.State}» не описано в таблице brief.kind (FeatureBrief.KindByState).");
        return run.State == FeatureStates.Paused && run.Hold?.Reason == FeatureHoldReasons.Queue
            ? Kinds.Queued
            : kind;
    }

    // «Сейчас»: первая запись Now; иначе тревога (Attention). Стадия тревоги — активная стадия
    // конвейера (у Attention своего ключа стадии нет). Ни того ни другого — null.
    private static BriefNow? NowOf(FeatureRun run)
    {
        var first = run.Now.FirstOrDefault();
        if (first is not null)
            return new BriefNow(first.Stage, first.TaskId, first.PersonaId, first.StepRef,
                LaneOf(run, first.StepRef), first.StartedAt);

        var attention = run.Attention;
        if (attention is null) return null;
        var stage = run.Stages.FirstOrDefault(s => s.State == "active")?.Key ?? "";
        return new BriefNow(stage, attention.TaskId, attention.PersonaId, null, null, attention.Since);
    }

    // Линия — у готовой цепочки шага (FeatureStepChain.Lane); шага нет — null.
    private static string? LaneOf(FeatureRun run, string? stepRef) =>
        stepRef is null ? null : run.StepChains.FirstOrDefault(c => c.StepRef == stepRef)?.Lane;

    // Причина: Attention.Reason; у паузы/очереди без тревоги — код Hold.Reason (manual/queue).
    private static string? ReasonOf(FeatureRun run, string kind)
    {
        if (!string.IsNullOrEmpty(run.Attention?.Reason)) return run.Attention!.Reason;
        return kind is Kinds.Paused or Kinds.Queued ? run.Hold?.Reason : null;
    }

    // «Дальше»: стадии ПОСЛЕ активной со state pending (skipped/absent пропускаются), ≤ MaxNext.
    // Нет активной стадии (всё пройдено) — пусто. Исполнитель — первый из PersonaIds стадии.
    private static List<BriefNext> NextOf(FeatureRun run)
    {
        var activeIndex = run.Stages.FindIndex(s => s.State == "active");
        if (activeIndex < 0) return [];
        return run.Stages
            .Skip(activeIndex + 1)
            .Where(s => s.State == "pending")
            .Take(MaxNext)
            .Select(s => new BriefNext(s.Key, s.PersonaIds.FirstOrDefault()))
            .ToList();
    }

    // estimate (B2b, разрез п.3): «осталось» ≈ stepMinutes × (Work + NotCreated + 0.5 × Review) + tailMinutes,
    // где stepMinutes/tailMinutes — медианы выборки FeatureTypical (чужой, см. Build), Work/NotCreated/
    // Review — корзины шагов run.StepProgress. Лента p25–p75 — та же формула по тем же квантилям
    // выборки: k ≥ 0 и p25 ≤ медиана ≤ p75 по каждой метрике — порядок ленты не инвертируется,
    // округление одно по концу (Math.Round, как хвостовые числа FeatureTypical).
    // Оценки нет в трёх случаях (разрез п.3 «Когда оценки нет»): состояние queued/paused/closed/draft
    // (часы стоят или фича не идёт), план не нарезан (PlanState = pending — знаменатель шагов
    // неизвестен), выборка не построена (n < 3 — FeatureTypical отдаёт шаговые метрики null).
    private static BriefEstimate? EstimateOf(FeatureRun run,
        FeatureTypical.TypicalFeatureStats? typical, string kind)
    {
        if (kind is Kinds.Queued or Kinds.Paused or Kinds.Closed or Kinds.Draft) return null;
        if (run.StepProgress.PlanState == "pending") return null;
        var steps = typical?.StepMinutes;
        var tails = typical?.TailMinutes;
        if (steps is null || tails is null) return null;

        double k = run.StepProgress.Work + run.StepProgress.NotCreated
            + ReviewCoefficient * run.StepProgress.Review;
        return new BriefEstimate(
            (int)Math.Round(steps.Median * k + tails.Median),
            (int)Math.Round(steps.P25 * k + tails.P25),
            (int)Math.Round(steps.P75 * k + tails.P75),
            EstimateSource,
            typical!.StepCount);
    }
}
