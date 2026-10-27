using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// TR-B1a: «Сверка трекера» (разрез Александра 26.10): список расхождений трекера с реальностью —
// всё, что не породило доклада оркестратора. Чистая функция на ТОМ ЖЕ проходе, что и учёт шагов:
// витки цепочки и последняя сданная задача приходят из FeatureStepAccounting.Result (оба поля
// считаются там), остальное — из переданных дочерних задач. Второй точки правды и новых чтений
// нет; сервер расхождения только показывает — чинит их оркестратор (колонка «Кто чинит» разреза).
//
// Виды (таблица разреза, порог TrackerGraceMinutes):
//   unreviewed            — цепочка сдана, покрывающего витка нет, старше порога;
//   returned_no_rework    — последний закрытый виток returned, работы после него нет;
//   dead_verdict          — review/qa/final в Todo, все цели уже приняты или сняты;
//   reported_open         — исполнитель отчитался (ClaudeResult != null), задача InProgress,
//                           чат исполнителя не в работе, старше порога;
//   epic_open_after_merge — мерж Done, все дочерние Done, эпик открыт;
//   orphans_in_closed     — эпик Done, в нём остались не-Done задачи.
internal static class FeatureTracker
{
    // Порог (минуты): меньше него после сдачи/возврата расхождения ещё нет — оркестратор мог
    // как раз заводить ревью или доработку. Константа, не конфиг (разрез: настройка порога за скобками).
    internal const int TrackerGraceMinutes = 30;

    // Метка задач, ревью не требующих (доки, чек-листы, стенд, замеры). Сравнение: с тримом,
    // без учёта регистра — регистр и случайные пробелы метки никем не контролируются.
    internal const string NoReviewLabel = "без-ревью";

    // Виды — строки таблицы разреза (значения поля `tracker` в GET features/{id}).
    internal const string Unreviewed = "unreviewed";
    internal const string ReturnedNoRework = "returned_no_rework";
    internal const string DeadVerdict = "dead_verdict";
    internal const string ReportedOpen = "reported_open";
    internal const string EpicOpenAfterMerge = "epic_open_after_merge";
    internal const string OrphansInClosed = "orphans_in_closed";

    // Значение FeatureStepChain.Outcome «снят» (FeatureRun.cs): отдельный вердикт, не TaskVerdicts.
    private const string ChainWithdrawn = "withdrawn";

    // epic/children — те же задачи, из которых построен run; accounting — результат учёта шагов
    // по этим задачам (тот же проход, вызывающий передаёт его как есть). sessions — снимок сессий
    // владельца (FeatureChatDoor.Snapshot, тот же, что у FeatureStepAccounting); null — снимка нет,
    // reported_open тогда не выдаётся. nowUtc — «сейчас», фиксируется вызывающим, как в
    // FeatureProjector.Build.
    internal static List<FeatureTrackerIssue> Build(
        TaskItem epic, List<TaskItem> children, FeatureStepAccounting.Result accounting, DateTime nowUtc,
        IReadOnlyCollection<FeatureIndexSession>? sessions)
    {
        ArgumentNullException.ThrowIfNull(epic);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(accounting);

        var issues = new List<FeatureTrackerIssue>();
        var grace = TimeSpan.FromMinutes(TrackerGraceMinutes);

        var byId = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var t in children) byId.TryAdd(t.Id, t);
        // Цепочка по id любой её задачи: доработка, привязанная через reviewOf (без stepRef), живёт
        // в цепочке шага — по KeyOf(доработки) её бы не нашли.
        var chainByTaskId = new Dictionary<string, FeatureStepChain>(StringComparer.Ordinal);
        foreach (var chain in accounting.Chains)
            foreach (var id in chain.TaskIds) chainByTaskId.TryAdd(id, chain);

        // ─── Цепочечные виды: unreviewed, returned_no_rework ──────────────────────────────────
        // Снятая цепочка пропускается целиком: её сняли с выкладки намеренно, ревью и доработок не нужно.
        foreach (var chain in accounting.Chains)
        {
            if (chain.Outcome == ChainWithdrawn) continue;
            var facts = accounting.Facts.GetValueOrDefault(chain.StepRef);
            if (facts is null) continue; // защита: Facts строится в том же проходе, что и Chains
            var rounds = facts.Rounds;

            // unreviewed: последняя сданная задача старше порога, и нет ни одного витка, который
            // ещё открыт или закрыт ПОСЛЕ её сдачи (открытое пачечное ревью на исходный шаг
            // покрывает всю цепочку — она общая по stepRef). Финал с пустым ReviewOf — тоже виток
            // и покрывает любую цепочку на том же временном условии; у review/qa пустой ReviewOf
            // витком не считается. Исключение — метка «без-ревью».
            var last = facts.LastSubmitted;
            if (last is not null && !HasNoReviewLabel(last)
                && nowUtc - last.CompletedAt!.Value > grace
                && !rounds.Any(r => r.CompletedAt is null || r.CompletedAt > last.CompletedAt)
                && !children.Any(f => f.Role == TaskRoles.Final && (f.ReviewOf is null || f.ReviewOf.Count == 0)
                    && (f.CompletedAt is null || f.CompletedAt > last.CompletedAt)))
            {
                issues.Add(new FeatureTrackerIssue(Unreviewed, last.Id, chain.StepRef, last.CompletedAt));
            }

            // returned_no_rework: последний виток закрыт returned старше порога, и после него
            // ни одна задача цепочки не начиналась. Открытый виток после возврата — повторное
            // ревью уже идёт, это не расхождение (открытые витки стоят последними в порядке
            // завершения, поэтому достаточно посмотреть последний).
            if (rounds.Count > 0)
            {
                var lastRound = rounds[^1];
                if (lastRound.CompletedAt is not null && lastRound.Verdict == TaskVerdicts.Returned
                    && nowUtc - lastRound.CompletedAt.Value > grace
                    && !chain.TaskIds.Any(id => byId.TryGetValue(id, out var t)
                        && FeatureProjector.TaskStart(t) > lastRound.CompletedAt.Value))
                {
                    issues.Add(new FeatureTrackerIssue(ReturnedNoRework, lastRound.ReviewTaskId, chain.StepRef, lastRound.CompletedAt));
                }
            }
        }

