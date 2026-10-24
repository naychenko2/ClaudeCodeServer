using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Учёт шагов фичи («честный прогресс», разрез Александра 21.10): цепочки шагов, корзины
// accepted/review/work/notCreated и линии — из ОДНОГО прохода, чтобы числа не расходились.
//
// Единица учёта — шаг (код stepRef; у шага без stepRef ключ = id задачи). Шаг один, сколько бы
// доработок у него ни было. «Принят» = последний виток (ревью/проверка/финал по ReviewOf) с
// вердиктом accepted/accepted_with_remarks (FeatureProjector.IsAcceptance — единый предикат) И
// нет незакрытой доработки. «Сдан» (Status=Done) — не «принят».
internal static class FeatureStepAccounting
{
    internal sealed record Result(
        List<FeatureStepChain> Chains,
        FeatureStepProgress Progress,
        List<FeatureLane> Lanes);

    private const string Accepted = "accepted";
    private const string Review = "review";
    private const string Work = "work";
    private const string Withdrawn = "withdrawn";
    private const string NotCreated = "notCreated";
    private const string PlanPending = "pending";
    private const string PlanPartial = "partial";
    private const string PlanCut = "cut";
    private const string PlanDone = "done";

    // Единый сравнитель кодов шагов (решение 4 ревью Александра): «Б1» и «б1» — один шаг.
    // OrdinalIgnoreCase: детерминизм без зависимости от культуры; им пользуются ВСЕ ключи
    // Build (план, группы, доработки, корзины) и гард дублей плана в TaskManager.
    internal static readonly StringComparer StepKeyComparer = StringComparer.OrdinalIgnoreCase;

    // Ключ шага: stepRef (после трима — для старых данных), а без него — id задачи (каждая старая
    // задача-шаг без stepRef — отдельный шаг, а не общая цепочка «»).
    internal static string KeyOf(TaskItem t) => string.IsNullOrWhiteSpace(t.StepRef) ? t.Id : t.StepRef.Trim();

