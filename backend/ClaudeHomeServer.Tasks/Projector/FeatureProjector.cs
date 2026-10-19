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
    public static FeatureRun Build(TaskItem epic, IEnumerable<TaskItem> featureTasks, DateTime? now = null)
    {
        var nowUtc = now ?? DateTime.UtcNow;
        ArgumentNullException.ThrowIfNull(epic);
        ArgumentNullException.ThrowIfNull(featureTasks);

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
        var stepChains = BuildStepChains(steps, verdictTasks, nowUtc);

        // Возвраты по источникам (ревью/проверка/финал): число возвратов и сколько открыто.
        var returnsBySource = BuildReturnsBySource(reviews, qa, final, steps);

        // Линии (back/front/прочее): исполнители и счётчики.
        var lanes = BuildLanes(steps, verdictTasks);

        // ─── Б3: «Сейчас», тревоги, итог ───────────────────────────────────────────────────
        // Attention вычисляется ДО цикла стадий: тон активной стадии — производное от него
        // (MAJOR-1 финального ревью: тон и Attention не должны расхождаться).

        // Тревога: приоритет stopped → wait явный → wait «мерж».
        FeatureAttention? attention = null;

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
                LinkedSessionId = stoppedTask.LinkedSessionId,
            };
        }

        // 2. wait явный: AwaitingHumanSince у эпика или задачи (ждущий дольше всех).
        if (attention is null)
        {
            var waitTask = featureTasks
                .Where(t => t.AwaitingHumanSince is not null)
                .OrderBy(t => t.AwaitingHumanSince)
                .FirstOrDefault();
            if (waitTask is not null)
            {
                attention = new FeatureAttention
                {
                    Kind = "wait",
                    TaskId = waitTask.Id,
                    Since = waitTask.AwaitingHumanSince,
                    Reason = waitTask.AwaitingHumanReason,
                    PersonaId = waitTask.PersonaId,
                    LinkedSessionId = waitTask.LinkedSessionId,
                };
            }
        }

        // 3. wait «мерж» — вычисляется, не хранится: финал принят (accepted/awr, закрыт),
        //    а merge-задачи нет или она не Done. since = CompletedAt финала.
        if (attention is null)
        {
            bool finalAccepted = final is not null
                && final.Status == TaskItemStatus.Done
                && final.Verification?.Verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks;
            bool mergeNotDone = merge is null || merge.Status != TaskItemStatus.Done;
            if (finalAccepted && mergeNotDone)
            {
                attention = new FeatureAttention
                {
                    Kind = "wait",
                    TaskId = final!.Id,
                    Since = final.CompletedAt,
                    Reason = "мерж",
                    PersonaId = final.PersonaId,
                    LinkedSessionId = final.LinkedSessionId,
                };
            }
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
                else if (attention is not null && attention.Kind == "wait") tone = "wait";
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
                    StartedAt = TaskStart(t),
                    CompletedAt = t.CompletedAt,
                    Reason = t.Verification?.Notes,
                }).ToList()
                : [];
            string? longestStepRef = key == "review" ? LongestStepRef(stepChains) : null;

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
                LinkedSessionId = t.LinkedSessionId,
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
            Attention = attention,
            StepsAccepted = stepsAccepted,
            StepsTotal = stepsTotal,
            LastActivityAt = lastActivityAt,
            Closed = closed,
            Summary = summary,
        };
    }

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
        List<TaskItem> steps, List<TaskItem> verdictTasks, DateTime nowUtc)
    {
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

    // Одна фича по id эпика (владельца ownerId). null — эпика нет.
    public FeatureRun? Project(string featureId, string ownerId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        var epic = owner.FirstOrDefault(t => t.Id == featureId && t.Role == TaskRoles.Feature);
        return epic is null ? null : Build(epic, FeatureTasks(epic, owner));
    }

    // Все фичи владельца (REST без sessionId): каждый эпик → свой FeatureRun.
    public IReadOnlyList<FeatureRun> ProjectAll(string ownerId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        return owner
            .Where(t => t.Role == TaskRoles.Feature)
            .Select(epic => Build(epic, FeatureTasks(epic, owner)))
            .ToList();
    }

    // Фичи чата (REST ?sessionId=): эпик связан с чатом через SourceSessionId/
    // ExecutorLaunchedBySessionId (разрез §2: «фича переезжает за оркестратором»).
    // Порядок (правило v2): 1) не закрытые с Attention — по since ascending (ждущая дольше всех);
    // 2) не закрытые идущие — по max ClaudeStartedAt descending; 3) не закрытые прочие —
    // по LastActivityAt descending; 4) закрытые — по LastActivityAt descending.
    // Пилюля показывает runs[0], поэтому порядок определяет бэк, фронт его не пересортирует.
    public IReadOnlyList<FeatureRun> ProjectForSession(string sessionId, string ownerId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        var runs = owner
            .Where(t => t.Role == TaskRoles.Feature
                && (t.SourceSessionId == sessionId || t.ExecutorLaunchedBySessionId == sessionId))
            .Select(epic => Build(epic, FeatureTasks(epic, owner)))
            .ToList();
        return OrderRuns(runs);
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
        var running = runs
            .Where(r => Bucket(r) == "running")
            .OrderByDescending(r => r.Now[0].StartedAt ?? DateTime.MinValue)
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
        var runs = survivingEpics
            .Select(epic => Build(epic, FeatureTasks(epic, byFeature), now))
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
            if (entries.Count > 0)
                chats[sessionId] = entries;
        }

        return new FeatureIndex { Chats = chats, Features = rows };
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
            chatCount);
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

    // Группа фичи (контракт §1, план С0 §2 — единая точка правды: bucket строки и порядок
    // OrderRuns выведены из неё, не расходятся): closed → attention → running → idle.
    internal static string Bucket(FeatureRun run)
    {
        if (run.Closed) return "closed";
        if (run.Attention is not null) return "attention";
        if (run.Now.Count > 0) return "running";
        return "idle";
    }

    // Чипы-фильтры (контракт §1): open = bucket ≠ closed; closed = bucket = closed;
    // у тревоги — чип по attention: stopped → stopped, wait/«мерж» → merge, wait (явный) → waiting.
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
                    // wait: «мерж» → merge, явный wait → waiting.
                    _ => run.Attention.Reason == "мерж" ? "merge" : "waiting",
                };
                chips.Add(chip);
            }
        }
        return chips;
    }

    // Тревога строки: из FeatureRun.Attention; kind «wait»+«мерж» → merge; reason у merge — null;
    // sessionId — LinkedSessionId, прошедший по снимку живых сессий владельца (иначе null,
    // как в KeySession: мёртвый/чужой чат наружу не уходит).
    internal static FeatureIndexAttention? AttentionOf(FeatureRun run, IReadOnlyCollection<FeatureIndexSession> sessions)
    {
        var a = run.Attention;
        if (a is null) return null;
        bool isMerge = a.Kind == "wait" && a.Reason == "мерж";
        var live = new HashSet<string>(sessions.Select(s => s.Id));
        string? sessionId = a.LinkedSessionId is not null && live.Contains(a.LinkedSessionId)
            ? a.LinkedSessionId : null;
        return new FeatureIndexAttention(
            isMerge ? "merge" : a.Kind,
            a.Since,
            isMerge ? null : a.Reason,
            a.PersonaId,
            sessionId);
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