        // ─── dead_verdict: review/qa/final в Todo, все цели уже разрешены ─────────────────────
        // Цель «разрешена»: сама снята вердиктом, её цепочка снята, или цель сдана и в её цепочке
        // есть закрытый виток-принятие НЕ этого задачи, закрывшийся после сдачи цели (свежая работа
        // после принятия остаётся нерешённой — ревью по новой доработке живое). Пустой ReviewOf —
        // не признак: под role=qa живут дефекты без связи.
        foreach (var v in children)
        {
            if (v.Role is not (TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final)) continue;
            if (v.Status != TaskItemStatus.Todo) continue;
            if (v.ReviewOf is null || v.ReviewOf.Count == 0) continue;
            if (v.ReviewOf.All(id => TargetResolved(id, v)))
                issues.Add(new FeatureTrackerIssue(DeadVerdict, v.Id, v.StepRef, v.CreatedAt));
        }

        bool TargetResolved(string targetId, TaskItem verdictTask)
        {
            if (!byId.TryGetValue(targetId, out var t)) return false; // цели нет в фиче — не расхождение (осторожно)
            if (t.Verification?.Verdict == TaskVerdicts.Withdrawn) return true;
            if (!chainByTaskId.TryGetValue(t.Id, out var chain)) return false; // цель вне цепочки
            if (chain.Outcome == ChainWithdrawn) return true;
            return t.Status == TaskItemStatus.Done && t.CompletedAt is not null
                && chain.Rounds.Any(r => r.ReviewTaskId != verdictTask.Id
                    && FeatureProjector.IsAcceptance(r.Verdict)
                    && r.CompletedAt is not null && r.CompletedAt > t.CompletedAt);
        }

        // ─── reported_open: исполнитель отчитался, задача открыта, чат свободен ────────────────
        // Ход завершён (ClaudeResult != null — success или error: в обоих случаях доклад пришёл, а
        // задача не закрыта), и с последней активности задачи прошло больше порога (UpdatedAt
        // двигается MarkClaudeResult и любой правкой задачи — тишина считается от последней
        // активности, как у сторожа). Чат исполнителя в снимке не занят (Busy == false). Ни записи
        // о чате в снимке, ни признака занятости у записи — расхождения нет (fail-closed: о чате,
        // которого не видим, не утверждаем). Сторож зависания ClassifyStall — про живого
        // исполнителя; этот вид его не дублирует: здесь ход уже завершён.
        var busyBySessionId = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (sessions is not null)
            foreach (var s in sessions)
                if (s.Busy is { } busy) busyBySessionId.TryAdd(s.Id, busy);

        foreach (var t in children)
        {
            if (t.Status != TaskItemStatus.InProgress || t.ClaudeResult is null) continue;
            if (nowUtc - t.UpdatedAt <= grace) continue;
            if (t.LinkedSessionId is not { } chatId) continue;
            if (!busyBySessionId.TryGetValue(chatId, out var chatBusy) || chatBusy) continue;
            issues.Add(new FeatureTrackerIssue(ReportedOpen, t.Id, t.StepRef, t.UpdatedAt));
        }

        // ─── Виды эпика: epic_open_after_merge, orphans_in_closed ─────────────────────────────
        if (epic.Status != TaskItemStatus.Done)
        {
            if (children.Count > 0)
            {
                // «После мержа» буквально: закрытая merge-задача и все дочерние Done при открытом
                // эпике. Без merge-задачи сигнала нет (признак — роль, не название).
                var merge = children.LastOrDefault(t => t.Role == TaskRoles.Merge && t.Status == TaskItemStatus.Done);
                if (merge is not null && children.All(t => t.Status == TaskItemStatus.Done))
                    issues.Add(new FeatureTrackerIssue(EpicOpenAfterMerge, merge.Id, null, merge.CompletedAt));
            }
        }
        else
        {
            // orphans_in_closed: эпик закрыт, дочерние задачи оставлены открытыми — по записи на каждую.
            foreach (var t in children)
                if (t.Status != TaskItemStatus.Done)
                    issues.Add(new FeatureTrackerIssue(OrphansInClosed, t.Id, t.StepRef, epic.CompletedAt));
        }

        return issues;
    }

    private static bool HasNoReviewLabel(TaskItem t) =>
        t.Labels.Any(l => l is not null && string.Equals(l.Trim(), NoReviewLabel, StringComparison.OrdinalIgnoreCase));
}
