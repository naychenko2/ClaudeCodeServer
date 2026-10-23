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
        var design = children.Where(t => t.Role == TaskRoles.Design).ToList();
        var plan = children.Where(t => t.Role == TaskRoles.Plan).ToList();
        // Безролевые задачи (role=null): ни в одну стадию не попадают, но обязаны быть в
        // ленте событий (W4-DES-B: хронология — ВСЕ задачи с featureId).
        var unroled = children.Where(t => t.Role is null).ToList();

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

        // Ready: финал ЗАКРЫТ (Done) и принят (accepted/accepted_with_remarks) — единый хелпер
        // IsFinalAccepted (тот же, что у merge_pending/merged и гарда role=merge в TaskManager).
        bool ready = IsFinalAccepted(final);

        // ─── Б2: цепочки шагов, источники возврата, линии ─────────────────────────────────
        // Задачи, несущие вердикт витка (ревью/проверка/финал) — для витков и возвратов.
        var verdictTasks = reviews.Concat(qa).Concat(final is not null ? [final] : []).ToList();

        // Цепочки: по одной на шаг (stepRef). Шаг = stepRef; в цепочку входят задача шага
        // (role=step) и её доработки (role=rework, тот же stepRef), в порядке времени.
        var accounting = FeatureStepAccounting.Build(steps, verdictTasks, plan, nowUtc, sessions);
        var stepChains = accounting.Chains;

        // Возвраты по источникам (ревью/проверка/финал): число возвратов и сколько открыто.
        var returnsBySource = BuildReturnsBySource(reviews, qa, final, steps);

        // Линии (back/front/прочее): исполнители и счётчики.
        var lanes = accounting.Lanes;

        // ─── Б3: «Сейчас», тревоги, итог ───────────────────────────────────────────────────
        // Attention вычисляется ДО цикла стадий: тон активной стадии — производное от него
        // (MAJOR-1 финального ревью: тон и Attention не должны расхождаться).

        // Тревога: приоритет stopped → wait явный → merge.
        FeatureAttention? attention = null;

        // waitTask — явный wait (AwaitingHumanSince, ждущий дольше всех); finalAccepted —
        // для merge (attention) и для state (merge_pending/merged). Вынесены в scope метода:
        // используются и в attention, и в расчёте state.
        var waitTask = featureTasks
            .Where(t => t.AwaitingHumanSince is not null)
            .OrderBy(t => t.AwaitingHumanSince)
            .FirstOrDefault();
        bool finalAccepted = IsFinalAccepted(final);

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

        // «Сейчас»: все InProgress-задачи фичи (эпик + дочерние), по убыванию ClaudeStartedAt.
        // Эпик — контейнер фичи, не стадия конвейера: идущий эпик в «Сейчас» не показываем.
        // У закрытой фичи список пуст (ни одна задача не InProgress). Вынесено до цикла стадий:
        // nowCount нужен для ComputeState, а state — для гашения тревоги при паузе (R-HOLD-B1).
        var nowList = featureTasks
            .Where(t => t.Status == TaskItemStatus.InProgress && !ReferenceEquals(t, epic))
            .OrderByDescending(t => t.ClaudeStartedAt ?? t.CreatedAt)
            .Select(t => new FeatureNow
            {
                TaskId = t.Id,
                Stage = FeatureTimeMath.RoleToStage(t.Role),
                PersonaId = t.PersonaId,
                StepRef = t.StepRef,
                Title = t.Title,
                StartedAt = t.ClaudeStartedAt,
                LinkedSessionId = LiveSession(t.LinkedSessionId)?.Id, // Б7: fail-closed (только если в снимке)
            })
            .ToList();

        // Состояние фичи (единая точка правды; из него bucket строки и порядок OrderRuns —
        // см. FeatureStates). Приоритет: merged → closed → paused → stopped → waiting → merging →
        // merge_pending → running → not_started → idle. finalAccepted/waitTask — выше (scope метода).
        // Считается ДО цикла стадий: тон активной стадии — производное от Attention, а пауза гасит
        // Attention — значит state должен быть известен раньше тона (R-HOLD-B1).
        string featureState = ComputeState(
            epic, merge, finalAccepted,
            hasStopped: stoppedTask is not null,
            hasExplicitWait: waitTask is not null,
            nowCount: nowList.Count,
            anyStarted: featureTasks.Any(t => t.ClaudeStartedAt is not null));

        // W4-HOLD-B1 (R-HOLD-B1): пауза — решение человека («фичу не вести»), а не тревога: при
        // state=paused Attention гасится — иначе фича на паузе показывает «stopped» (тон, чипы,
        // тревога). Для draft не гасим: state=draft только когда остановленных задач нет (stopped
        // стоит выше), и упавший исполнитель разреза в черновике должен быть виден.
        if (featureState == FeatureStates.Paused) attention = null;

        // W4-HOLD-D: на паузе никто не идёт — «Сейчас» пустой. SetHold прервал исполнителей
        // (feature_held), но статус остаётся InProgress: без гашения строка показывала бы
        // «Сейчас: <исполнитель>», а Now0Of/NowPersonaIds отдавали бы прерванную задачу.
        if (featureState == FeatureStates.Paused) nowList.Clear();

        // W4-HOLD-D2 (контракт §1, :364): прерванные ИМЕННО текущей паузой (R-HOLE-D находка 2):
        // пометка feature_held и стоп не раньше начала действующего удержания — «все когда-либо
        // помеченные» не годится: после ClearHold и новой паузы старые пометки — не прерванные
        // этой паузой. Без Hold поля нет вовсе. Ответ ручки POST hold и блок «на паузе» читают
        // это поле, а не Now: Now на паузе пуст. Закрытая после прервания задача из списка
        // выпадает — её ход завершён.
        var interrupted = epic.Hold is null
            ? new List<string>()
            : featureTasks
                .Where(t => t.Status != TaskItemStatus.Done
                    && t.ExecutorStopReason == ExecutorStopClassifier.FeatureHeldReason
                    && t.ExecutorStoppedAt >= epic.Hold!.Since)
                .Select(t => t.Id)
                .ToList();

        // Конвейер: стадии в порядке жизненного цикла. Порядок задаётся ЯВНЫМ списком,
        // а не выводится из порядка кода (W4-DES-B): «design» — необязательный этап после
        // «cut»; без дизайн-задач — состояние skipped, на готовность не влияет.
        var stageDefs = new (string key, List<TaskItem> tasks)[]
        {
            ("cut", cut),
            ("design", design),
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
            // Пустая необязательная «design» не может быть активной: активность
            // уходит на следующую стадию (W4-DES-B).
            if (stageDefs[activeIndex].key == "design" && stageDefs[activeIndex].tasks.Count == 0)
                activeIndex++;
        }

        // W4-3B: множество id этого run — фильтр связи «оценивает →» у исполнителей стадии.
        // В Build попадают только задачи фичи (чужой эпик — throw выше), поэтому всё, чего
        // нет здесь, — висящий или чужой id: наружу не выходит, считается в ReviewOfOutside.
        var runIds = new HashSet<string>(featureTasks.Select(t => t.Id));

        var stages = new List<FeatureStage>(stageDefs.Length);
        for (int i = 0; i < stageDefs.Length; i++)
        {
            var (key, tasks) = stageDefs[i];

            // Состояние стадии.
            string state;
            if (key == "design" && tasks.Count == 0)
            {
                // Необязательный «Дизайн» без задач: skipped — никогда не активна,
                // на готовность фичи не влияет (W4-DES-B).
                state = "skipped";
            }
            else if (i == activeIndex)
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
                // W4-3B: «оценивает →»: ReviewOf только по задачам этого run; висящие и чужие
                // id наружу не выходят, их число — ReviewOfOutside.
                List<string> reviewOf = t.ReviewOf is null
                    ? []
                    : t.ReviewOf.Where(id => runIds.Contains(id)).ToList();
                int reviewOfOutside = t.ReviewOf is null ? 0 : t.ReviewOf.Count(id => !runIds.Contains(id));
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
                    ReviewOf = reviewOf,   // W4-3B
                    ReviewOfOutside = reviewOfOutside, // W4-3B
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

        // ─── Б3: итог ────────────────────────────────────────────────────────────────────────
        // «Сейчас» (nowList) и featureState — выше, до цикла стадий (R-HOLD-B1: тон стадии выводится
        // из Attention, а пауза гасит Attention — state нужен раньше тона).

        // Счётчик шагов: принятые ревью / знаменатель (план ∪ заведённые) — из StepProgress.
        var stepProgress = accounting.Progress;
        int stepsTotal = stepProgress.Total;
        int stepsAccepted = stepProgress.Accepted;

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

        // Закрыта: выводится из State (единая точка правды, §1 разреза «момент закрытия фичи»):
        // merged или closed. Старое «merge Done || epic Done» — вторая точка правды: частичный
        // мерж (merge Done, финал не принят, эпик открыт) закрывал фичу (инцидент 21.10).
        bool closed = featureState is FeatureStates.Merged or FeatureStates.Closed;

        // Итог смерженной фичи: только у закрытой, иначе null.
        FeatureSummary? summary = null;
        if (closed)
        {
            // MergedAt: CompletedAt merge-задачи — только когда State == merged (полный мерж
            // после принятого финала, либо мерж через epic Done). Закрытая эпиком Done БЕЗ
            // смерженной merge-задачи («честный мерж») → mergedAt = null: фронт читает «закрыта»
            // (bucket=closed && mergedAt=null), а не «смержена». (Раньше сюда падал CompletedAt
            // эпика — дефект n4: закрытая без мержа показывалась «смержена <дата закрытия эпика>».)
            var mergedAt = featureState == FeatureStates.Merged
                && merge is not null && merge.Status == TaskItemStatus.Done
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
                StepsOpen = stepsTotal - stepsAccepted,
                Loops = verdicts.Loops,
            };
        }

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
            Hold = epic.Hold,
            Verdicts = verdicts,
            Progress = progress,
            Ready = ready,
            Cut = cut,
            Design = design,
            Plan = plan,
            Unroled = unroled,
            Stages = stages,
            StepChains = stepChains,
            ReturnsBySource = returnsBySource,
            Lanes = lanes,
            Now = nowList,
            Interrupted = interrupted,
            State = featureState,
            Attention = attention,
            StepsAccepted = stepsAccepted,
            StepsTotal = stepsTotal,
            StepProgress = stepProgress,
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

        // Все задачи фичи: эпик + шаги/доработки + ревью + проверки + финал + мерж + cut +
        // design + plan + безролевые (W4-DES-B: хронология — ВСЕ задачи с featureId).
        var tasks = new List<TaskItem> { run.Epic };
        tasks.AddRange(run.Steps);
        tasks.AddRange(run.Reviews);
        tasks.AddRange(run.Qa);
        if (run.Final is not null) tasks.Add(run.Final);
        if (run.Merge is not null) tasks.Add(run.Merge);
        tasks.AddRange(run.Cut);
        tasks.AddRange(run.Design);
        tasks.AddRange(run.Plan);
        tasks.AddRange(run.Unroled);

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
                else if (t.Role == TaskRoles.Merge && run.State == FeatureStates.Merged)
                {
                    // «merged» — только когда фича реально смержена (State == merged). Частичный
                    // мерж (merge Done, финал не принят, эпик открыт) — не «merged», а обычное
                    // завершение задачи (else → completed): фича ещё открыта.
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
        Hold = run.Hold,
        Verdicts = run.Verdicts,
        Progress = run.Progress,
        Ready = run.Ready,
        Cut = SlimAll(run.Cut),
        Design = SlimAll(run.Design),
        Plan = SlimAll(run.Plan),
        Unroled = SlimAll(run.Unroled),
        Stages = run.Stages,
        StepChains = run.StepChains,
        ReturnsBySource = run.ReturnsBySource,
        Lanes = run.Lanes,
        Now = run.Now,
        Interrupted = run.Interrupted,
        State = run.State,
        Attention = run.Attention,
        StepsAccepted = run.StepsAccepted,
        StepsTotal = run.StepsTotal,
        StepProgress = run.StepProgress,
        LastActivityAt = run.LastActivityAt,
        Closed = run.Closed,
        Summary = run.Summary,
    };

    // ─── Б2: хелперы и вычислители цепочек/возвратов/линий ─────────────────────────────────

    // Старт задачи: ClaudeStartedAt (запуск исполнителя), иначе CreatedAt.
    internal static DateTime TaskStart(TaskItem t) => t.ClaudeStartedAt ?? t.CreatedAt;

    // Исход витка: вердикт задачи, если он один из четырёх; иначе «running» (открыт, вердикта нет).
    internal static string RoundVerdict(TaskItem t)
    {
        var v = t.Verification?.Verdict;
        return v is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks
            or TaskVerdicts.Accepted or TaskVerdicts.Withdrawn
            ? v
            : "running";
    }

    // Виток-возврат: returned или accepted_with_remarks (работа вернулась к исполнителю).
    internal static bool IsReturn(string verdict) =>
        verdict is TaskVerdicts.Returned or TaskVerdicts.AcceptedWithRemarks;

    // Источник возврата: роль задачи → review | qa | final; прочее → null.
    internal static string? RoleToSource(string? role) => role switch
    {
        TaskRoles.Review => "review",
        TaskRoles.Qa => "qa",
        TaskRoles.Final => "final",
        _ => null,
    };

    // Нормализация линии как laneOf во фронтовом PipelineFlow.tsx:
    // back/backend/бэк/бэкенд → back; front/frontend/фронт/фронтенд → front; прочее → other.
    internal static string NormalizeLane(string? lane)
    {
        var l = (lane ?? "").Trim().ToLowerInvariant();
        if (l is "back" or "backend" or "бэк" or "бэкенд") return "back";
        if (l is "front" or "frontend" or "фронт" or "фронтенд") return "front";
        return "other";
    }

    // (Роль → ключ стадии — общий FeatureTimeMath.RoleToStage, дедуп с FeatureTimeline, минор 8.
    // Эпик (feature) в «Сейчас» не доходит: он контейнер фичи и исключён из Now, см. Build.)

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

    // «Ход идёт сейчас» (W4-HOLD-D2, R-HOLE-D находка 4): единый критерий running — его
    // используют и статус стадии, и выборка прерывания в SetHold, чтобы «running» в двух
    // местах не расходился. Контракт §1 (:199): «Status != Done, есть LinkedSessionId и в этой
    // сессии сейчас идёт ход», а не «запускался когда-то»: у задачи, чей ход закончился
    // результатом (ClaudeResult) и ещё не закрыт, прерывать нечего — feature_held рисовал бы
    // ложный «остановлен» в ленте.
    public static bool IsTurnRunning(TaskItem t) =>
        t.Status == TaskItemStatus.InProgress
        && t.ClaudeStartedAt is not null
        && t.ExecutorStoppedAt is null
        && t.AwaitingHumanSince is null
        && t.ClaudeResult is null;

    // Б2: статус исполнителя из полей задачи. Приоритет: running (ход идёт сейчас —
    // IsTurnRunning) → waiting (AwaitingHumanSince) → stopped (ExecutorStoppedAt) → done (Done)
    // → pending (остальное, в том числе не стартовала).
    // waiting/stopped — тот же guard, что у тревоги: только InProgress-задачи; закрытая
    // вручную (Done) задача со stale-пометками не «stopped»/«waiting» (тревоги по ней тоже нет).
    private static string ExecutorStatus(TaskItem t)
    {
        if (IsTurnRunning(t))
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

    // Фичи чата (REST ?sessionId=): правило членства ЕДИНОЕ — FeatureChatMembership (W4-CHT-B,
    // контракт §2): тот же набор фич, что у индекса и вкладки «Чаты» страницы. Без снимка
    // executor-ось пуста (остаётся curator/orchestrator — как была старая локальная копия).
    // Порядок (правило v2): 1) не закрытые с Attention — по since ascending (ждущая дольше всех);
    // 2) не закрытые идущие — по max ClaudeStartedAt descending; 3) не закрытые прочие —
    // по LastActivityAt descending; 4) закрытые — по LastActivityAt descending.
    // Пилюля показывает runs[0], поэтому порядок определяет бэк, фронт его не пересортирует.
    public IReadOnlyList<FeatureRun> ProjectForSession(string sessionId, string ownerId,
        IReadOnlyCollection<FeatureIndexSession>? sessions = null,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        // Набор фич чата — из единого правила, не из локального фильтра по эпику.
        var snapshot = sessions ?? Array.Empty<FeatureIndexSession>();
        var featureIds = new HashSet<string>(StringComparer.Ordinal);
        if (FeatureChatMembership.BuildDetailed(owner, snapshot).TryGetValue(sessionId, out var links))
            featureIds.UnionWith(links.Keys);
        var runs = owner
            .Where(t => t.Role == TaskRoles.Feature && featureIds.Contains(t.Id))
            .Select(epic => Build(epic, FeatureTasks(epic, owner), null, sessions, modelResolver))
            .ToList();
        var ordered = OrderRuns(runs);
        // Фича, чья задача — TaskId чата, первой ВНУЗИ своего класса (открытые/закрытые),
        // не поверх класса (W4-CHT-B §2): закрытая «своя» фича не прыгает выше открытых.
        var byId = owner.ToDictionary(t => t.Id);
        var own = OwnFeatureId(sessionId, snapshot, byId);
        var open = new List<FeatureRun>();
        var closed = new List<FeatureRun>();
        foreach (var r in ordered)
            (Bucket(r) == "closed" ? closed : open).Add(r);
        return PreferFirst(open, r => r.FeatureId, own)
            .Concat(PreferFirst(closed, r => r.FeatureId, own))
            .ToList();
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
    // Членство и вид связи — ЕДИНОЕ правило FeatureChatMembership (W4-CHT-B, контракт §2):
    // здесь нет локальной копии — индекс только фильтрует и упорядочивает членство.
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

        // Задача по id — для OwnFeatureId («своя» фича чата — первая в своём классе).
        var byId = owner.ToDictionary(t => t.Id);
        // Эпики (role=feature) — источники строк фич.
        var epics = owner.Where(t => t.Role == TaskRoles.Feature).ToList();
        // Задачи, сгруппированные по FeatureId, ОДИН раз (ToLookup): O(E×T) старого
        // FeatureTasks (полный скан owner на каждый эпик) уходит.
        var byFeature = owner.Where(t => t.FeatureId is not null).ToLookup(t => t.FeatureId!);

        // Область: сессии проекта (или все, если projectId не задан). Материализуем (List):
        // FeatureChatMembership ожидает IReadOnlyCollection, а .Where даёт IEnumerable.
        var inScope = (projectId is null
            ? sessions
            : sessions.Where(s => s.ProjectId == projectId)).ToList();

        // sessionId → featureId → сильнейший Link (вид и сила) — единая точка правды
        // FeatureChatMembership (W4-CHT-B): локальной копии правил здесь больше нет.
        var map = FeatureChatMembership.BuildDetailed(owner, inScope);

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

        // Chats: sessionId → записи. Членство в фичу, чей эпик исчез (удалён), не показываем —
        // фича «исчезает целиком», чат за ней не тянется. Порядок — в блоке ниже (W4-CHT-B):
        // открытые фичи перед закрытыми, внутри класса — по силе связи.
        var surviving = new HashSet<string>(rows.Select(r => r.FeatureId));
        // Порядок (W4-CHT-B, контракт §2): сначала открытые фичи, потом закрытые (bucket =
        // "closed" — из той же строки, единая точка правды); внутри класса — по силе связи,
        // при равенстве — по порядку фичи в Features.
        var closedFeature = new HashSet<string>(
            rows.Where(r => r.Bucket == "closed").Select(r => r.FeatureId), StringComparer.Ordinal);
        var chats = new Dictionary<string, List<FeatureIndexChatEntry>>();
        foreach (var (sessionId, links) in map)
        {
            // Порядок внутри класса задаётся сортировкой (сила, затем позиция фичи);
            // открытый/закрытый класс даёт разбиение ниже — OrderBy по closedFeature здесь
            // был бы мёртвым термином.
            var entries = links
                .Where(kv => surviving.Contains(kv.Key))
                .OrderBy(kv => kv.Value.Strength)
                .ThenBy(kv => featureOrder.TryGetValue(kv.Key, out var i) ? i : int.MaxValue)
                .Select(kv => new FeatureIndexChatEntry(
                    kv.Key, kv.Value.Kind, kv.Value.Role, kv.Value.StepRef, kv.Value.Lane))
                .ToList();
            // Фича, чья задача — TaskId чата, первой ВНУЗИ своего класса (PreferFirst), не
            // поверх него: закрытая «своя» фича не прыгает выше открытой.
            var own = OwnFeatureId(sessionId, sessions, byId);
            var open = new List<FeatureIndexChatEntry>();
            var closed = new List<FeatureIndexChatEntry>();
            foreach (var e in entries)
                (closedFeature.Contains(e.FeatureId) ? closed : open).Add(e);
            var ordered = PreferFirst(open, e => e.FeatureId, own)
                .Concat(PreferFirst(closed, e => e.FeatureId, own))
                .ToList();
            if (ordered.Count > 0)
                chats[sessionId] = ordered;
        }

        return new FeatureIndex { Chats = chats, Features = rows };
    }

    // Чаты фичи (GET /api/tasks/features/{id}, chats[]; Б4 + W4-CHT-B): правило членства
    // ЕДИНОЕ — FeatureChatMembership, тот же набор чатов, что у индекса и шторки. Членство
    // этой фичи полностью определяют её задачи (эпик + дочерние), поэтому область — список
    // задач фичи, не весь owner. Вид связи (curator/orchestrator/executor) определяет правило,
    // а не локальный фильтр по TaskId: «ключевой» чат помечает фронт по keySessionId
    // (FeatureChatsTab), второй копии «key» на бэке нет. Чужой/удалённый чат не в снимке
    // (fail-closed, как исполнители). modelLabel/modelFull — из modelResolver (как у
    // исполнителей). Только для features/{id}: в SlimRun/индекс chats не входит.
    internal static List<FeatureChat> BuildFeatureChats(
        TaskItem epic,
        IReadOnlyList<TaskItem> allFeatureTasks,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver = null)
    {
        var membership = FeatureChatMembership.BuildDetailed(allFeatureTasks, sessions);

        // taskId → задача фичи: данные исполнителя (personaId/role/stepRef/lane) для чата
        // с этим TaskId; у чистых curator/orchestrator-чатов TaskId нет → null.
        var byTaskId = allFeatureTasks.ToDictionary(t => t.Id);

        var chats = new List<FeatureChat>();
        foreach (var s in sessions)
        {
            if (!membership.TryGetValue(s.Id, out var links)) continue;
            if (!links.TryGetValue(epic.Id, out var link)) continue;
            TaskItem? task = s.TaskId is not null && byTaskId.TryGetValue(s.TaskId, out var t) ? t : null;
            (string? label, string? full) = (null, null);
            if (modelResolver is not null) (label, full) = modelResolver(s);
            chats.Add(new FeatureChat(
                s.Id, link.Kind, s.TaskId, s.Archived,
                task?.PersonaId, task?.Role, task?.StepRef, task?.Lane,
                label, full));
        }
        return chats;
    }

    // Фичи, в которые входит чат (те же правила членства, что BuildIndexCore: executor/curator/
    // orchestrator). Для ключевого чата фичи: |set| == 1 и set == {эта фича} → чат исключителен
    // (его расход целиком фичи, Б9b); иначе общий (деление — Б9c). internal — для золотых тестов.
    // Доработка Б9c: правило вынесено в FeatureChatMembership (единая точка правды) — здесь
    // только lookup по чату, чтобы правило не жило в двух копиях.
    internal static HashSet<string> ChatFeatureIds(string chatId, IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> sessions)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(chatId)) return result;
        if (FeatureChatMembership.Build(owner, sessions).TryGetValue(chatId, out var features))
            result.UnionWith(features);
        return result;
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
            OrchestratorOf(run.Epic, sessions, key),
            chatCount,
            run.Epic.WorktreeBranch,
            run.Epic.CreatedAt,
            featureTasks.Count,
            NowPersonaIdsOf(run),
            run.StepProgress.Review,
            run.StepProgress.Work,
            run.StepProgress.NotCreated,
            run.StepProgress.PlanState,
            run.StepProgress.Withdrawn,
            run.StepProgress.UnlinkedReworks,
            run.Hold?.Reason,
            run.Hold?.Since);
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

    // Оркестратор фичи (W4-1B): персона, ведущая фичу. Ключевой чат приходит готовым из
    // BuildRow (одна точка правды: здесь он не пересчитывается — KeySession уже отфильтровал
    // чужой/удалённый по снимку владельца). Цепочка звеньев: персона ключевого чата →
    // персона, запустившая исполнителя эпика (ExecutorLaunchedByPersonaId) → персона,
    // создавшая эпик (CreatedByPersonaId). Каждое звено проверяется одинаково: пустое/пробельное
    // пропускается. Ничего нет → null. Чистая функция — на ней тесты (FeatureIndexRowTests).
    internal static string? OrchestratorOf(TaskItem epic,
        IReadOnlyCollection<FeatureIndexSession> sessions, string? key)
    {
        if (key is not null)
        {
            var persona = sessions.FirstOrDefault(s => s.Id == key)?.PersonaId;
            if (!string.IsNullOrWhiteSpace(persona)) return persona;
        }
        if (!string.IsNullOrWhiteSpace(epic.ExecutorLaunchedByPersonaId)) return epic.ExecutorLaunchedByPersonaId;
        if (!string.IsNullOrWhiteSpace(epic.CreatedByPersonaId)) return epic.CreatedByPersonaId;
        return null;
    }

    // «Финал принят»: задача финала ЗАКРЫТА (Done) и вердикт accepted/accepted_with_remarks.
    // Единая точка правды для Ready, merge_pending, merged (ComputeState) и гарда role=merge
    // (TaskManager.ValidateFeatureFields) — без копий условия «принят/принят с замечаниями».
    public static bool IsFinalAccepted(TaskItem? final) =>
        final is not null
        && final.Status == TaskItemStatus.Done
        && IsAcceptance(final.Verification?.Verdict);

    // ─── Пауза фичи (W4-HOLD-G, R-HOLD-G2) — единая точка правды ───────────────────────────
    // «Пауза» — явное удержание человека (manual/queue): фичу не вести. Формула одна и для
    // гейта запуска (TaskManager.FeatureHoldRefusal), и для состояния (ComputeState). Явный
    // список причин, а не «любой не-draft»: словарь FeatureHoldReasons закрыт (ручка REST
    // валидирует), и «неизвестная причина = пауза» больше не подразумевается.
    // Причина вне словаря (старые/битые данные, SetHold его не фильтрует) трактуется как
    // черновик — IsDraft ниже. Так состояние и гейт говорят одно и то же: рабочие роли
    // закрыты, подготовка разрешена (R-HOLD-G2 находка 2).
    public static bool IsPaused(TaskItem epic) =>
        epic.Hold is not null && epic.Hold.Reason is FeatureHoldReasons.Manual or FeatureHoldReasons.Queue;

    // «Черновик»: любое удержание, не пауза — включая причину вне словаря (фоллбэк). Гейт для
    // такого Hold ведёт себя идентично: пропускает только роли подготовки (HoldPrepRoles).
    public static bool IsDraft(TaskItem epic) => epic.Hold is not null && !IsPaused(epic);

    // Роли, разрешённые на черновике: только подготовка (cut/design/plan/consult), рабочие — нет.
    public static readonly IReadOnlyList<string> HoldPrepRoles =
        [TaskRoles.Cut, TaskRoles.Design, TaskRoles.Plan, TaskRoles.Consult];

    // «Запуск разрешён»: Hold нет — разрешён; пауза (manual/queue) — ничего; draft — только
    // подготовка. Гейт зовёт этот предикат, ComputeState — IsPaused; второго выражения
    // правила быть не должно (ср. IsFinalAccepted).
    public static bool HoldAllows(TaskItem task, TaskItem epic)
    {
        if (epic.Hold is null) return true;
        if (IsPaused(epic)) return false;
        return HoldPrepRoles.Contains(task.Role);
    }

    // «Принятие» — ОДИН предикат о вердикте: accepted ИЛИ accepted_with_remarks. На нём Ready,
    // merge_pending/merged, гард role=merge и «шаг принят» (FeatureStepAccounting): две правды о
    // принятии недопустимы. Счётчик петель ↺ (IsReturn: returned/awr) — отдельная ось «сколько раз
    // возвращали», она принятие не определяет.
    public static bool IsAcceptance(string? verdict) =>
        verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks;

    // Состояние фичи (11 значений, приоритет сверху вниз; первое совпавшее побеждает):
    // merged → closed → paused → stopped → waiting → draft → merging → merge_pending → running →
    // not_started → idle (W4-HOLD-B1, разрез §1).
    // Чистая функция от полей эпика (в т.ч. Hold)/merge/тревог/«сейчас» — на ней золотые тесты
    // (FeatureProjectorTests). hasExplicitWait — явный wait (AwaitingHumanSince), КРОМЕ вычисленного
    // «мерж» (тот → merge_pending).
    internal static string ComputeState(
        TaskItem epic,
        TaskItem? merge,
        bool finalAccepted,
        bool hasStopped,
        bool hasExplicitWait,
        int nowCount,
        bool anyStarted)
    {
        // 1. merged: merge-задача Done И (финал принят ИЛИ эпик Done).
        //    Частичный мерж (merge Done, финал не принят, эпик открыт) фичу НЕ закрывает:
        //    инцидент 21.10 — фича с двенадцатью шагами в работе стала «смержена».
        if (merge is not null && merge.Status == TaskItemStatus.Done
            && (finalAccepted || epic.Status == TaskItemStatus.Done)) return FeatureStates.Merged;
        // 2. closed: эпик Done и не merged (merged уже отфильтрован выше).
        //    Эпик Done — решение человека «фича закончена»: даже если работа ещё идёт
        //    (running/stopped/waiting) — фича закрыта (старое «и нет активной работы» убрано).
        if (epic.Status == TaskItemStatus.Done) return FeatureStates.Closed;
        // 3. paused: явное удержание человека — формула в FeatureProjector.IsPaused (общая с
        //    гейтом): «фичу не вести», решение человека, а не тревога. Стоит ДО stopped: пауза,
        //    прервавшая исполнителей, не должна показывать «ждёт вас» (W4-HOLD-B1).
        if (IsPaused(epic)) return FeatureStates.Paused;
        // 4. stopped: есть остановленная задача (как тревога kind=stopped).
        if (hasStopped) return FeatureStates.Stopped;
        // 5. waiting: явное ожидание человека (как тревога kind=wait, КРОМЕ «мерж»).
        if (hasExplicitWait) return FeatureStates.Waiting;
        // 6. draft: черновик — любое удержание, не пауза (Reason draft или фоллбэк вне
        //    словаря, IsDraft): фичу готовят, исполнять шаги нельзя — даже если разрез или
        //    план идут (nowCount > 0): человеку важнее «фича ещё не запущена».
        //    Тревоги stopped/waiting уже отсеются выше.
        if (IsDraft(epic)) return FeatureStates.Draft;
        // 7. merging: merge-задача запущена (InProgress) — мерж идёт.
        if (merge is not null && merge.Status == TaskItemStatus.InProgress) return FeatureStates.Merging;
        // 8. merge_pending: финал принят, merge-задачи нет ИЛИ она создана, но не запущена
        //    (Todo) — ждёт согласия/запуска (тревога «мерж», как и у merge is null).
        if (finalAccepted && (merge is null || merge.Status == TaskItemStatus.Todo)) return FeatureStates.MergePending;
        // 9. running: Now не пуст (идут задачи).
        if (nowCount > 0) return FeatureStates.Running;
        // 10. not_started: ни одна задача фичи не стартовала.
        if (!anyStarted) return FeatureStates.NotStarted;
        // 11. idle: иначе (ничего не идёт, ничего не ждёт, фича не закрыта).
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
        // paused и draft — не идут и внимания не требуют (W4-HOLD-B1): обе в idle.
        FeatureStates.Paused => "idle",
        FeatureStates.Stopped => "attention",
        FeatureStates.Waiting => "attention",
        FeatureStates.Draft => "idle",
        FeatureStates.MergePending => "attention",
        FeatureStates.Merging => "running",
        FeatureStates.Running => "running",
        FeatureStates.NotStarted => "idle",
        FeatureStates.Idle => "idle",
        _ => "idle", // неизвестное значение — как idle (фоллбэк не ронит индекс)
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
            else if (Bucket(run) == "running")
            {
                // bucket = running (идёт, без тревоги) → чип «running». Выводится из bucket, а не из Now:
                // у фичи на паузе (bucket = idle) могут остаться InProgress-задачи — она не идёт (R-HOLD-B1).
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

    // Рельса: все стадии FeatureRun.Stages (8, с необязательной «design») → сегменты {key, state, tone}.
    // Нормализация закрытой фичи (контракт С0): у closed active нет — все стадии, кроме absent,
    // приводятся к done, tone=null (merge в том числе: «честный мерж» epic Done без merge-задачи
    // иначе даёт merge=active, tone=run). absent остаётся absent.
    internal static List<FeatureIndexRailSegment> RailOf(FeatureRun run) =>
        run.Stages
            .Select(s =>
            {
                if (!run.Closed)
                    return new FeatureIndexRailSegment(s.Key, s.State, s.Tone);
                // skipped (пустая необязательная «design») остаётся skipped: она не
                // «пройдена», даже когда фича закрыта (W4-DES-B).
                var state = s.State is "absent" or "skipped" ? s.State : "done";
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
        return new FeatureIndexSummary(s.MergedAt, s.DurationMinutes, s.StepsOpen);
    }

    // Чатов фичи в этом ответе: число сессий в map, чья карта фич содержит featureId.
    private static int ChatCount(
        IReadOnlyDictionary<string, Dictionary<string, FeatureChatMembership.Link>> map,
        string featureId)
    {
        int count = 0;
        foreach (var feats in map.Values)
            if (feats.ContainsKey(featureId)) count++;
        return count;
    }
}
