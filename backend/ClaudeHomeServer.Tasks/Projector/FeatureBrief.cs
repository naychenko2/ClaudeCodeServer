using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Волна 6: «Сводка состояния фичи» (GET features/{id} → brief), шаг B1a ─────────────────────
// Чистая функция над УЖЕ посчитанными объектами страницы фичи (FeatureRun и FeatureTimeline):
// ни чтения стора, ни журнала, ни собственного вывода состояния. Новой арифметики нет —
// kind — словарь от run.State (+ Hold.Reason), now — run.Now[0] либо Attention, done — счётчики
// run, elapsed — работа по часам стены из timeline.Totals.
//
// B1a: без «голоса» и оценки — note, estimate и queue всегда null (их заполнят шаги Б-2/Б-3/очередь).
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

    // «Сейчас»: = run.Now[0] либо Attention (без Now).
    public sealed record BriefNow(
        string Stage, string? TaskId, string? PersonaId, string? StepRef, string? Lane, DateTime? Since);

    // «Сделано»: счётчики run как есть.
    public sealed record BriefDone(int StepsAccepted, int StepsTotal, int Loops, string PlanState);

    // «Дальше»: ближайшие не начатые стадии.
    public sealed record BriefNext(string Stage, string? PersonaId);

    // Заглушки полей следующих шагов: типы появятся вместе с их расчётом, в B1a — всегда null.
    public sealed record BriefEstimate(
        int RemainingMinutes, int P25, int P75, string Source, int SampleSize);
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

    // timeline — null, если статистики нет (elapsedMinutes тогда null). Состояние вне таблицы —
    // InvalidOperationException: молча подставлять категорию нельзя (фронт сам не маппит).
    public static Brief Build(FeatureRun run, FeatureTimeline? timeline, DateTime now)
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
            Estimate: null,
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
}
