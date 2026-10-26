using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Волна 6: «Сводка состояния фичи» (GET features/{id} → brief), шаги B1a+B2b ────────────────
// Чистая функция над УЖЕ посчитанными объектами страницы фичи (FeatureRun, FeatureTimeline и
// выборкой FeatureTypical): ни чтения стора, ни журнала, ни собственного вывода состояния.
// kind — словарь от run.State (+ Hold.Reason, run.Closed), now — run.Now[0] либо Attention,
// done — счётчики run, elapsed — работа по часам стены из timeline.Totals.
//
// B2b: estimate — формула разреза п.3 над чужой выборкой: вызывающий передаёт тот же typical,
// что пошёл в stats; своего прохода по смерженным фичам здесь нет (EstimateOf).
//
// B5: note — голос эпика из TaskItem.Note (NoteOf/StaleOf, разрез п.1–2); queue всегда null
// (за скобками этой волны).
//
// B6 (дефект f2da9354): updatedAt — время последнего ЗАПИСАННОГО события состояния фичи (UpdatedAtOf),
// отдельное от computedAt: computedAt — момент этого ответа («насколько свеж снимок»), updatedAt —
// время состояния («насколько old показано»); снятие паузы/ожидания состояние меняет, но события не
// создаёт — значение откатывается к предыдущему событию. Оба поля остаются: они отвечают на разные вопросы.
//
// B8 (MAJOR-1 ревью R-F8): queueWaitMinutes — время в очереди для kind=queued (QueueWaitOf): от
// момента постановки удержания (FeatureHold.Since) до now, НЕ из work-интервалов (у фичи в очереди
// elapsed = WorkWallMinutes = 0, и подпись «в очереди N мин» была бы недостижима). Null — фича
// не в очереди.
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
        public const string Merged = "merged";
        public const string Closed = "closed";

        // Все категории разом (B15): значение, записанное в голос, может быть САМОЙ категорией
        // (новый штамп) или состоянием (голоса до B15 — достраиваются таблицей KindByState).
        internal static readonly IReadOnlySet<string> All = new HashSet<string>
        {
            Running, WaitingYou, Error, Paused, Queued, Draft, Idle, Merged, Closed,
        };
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
    // Заглушка: queue — шов очереди (за скобками этой волны).
    public sealed record BriefQueue(int Position, int Total);
    // BriefNote — контракт голоса наружу (разрез п.6): ровно эти поля. StateAtWrite хранимого
    // FeatureNote наружу не уходит (нужен только StaleOf), BySessionId — тем более.
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
        // B8 (MAJOR-1 ревью R-F8): время в очереди (минуты) — только у kind=queued, от момента
        // постановки удержания (FeatureHold.Since) до now. Отдельное от ElapsedMinutes: у фичи в
        // очереди work-интервалов нет (elapsed = 0), и «в очереди N мин» отдаёт ровно это поле.
        // null — фича не в очереди.
        int? QueueWaitMinutes,
        BriefNote? Note,
        // B6 (дефект f2da9354): время последнего записанного события состояния фичи (UpdatedAtOf).
        // Отличается от ComputedAt: он — момент этого ответа, updatedAt — момент последнего записанного
        // события состояния (снятие паузы/ожидания события не создаёт, значение откатывается).
        // null — событий состояния нет вовсе (run без эпика и без задач — тестовая фикстура);
        // в реальных данных эпик есть всегда, поэтому у живой фичи поле заполнено.
        DateTime? UpdatedAt,
        DateTime ComputedAt);

    // Таблица kind: FeatureStates → категория. paused расщепляется по Hold.Reason, merged — по
    // run.Closed (см. KindOf). Единая точка правды; тест перебирает константы FeatureStates
    // рефлексией, поэтому новое состояние без строки здесь даёт красный тест (и исключение в Build).
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
        [FeatureStates.Merged] = Kinds.Merged,
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
            QueueWaitMinutes: QueueWaitOf(run, kind, now),
            Note: NoteOf(run, kind),
            UpdatedAt: UpdatedAtOf(run),
            now);
    }

    // paused + Hold.Reason = queue → queued («в очереди»), иначе paused («на паузе»).
    // merged + run.Closed → closed: смерженная с закрытым эпиком закрыта; смерженная с открытым
    // эпиком (Closed=false, FeatureRun.cs:95-98) остаётся merged — то же расщепление, что
    // FeatureProjector.Bucket (FeatureProjector.cs:1764), kind и bucket не расходятся.
    // internal — для строки индекса: Б-1.2 берёт из этой же функции, своего маппинга не заводим.
    internal static string KindOf(FeatureRun run)
    {
        if (!KindByState.TryGetValue(run.State, out var kind))
            throw new InvalidOperationException(
                $"Состояние фичи «{run.State}» не описано в таблице brief.kind (FeatureBrief.KindByState).");
        if (run.State == FeatureStates.Paused && run.Hold?.Reason == FeatureHoldReasons.Queue)
            return Kinds.Queued;
        return run.State == FeatureStates.Merged && run.Closed ? Kinds.Closed : kind;
    }

    // B15: категория ДЛЯ ШТАМПА голоса — та же величина, что и brief.kind (тот же KindOf).
    // Ею, а не сырым run.State, помечается голос в момент записи: у фичи в очереди run.State =
    // «paused» (причина удержания не хранится), а категория — «queued»; сравнение штампа с текущей
    // категорией иначе даёт «сменилась» с первой секунды (дефект 415c1c80).
    // Состояние вне таблицы — сырым state: StaleOf его не достроит (fail-closed) и голос будет
    // устаревшим, но штамп не уронит запись голоса исключением.
    internal static string KindAtWriteOf(FeatureRun run) =>
        KindByState.ContainsKey(run.State) ? KindOf(run) : run.State;

    // B8 (MAJOR-1 ревью R-F8): время в очереди — от момента постановки удержания до now. Момент —
    // FeatureHold.Since: TaskManager.SetHold штампует его моментом постановки, проектор копирует
    // поле эпика в run.Hold (FeatureRun.cs:29) — нового хранилища нет. Не work-интервалы: у фичи
    // в очереди elapsedMinutes = WorkWallMinutes = 0. Null — фича не в очереди (kind ≠ queued —
    // paused/черновик/остальные, и queue-удержание вне paused); 0 — граница [Since, now): удержание,
    // поставленное в момент ответа, и будущий Since (аномалия данных: отрицательного времени не
    // обещаем). При пере-постановке в очередь Since — момент последней постановки: честное время
    // ТЕКУЩЕГО стояния, прошлые интервалы очереди сюда не суммируются.
    private static int? QueueWaitOf(FeatureRun run, string kind, DateTime now)
    {
        if (kind != Kinds.Queued || run.Hold is null) return null;
        var minutes = (long)(now - run.Hold.Since).TotalMinutes;
        return minutes < 0 ? 0 : (int)minutes;
    }

    // Активная стадия конвейера (B14, возврат FIN 74722f13): ЕДИНАЯ база для штампа StageAtWrite
    // (TasksController.SetFeatureNote, TasksToolset featureNote зовут её) и для сравнения StaleOf.
    // Now[0] для штампа не используется: на waiting/stopped/idle/paused Now пуст (в нём только
    // InProgress-задачи), и голос, записанный там, был бы устаревшим с первой секунды. Стадию
    // определяет проектор (FeatureProjector.cs:266-291): она есть почти всегда — фича без задач
    // стоит на «cut», с todo-шагом — на «prod»; пусто только когда все стадии пройдены (merge done).
    internal static string ActiveStageOf(FeatureRun run) =>
        run.Stages.FirstOrDefault(s => s.State == "active")?.Key ?? "";

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
        return new BriefNow(ActiveStageOf(run), attention.TaskId, attention.PersonaId, null, null, attention.Since);
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
    // Оценки нет в случаях (разрез п.3 «Когда оценки нет»): состояние queued/paused/closed/draft/merged
    // (часы стоят, фича не идёт или уже смержена — вместо «осталось» итог Summary), план не нарезан
    // или нарезан частично (PlanState = pending/partial:
    // знаменатель шагов неизвестен или неполон — при partial NotCreated считает только шаги нарезанных
    // планов, оценка вышла бы заниженной; решение Софьи: обещать число по неполному плану хуже, чем
    // не обещать ничего), выборка не построена (n < 3 — FeatureTypical отдаёт шаговые метрики null).
    // PlanState сравнивается только с константами FeatureStepAccounting.Plan* — определение значения
    // одно, сравнение с литералом разъехало бы с ним молча.
    private static BriefEstimate? EstimateOf(FeatureRun run,
        FeatureTypical.TypicalFeatureStats? typical, string kind)
    {
        if (kind is Kinds.Queued or Kinds.Paused or Kinds.Closed or Kinds.Draft or Kinds.Merged) return null;
        if (run.StepProgress.PlanState is FeatureStepAccounting.PlanPending or FeatureStepAccounting.PlanPartial)
            return null;
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

    // B5 (разрез п.1–2, п.6): голос эпика из TaskItem.Note (хранимое поле B3; доходит сюда через
    // FeatureRun.Epic — BuildCore держит исходный TaskItem). Наружу — ровно поля контракта:
    // text/at/byPersonaId/byUser/stageAtWrite/stale; bySessionId наружу не уходит, stateAtWrite
    // служит только StaleOf. ByPersonaId — null: хранимый FeatureNote (B3) persona автора не несёт
    // (только чат BySessionId); заполнить его — аддитивная правка модели, не проектора.
    // Run без эпика (тестовая фикстура) или без голоса — null: «голоса нет → строки нет совсем».
    private static BriefNote? NoteOf(FeatureRun run, string kind)
    {
        var note = run.Epic?.Note;
        if (note is null) return null;

        return new BriefNote(
            note.Text, note.At, null, note.ByUser, note.StageAtWrite,
            StaleOf(run, note, kind));
    }

    // Признак устаревания (разрез п.2, B14 возврат FIN 74722f13, B15 дефект 415c1c80): голос устарел,
    // если StageAtWrite ≠ текущей активной стадии ИЛИ категория (brief.kind) сменилась с момента записи.
    // Единая база: StageAtWrite штампуется той же ActiveStageOf, что и это сравнение (её зовут
    // REST PUT и MCP featureNote; Now[0] для штампа не используется) — голос, записанный на
    // «ждёт вас» / паузе / idle, не устаревший с первой секунды: стадия не менялась. Стадия
    // задачи в петле (шла не в активной) на штамп больше не влияет: штамп — стадия конвейера.
    // Категория на момент записи (B15): штамп — САМА категория (FeatureBrief.KindAtWriteOf, та же
    // величина, что brief.kind), поэтому голос, записанный в очереди, свежий до смены категории.
    // Старые голоса (до B15) хранят СОСТОЯНИЕ — они достраиваются таблицей KindByState (fail-closed
    // для неизвестного значения).
    private static bool StaleOf(FeatureRun run, FeatureNote note, string kind)
    {
        if (note.StageAtWrite != ActiveStageOf(run)) return true;
        var kindAtWrite = KindFromStamp(note.StateAtWrite);
        if (kindAtWrite is null) return true;
        return kindAtWrite != kind;
    }

    // Категория, записанная в голос: новое значение — сама категория (brief.kind); старое (до B15) —
    // состояние проекции, достраивается таблицей KindByState. Пересечений нет: имя состояния,
    // совпадающее с именем категории, отображается в ту же категорию (running → running,
    // paused → paused, draft → draft, idle → idle, closed → closed); «stopped» → error,
    // «waiting» → waiting_you. Ничего не известно — null: голос устарел (fail-closed).
    private static string? KindFromStamp(string stamp) =>
        KindByState.TryGetValue(stamp, out var kind) ? kind
            : Kinds.All.Contains(stamp) ? stamp
            : null;

    // B6 (дефект f2da9354): updatedAt — время последнего записанного события состояния: максимум событий
    // состояния её задач — моментов, которыми такие события записаны: CreatedAt (задача появилась —
    // состав задач и знаменатель плана), ClaudeStartedAt (running/merging), ExecutorStoppedAt (stopped),
    // CompletedAt (прогресс, merge_pending, merged/closed), AwaitingHumanSince (waiting),
    // Hold.Since эпика (paused/draft). Иных событий состояния у трекера нет. Снятие паузы (ClearHold,
    // TaskManager.cs:1421) и снятие ожидания (ClearAwaitingHuman, :1357) состояние меняют, но события
    // не записывают: вклад отметки исчезает из максимума, значение откатывается к предыдущему событию
    // и может уменьшиться (финал принят 10:00, пауза 12:00, снята 12:05 → updatedAt снова 10:00).
    // Характеризующий тест — FeatureBriefTests.UpdatedAt_СнятиеПаузыИОжиданияНеСоздаётСобытияЗначениеОткатывается.
    // НЕ TaskItem.UpdatedAt: это время касания записи, оно двигается настройками (метки, приоритет) —
    // состояние от них не меняется. НЕ LastActivityAt: в нём только CompletedAt/ClaudeStartedAt,
    // а случай дефекта («исполнитель встал N часов назад» / «ждёт вас») в нём не виден.
    // null — событий нет вовсе (run без эпика и без задач — тестовая фикстура): подставлять «сейчас»
    // значит врать; в реальных данных эпик есть всегда, и его CreatedAt — якорь (тот же, что
    // quietAnchor в FeatureProjector.cs:507 для Quiet).
    private static DateTime? UpdatedAtOf(FeatureRun run)
    {
        DateTime? updatedAt = null;
        foreach (var t in AllTasksOf(run))
        {
            if (updatedAt is null || t.CreatedAt > updatedAt) updatedAt = t.CreatedAt;
            if (t.ClaudeStartedAt is not null && (updatedAt is null || t.ClaudeStartedAt > updatedAt))
                updatedAt = t.ClaudeStartedAt;
            if (t.ExecutorStoppedAt is not null && (updatedAt is null || t.ExecutorStoppedAt > updatedAt))
                updatedAt = t.ExecutorStoppedAt;
            if (t.CompletedAt is not null && (updatedAt is null || t.CompletedAt > updatedAt))
                updatedAt = t.CompletedAt;
            if (t.AwaitingHumanSince is not null && (updatedAt is null || t.AwaitingHumanSince > updatedAt))
                updatedAt = t.AwaitingHumanSince;
        }
        // Пауза — событие состояния фичи (paused/draft), её момент лежит на эпике (run.Hold — его копия).
        if (run.Hold is not null && (updatedAt is null || run.Hold.Since > updatedAt))
            updatedAt = run.Hold.Since;
        return updatedAt;
    }

    // Все задачи рана: эпик + всё, что BuildCore разложил по ролям (Fields непересекающиеся,
    // FeatureProjector.cs:547-581) — двойного счёта нет; отсутствующий эпик (тестовая фикстура) пропускается.
    private static IEnumerable<TaskItem> AllTasksOf(FeatureRun run) =>
        (run.Epic is not null ? new[] { run.Epic } : Array.Empty<TaskItem>())
            .Concat(run.Steps).Concat(run.Reviews).Concat(run.Qa)
            .Concat(run.Final is not null ? [run.Final] : Array.Empty<TaskItem>())
            .Concat(run.Merge is not null ? [run.Merge] : Array.Empty<TaskItem>())
            .Concat(run.Cut).Concat(run.Design).Concat(run.Plan).Concat(run.Unroled);
}
