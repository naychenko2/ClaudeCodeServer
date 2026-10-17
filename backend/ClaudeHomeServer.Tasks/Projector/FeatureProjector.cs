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
    public static FeatureRun Build(TaskItem epic, IEnumerable<TaskItem> featureTasks)
    {
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

        bool ready = final is not null
            && final.Verification?.Verdict is TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks;

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

            stages.Add(new FeatureStage
            {
                Key = key,
                State = state,
                Tone = tone,
                Passes = passes,
                TaskIds = tasks.Select(t => t.Id).ToList(),
                RunningSince = runningSince,
            });
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
        };
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
    public IReadOnlyList<FeatureRun> ProjectForSession(string sessionId, string ownerId)
    {
        var owner = tasks.GetByOwner(ownerId).ToList();
        return owner
            .Where(t => t.Role == TaskRoles.Feature
                && (t.SourceSessionId == sessionId || t.ExecutorLaunchedBySessionId == sessionId))
            .Select(epic => Build(epic, FeatureTasks(epic, owner)))
            .ToList();
    }

    // Эпик + все его дочерние (FeatureId == epic.Id) из уже отобранных задач владельца.
    private static List<TaskItem> FeatureTasks(TaskItem epic, List<TaskItem> owner)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(owner.Where(t => t.FeatureId == epic.Id));
        return all;
    }
}