    // steps — задачи role=step/rework; verdictTasks — ревью/проверки/финал; plan — задачи role=plan.
    internal static Result Build(
        List<TaskItem> steps, List<TaskItem> verdictTasks, List<TaskItem> plan, DateTime nowUtc,
        IReadOnlyCollection<FeatureIndexSession>? sessions)
    {
        // Б7: чаты шага/витка наружу только если в снимке сессий владельца (fail-closed).
        var sessionById = sessions is null
            ? new Dictionary<string, FeatureIndexSession>()
            : sessions.ToDictionary(s => s.Id);
        string? LiveChat(string? chatId) =>
            chatId is not null && sessionById.ContainsKey(chatId) ? chatId : null;

        // ─── План: структурный список шагов (объединение по всем задачам role=plan) ──────────
        var planned = new Dictionary<string, PlannedStep>(StepKeyComparer);
        var plannedOrder = new List<string>();
        foreach (var p in plan)
            foreach (var ps in p.PlanSteps ?? [])
            {
                if (ps is null || string.IsNullOrWhiteSpace(ps.StepRef)) continue;
                // Ключ шага плана тримится так же, как KeyOf (замечание 1 ревью Глеба): без трима
                // «Б1 » в плане и «Б1» в шаге — два разных ключа, шаг считается дважды.
                var key = ps.StepRef.Trim();
                if (planned.TryAdd(key, ps)) plannedOrder.Add(key);
            }
        // Маппинг состояния плана (решение Александра, шов 63a69b77 п.2а; строки контракта не
        // меняются): учитываются задачи role=plan, КРОМЕ снятых (withdrawn) — снятый старый план
        // не должен держать фичу в partial. pending — таких задач нет, или у всех planSteps пуст;
        // partial — списки есть только у части планов; cut — у всех; done выводится ниже, когда
        // план разобран и всё принято.
        var countedPlans = plan.Where(p => p.Verification?.Verdict != TaskVerdicts.Withdrawn).ToList();
        int plansWithSteps = countedPlans.Count(p => p.PlanSteps is not null && p.PlanSteps.Count > 0);
        string planState = plansWithSteps == 0 ? PlanPending
            : plansWithSteps < countedPlans.Count ? PlanPartial : PlanCut;

        // ─── Шаги и доработки ───────────────────────────────────────────────────────────────
        var stepTasks = steps.Where(t => t.Role == TaskRoles.Step).ToList();
        var reworks = steps.Where(t => t.Role == TaskRoles.Rework).ToList();

        var groups = new List<(string Key, List<TaskItem> Tasks)>();
        var groupIndex = new Dictionary<string, int>(StepKeyComparer);
        foreach (var t in stepTasks)
        {
            var key = KeyOf(t);
            if (!groupIndex.TryGetValue(key, out var i))
            {
                groupIndex[key] = i = groups.Count;
                groups.Add((key, []));
            }
            groups[i].Tasks.Add(t);
        }

        var taskById = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var t in steps) taskById.TryAdd(t.Id, t);
        var verdictById = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var v in verdictTasks) verdictById.TryAdd(v.Id, v);

        // Принадлежность доработки шагам: тот же stepRef, ИЛИ reviewOf ведёт на виток, у которого
        // в reviewOf задачи шага (многие-ко-многим для пакетных; транзитивно через предыдущие
        // доработки). Без привязки — пустой набор (UnlinkedReworks, цепочку не заводит).
        HashSet<string> OwnerKeys(TaskItem rework, HashSet<string> path)
        {
            var result = new HashSet<string>(StepKeyComparer);
            // В набор кладётся КАНОНИЧНЫЙ ключ группы (написание первого появления), а не
            // написание доработки: «Б1» в плане и «б1» в шаге — один шаг.
            if (!string.IsNullOrWhiteSpace(rework.StepRef) && groupIndex.TryGetValue(rework.StepRef.Trim(), out var gi))
                result.Add(groups[gi].Key);
            foreach (var vid in rework.ReviewOf ?? [])
            {
                if (!verdictById.TryGetValue(vid, out var v)) continue;
                foreach (var tid in v.ReviewOf ?? [])
                {
                    if (!taskById.TryGetValue(tid, out var target)) continue;
                    if (target.Role == TaskRoles.Step) result.Add(KeyOf(target));
                    else if (target.Role == TaskRoles.Rework && path.Add(target.Id))
                    {
                        result.UnionWith(OwnerKeys(target, path));
                        path.Remove(target.Id);
                    }
                }
            }
            return result;
        }

        var reworksByKey = new Dictionary<string, List<TaskItem>>(StepKeyComparer);
        int unlinked = 0;
        foreach (var r in reworks)
        {
            var keys = OwnerKeys(r, [r.Id]);
            if (keys.Count == 0) { unlinked++; continue; }
            foreach (var k in keys)
            {
                if (!reworksByKey.TryGetValue(k, out var list)) reworksByKey[k] = list = [];
                list.Add(r);
            }
        }

        // ─── Цепочки и корзины ───────────────────────────────────────────────────────────────
        var chains = new List<FeatureStepChain>(groups.Count);
        var bucketByKey = new Dictionary<string, string>(StepKeyComparer);
        var laneByKey = new Dictionary<string, string>(StepKeyComparer);
        var returnedKeys = new HashSet<string>(StepKeyComparer);
        var personasByLane = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, groupTasks) in groups)
        {
            // Снятая (withdrawn) доработка исключается из цепочки целиком (решение 3 ревью):
            // не открывает work, не считается в reworkOpen/allDone, не даёт withdrawn.
            var members = groupTasks
                .Concat((reworksByKey.GetValueOrDefault(key) ?? []).Where(r => r.Verification?.Verdict != TaskVerdicts.Withdrawn))
                .ToList();
            // Порядок задач цепочки — по старту (длительность и шапка), НЕ трогать.
            var ordered = members.OrderBy(FeatureProjector.TaskStart).ToList();
            // Шапка цепочки — задача шага (role=step); доработки её не подменяют.
            var head = ordered.First(t => t.Role == TaskRoles.Step);
            var idSet = new HashSet<string>(ordered.Select(t => t.Id));

            // Витки: ревью/проверки/финалы, связанные с цепочкой через ReviewOf. Порядок — по
            // ЗАВЕРШЕНИЮ: (CompletedAt ?? +∞, TaskStart) (решение 2 ревью Александра): вердикт
            // появляется в момент закрытия витка, поэтому решает самая свежая информация. Идущий
            // виток без CompletedAt встаёт последним — шаг честно числится «на ревью». Инвариант
            // (шов Александра 63a69b77 п.1): закрытая задача всегда с CompletedAt — TaskManager.Update
            // при уходе из Done обнуляет и вердикт, и CompletedAt, а Create не принимает вердикт
            // у незакрытой задачи; без CompletedAt остаются только идущие витки, для которых
            // «+∞, последним» верно. Порядок один на всю цепочку: Rounds/N, lastVerdict и Source
            // берутся из этого же списка.
            // Одно ревью на два шага попадает в обе цепочки (связка многие-ко-многим).
            var rounds = verdictTasks
                .Where(t => t.ReviewOf is not null && t.ReviewOf.Any(idSet.Contains))
                .OrderBy(t => t.CompletedAt ?? DateTime.MaxValue)
                .ThenBy(FeatureProjector.TaskStart)
                .ToList();
            var featureRounds = rounds.Select((t, i) => new FeatureRound
            {
                N = i + 1,
                Verdict = FeatureProjector.RoundVerdict(t),
                ReviewTaskId = t.Id,
                PersonaId = t.PersonaId,
                ReviewChatId = LiveChat(t.LinkedSessionId),
                StartedAt = FeatureProjector.TaskStart(t),
                CompletedAt = t.CompletedAt,
                Reason = t.Verification?.Notes,
            }).ToList();
            int loops = featureRounds.Count(r => FeatureProjector.IsReturn(r.Verdict));

            // Источник возврата: последний виток-возврат в порядке по завершению; связи нет — null.
            var returnTasks = rounds.Where(t => FeatureProjector.IsReturn(FeatureProjector.RoundVerdict(t))).ToList();
            string? source = returnTasks.Count > 0 ? FeatureProjector.RoleToSource(returnTasks[^1].Role) : null;

            string? lastVerdict = featureRounds.Count > 0 ? featureRounds[^1].Verdict : null;
            // Снятие шага (решение 3): последний виток (в порядке по завершению) withdrawn, либо
            // снята сама задача-шаг (role=step с вердиктом withdrawn). Снятый виток в середине
            // цепочки ничего не решает; снятая доработка уже исключена из members.
            bool withdrawn = lastVerdict == TaskVerdicts.Withdrawn
                || ordered.Any(t => t.Role == TaskRoles.Step && t.Verification?.Verdict == TaskVerdicts.Withdrawn);
            bool reworkOpen = ordered.Any(t => t.Role == TaskRoles.Rework && t.Status != TaskItemStatus.Done);
            bool allDone = ordered.All(t => t.Status == TaskItemStatus.Done);

            // Корзина (взаимоисключающие): снят → принят → в работе → на ревью.
            string bucket, outcome, status;
            if (withdrawn)
            {
                bucket = Withdrawn; outcome = "withdrawn"; status = "снят с выкладки";
            }
            else if (FeatureProjector.IsAcceptance(lastVerdict) && !reworkOpen)
            {
                bucket = Accepted; outcome = "accepted"; status = "принят";
            }
            else if (!allDone || lastVerdict == TaskVerdicts.Returned)
            {
                bucket = Work;
                // Вернули и всё закрыто — «закрыт без повторного ревью»; иначе работа идёт.
                bool closedReturned = allDone && lastVerdict == TaskVerdicts.Returned;
                outcome = closedReturned ? "returned" : "running";
                status = closedReturned ? "закрыт без повторного ревью" : "идёт";
            }
            else
            {
                bucket = Review;
                // Витков нет — «ждёт ревью»; виток идёт — «идёт».
                bool hasRound = featureRounds.Count > 0;
                outcome = hasRound ? "running" : "pending";
                status = hasRound ? "идёт" : "ждёт ревью";
            }
            // Моменты ожидания и принятия (макет A2, плашка карточки) — из существующих полей,
            // новой точки правды нет.
            // Ожидание: статус «ждёт ревью» = все задачи цепочки закрыты и витков нет; начинается
            // в момент закрытия последней из них. «Идёт» (открытый виток) и «вернулся» — не
            // ожидание: null, фронт честно деградирует без времени.
            DateTime? waitingSince = bucket == Review && featureRounds.Count == 0
                ? ordered.Max(t => t.CompletedAt ?? nowUtc)
                : null;
            // Принятие: момент закрытия последнего витка-принятия — ровно того, что признал шаг
            // (lastVerdict выше). Только у принятого шага; виток закрыт всегда (инвариант
            // TaskManager: вердикт только у Done-задачи), поэтому дата есть.
            DateTime? acceptedAt = outcome == Accepted ? featureRounds[^1].CompletedAt : null;

            bucketByKey[key] = bucket;
            string lane = FeatureProjector.NormalizeLane(head.Lane);
            laneByKey[key] = lane;
            if (loops > 0) returnedKeys.Add(key);

            // Длительность: от старта первой задачи до закрытия последней; у идущей — до сейчас.
            var start = FeatureProjector.TaskStart(ordered[0]);
            var end = ordered[^1].CompletedAt ?? nowUtc;
            chains.Add(new FeatureStepChain
            {
                StepRef = key,
                Lane = head.Lane,
                Title = head.Title,
                PersonaId = head.PersonaId,
                ChatId = LiveChat(head.LinkedSessionId),
                TaskIds = ordered.Select(t => t.Id).ToList(),
                Outcome = outcome,
                Status = status,
                WaitingSince = waitingSince,
                AcceptedAt = acceptedAt,
                DurationMinutes = Math.Max(0, Math.Round((end - start).TotalMinutes)),
                Source = source,
                Loops = loops,
                Rounds = featureRounds,
            });

            // Исполнители линии — по задачам-шагам (role=step) в порядке появления.
            if (!personasByLane.TryGetValue(lane, out var personas)) personasByLane[lane] = personas = [];
            foreach (var t in groupTasks)
                if (t.PersonaId is not null && !personas.Contains(t.PersonaId)) personas.Add(t.PersonaId);
        }

        // Шаги плана без задачи — корзина notCreated (линия — из плана).
        var notCreatedKeys = plannedOrder.Where(k => !groupIndex.ContainsKey(k)).ToList();
        foreach (var k in notCreatedKeys)
        {
            bucketByKey[k] = NotCreated;
            laneByKey[k] = FeatureProjector.NormalizeLane(planned[k].Lane);
        }

        // ─── Итог: контрактные числа ─────────────────────────────────────────────────────────
        int accepted = bucketByKey.Values.Count(b => b == Accepted);
        int review = bucketByKey.Values.Count(b => b == Review);
        int work = bucketByKey.Values.Count(b => b == Work);
        int notCreated = notCreatedKeys.Count;
        int total = accepted + review + work + notCreated;
        // done — план разобран и все его шаги приняты (known-знаменатель, пустой фичи не бывает).
        if (planState == PlanCut && total > 0 && accepted == total) planState = PlanDone;
        var progress = new FeatureStepProgress
        {
            PlanState = planState,
            Total = total,
            Accepted = accepted,
            Review = review,
            Work = work,
            NotCreated = notCreated,
            Withdrawn = bucketByKey.Values.Count(b => b == Withdrawn),
            UnlinkedReworks = unlinked,
        };

        // Линии: порядок — по первому появлению (сначала заведённые шаги, затем шаги плана без задачи).
        var laneOrder = new List<string>();
        foreach (var k in groups.Select(g => g.Key).Concat(notCreatedKeys))
            if (!laneOrder.Contains(laneByKey[k])) laneOrder.Add(laneByKey[k]);
        var lanes = new List<FeatureLane>(laneOrder.Count);
        foreach (var lane in laneOrder)
        {
            var keys = laneByKey.Where(kv => kv.Value == lane && bucketByKey[kv.Key] != Withdrawn)
                .Select(kv => kv.Key).ToList();
            int Count(string b) => keys.Count(k => bucketByKey[k] == b);
            lanes.Add(new FeatureLane
            {
                Lane = lane,
                PersonaIds = personasByLane.GetValueOrDefault(lane) ?? [],
                Total = keys.Count,
                Accepted = Count(Accepted),
                Review = Count(Review),
                Work = Count(Work),
                NotCreated = Count(NotCreated),
                Returns = keys.Count(returnedKeys.Contains),
            });
        }

        return new Result(chains, progress, lanes);
    }
}
