using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Агрегат фичи: собирает <see cref="FeatureRun"/> из задач трекера (разрез §3: «чистая функция
// в вертикали Tasks», от сессий нужны только поля задачи — нового шва нет).
//
// <see cref="Build"/> — чистое ядро (эпик + дочерние → конвейер), на нём золотые тесты.
// <see cref="Project"/>/<see cref="ProjectAll"/>/<see cref="ProjectForSession"/> — обёртки над
// <see cref="TaskManager"/> для REST: без второго источника правды, только выборка задач.
public sealed class FeatureProjector(TaskManager tasks)
{
    // Чистая проекция: эпик + ВСЕ задачи фичи (эпик + дочерние) → конвейер.
    // featureTasks — в порядке, в котором их отдал вызывающий (фикстура/трекер): шаги/ревью/
    // проверки сохраняют этот порядок, финал/мерж берутся как последние по этому порядку.
    // now — «сейчас» для длительностей идущих задач (null = DateTime.UtcNow); тесты фиксируют.
    // Выход — облегчённые копии задач (Slim, контракт master для списков/ответа:
    // Project/ProjectAll/ProjectForSession/BuildIndexCore). Полный (необлегчённый) run —
    // BuildCore: страница фичи (GetFeature) считает на нём ленту событий (BuildEvents:
    // reason вердикта = Verification.Notes, который Slim обнуляет) и облегчает run для ответа.
    // sessions — снимок сессий владельца (Б7): LinkedSessionId наружу (chatId/reviewChatId)
    // только если в нём (fail-closed); null — снимка нет, все чаты null (как в BuildIndexCore).
    public static FeatureRun Build(TaskItem epic, IEnumerable<TaskItem> featureTasks, DateTime? now = null,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
        => SlimRun(BuildCore(epic, featureTasks, now, sessions, modelResolver));

    internal static FeatureRun BuildCore(TaskItem epic, IEnumerable<TaskItem> featureTasks, DateTime? now = null,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var nowUtc = now ?? DateTime.UtcNow;
        ArgumentNullException.ThrowIfNull(epic);
        ArgumentNullException.ThrowIfNull(featureTasks);

        // Б7: снимок сессий владельца → lookup по id: LinkedSessionId наружу (chatId/reviewChatId)
        // только если в снимке (fail-closed, как SessionOf/KeySession: чужой/удалённый/неизвестный
        // чат → null). sessions null → пустой lookup (все чаты null).
        var sessionById = sessions is null
            ? new Dictionary<string, FeatureIndexSession>()
            : sessions.ToDictionary(s => s.Id);
        FeatureIndexSession? LiveSession(string? chatId) =>
            chatId is not null && sessionById.TryGetValue(chatId, out var s) ? s : null;

        // Вход обещает «эпик + дочерние»: дочерние обязаны иметь FeatureId == epic.Id.
        // Эпик сам — единственный, у кого FeatureId может отличаться (у него null: он и есть фича).
        // Чужая задача (FeatureId ≠ epic.Id, и не сам эпик) — падаем явно, не фильтруем молча.
        var foreign = featureTasks.FirstOrDefault(t => !ReferenceEquals(t, epic) && t.FeatureId != epic.Id);
        if (foreign is not null)
            throw new ArgumentException(
                $"В Build попал чужой эпик: задача {foreign.Id} с FeatureId={foreign.FeatureId} ≠ {epic.Id}",
                nameof(featureTasks));

        var children = featureTasks
            .Where(t => t.FeatureId == epic.Id)
            .ToList();

        var steps = children.Where(t => t.Role is TaskRoles.Step or TaskRoles.Rework).ToList();
        var reviews = children.Where(t => t.Role == TaskRoles.Review).ToList();
        var qa = children.Where(t => t.Role == TaskRoles.Qa).ToList();
        var final = children.LastOrDefault(t => t.Role == TaskRoles.Final);
        var merge = children.LastOrDefault(t => t.Role == TaskRoles.Merge);
        var cut = children.Where(t => t.Role is TaskRoles.Cut or TaskRoles.Consult).ToList();
        var plan = children.Where(t => t.Role == TaskRoles.Plan).ToList();

        // Вердикты — только по задачам, которые их несут (ревью/проверка/финал).
        var verdictBearing = reviews.Concat(qa).Concat(final is not null ? [final] : []).ToList();
        var verdicts = new FeatureVerdicts
        {
            Accepted = verdictBearing.Count(t => t.Verification?.Verdict == TaskVerdicts.Accepted),
            AcceptedWithRemarks = verdictBearing.Count(t => t.Verification?.Verdict == TaskVerdicts.AcceptedWithRemarks),
            Returned = verdictBearing.Count(t => t.Verification?.Verdict == TaskVerdicts.Returned),
            Withdrawn = verdictBearing.Count(t => t.Verification?.Verdict == TaskVerdicts.Withdrawn),
        };
        // Прогресс: доля решённых витков (с вердиктом) от всех ревью/проверок/финалов.
        int total = verdictBearing.Count;
        int decided = verdictBearing.Count(t => !string.IsNullOrEmpty(t.Verification?.Verdict));
        double progress = total == 0 ? 0 : Math.Round((double)decided / total, 4);

        // Ready: финал ЗАКРЫТ (Done) и принят (accepted/accepted_with_remarks).
        // Согласовано с Attention «мерж» (блок ниже в Build): и там, и тут «финал принят» = Done + accepted/awr.
        bool ready = final is not null
            && final.Status == TaskItemStatus.Done
            && final.Verification?.Verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks;

        // ─── Б2: цепочки шагов, источники возврата, линии ─────────────────────────────────
        // Задачи, несущие вердикт витка (ревью/проверка/финал) — для витков и возвратов.
        var verdictTasks = reviews.Concat(qa).Concat(final is not null ? [final] : []).ToList();

        // Цепочки: по одной на шаг (stepRef). Шаг = stepRef; в цепочку входят задача шага
        // (role=step) и её доработки (role=rework, тот же stepRef), в порядке времени.
        var stepChains = BuildStepChains(steps, verdictTasks, nowUtc, sessions);

        // Возвраты по источникам (ревью/проверка/финал): число возвратов и сколько открыто.
        var returnsBySource = BuildReturnsBySource(reviews, qa, final, steps);

        // Линии (back/front/прочее): исполнители и счётчики.
        var lanes = BuildLanes(steps, verdictTasks);

        // ─── Б3: «Сейчас», тревоги, итог ───────────────────────────────────────────────────
        // Attention вычисляется ДО цикла стадий: тон активной стадии — производное от него
        // (MAJOR-1 финального ревью: тон и Attention не должны расхождаться).

        // Тревога: приоритет stopped → wait явный → merge.
        FeatureAttention? attention = null;

        // waitTask — явный wait (AwaitingHumanSince, ждущий дольше всех); finalAccepted —
        // для merge (attention) и для state (merge_pending). Вынесены в scope метода:
        // используются и в attention, и в расчёте state.
        var waitTask = featureTasks
            .Where(t => t.AwaitingHumanSince is not null)
            .OrderBy(t => t.AwaitingHumanSince)
            .FirstOrDefault();
        bool finalAccepted = final is not null
            && final.Status == TaskItemStatus.Done
            && final.Verification?.Verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks;

        // 1. stopped: InProgress-задача с ExecutorStoppedAt позже ClaudeStartedAt.
        //    Guard «позже ClaudeStartedAt» — защита от stale-пометки: если исполнитель
        //    перезапустили (MarkClaudeStarted сбрасывает ExecutorStoppedAt), но пометка
        //    не успела сброситься, ClaudeStartedAt будет позже → stopped не показываем.
        var stoppedTask = featureTasks
            .Where(t => t.Status == TaskItemStatus.InProgress
                && t.ExecutorStoppedAt is not null
                && (t.ClaudeStartedAt is null || t.ExecutorStoppedAt > t.ClaudeStartedAt))
            .OrderByDescending(t => t.ExecutorStoppedAt)
            .FirstOrDefault();
        if (stoppedTask is not null)
        {
            attention = new FeatureAttention
            {
                Kind = "stopped",
                TaskId = stoppedTask.Id,
                Since = stoppedTask.ExecutorStoppedAt,
                Reason = stoppedTask.ExecutorStopReason,
                PersonaId = stoppedTask.PersonaId,
                LinkedSessionId = LiveSession(stoppedTask.LinkedSessionId)?.Id, // Б7: fail-closed (только если в снимке)
            };
        }

        // 2. wait явный: AwaitingHumanSince у эпика или задачи (ждущий дольше всех).
        if (attention is null && waitTask is not null)
        {
            attention = new FeatureAttention
            {
                Kind = "wait",
                TaskId = waitTask.Id,
                Since = waitTask.AwaitingHumanSince,
                Reason = waitTask.AwaitingHumanReason,
                PersonaId = waitTask.PersonaId,
                LinkedSessionId = LiveSession(waitTask.LinkedSessionId)?.Id, // Б7: fail-closed (только если в снимке)
            };
        }

        // 3. merge — вычисляется, не хранится: финал принят (accepted/awr, закрыт), а
        //    merge-задачи НЕТ или она создана, но не запущена (Todo, merge_pending).
        //    since = CompletedAt финала. У идущего мержа (merge-задача InProgress) тревоги нет:
        //    state = merging.
        if (attention is null && finalAccepted && (merge is null || merge.Status == TaskItemStatus.Todo))
        {
            attention = new FeatureAttention
            {
                Kind = "merge",
                TaskId = final!.Id,
                Since = final.CompletedAt,
                Reason = "мерж",
                PersonaId = final.PersonaId,
                LinkedSessionId = LiveSession(final.LinkedSessionId)?.Id, // Б7: fail-closed (только если в снимке)
            };
        }

        // Конвейер: 7 стадий в порядке жизненного цикла.
        var stageDefs = new (string key, List<TaskItem> tasks)[]
        {
            ("cut", cut),
            ("plan", plan),
            ("prod", steps),
            ("review", reviews),
            ("qa", qa),
            ("final", final is not null ? [final] : []),
            ("merge", merge is not null ? [merge] : []),
        };

        // isDone[i]: стадия имеет ≥1 задачи И все они Done.
        // Мерж done ТОЛЬКО при merge.Status == Done (финал зелёный, а мержа нет → мерж active).
        bool[] isDone = stageDefs
            .Select((_, i) => stageDefs[i].tasks.Count > 0
                && stageDefs[i].tasks.All(t => t.Status == TaskItemStatus.Done))
            .ToArray();

        // activeIndex: текущая стадия конвейера.
        // 1) Первая стадия, которая ИМЕТ задачи и НЕ пройдена (есть работа).
        // 2) Если таких нет (все стадии с задачами пройдены) и merge не пройдена —
        //    активной считается стадия СРАЗУ ПОСЛЕДНЕЙ стадии, у которой есть задачи
        //    (все стадии с задачами пройдены, merge ещё нет): фича «дошла» до последней
        //    заполненной стадии и ждёт следующую. Задач нет вообще (все стадии пусты) →
        //    cut (Разрез) — фича ещё не стартовала. Честный мерж (финал принят, merge-задачи
        //    нет) — частный случай: последняя с задачами = final → active = merge.
        //    Ложного «Мерж · сейчас» нет: cut done + пусто дальше → plan, plan done + пусто → prod.
        // 3) -1 — все стадии пройдены (merge done).
        int activeIndex = -1;
        for (int i = 0; i < isDone.Length; i++)
        {
            if (!isDone[i] && stageDefs[i].tasks.Count > 0) { activeIndex = i; break; }
        }
        if (activeIndex == -1 && !isDone[^1])
        {
            int lastWithTasks = -1;
            for (int i = 0; i < stageDefs.Length; i++)
                if (stageDefs[i].tasks.Count > 0) lastWithTasks = i;
            activeIndex = lastWithTasks < 0 ? 0 : lastWithTasks + 1;
        }

        var stages = new List<FeatureStage>(stageDefs.Length);
        for (int i = 0; i < stageDefs.Length; i++)
        {
            var (key, tasks) = stageDefs[i];

            // Состояние стадии.
            string state;
            if (i == activeIndex)
                state = "active";
            else if (isDone[i])
                state = "done";
            else
            {
                // Не пройдена, не активная: ноль задач → «нет в трекере»,
                // если какая-либо последующая стадия уже начата (имеет задачи).
                if (tasks.Count == 0)
                {
                    bool laterStarted = stageDefs
                        .Skip(i + 1).Any(d => d.tasks.Count > 0);
                    state = laterStarted ? "absent" : "pending";
                }
                else
                {
                    state = "pending";
                }
            }

            // Тон (только у active, иначе null): stopped/wait из Attention, иначе loop/run.
            // Принцип: тон стадии — производное от Attention, отдельного расчёта быть не должно.
            string? tone = null;
            if (state == "active")
            {
                // loop: активная prod, куда вернули работу (финал или проверка вернули, доработка идёт).
                bool isLoop = key == "prod"
                    && ((final is not null
                        && final.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks)
                        || qa.Any(t => t.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks));

                if (attention is not null && attention.Kind == "stopped") tone = "stopped";
                else if (attention is not null && attention.Kind is "wait" or "merge") tone = "wait";
                else if (isLoop) tone = "loop";
                else tone = "run";
            }

            // Число проходов: у prod — 1 + возвраты (returned/awr у review/qa/final),
            // у остальных — число задач.
            int passes;
            switch (key)
            {
                case "prod":
                    int returns = reviews.Count(t => t.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks)
                        + qa.Count(t => t.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks);
                    if (final is not null && final.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks)
                        returns++;
                    passes = 1 + returns;
                    break;
                case "review":
                    passes = reviews.Count;
                    break;
                case "qa":
                    passes = qa.Count;
                    break;
                case "final":
                    passes = final is not null ? 1 : 0;
                    break;
                case "cut":
                    passes = cut.Count;
                    break;
                case "plan":
                    passes = plan.Count;
                    break;
                case "merge":
                    passes = merge is not null ? 1 : 0;
                    break;
                default:
                    passes = tasks.Count;
                    break;
            }

            // RunningSince: ClaudeStartedAt идущей (InProgress) задачи стадии.
            DateTime? runningSince = state == "active"
                ? tasks.FirstOrDefault(t => t.Status == TaskItemStatus.InProgress)?.ClaudeStartedAt
                : null;

            // Витки стадии: у qa/final каждая задача стадии — виток (формат как у цепочки шагов).
            // У review — шаг с наибольшим числом витков (строка «дольше всех D1c»).
            var stageRounds = key is "qa" or "final"
                ? tasks.Select((t, i) => new FeatureRound
                {
                    N = i + 1,
                    Verdict = RoundVerdict(t),
                    ReviewTaskId = t.Id,
                    PersonaId = t.PersonaId,
                    ReviewChatId = LiveSession(t.LinkedSessionId)?.Id, // Б7: чат витка (fail-closed)
                    StartedAt = TaskStart(t),
                    CompletedAt = t.CompletedAt,
                    Reason = t.Verification?.Notes,
                }).ToList()
                : [];
            string? longestStepRef = key == "review" ? LongestStepRef(stepChains) : null;

            // Б2: границы, длительность, пересечение с идущим, исполнители.
            var firstStartedAt = StageFirstStartedAt(tasks);
            var lastCompletedAt = StageLastCompletedAt(tasks);
            var approxMinutes = StageApproxMinutes(tasks, nowUtc, lastCompletedAt);
            var overlapsActive = StageOverlapsActive(i, stageDefs, nowUtc);
            var executors = tasks.Select(t =>
            {
                var chat = LiveSession(t.LinkedSessionId); // Б7: чат исполнителя (fail-closed)
                // Б4: модель чата (label/full) — готовая функция от контроллера: резолв слота
                // «Авто» — только UserModelTierResolver (инвариант CLAUDE.md), проектор в Main не
                // лезет. chat null (чужой/удалённый) или modelResolver null (тесты) → null.
                (string? label, string? full) = (null, null);
                if (chat is not null && modelResolver is not null)
                    (label, full) = modelResolver(chat);
                return new FeatureStageExecutor
                {
                    PersonaId = t.PersonaId,
                    TaskId = t.Id,
                    TaskRole = t.Role,
                    StepRef = t.StepRef,
                    Lane = t.Lane,
                    Status = ExecutorStatus(t),
                    ChatId = chat?.Id,               // Б7: LinkedSessionId, только если в снимке владельца (иначе null)
                    ChatArchived = chat?.Archived ?? false, // Б7: чат исполнителя в архиве
                    ModelLabel = label,    // Б4
                    ModelFull = full,    // Б4
                };
            }).ToList();

            stages.Add(new FeatureStage
            {
                Key = key,
                State = state,
                Tone = tone,
                Passes = passes,
                TaskIds = tasks.Select(t => t.Id).ToList(),
                RunningSince = runningSince,
                Rounds = stageRounds,
                LongestStepRef = longestStepRef,
                // Б5: исполнители, число задач и исход последней по времени закрытой задачи стадии.
                PersonaIds = StagePersonaIds(tasks),
                TaskCount = tasks.Count,
                Verdict = StageVerdict(tasks),
                // Б2: границы, длительность (объединение интервалов), пересечение, исполнители.
                FirstStartedAt = firstStartedAt,
                LastCompletedAt = lastCompletedAt,
                ApproxMinutes = approxMinutes,
                OverlapsActive = overlapsActive,
                Executors = executors,
            });
        }

        // ─── Б3: «Сейчас», итог ─────────────────────────────────────────────────────────────

        // «Сейчас»: все InProgress-задачи фичи (эпик + дочерние), по убыванию ClaudeStartedAt.
        // Эпик — контейнер фичи, не стадия конвейера: идущий эпик в «Сейчас» не показываем.
        // У закрытой фичи список пуст (ни одна задача не InProgress).
        var nowList = featureTasks
            .Where(t => t.Status == TaskItemStatus.InProgress && !ReferenceEquals(t, epic))
            .OrderByDescending(t => t.ClaudeStartedAt ?? t.CreatedAt)
            .Select(t => new FeatureNow
            {
                TaskId = t.Id,
                Stage = RoleToStage(t.Role),
                PersonaId = t.PersonaId,
                StepRef = t.StepRef,
                Title = t.Title,
                StartedAt = t.ClaudeStartedAt,
                LinkedSessionId = LiveSession(t.LinkedSessionId)?.Id, // Б7: fail-closed (только если в снимке)
            })
            .ToList();

        // Счётчик шагов: принятые (outcome=accepted) / все (из StepChains).
        int stepsTotal = stepChains.Count;
        int stepsAccepted = stepChains.Count(c => c.Outcome == "accepted");

        // Последняя активность: максимум по CompletedAt/ClaudeStartedAt задач фичи
        // (время мержа — это CompletedAt merge-задачи, уже учтено).
        DateTime? lastActivityAt = null;
        foreach (var t in featureTasks)
        {
            if (t.CompletedAt is not null && (lastActivityAt is null || t.CompletedAt > lastActivityAt))
                lastActivityAt = t.CompletedAt;
            if (t.ClaudeStartedAt is not null && (lastActivityAt is null || t.ClaudeStartedAt > lastActivityAt))
                lastActivityAt = t.ClaudeStartedAt;
        }

        // Закрыта: merge-задача Done или эпик Done.
        bool closed = (merge is not null && merge.Status == TaskItemStatus.Done)
            || epic.Status == TaskItemStatus.Done;

        // Итог смерженной фичи: только у закрытой, иначе null.
        FeatureSummary? summary = null;
        if (closed)
        {
            // МergedAt: CompletedAt merge-задачи; только когда merge-задача ЗАКРЫТА (Done).
            // Закрытая эпиком БЕЗ merge-задачи (эпик Done, мержа нет) → mergedAt = null:
            // фронт читает «закрыта» (bucket=closed && mergedAt=null), а не «смержена».
            // (Раньше сюда падал CompletedAt эпика — дефект n4: закрытая без мержа
            // показывалась «смержена <дата закрытия эпика>».)
            var mergedAt = merge is not null && merge.Status == TaskItemStatus.Done
                ? merge.CompletedAt
                : null;
            // Длительность: от старта первой задачи (ClaudeStartedAt, иначе CreatedAt) до mergedAt.
            var firstStart = featureTasks
                .Min(t => t.ClaudeStartedAt ?? t.CreatedAt);
            var end = mergedAt ?? lastActivityAt ?? DateTime.UtcNow;
            var duration = Math.Max(0, Math.Round((end - firstStart).TotalMinutes));
            summary = new FeatureSummary
            {
                MergedAt = mergedAt,
                DurationMinutes = duration,
                Tasks = featureTasks.Count(),
                Steps = stepsTotal,
                Loops = verdicts.Loops,
            };
        }

        // Состояние фичи (единая точка правды; из него bucket строки и порядок OrderRuns —
        // см. FeatureStates). Приоритет: merged → closed → stopped → waiting → merging →
        // merge_pending → running → not_started → idle. finalAccepted/waitTask — выше (scope метода).
        string featureState = ComputeState(
            epic, merge, finalAccepted,
            hasStopped: stoppedTask is not null,
            hasExplicitWait: waitTask is not null,
            nowCount: nowList.Count,
            anyStarted: featureTasks.Any(t => t.ClaudeStartedAt is not null));

        // Все расчёты выше идут по исходным задачам. BuildCore возвращает ПОЛНЫЙ (необлегчённый)
        // run: на нём страница фичи (GetFeature) считает ленту событий (BuildEvents: reason
        // вердикта = Verification.Notes, который Slim обнуляет). Облегчение для списков/ответа
        // (контракт master) — SlimRun: тексты (Description/ResultMarkdown), Verification.Notes,
        // Repro, подзадачи и файлы пульту фичи не нужны (причина витка уже лежит в Rounds[].Reason),
        // а на фиче из десятков задач именно они делали ответ в сотни КБ.
        return new FeatureRun
        {
            FeatureId = epic.Id,
            Epic = epic,
            Steps = steps,
            Reviews = reviews,
            Qa = qa,
            Final = final,
            Merge = merge,
            AwaitingHumanSince = epic.AwaitingHumanSince,
            AwaitingHumanReason = epic.AwaitingHumanReason,
            Verdicts = verdicts,
            Progress = progress,
            Ready = ready,
            Cut = cut,
            Plan = plan,
            Stages = stages,
            StepChains = stepChains,
            ReturnsBySource = returnsBySource,
            Lanes = lanes,
            Now = nowList,
            State = featureState,
            Attention = attention,
            StepsAccepted = stepsAccepted,
            StepsTotal = stepsTotal,
            LastActivityAt = lastActivityAt,
            Closed = closed,
            Summary = summary,
        };
    }

    // ─── Б1.2: лента событий фичи (волна 2 «Обзор фич», разрез §2) ─────────────────────────
    // Чистая проекция FeatureRun → события БЕЗ хранилища: каждое событие выводится из полей
    // TaskItem/витков. На ней золотые тесты (FeatureEventsTests) — internal, как BuildIndexCore/
    // BuildRow. sessionId наружу только если в снимке сессий владельца (иначе null — fail-closed,
    // как KeySession/AttentionOf); reason ≤ 200 символов. Честное ограничение: видна ПОСЛЕДНЯЯ
    // пара старт/стоп задачи (TaskItem держит одно ClaudeStartedAt/ExecutorStoppedAt) — журнал
    // не заводим.
    internal static List<FeatureEvent> BuildEvents(
        FeatureRun run,
        IReadOnlyCollection<FeatureIndexSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sessions);

        // Живые сессии владельца: sessionId события наружу только если в этом множестве.
        var live = new HashSet<string>(sessions.Select(s => s.Id));

        // Номер витка: из УЖЕ вычисленных FeatureRound (цепочки шагов + стадии qa/final) —
        // N берём, арифметику (N = i + 1) не пересчитываем. Ревью, попавшее в несколько цепочек
        // (связка многие-ко-многие), даёт минимальный N (детерминизм).
        var roundByTask = new Dictionary<string, int>();
        void AddRound(FeatureRound r)
        {
            if (r.ReviewTaskId is null) return;
            if (roundByTask.TryGetValue(r.ReviewTaskId, out var cur))
                roundByTask[r.ReviewTaskId] = Math.Min(cur, r.N);
            else
                roundByTask[r.ReviewTaskId] = r.N;
        }
        foreach (var chain in run.StepChains)
            foreach (var r in chain.Rounds)
                AddRound(r);
        foreach (var stage in run.Stages)
            foreach (var r in stage.Rounds)
                AddRound(r);
        int? RoundOf(TaskItem t) => roundByTask.TryGetValue(t.Id, out var n) ? n : null;

        // Все задачи фичи: эпик + шаги/доработки + ревью + проверки + финал + мерж + cut + plan.
        var tasks = new List<TaskItem> { run.Epic };
        tasks.AddRange(run.Steps);
        tasks.AddRange(run.Reviews);
        tasks.AddRange(run.Qa);
        if (run.Final is not null) tasks.Add(run.Final);
        if (run.Merge is not null) tasks.Add(run.Merge);
        tasks.AddRange(run.Cut);
        tasks.AddRange(run.Plan);

        var events = new List<FeatureEvent>();
        // sessionId: LinkedSessionId задачи, наружу только если в снимке владельца (fail-closed).
        string? SessionOf(TaskItem t) => t.LinkedSessionId is not null && live.Contains(t.LinkedSessionId)
            ? t.LinkedSessionId : null;
        // Событие задачи: kind/at/reason/round задаёт вид, остальное (role/stepRef/personaId/
        // sessionId) — атрибуты самой задачи. Порядок аргументов — контракт §2:
        // at, kind, taskId, role, stepRef?, personaId?, sessionId?, round?, reason?.
        void Add(TaskItem t, string kind, DateTime at, string? reason, int? round = null) =>
            events.Add(new FeatureEvent(at, kind, t.Id, t.Role, t.StepRef, t.PersonaId,
                SessionOf(t), round, reason));

        foreach (var t in tasks)
        {
            // created — всегда (момент создания задачи).
            Add(t, "created", t.CreatedAt, null);

            // started — последний старт исполнителя (ClaudeStartedAt); журнал не заводим.
            if (t.ClaudeStartedAt is not null)
                Add(t, "started", t.ClaudeStartedAt.Value, null);

            // stopped — только у незакрытой (InProgress) задачи и только когда ExecutorStoppedAt >
            // ClaudeStartedAt (то же правило, что у Attention: guard от stale-пометки).
            // reason = ExecutorStopReason.
            if (t.Status == TaskItemStatus.InProgress
                && t.ExecutorStoppedAt is not null
                && (t.ClaudeStartedAt is null || t.ExecutorStoppedAt > t.ClaudeStartedAt))
                Add(t, "stopped", t.ExecutorStoppedAt.Value, Truncate(t.ExecutorStopReason));

            // waiting — «ждёт вас» (AwaitingHumanSince); reason = AwaitingHumanReason.
            if (t.AwaitingHumanSince is not null)
                Add(t, "waiting", t.AwaitingHumanSince.Value, Truncate(t.AwaitingHumanReason));

            // dropped — снято человеком (DroppedByHumanAt); причины в модели нет.
            if (t.DroppedByHumanAt is not null)
                Add(t, "dropped", t.DroppedByHumanAt.Value, null);

            // Терминальное событие задачи — одно, по приоритету:
            //   вердикт (accepted/awr/returned/withdrawn) — ревью/проверка/финал с вердиктом
            //     (+ номер витка из FeatureRound);
            //   merged — мерж закрыт; closed — эпик закрыт; completed — прочая задача Done.
            if (t.CompletedAt is not null)
            {
                var verdict = t.Verification?.Verdict;
                if (verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks
                    or TaskVerdicts.Returned or TaskVerdicts.Withdrawn)
                {
                    Add(t, verdict, t.CompletedAt.Value, Truncate(t.Verification!.Notes), RoundOf(t));
                }
                else if (t.Role == TaskRoles.Merge)
                {
                    Add(t, "merged", t.CompletedAt.Value, null);
                }
                else if (t.Role == TaskRoles.Feature)
                {
                    Add(t, "closed", t.CompletedAt.Value, null);
                }
                else
                {
                    Add(t, "completed", t.CompletedAt.Value, null);
                }
            }
        }

        // Порядок: at desc, при равенстве — taskId (детерминизм).
        return events
            .OrderByDescending(e => e.At)
            .ThenBy(e => e.TaskId)
            .ToList();
    }

    // ─── Б-pl: документы фичи (волна 3 «Обзор фич», вкладка «Планы и макеты») ────────────────
    // Чистая проекция FeatureRun → List<FeaturePlan> БЕЗ хранилища: разрез (cut), план (plan) и
    // консультация (consult) фичи — СПИСОК документов вкладки, без текста. Text (ResultMarkdown)
    // в ответ НЕ попадает: вкладка берёт список, текст подгружает по клику (GET /api/tasks/{id}).
    // Считается по ПОЛНОМУ (необлегчённому) run ДО SlimRun: Slim обнуляет ResultMarkdown, а
    // hasText/mockupPaths — из него.
    //
    // Источник: run.Cut (несёт cut И consult) + run.Plan (plan). Роль — из TaskItem.Role как есть
    // (step в документы не попадает, догадок по заголовку нет). hasText — ResultMarkdown не пуст
    // после Trim; mockupPaths — пути макетов (docs/mockups/….{html,md}) из ResultMarkdown: distinct,
    // порядок появления, ≤ 20, пути с «..» отброшены.
    //
    // Порядок списка: роль (cut → plan → consult), внутри роли — по CreatedAt.
    public static List<FeaturePlan> BuildPlans(FeatureRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // cut/consult — из run.Cut (там обе роли); plan — из run.Plan. Роль — из TaskItem.Role
        // как есть (у задач run.Cut/run.Plan она всегда non-null: cut/consult/plan).
        var cut = run.Cut.Where(t => t.Role == TaskRoles.Cut).ToList();
        var consult = run.Cut.Where(t => t.Role == TaskRoles.Consult).ToList();
        var plan = run.Plan.Where(t => t.Role == TaskRoles.Plan).ToList();

        // Порядок: cut → plan → consult; внутри роли — по CreatedAt (OrderBy стабилен: равные
        // CreatedAt сохраняют порядок, в котором задачи дал вызывающий).
        var ordered = new List<TaskItem>();
        ordered.AddRange(cut.OrderBy(t => t.CreatedAt));
        ordered.AddRange(plan.OrderBy(t => t.CreatedAt));
        ordered.AddRange(consult.OrderBy(t => t.CreatedAt));

        return ordered
            .Select(t => new FeaturePlan(
                t.Id,
                t.Role ?? "",
                t.Title,
                t.PersonaId,
                t.CreatedAt,
                t.CompletedAt,
                HasText(t),
                MockupPaths(t)))
            .ToList();
    }

    // hasText: ResultMarkdown не пуст после Trim (null/""/пробелы → false).
    private static bool HasText(TaskItem t) => !string.IsNullOrWhiteSpace(t.ResultMarkdown);

    // Пути макетов (docs/mockups/….{html,md}) из ResultMarkdown: distinct, порядок появления,
    // ≤ 20, пути с «..» отброшены. null/пусто → [].
    private static List<string> MockupPaths(TaskItem t)
    {
        var text = t.ResultMarkdown;
        if (string.IsNullOrEmpty(text)) return [];
        var paths = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in MockupPathRegex.Matches(text))
        {
            var p = m.Value;
            // «..» — выход за docs/mockups (pathtraversal) → отбрасываем.
            if (p.Contains("..")) continue;
            if (!paths.Contains(p)) paths.Add(p);
            if (paths.Count >= 20) break;
        }
        return paths;
    }

    // docs/mockups/ + путь из [A-Za-z0-9._/-] + «.html»/«.md».
    private static readonly System.Text.RegularExpressions.Regex MockupPathRegex =
        new(@"docs/mockups/[A-Za-z0-9._/-]+\.(html|md)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // reason ≤ 200 символов (обрезка по кодовым позициям; null — как есть). internal — для золотого
    // теста (ClaudeHomeServer.Tasks.Tests): кириллица (1 кодовая точка = 1 UTF-16-единица) и
    // суррогатные пары (эмодзи, 2 единицы) не рвутся — разорванный старший суррогат отбрасывается.
    internal static string? Truncate(string? s)
    {
        if (s is null) return null;
        if (s.Length <= 200) return s;
        var cut = s.AsSpan(0, 200);
        // s[..200] режет по UTF-16-единицам: если 200-я позиция — старший суррогат пары, пара
        // разорвана (висит без младшего) → отбрасываем его, чтобы не оставлять висячий суррогат.
        if (char.IsHighSurrogate(cut[^1])) cut = cut.Slice(0, cut.Length - 1);
        return cut.ToString();
    }

    // Облегчённая копия задачи для выхода агрегата: TaskListItem (без Description/ResultMarkdown,
    // с маркером listItem — фронт догрузит полную карточку по id) + без Verification.Notes,
    // Repro, подзадач, файлов и меток. Остальные поля — как есть (роль, статус, связи, времена).
    private static TaskItem Slim(TaskItem t)
    {
        var item = TaskListItem.From(t);
        item.Verification = t.Verification is null
            ? null
            : new TaskVerification
            {
                VerifiedAt = t.Verification.VerifiedAt,
                PersonaId = t.Verification.PersonaId,
                Verdict = t.Verification.Verdict,
            };
        item.Repro = null;
        item.LinkedFiles = [];
        item.Subtasks = [];
        item.Labels = [];
        return item;
    }

    private static List<TaskItem> SlimAll(List<TaskItem> tasks) => tasks.Select(Slim).ToList();

    // Облегчённая копия агрегата (контракт master для списков/ответа): коллекции задач заменяются
    // облегчёнными копиями (Slim), вычисленные поля (стадии/цепочки/счётчики/«сейчас»/тревога)
    // передаются по ссылке — они уже вычислены по исходным задачам. Страница фичи (GetFeature)
    // сначала считает ленту событий (BuildEvents) по ПОЛНОМУ run, а в ответ run облегчает SlimRun.
    internal static FeatureRun SlimRun(FeatureRun run) => new()
    {
        FeatureId = run.FeatureId,
        Epic = Slim(run.Epic),
        Steps = SlimAll(run.Steps),
        Reviews = SlimAll(run.Reviews),
        Qa = SlimAll(run.Qa),
        Final = run.Final is null ? null : Slim(run.Final),
        Merge = run.Merge is null ? null : Slim(run.Merge),
        AwaitingHumanSince = run.AwaitingHumanSince,
        AwaitingHumanReason = run.AwaitingHumanReason,
        Verdicts = run.Verdicts,
        Progress = run.Progress,
        Ready = run.Ready,
        Cut = SlimAll(run.Cut),
        Plan = SlimAll(run.Plan),
        Stages = run.Stages,
        StepChains = run.StepChains,
        ReturnsBySource = run.ReturnsBySource,
        Lanes = run.Lanes,
        Now = run.Now,
        State = run.State,
        Attention = run.Attention,
        StepsAccepted = run.StepsAccepted,
        StepsTotal = run.StepsTotal,
        LastActivityAt = run.LastActivityAt,
        Closed = run.Closed,
        Summary = run.Summary,
    };

    // ─── Б2: хелперы и вычислители цепочек/возвратов/линий ─────────────────────────────────

    // Старт задачи: ClaudeStartedAt (запуск исполнителя), иначе CreatedAt.
    private static DateTime TaskStart(TaskItem t) => t.ClaudeStartedAt ?? t.CreatedAt;

    // Исход витка: вердикт задачи, если он один из четырёх; иначе «running» (открыт, вердикта нет).
    private static string RoundVerdict(TaskItem t)
    {
        var v = t.Verification?.Verdict;
        return v is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks
            or TaskVerdicts.Accepted or TaskVerdicts.Withdrawn
            ? v
            : "running";
    }

    // Виток-возврат: returned или accepted_with_remarks (работа вернулась к исполнителю).
    private static bool IsReturn(string verdict) =>
        verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks;

    // Источник возврата: роль задачи → review | qa | final; прочее → null.
    private static string? RoleToSource(string? role) => role switch
    {
        TaskRoles.Review => "review",
        TaskRoles.Qa => "qa",
        TaskRoles.Final => "final",
        _ => null,
    };

    // Нормализация линии как laneOf во фронтовом PipelineFlow.tsx:
    // back/backend/бэк/бэкенд → back; front/frontend/фронт/фронтенд → front; прочее → other.
    private static string NormalizeLane(string? lane)
    {
        var l = (lane ?? "").Trim().ToLowerInvariant();
        if (l is "back" or "backend" or "бэк" or "бэкенд") return "back";
        if (l is "front" or "frontend" or "фронт" or "фронтенд") return "front";
        return "other";
    }

    // Роль задачи → ключ стадии конвейера (для блока «Сейчас»).
    // cut/consult → cut (Разрез); step/rework → prod; прочее → prod.
    // Эпик (feature) сюда не доходит: он контейнер фичи и исключён из Now (см. «Сейчас» в Build).
    private static string RoleToStage(string? role) => role switch
    {
        TaskRoles.Cut or TaskRoles.Consult => "cut",
        TaskRoles.Plan => "plan",
        TaskRoles.Step or TaskRoles.Rework => "prod",
        TaskRoles.Review => "review",
        TaskRoles.Qa => "qa",
        TaskRoles.Final => "final",
        TaskRoles.Merge => "merge",
        _ => "prod",
    };

    // Цепочки шагов: группировка steps (role=step/rework) по stepRef. Порядок групп — по первому
    // появлению (порядок вызывающего); внутри группы — по времени (стабильная сортировка: равные
    // старты сохраняют порядок вызывающего).
    private static List<FeatureStepChain> BuildStepChains(
        List<TaskItem> steps, List<TaskItem> verdictTasks, DateTime nowUtc,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null)
    {
        // Б7: снимок сессий владельца → lookup по id: чаты шага/витка наружу только если в снимке
        // (fail-closed, как в BuildCore; sessions null → все чаты null).
        var sessionById = sessions is null
            ? new Dictionary<string, FeatureIndexSession>()
            : sessions.ToDictionary(s => s.Id);
        FeatureIndexSession? LiveSession(string? chatId) =>
            chatId is not null && sessionById.TryGetValue(chatId, out var s) ? s : null;

        var groups = new List<(string Key, List<TaskItem> Tasks)>();
        var index = new Dictionary<string, int>();
        foreach (var t in steps)
        {
            var key = t.StepRef ?? "";
            if (!index.TryGetValue(key, out var i))
            {
                index[key] = i = groups.Count;
                groups.Add((key, []));
            }
            groups[i].Tasks.Add(t);
        }

        var chains = new List<FeatureStepChain>(groups.Count);
        foreach (var (key, groupTasks) in groups)
        {
            var ordered = groupTasks.OrderBy(t => TaskStart(t)).ToList();
            // Шапка цепочки — задача шага (role=step); если её нет (только доработки) — первая.
            var head = ordered.FirstOrDefault(t => t.Role == TaskRoles.Step) ?? ordered[0];
            var idSet = new HashSet<string>(ordered.Select(t => t.Id));

            // Витки: ревью/проверки/финалы, связанные с цепочкой через ReviewOf, в порядке времени.
            // Одно ревью на два шага попадает в обе цепочки (связка многие-ко-многие).
            var rounds = verdictTasks
                .Where(t => t.ReviewOf is not null && t.ReviewOf.Any(id => idSet.Contains(id)))
                .OrderBy(t => TaskStart(t))
                .ToList();
            var featureRounds = rounds.Select((t, i) => new FeatureRound
            {
                N = i + 1,
                Verdict = RoundVerdict(t),
                ReviewTaskId = t.Id,
                PersonaId = t.PersonaId,
                ReviewChatId = LiveSession(t.LinkedSessionId)?.Id, // Б7: чат витка (fail-closed)
                StartedAt = TaskStart(t),
                CompletedAt = t.CompletedAt,
                Reason = t.Verification?.Notes,
            }).ToList();
            int loops = featureRounds.Count(r => IsReturn(r.Verdict));

            // Источник возврата: ближайшая (последняя по времени) связанная задача-возврат;
            // роль → review/qa/final. Связи нет — null (догадок не делаем).
            var returnTasks = rounds.Where(t => IsReturn(RoundVerdict(t))).ToList();
            string? source = returnTasks.Count > 0 ? RoleToSource(returnTasks[^1].Role) : null;

            // Исход и шапка.
            string outcome, status;
            bool withdrawn = ordered.Any(t => t.Verification?.Verdict == TaskVerdicts.Withdrawn);
            if (withdrawn)
            {
                outcome = "withdrawn";
                status = "снят с выкладки";
            }
            else if (featureRounds.Count > 0)
            {
                var last = featureRounds[^1];
                if (last.Verdict == TaskVerdicts.Accepted)
                {
                    outcome = "accepted";
                    status = "принят";
                }
                else if (IsReturn(last.Verdict))
                {
                    // Вернули: доработка ещё идёт → «идёт»; закрыто без повторного ревью → «закрыт…».
                    bool reworkOpen = ordered.Any(t => t.Role == TaskRoles.Rework && t.Status != TaskItemStatus.Done);
                    if (reworkOpen)
                    {
                        outcome = "running";
                        status = "идёт";
                    }
                    else
                    {
                        outcome = "returned";
                        status = "закрыт без повторного ревью";
                    }
                }
                else
                {
                    // Виток открыт (running) или снят (withdrawn) без вердикта на задаче.
                    outcome = "running";
                    status = "идёт";
                }
            }
            else
            {
                // Витков нет: шаг закрыт, но ревью ещё не было → «ждёт ревью»; иначе «идёт».
                bool allDone = ordered.All(t => t.Status == TaskItemStatus.Done);
                if (allDone)
                {
                    outcome = "pending";
                    status = "ждёт ревью";
                }
                else
                {
                    outcome = "running";
                    status = "идёт";
                }
            }

            // Длительность: от старта первой задачи до закрытия последней; у идущей — до сейчас.
            var first = ordered[0];
            var lastTask = ordered[^1];
            var start = TaskStart(first);
            var end = lastTask.CompletedAt ?? nowUtc;
            var duration = Math.Max(0, Math.Round((end - start).TotalMinutes));

            chains.Add(new FeatureStepChain
            {
                StepRef = key,
                Lane = head.Lane,
                Title = head.Title,
                PersonaId = head.PersonaId,
                ChatId = LiveSession(head.LinkedSessionId)?.Id, // Б7: чат шага (fail-closed)
                TaskIds = ordered.Select(t => t.Id).ToList(),
                Outcome = outcome,
                Status = status,
                DurationMinutes = duration,
                Source = source,
                Loops = loops,
                Rounds = featureRounds,
            });
        }
        return chains;
    }

    // Возвраты по источникам: для каждого источника (ревью/проверка/финал) — число возвратов
    // (уникальные задачи с returned/accepted_with_remarks) и сколько из них открыто (их доработка
    // ещё не done). «Доработка» — rework-задача, чей ReviewOf ссылается на задачу-источник.
    private static FeatureReturnsBySource BuildReturnsBySource(
        List<TaskItem> reviews, List<TaskItem> qa, TaskItem? final, List<TaskItem> steps)
    {
        var reworks = steps.Where(t => t.Role == TaskRoles.Rework).ToList();
        FeatureSourceReturns Count(List<TaskItem> tasks)
        {
            int count = 0, open = 0;
            foreach (var t in tasks)
            {
                if (!IsReturn(RoundVerdict(t))) continue;
                count++;
                // Открыто: есть доработка, ссылающаяся на эту задачу, и она ещё не done.
                if (reworks.Any(r => r.ReviewOf is not null && r.ReviewOf.Contains(t.Id) && r.Status != TaskItemStatus.Done))
                    open++;
            }
            return new FeatureSourceReturns { Count = count, Open = open };
        }
        List<TaskItem> finalList = final is not null ? [final] : [];
        return new FeatureReturnsBySource
        {
            Review = Count(reviews),
            Qa = Count(qa),
            Final = Count(finalList),
        };
    }

    // Линии (back/front/прочее): исполнители (уникальные, по первому появлению) и счётчики.
    // Считаем по задачам-шагам (role=step): доработка — часть цепочки шага, а не отдельная
    // позиция линии. total — шаги линии; accepted — Done; returns — шаги с возвратом.
    private static List<FeatureLane> BuildLanes(List<TaskItem> steps, List<TaskItem> verdictTasks)
    {
        var stepTasks = steps.Where(t => t.Role == TaskRoles.Step).ToList();
        var groups = new List<(string Lane, List<TaskItem> Tasks)>();
        var index = new Dictionary<string, int>();
        foreach (var t in stepTasks)
        {
            var lane = NormalizeLane(t.Lane);
            if (!index.TryGetValue(lane, out var i))
            {
                index[lane] = i = groups.Count;
                groups.Add((lane, []));
            }
            groups[i].Tasks.Add(t);
        }

        // Шаги с возвратом: есть связанный (ReviewOf ∩ id шага) виток-возврат.
        var returnedIds = new HashSet<string>();
        foreach (var v in verdictTasks)
        {
            if (!IsReturn(RoundVerdict(v)) || v.ReviewOf is null) continue;
            foreach (var id in v.ReviewOf)
                returnedIds.Add(id);
        }

        var lanes = new List<FeatureLane>(groups.Count);
        foreach (var (lane, tasks) in groups)
        {
            var personaIds = new List<string>();
            foreach (var t in tasks)
                if (t.PersonaId is not null && !personaIds.Contains(t.PersonaId))
                    personaIds.Add(t.PersonaId);
            lanes.Add(new FeatureLane
            {
                Lane = lane,
                PersonaIds = personaIds,
                Total = tasks.Count,
                Accepted = tasks.Count(t => t.Status == TaskItemStatus.Done),
                Returns = tasks.Count(t => returnedIds.Contains(t.Id)),
            });
        }
        return lanes;
    }

    // Шаг с наибольшим числом витков (для стадии review: строка «дольше всех D1c»).
    // Равные числа — первый по порядку; витков нет ни у кого → null.
    private static string? LongestStepRef(List<FeatureStepChain> chains)
    {
        string? best = null;
        int bestCount = 0;
        foreach (var c in chains)
            if (c.Rounds.Count > bestCount)
            {
                bestCount = c.Rounds.Count;
                best = c.StepRef;
            }
        return best;
    }

    // Б5: исполнители стадии — уникальные PersonaId задач в порядке первого появления;
    // пустые (null/"") id не включаются.
    private static List<string> StagePersonaIds(List<TaskItem> tasks)
    {
        var ids = new List<string>();
        foreach (var t in tasks)
            if (!string.IsNullOrEmpty(t.PersonaId) && !ids.Contains(t.PersonaId))
                ids.Add(t.PersonaId);
        return ids;
    }

    // Б5: исход стадии — исход последней по времени закрытой (Done) задачи стадии.
    // Порядок: 1) стадия без задач → null; 2) любая задача стадии идёт (InProgress) → "running";
    // 3) иначе — Verification.Verdict последней по CompletedAt закрытой задачи, если он один из
    //    четырёх (returned/accepted_with_remarks/accepted/withdrawn), иначе null.
    // У стадий без вердиктов (cut/plan/merge/prod) это даёт null, кроме случая, когда у задачи
    // есть Verification.Verdict (тогда он же) — ровно как требует постановка.
    private static string? StageVerdict(List<TaskItem> tasks)
    {
        if (tasks.Count == 0)
            return null;
        if (tasks.Any(t => t.Status == TaskItemStatus.InProgress))
            return "running";

        var last = tasks
            .Where(t => t.Status == TaskItemStatus.Done)
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .FirstOrDefault();
        if (last is null)
            return null;

        var v = last.Verification?.Verdict;
        return v is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks
            or TaskVerdicts.Accepted or TaskVerdicts.Withdrawn
            ? v
            : null;
    }

    // ─── Б2: границы, длительность, пересечение, исполнители стадий ───────────────────────────

    // Б2: начало стадии — минимальный ClaudeStartedAt задач стадии; null — ни одна не стартовала.
    private static DateTime? StageFirstStartedAt(List<TaskItem> tasks)
        => tasks.Where(t => t.ClaudeStartedAt is not null).Min(t => t.ClaudeStartedAt);

    // Б2: конец стадии — максимальный CompletedAt, ТОЛЬКО если все задачи стадии закрыты (Done),
    // иначе null. Done-задача без CompletedAt (аномалия данных) в max не входит (ранее
    // подставлялся CreatedAt — конец мог оказаться позже реального: новая задача, созданная
    // позже закрытия старой).
    private static DateTime? StageLastCompletedAt(List<TaskItem> tasks)
    {
        if (tasks.Count == 0 || !tasks.All(t => t.Status == TaskItemStatus.Done))
            return null;
        var completed = tasks
            .Where(t => t.CompletedAt is not null)
            .Select(t => t.CompletedAt!.Value)
            .ToList();
        return completed.Count > 0 ? completed.Max() : null;
    }

    // Б2: интервал задачи: [ClaudeStartedAt, конец]. Не стартовала (нет ClaudeStartedAt) →
    // интервала нет. Конец: CompletedAt; если его нет — now (задача идёт) или конец стадии
    // (stageEnd: Done-задача без CompletedAt в закрытой стадии не растягивает интервал до now,
    // иначе длительность закрытой стадии росла со временем). Конец раньше начала (аномалия)
    // → нулевая длительность.
    private static (DateTime start, DateTime end)? IntervalOf(TaskItem t, DateTime now, DateTime? stageEnd = null)
    {
        if (t.ClaudeStartedAt is null) return null;
        var end = t.CompletedAt ?? (t.Status == TaskItemStatus.Done ? stageEnd : null) ?? now;
        return (t.ClaudeStartedAt.Value, end < t.ClaudeStartedAt.Value ? t.ClaudeStartedAt.Value : end);
    }

    // Б2: длительность по часам стены (минуты): ОБЪЕДИНЕНИЕ интервалов задач стадии —
    // параллельные задачи не складываются (объединение, не сумма). Округление вверх до минуты;
    // стадия без интервалов → 0. stageEnd (конец стадии, null — стадия не закрыта) — конец
    // интервала Done-задачи без CompletedAt (не растягивается до now).
    private static int StageApproxMinutes(List<TaskItem> tasks, DateTime now, DateTime? stageEnd = null)
    {
        var intervals = tasks
            .Select(t => IntervalOf(t, now, stageEnd))
            .Where(iv => iv is not null)
            .Select(iv => iv!.Value)
            .OrderBy(iv => iv.start)
            .ToList();
        if (intervals.Count == 0)
            return 0;

        var total = TimeSpan.Zero;
        var (curStart, curEnd) = intervals[0];
        foreach (var (start, end) in intervals.Skip(1))
        {
            if (start <= curEnd)
            {
                if (end > curEnd) curEnd = end;
            }
            else
            {
                total += curEnd - curStart;
                (curStart, curEnd) = (start, end);
            }
        }
        total += curEnd - curStart;
        return (int)Math.Ceiling(total.TotalMinutes);
    }

    // Б2: пересечение с идущим — интервалы стадии пересекаются с интервалами задач ДРУГОЙ стадии,
    // которая ещё идёт (имеет ≥1 задачу, не все Done). Стадия без интервалов → false.
    // Пересечение строгое (положительный нахлёст): касание «подряд» (конец одного = старт
    // другого) пересечением не считается.
    private static bool StageOverlapsActive(
        int index, (string key, List<TaskItem> tasks)[] stageDefs, DateTime now)
    {
        var selfEnd = StageLastCompletedAt(stageDefs[index].tasks);
        var selfIntervals = stageDefs[index].tasks
            .Select(t => IntervalOf(t, now, selfEnd))
            .Where(iv => iv is not null)
            .Select(iv => iv!.Value)
            .ToList();
        if (selfIntervals.Count == 0)
            return false;

        for (int j = 0; j < stageDefs.Length; j++)
        {
            if (j == index) continue;
            var other = stageDefs[j].tasks;
            // «Ещё идёт»: задачи есть и хотя бы одна не закрыта (Done).
            if (other.Count == 0 || other.All(t => t.Status == TaskItemStatus.Done))
                continue;
            if (other.Any(t =>
                    IntervalOf(t, now, StageLastCompletedAt(other)) is { } o
                    && selfIntervals.Any(iv => iv.start < o.end && o.start < iv.end)))
                return true;
        }
        return false;
    }

    // Б2: статус исполнителя из полей задачи. Приоритет: running (идёт: ClaudeStartedAt,
    // без завершения, не остановлена, не ждёт) → waiting (AwaitingHumanSince) → stopped
    // (ExecutorStoppedAt) → done (Done) → pending (остальное, в том числе не стартовала).
    // waiting/stopped — тот же guard, что у тревоги: только InProgress-задачи; закрытая
    // вручную (Done) задача со stale-пометками не «stopped»/«waiting» (тревоги по ней тоже нет).
    private static string ExecutorStatus(TaskItem t)
    {
        if (t.ClaudeStartedAt is not null
            && t.CompletedAt is null
            && t.ExecutorStoppedAt is null
            && t.AwaitingHumanSince is null)
            return "running";
        if (t.Status == TaskItemStatus.InProgress && t.AwaitingHumanSince is not null)
            return "waiting";
        if (t.Status == TaskItemStatus.InProgress && t.ExecutorStoppedAt is not null)
            return "stopped";
        if (t.Status == TaskItemStatus.Done)
            return "done";
        return "pending";
    }

    // Одна фича по id эпика (владельца ownerId). null — эпика нет.
    public FeatureRun? Project(string featureId, string ownerId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        var epic = owner.FirstOrDefault(t => t.Id == featureId && t.Role == TaskRoles.Feature);
        return epic is null ? null : Build(epic, FeatureTasks(epic, owner));
    }

    // Одна фича по id эпика — ПОЛНЫЙ (необлегчённый) run: для страницы фичи (GetFeature).
    // Лента событий (BuildEvents) считается по полным задачам (reason вердикта =
    // Verification.Notes, который Slim обнуляет для списков/ответа); run для ответа
    // облегчается отдельно (SlimRun, контракт master). null — эпика нет (как у Project).
    // modelResolver (Б4) — готовая функция модели чата от контроллера: исполнители получают
    // modelLabel/modelFull (оба маршрута GetFeatures/GetFeature — одна и та же функция).
    public FeatureRun? ProjectFull(string featureId, string ownerId,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        var epic = owner.FirstOrDefault(t => t.Id == featureId && t.Role == TaskRoles.Feature);
        return epic is null ? null : BuildCore(epic, FeatureTasks(epic, owner), null, sessions, modelResolver);
    }

    // Все фичи владельца (REST без sessionId): каждый эпик → свой FeatureRun.
    // sessions (Б7) — снимок сессий владельца: исполнители (chatId/chatArchived) fail-closed,
    // как в ProjectForSession/ProjectFull — все ветвления GetFeatures дают одинаковых исполнителей.
    // modelResolver (Б4) — как в ProjectFull: одинаковые modelLabel/modelFull во всех ветвлениях.
    public IReadOnlyList<FeatureRun> ProjectAll(string ownerId,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        return owner
            .Where(t => t.Role == TaskRoles.Feature)
            .Select(epic => Build(epic, FeatureTasks(epic, owner), null, sessions, modelResolver))
            .ToList();
    }

    // Фичи чата (REST ?sessionId=): эпик связан с чатом через SourceSessionId/
    // ExecutorLaunchedBySessionId (разрез §2: «фича переезжает за оркестратором»).
    // Порядок (правило v2): 1) не закрытые с Attention — по since ascending (ждущая дольше всех);
    // 2) не закрытые идущие — по max ClaudeStartedAt descending; 3) не закрытые прочие —
    // по LastActivityAt descending; 4) закрытые — по LastActivityAt descending.
    // Пилюля показывает runs[0], поэтому порядок определяет бэк, фронт его не пересортирует.
    public IReadOnlyList<FeatureRun> ProjectForSession(string sessionId, string ownerId,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        var runs = owner
            .Where(t => t.Role == TaskRoles.Feature
                && (t.SourceSessionId == sessionId || t.ExecutorLaunchedBySessionId == sessionId))
            .Select(epic => Build(epic, FeatureTasks(epic, owner), null, sessions, modelResolver))
            .ToList();
        var ordered = OrderRuns(runs);
        // Фича, чья задача — TaskId чата, первой (общий сортировщик, не две копии).
        var byId = owner.ToDictionary(t => t.Id);
        var own = OwnFeatureId(sessionId, sessions ?? Array.Empty<FeatureIndexSession>(), byId);
        return PreferFirst(ordered, r => r.FeatureId, own);
    }

    // Порядок фич (правило v2): 4 группы, внутри каждой — свой ключ сортировки.
    // internal — для золотых тестов (InternalsVisibleTo ClaudeHomeServer.Tasks.Tests).
    internal static IReadOnlyList<FeatureRun> OrderRuns(List<FeatureRun> runs)
    {
        // 4 группы — по Bucket (единая точка правды, план С0 §2: bucket и OrderRuns не
        // расходятся): attention → running → idle → closed, у каждой — свой ключ сортировки.

        // 1. Не закрытые с Attention: по since ascending (ждущая дольше всех — первая).
        var withAttention = runs
            .Where(r => Bucket(r) == "attention")
            .OrderBy(r => r.Attention!.Since ?? DateTime.MaxValue)
            .ToList();

        // 2. Не закрытые идущие (Now не пуст, Attention null): по max ClaudeStartedAt descending.
        //    Now[0] — задача с самым поздним ClaudeStartedAt (Now отсортирован по убыванию).
        //    Guard: Now пуст — один «отравленный» run не должен ронять весь список/индекс
        //    (до фикса машины состояний так попадал merge-задача в Todo: state=merging →
        //    bucket=running, Now пуст → r.Now[0] падал → 500). Пустой Now → последняя
        //    активность, затем MinValue.
        var running = runs
            .Where(r => Bucket(r) == "running")
            .OrderByDescending(r => r.Now.Count > 0
                ? r.Now[0].StartedAt ?? DateTime.MinValue
                : r.LastActivityAt ?? DateTime.MinValue)
            .ToList();

        // 3. Не закрытые прочие (без Attention, не идут): по LastActivityAt descending.
        var other = runs
            .Where(r => Bucket(r) == "idle")
            .OrderByDescending(r => r.LastActivityAt ?? DateTime.MinValue)
            .ToList();

        // 4. Закрытые: в конец, по LastActivityAt descending.
        var closed = runs
            .Where(r => Bucket(r) == "closed")
            .OrderByDescending(r => r.LastActivityAt ?? DateTime.MinValue)
            .ToList();

        var result = new List<FeatureRun>(runs.Count);
        result.AddRange(withAttention);
        result.AddRange(running);
        result.AddRange(other);
        result.AddRange(closed);
        return result;
    }

    // Фича, чья задача — TaskId чата (executor-членство): FeatureId задачи или id эпика.
    // null — чат не исполнитель задачи фичи (оркестратор/куратор/нет TaskId) → прежний порядок.
    // internal — для тестов (InternalsVisibleTo ClaudeHomeServer.Tasks.Tests).
    internal static string? OwnFeatureId(
        string sessionId,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        IReadOnlyDictionary<string, TaskItem> byId)
    {
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session?.TaskId is null) return null;
        if (!byId.TryGetValue(session.TaskId, out var task)) return null;
        return task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);
    }

    // Общий сортировщик «своя фича первой»: элемент с preferredId — первым, остальное —
    // в исходном порядке. preferredId == null или не найден → исходный список.
    // internal — для тестов; используется и ProjectForSession, и BuildIndexCore (не две копии).
    internal static IReadOnlyList<T> PreferFirst<T>(
        IReadOnlyList<T> items,
        Func<T, string> featureIdOf,
        string? preferredId)
    {
        if (preferredId is null) return items;
        var idx = -1;
        for (var i = 0; i < items.Count; i++)
            if (featureIdOf(items[i]) == preferredId) { idx = i; break; }
        if (idx < 0) return items;
        var result = new List<T>(items.Count);
        result.Add(items[idx]);
        for (var i = 0; i < items.Count; i++)
            if (i != idx) result.Add(items[i]);
        return result;
    }

    // Эпик + все его дочерние (FeatureId == epic.Id) из уже отобранных задач владельца.
    private static List<TaskItem> FeatureTasks(TaskItem epic, IReadOnlyList<TaskItem> owner)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(owner.Where(t => t.FeatureId == epic.Id));
        return all;
    }

    // То же, но из lookup, сгруппированного по FeatureId (BuildIndexCore): эпик кладётся
    // первым (сторож Build), дочерние — по ключу lookup; полного скана owner на эпик нет.
    private static List<TaskItem> FeatureTasks(TaskItem epic, ILookup<string, TaskItem> byFeature)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(byFeature[epic.Id]);
        return all;
    }

    // ─── Б1: индекс «чат → фичи» (контракт С0, GET /api/tasks/features/index) ────────────────

    // Обёртка для REST (Б3): owner = задачи владельца (per-owner, fail-closed), сессии — узкий
    // снимок от контроллера (SessionManager). Ядро — чистая функция <see cref="BuildIndexCore"/>.
    public FeatureIndex BuildIndex(
        string ownerId,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        string? projectId = null,
        DateTime? now = null)
        => BuildIndexCore(tasks.GetByOwner(ownerId).ToList(), sessions, projectId, now);

    // Чистое ядро индекса (на нём золотые тесты, как на <see cref="Build"/>): задачи владельца
    // (owner) + узкий снимок сессий (sessions) → карта членства (Chats) и строки-заготовки фич
    // (Features). Хранилище сессий внутри проектора НЕ трогает (шов решён в С0).
    //
    // Членство чата в фиче F — сильнейший вид на пару «чат × фича» (key > curator > orchestrator
    // > executor; key добавит Б2):
    //   executor     — Session.TaskId → задача с FeatureId == F (или сама эпик F: Role=feature);
    //   curator      — epic.SourceSessionId == чат;
    //   orchestrator — epic.ExecutorLaunchedBySessionId == чат ИЛИ ExecutorLaunchedBySessionId
    //                  дочерней задачи фичи == чат.
    // SourceSessionId дочерних задач — НЕ членство (слабая связь: чат, создавший задачу, в фичу
    // не «входит» — показывает только плашку).
    //
    // Задачи группируются по FeatureId ОДИН раз (ToLookup) — уходит O(E×T) старого FeatureTasks.
    internal static FeatureIndex BuildIndexCore(
        IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        string? projectId = null,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _ = now ?? DateTime.UtcNow; // «сейчас» — для длительностей (Б2); Б1 пока не трогает

        // Задача по id — для executor-членства (Session.TaskId → задача).
        var byId = owner.ToDictionary(t => t.Id);
        // Эпики (role=feature) — источники curator/orchestrator-членства.
        var epics = owner.Where(t => t.Role == TaskRoles.Feature).ToList();
        // Задачи, сгруппированные по FeatureId, ОДИН раз (ToLookup): O(E×T) старого
        // FeatureTasks (полный скан owner на каждый эпик) уходит.
        var byFeature = owner.Where(t => t.FeatureId is not null).ToLookup(t => t.FeatureId!);

        // curator: кто создал эпик (SourceSessionId) → фичи.
        var curatorBySession = new Dictionary<string, HashSet<string>>();
        foreach (var epic in epics)
            if (epic.SourceSessionId is not null)
                AddToSet(curatorBySession, epic.SourceSessionId, epic.Id);

        // orchestrator: кто запускал исполнителя эпика (ExecutorLaunchedBySessionId эпика)
        // ИЛИ дочерней задачи фичи (ExecutorLaunchedBySessionId задачи) → фичи.
        var orchestratorBySession = new Dictionary<string, HashSet<string>>();
        foreach (var epic in epics)
            if (epic.ExecutorLaunchedBySessionId is not null)
                AddToSet(orchestratorBySession, epic.ExecutorLaunchedBySessionId, epic.Id);
        foreach (var t in owner)
            if (t.FeatureId is not null && t.ExecutorLaunchedBySessionId is not null)
                AddToSet(orchestratorBySession, t.ExecutorLaunchedBySessionId, t.FeatureId);

        // Область: сессии проекта (или все, если projectId не задан).
        var inScope = projectId is null
            ? sessions
            : sessions.Where(s => s.ProjectId == projectId);

        // sessionId → featureId → (сила, вид, role, stepRef, lane); на пару «чат × фича» —
        // сильнейший вид (Add держит минимум по Strength).
        var map = new Dictionary<string,
            Dictionary<string, (int Strength, string Kind, string? Role, string? StepRef, string? Lane)>>();

        foreach (var s in inScope)
        {
            // executor: Session.TaskId → задача с FeatureId == F (или сама эпик F).
            if (s.TaskId is not null && byId.TryGetValue(s.TaskId, out var task))
            {
                var featureId = task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);
                if (featureId is not null)
                    Add(map, s.Id, featureId, KindStrength("executor"), "executor",
                        task.Role, task.StepRef, task.Lane);
            }

            // curator: epic.SourceSessionId == чат.
            if (curatorBySession.TryGetValue(s.Id, out var curators))
                foreach (var f in curators)
                    Add(map, s.Id, f, KindStrength("curator"), "curator", null, null, null);

            // orchestrator: эпик/дочерняя задача запускала исполнителя из этого чата.
            if (orchestratorBySession.TryGetValue(s.Id, out var orchestrators))
                foreach (var f in orchestrators)
                    Add(map, s.Id, f, KindStrength("orchestrator"), "orchestrator", null, null, null);
        }

        // Строки-заготовки фич (Features). Область фич: эпик в проекте ИЛИ хотя бы один чат
        // фичи в области (map). Эпик, которого нет в owner (удалён), строку не даёт — фича
        // исчезает целиком (висячий FeatureId дочерних не поднимает её).
        var featureIdsInScope = new HashSet<string>();
        foreach (var epic in epics)
            if (projectId is null || epic.ProjectId == projectId)
                featureIdsInScope.Add(epic.Id);
        foreach (var feats in map.Values)
            foreach (var f in feats.Keys)
                featureIdsInScope.Add(f);

        // Строки фич (контракт «Контракт индекса фич — утверждён»): bucket/rail/chips/attention/
        // summary/now0/ключевой чат/chatCount из FeatureRun; порядок — OrderRuns (фронт не
        // пересортировывает). Б1 держал заготовку (id/название/проект) по FeatureId.
        var survivingEpics = featureIdsInScope
            .Select(id => epics.FirstOrDefault(e => e.Id == id))
            .Where(e => e is not null)
            .Cast<TaskItem>()
            .ToList();
        // Снимок — и в Build (fail-closed полей run: now/attention/исполнители), и в BuildRow
        // (строка): один снимок — одна семантика, живые чаты не теряются, чужие не попадают.
        var runs = survivingEpics
            .Select(epic => Build(epic, FeatureTasks(epic, byFeature), now, sessions))
            .ToList();
        var rows = OrderRuns(runs)
            .Select(run => BuildRow(run, FeatureTasks(run.Epic, byFeature), sessions,
                ChatCount(map, run.FeatureId)))
            .ToList();
        var featureOrder = new Dictionary<string, int>();
        for (var i = 0; i < rows.Count; i++)
            featureOrder[rows[i].FeatureId] = i;

        // Chats: sessionId → записи, упорядоченные по силе связи (key > curator > orchestrator
        // > executor); при равенстве — по порядку фичи в Features. Членство в фичу, чей эпик
        // исчез (удалён), не показываем — фича «исчезает целиком», чат за ней не тянется.
        // Фича, чья задача — TaskId чата, первой (общий сортировщик PreferFirst, не две копии).
        var surviving = new HashSet<string>(rows.Select(r => r.FeatureId));
        var chats = new Dictionary<string, List<FeatureIndexChatEntry>>();
        foreach (var (sessionId, feats) in map)
        {
            var entries = feats
                .Where(kv => surviving.Contains(kv.Key))
                .OrderBy(kv => kv.Value.Strength)
                .ThenBy(kv => featureOrder.TryGetValue(kv.Key, out var i) ? i : int.MaxValue)
                .Select(kv => new FeatureIndexChatEntry(
                    kv.Key, kv.Value.Kind, kv.Value.Role, kv.Value.StepRef, kv.Value.Lane))
                .ToList();
            // Фича, чья задача — TaskId чата, первой (общий сортировщик, не две копии).
            var own = OwnFeatureId(sessionId, sessions, byId);
            entries = PreferFirst(entries, e => e.FeatureId, own).ToList();
            if (entries.Count > 0)
                chats[sessionId] = entries;
        }

        return new FeatureIndex { Chats = chats, Features = rows };
    }

    // Чаты фичи (GET /api/tasks/features/{id}, chats[]; Б4): все чаты фичи из снимка владельца.
    // Чат задачи фичи (Session.TaskId = задача) → executor; чат эпика (Session.TaskId = эпик) →
    // curator (создал эпик, SourceSessionId) → orchestrator (запустил исполнителя эпика,
    // ExecutorLaunchedBySessionId) → key (без признака). Чужой/удалённый чат не в снимке
    // (fail-closed, как исполнители). modelLabel/modelFull — из modelResolver (как у исполнителей).
    // Только для features/{id}: в SlimRun/индекс chats не входит.
    internal static List<FeatureChat> BuildFeatureChats(
        TaskItem epic,
        IReadOnlyList<TaskItem> allFeatureTasks,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        // taskId → задача фичи (эпик + дочерние). Чужая задача (не в списке) → чат не в фиче.
        var byTaskId = new Dictionary<string, TaskItem> { [epic.Id] = epic };
        foreach (var t in allFeatureTasks)
            if (!ReferenceEquals(t, epic))
                byTaskId[t.Id] = t;

        var chats = new List<FeatureChat>();
        foreach (var s in sessions)
        {
            if (s.TaskId is null || !byTaskId.TryGetValue(s.TaskId, out var task)) continue;
            // kind: чат эпика — по признакам (curator/orchestrator), иначе key; чат задачи — executor.
            var kind = ReferenceEquals(task, epic)
                ? (epic.SourceSessionId == s.Id ? "curator"
                    : epic.ExecutorLaunchedBySessionId == s.Id ? "orchestrator"
                    : "key")
                : "executor";
            (string? label, string? full) = (null, null);
            if (modelResolver is not null) (label, full) = modelResolver(s);
            chats.Add(new FeatureChat(
                s.Id, kind, s.TaskId, s.Archived,
                task.PersonaId, task.Role, task.StepRef, task.Lane,
                label, full));
        }
        return chats;
    }

    // Сила связи вида: key(0) > curator(1) > orchestrator(2) > executor(3).
    // key добавит Б2 (цепочка ключевого чата); пока в членстве не участвует.
    private static int KindStrength(string kind) => kind switch
    {
        "key" => 0,
        "curator" => 1,
        "orchestrator" => 2,
        _ => 3, // executor
    };

    // Добавляет (или усиливает) членство чата в фичу: на пару «чат × фича» держим сильнейший
    // вид (минимум по Strength) и его данные (role/stepRef/lane — только у executor).
    private static void Add(
        Dictionary<string, Dictionary<string, (int, string, string?, string?, string?)>> map,
        string sessionId, string featureId, int strength, string kind,
        string? role, string? stepRef, string? lane)
    {
        if (!map.TryGetValue(sessionId, out var feats))
            map[sessionId] = feats = new();
        if (!feats.TryGetValue(featureId, out var cur) || strength < cur.Item1)
            feats[featureId] = (strength, kind, role, stepRef, lane);
    }

    private static void AddToSet(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new();
        set.Add(value);
    }

    // ─── Б2: строка фичи (контракт «Контракт индекса фич — утверждён») ─────────────────────
    // Значения bucket/rail/chips/attention/now0/summary/lastActivityAt/loops/steps* берутся
    // из FeatureRun (тон выводится из Attention, MAJOR-1) — проектор не пересчитывает.
    // internal — для золотых тестов (InternalsVisibleTo ClaudeHomeServer.Tasks.Tests).

    // Строка фичи: FeatureRun + задачи фичи (эпик + дочерние, для ключевого чата) + сессии
    // (ключевой чат: archived/project) + chatCount.
    // Чистая функция — на ней золотые тесты (FeatureIndexRowTests).
    internal static FeatureIndexRow BuildRow(
        FeatureRun run,
        IReadOnlyList<TaskItem> featureTasks,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        int chatCount)
    {
        // Ключевой чат: первое живое звено цепочки (удалённый/чужой чат → следующее звено).
        var key = KeySession(run.Epic, featureTasks, sessions);
        var keySession = key is null ? null : sessions.FirstOrDefault(s => s.Id == key);
        return new FeatureIndexRow(
            run.FeatureId,
            run.Epic.Title,
            run.Epic.ProjectId,
            Bucket(run),
            run.State,
            AttentionOf(run, sessions),
            RailOf(run),
            ChipsOf(run),
            run.StepsAccepted,
            run.StepsTotal,
            run.Verdicts.Loops,
            Now0Of(run),
            run.LastActivityAt,
            SummaryOf(run),
            key,
            keySession?.Archived ?? false,
            keySession?.ProjectId,
            chatCount,
            run.Epic.WorktreeBranch,
            run.Epic.CreatedAt,
            featureTasks.Count,
            NowPersonaIdsOf(run));
    }

    // Ключевой чат фичи (контракт §1): первое ЖИВОЕ звено цепочки
    // epic.SourceSessionId → epic.ExecutorLaunchedBySessionId → самый частый
    // ExecutorLaunchedBySessionId у дочерних. Удалённый/чужой чат (не в снимке владельца
    // sessions) пропускается — берётся следующее звено; живых звеньев нет → null.
    internal static string? KeySession(TaskItem epic, IReadOnlyList<TaskItem> featureTasks,
        IReadOnlyCollection<FeatureIndexSession> sessions)
    {
        var live = new HashSet<string>(sessions.Select(s => s.Id));
        return KeyChain(epic, featureTasks).FirstOrDefault(live.Contains);
    }

    // Цепочка ключевого чата в порядке звеньев (без дублей): epic.SourceSessionId →
    // epic.ExecutorLaunchedBySessionId → самый частый ExecutorLaunchedBySessionId у дочерних
    // (эпик исключён; при равенстве частот — лексикографически меньший id, детерминизм).
    internal static List<string> KeyChain(TaskItem epic, IReadOnlyList<TaskItem> featureTasks)
    {
        var chain = new List<string>();
        if (epic.SourceSessionId is not null) chain.Add(epic.SourceSessionId);
        if (epic.ExecutorLaunchedBySessionId is not null) chain.Add(epic.ExecutorLaunchedBySessionId);
        var mostFrequent = featureTasks
            .Where(t => !ReferenceEquals(t, epic) && t.ExecutorLaunchedBySessionId is not null)
            .GroupBy(t => t.ExecutorLaunchedBySessionId!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefault();
        if (mostFrequent is not null) chain.Add(mostFrequent);
        return chain.Distinct().ToList();
    }

    // Состояние фичи (9 значений, приоритет сверху вниз; первое совпавшее побеждает):
    // merged → closed → stopped → waiting → merging → merge_pending → running → not_started → idle.
    // Чистая функция от полей эпика/merge/тревог/«сейчас» — на ней золотые тесты (FeatureProjectorTests).
    // hasExplicitWait — явный wait (AwaitingHumanSince), КРОМЕ вычисленного «мерж» (тот → merge_pending).
    internal static string ComputeState(
        TaskItem epic,
        TaskItem? merge,
        bool finalAccepted,
        bool hasStopped,
        bool hasExplicitWait,
        int nowCount,
        bool anyStarted)
    {
        // 1. merged: merge-задача Done.
        if (merge is not null && merge.Status == TaskItemStatus.Done) return FeatureStates.Merged;
        // 2. closed: эпик Done, мержа нет (merged уже отфильтрован выше).
        if (epic.Status == TaskItemStatus.Done) return FeatureStates.Closed;
        // 3. stopped: есть остановленная задача (как тревога kind=stopped).
        if (hasStopped) return FeatureStates.Stopped;
        // 4. waiting: явное ожидание человека (как тревога kind=wait, КРОМЕ «мерж»).
        if (hasExplicitWait) return FeatureStates.Waiting;
        // 5. merging: merge-задача запущена (InProgress) — мерж идёт.
        if (merge is not null && merge.Status == TaskItemStatus.InProgress) return FeatureStates.Merging;
        // 6. merge_pending: финал принят, merge-задачи нет ИЛИ она создана, но не запущена
        //    (Todo) — ждёт согласия/запуска (тревога «мерж», как и у merge is null).
        if (finalAccepted && (merge is null || merge.Status == TaskItemStatus.Todo)) return FeatureStates.MergePending;
        // 7. running: Now не пуст (идут задачи).
        if (nowCount > 0) return FeatureStates.Running;
        // 8. not_started: ни одна задача фичи не стартовала.
        if (!anyStarted) return FeatureStates.NotStarted;
        // 9. idle: иначе (ничего не идёт, ничего не ждёт, фича не закрыта).
        return FeatureStates.Idle;
    }

    // Группа фичи (контракт §1, план С0 §2 — единая точка правды: bucket строки и порядок
    // OrderRuns выведены из state, не расходятся): closed → attention → running → idle.
    // Изменение против старой версии: merging (идёт мерж) → running, а не attention —
    // «мерж идёт» теперь не «ждёт вас» (merge_pending — ждёт, merging — идёт).
    internal static string Bucket(FeatureRun run) => run.State switch
    {
        FeatureStates.Merged => "closed",
        FeatureStates.Closed => "closed",
        FeatureStates.Stopped => "attention",
        FeatureStates.Waiting => "attention",
        FeatureStates.MergePending => "attention",
        FeatureStates.Merging => "running",
        FeatureStates.Running => "running",
        FeatureStates.NotStarted => "idle",
        _ => "idle", // idle
    };

    // Чипы-фильтры (контракт §1): open = bucket ≠ closed; closed = bucket = closed;
    // у тревоги — чип по attention: stopped → stopped, merge → merge, wait (явный) → waiting;
    // bucket = running (идёт, без тревоги) → чип «running».
    // (Чип «waiting» ≠ attention.kind «wait»: словарь чипов и словарь kind различаются.)
    internal static List<string> ChipsOf(FeatureRun run)
    {
        var chips = new List<string>();
        if (run.Closed)
        {
            chips.Add("closed");
        }
        else
        {
            chips.Add("open");
            if (run.Attention is not null)
            {
                string chip = run.Attention.Kind switch
                {
                    "stopped" => "stopped",
                    "merge" => "merge",
                    _ => "waiting",
                };
                chips.Add(chip);
            }
            else if (run.Now.Count > 0)
            {
                // bucket = running (идёт, без тревоги) → чип «running».
                chips.Add("running");
            }
        }
        return chips;
    }

    // «Сейчас»: уникальные PersonaId идущих задач (Now, по убыванию ClaudeStartedAt) в порядке
    // их старта; null-персоны пропускаются. Пустой список — фича не идёт.
    internal static List<string> NowPersonaIdsOf(FeatureRun run)
    {
        var seen = new HashSet<string>();
        var ids = new List<string>();
        foreach (var n in run.Now)
            if (n.PersonaId is not null && seen.Add(n.PersonaId))
                ids.Add(n.PersonaId);
        return ids;
    }

    // Тревога строки: из FeatureRun.Attention; kind=merge → merge (reason у merge — null);
    // sessionId — LinkedSessionId, прошедший по снимку живых сессий владельца (иначе null,
    // как в KeySession: мёртвый/чужой чат наружу не уходит).
    internal static FeatureIndexAttention? AttentionOf(FeatureRun run, IReadOnlyCollection<FeatureIndexSession> sessions)
    {
        var a = run.Attention;
        if (a is null) return null;
        bool isMerge = a.Kind == "merge";
        var live = new HashSet<string>(sessions.Select(s => s.Id));
        string? sessionId = a.LinkedSessionId is not null && live.Contains(a.LinkedSessionId)
            ? a.LinkedSessionId : null;
        return new FeatureIndexAttention(
            isMerge ? "merge" : a.Kind,
            a.Since,
            isMerge ? null : a.Reason,
            a.PersonaId,
            sessionId,
            a.TaskId); // Б7: id задачи тревоги (FeatureAttention.TaskId)
    }

    // Рельса: ровно 7 стадий FeatureRun.Stages → сегменты {key, state, tone}.
    // Нормализация закрытой фичи (контракт С0): у closed active нет — все стадии, кроме absent,
    // приводятся к done, tone=null (merge в том числе: «честный мерж» epic Done без merge-задачи
    // иначе даёт merge=active, tone=run). absent остаётся absent.
    internal static List<FeatureIndexRailSegment> RailOf(FeatureRun run) =>
        run.Stages
            .Select(s =>
            {
                if (!run.Closed)
                    return new FeatureIndexRailSegment(s.Key, s.State, s.Tone);
                var state = s.State == "absent" ? "absent" : "done";
                return new FeatureIndexRailSegment(s.Key, state, null);
            })
            .ToList();

    // «Сейчас» (now0): первая InProgress-задача (Now[0], по убыванию ClaudeStartedAt); null — не идёт.
    internal static FeatureIndexNow? Now0Of(FeatureRun run)
    {
        if (run.Now.Count == 0) return null;
        var n = run.Now[0];
        return new FeatureIndexNow(n.PersonaId, n.Stage, n.StepRef);
    }

    // Итог: из FeatureRun.Summary (mergedAt + durationMinutes); null — фича не закрыта.
    internal static FeatureIndexSummary? SummaryOf(FeatureRun run)
    {
        var s = run.Summary;
        if (s is null) return null;
        return new FeatureIndexSummary(s.MergedAt, s.DurationMinutes);
    }

    // Чатов фичи в этом ответе: число сессий в map, чья карта фич содержит featureId.
    private static int ChatCount(
        Dictionary<string, Dictionary<string, (int, string, string?, string?, string?)>> map,
        string featureId)
    {
        int count = 0;
        foreach (var feats in map.Values)
            if (feats.ContainsKey(featureId)) count++;
        return count;
    }
}
