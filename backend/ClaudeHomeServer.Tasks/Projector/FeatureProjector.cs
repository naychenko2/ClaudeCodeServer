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
        var cut = children.Where(t => t.Role == TaskRoles.Cut).ToList();
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
        // 2) Если таких нет (все стадии с задачами пройдены) — merge (последняя),
        //    если не пройдена: финал зелёный, merge-задачи ещё нет → мерж active.
        // 3) -1 — все стадии пройдены.
        int activeIndex = -1;
        for (int i = 0; i < isDone.Length; i++)
        {
            if (!isDone[i] && stageDefs[i].tasks.Count > 0) { activeIndex = i; break; }
        }
        if (activeIndex == -1 && !isDone[^1])
            activeIndex = isDone.Length - 1;

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

            // Тон (только у active, иначе null): loop > stopped > wait > run.
            string? tone = null;
            if (state == "active")
            {
                // loop: активная prod, куда вернули работу (финал или проверка вернули, доработка идёт).
                bool isLoop = key == "prod"
                    && ((final is not null
                        && final.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks)
                        || qa.Any(t => t.Verification?.Verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks));
                // stopped: идущая задача стадии с ExecutorStoppedAt.
                bool isStopped = tasks.Any(t => t.Status == TaskItemStatus.InProgress && t.ExecutorStoppedAt is not null);
                // wait: эпик ждёт человека.
                bool isWait = epic.AwaitingHumanSince is not null;

                if (isLoop) tone = "loop";
                else if (isStopped) tone = "stopped";
                else if (isWait) tone = "wait";
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
            });
        }

        // ─── Б3: «Сейчас», тревоги, итог ───────────────────────────────────────────────────

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
            // МergedAt: CompletedAt merge-задачи; если мержа нет — CompletedAt эпика.
            var mergedAt = merge is not null && merge.Status == TaskItemStatus.Done
                ? merge.CompletedAt
                : epic.CompletedAt;
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
    // step/rework → prod; consult/прочее → prod. Эпик (feature) сюда не доходит:
    // он контейнер фичи и исключён из Now (см. «Сейчас» в Build).
    private static string RoleToStage(string? role) => role switch
    {
        TaskRoles.Cut => "cut",
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
        // 1. Не закрытые с Attention: по since ascending (ждущая дольше всех — первая).
        var withAttention = runs
            .Where(r => !r.Closed && r.Attention is not null)
            .OrderBy(r => r.Attention!.Since ?? DateTime.MaxValue)
            .ToList();

        // 2. Не закрытые идущие (Now не пуст, Attention null): по max ClaudeStartedAt descending.
        //    Now[0] — задача с самым поздним ClaudeStartedAt (Now отсортирован по убыванию).
        var running = runs
            .Where(r => !r.Closed && r.Attention is null && r.Now.Count > 0)
            .OrderByDescending(r => r.Now[0].StartedAt ?? DateTime.MinValue)
            .ToList();

        // 3. Не закрытые прочие (без Attention, не идут): по LastActivityAt descending.
        var other = runs
            .Where(r => !r.Closed && r.Attention is null && r.Now.Count == 0)
            .OrderByDescending(r => r.LastActivityAt ?? DateTime.MinValue)
            .ToList();

        // 4. Закрытые: в конец, по LastActivityAt descending.
        var closed = runs
            .Where(r => r.Closed)
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
    private static List<TaskItem> FeatureTasks(TaskItem epic, List<TaskItem> owner)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(owner.Where(t => t.FeatureId == epic.Id));
        return all;
    }
}
