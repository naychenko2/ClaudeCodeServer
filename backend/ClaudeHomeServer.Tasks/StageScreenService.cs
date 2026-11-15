using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services.Tasks;

// Данные экрана этапа (D1a, разрез ai-fabrika-workflow-cut-2026-11.md §8, строка D1): три блока —
// артефакты (коммиты с трейлером CCS-Task, linkedFiles, resultMarkdown), вход и транскрипт
// (слепок этапа, постановка исполнителю, прочитанные файлы и вызванные инструменты), ревью
// (вердикт, ревью-задачи, ожидание человека). Четвёртый блок — метрики этапа (D1b): исполнитель,
// ревью и человек раздельно, считает FeatureStageMetrics по журналу и расходу фичи. Сервис только читает: ни задач, ни git, ни ленты чата не меняет.
//
// Видимость: экран видит тот, кто видит задачу фичи — владелец записи или участник проекта с
// правом Read (IProjectAccess); иначе — «нет такой» (404), без различия «чужая» и «нет». Лента
// чата исполнителя — отдельная дверь: ЧУЖОЙ чат закрыт для постороннего, и тогда транскрипт пуст
// (transcriptAvailable = false), остальные блоки отдаются. FeatureOwnerGate здесь не нужен: он
// держит двери ЗАПИСИ фичевых полей, а ручка только читает.
public sealed class StageScreenService(
    TaskManager tasks,
    IProjectAccess? access = null,
    IWorktreeCommitReader? git = null,
    IChatTranscriptReader? transcripts = null,
    // D1b: журнал фичи и шов расхода — оба опциональны (подсистема выключена → approx / null-поля, не 500)
    FeatureJournalStore? journal = null,
    ISpendFeatureReader? spend = null,
    // B04: имя персоны-автора вопроса; null — имя берётся из названия задачи
    IPersonaLookup? personas = null,
    // B07: название шаблона для шапки этапа; null — templateName пуст
    WorkflowTemplateStore? templates = null)
{
    // Потолки блока вопросов: история отвеченных и длина ответа человека
    public const int MaxAnsweredQuestions = 100;
    public const int MaxAnswerChars = 4_000;

    // Потолок размера диффа одного коммита (символов): дальше — truncated = true
    public const int MaxDiffChars = 200_000;
    // Потолки списков: коммиты одной задачи цепочки, вызовы инструментов, прочитанные файлы
    public const int MaxCommitsPerTask = 50;
    public const int MaxToolCalls = 500;
    public const int MaxFilesRead = 200;
    // Потолки текстов: постановка исполнителю и краткая запись аргумента инструмента
    public const int MaxPromptChars = 20_000;
    public const int MaxSummaryChars = 160;

    // sha в запросе — только hex 7–40, нижний регистр: ничего, кроме него, до git не доходит
    private static readonly Regex ShaPattern = new(@"^[0-9a-f]{7,40}$", RegexOptions.Compiled);

    // Поля аргумента инструмента, по которым строится краткая запись, в порядке предпочтения
    private static readonly string[] SummaryKeys =
        ["file_path", "path", "notebook_path", "pattern", "command", "url", "query", "description", "skill", "prompt"];

    public StageResult<StageScreen> Build(string taskId, string actorId)
    {
        if (Visible(taskId, actorId) is not { } task) return StageResult<StageScreen>.NotFound();

        var chain = Chain(task);
        var commits = CollectCommits(task, chain, out var commitsAvailable);
        var screen = new StageScreen(
            TaskId: task.Id, FeatureId: task.FeatureId, Role: task.Role, StepRef: task.StepRef,
            Title: task.Title, Status: StatusName(task.Status),
            Stage: task.Stage,
            Artifacts: new StageArtifacts(commits, commitsAvailable, task.LinkedFiles.ToList(), task.ResultMarkdown,
                BuildFiles(commits, chain), new StageArtifactTotals(FilesOf(commits).Count, commits.Count)),
            Input: BuildInput(task, actorId),
            Review: BuildReview(task),
            Metrics: BuildMetrics(task),
            Questions: BuildQuestions(task, chain),
            StageSummary: BuildStageSummary(task, actorId));
        return StageResult<StageScreen>.Ok(screen);
    }

    // Вопросы человеку на этапе (B04). Источник — ожидания человека задач цепочки этапа: открытые
    // берутся из пары AwaitingHumanSince/Reason, история — из пар waiting → waiting_cleared журнала
    // фичи (текст ответа и автор лежат в записи снятия; снятие ответом в чате текста не несёт —
    // тогда answer.text пуст). Сначала открытые, затем отвеченные, новые сверху. id вопроса стабилен:
    // «{taskId}:{момент постановки в мс}».
    private List<StageQuestion> BuildQuestions(TaskItem task, List<TaskItem> chain)
    {
        var result = new List<StageQuestion>();
        var now = DateTime.UtcNow;
        foreach (var t in chain)
        {
            if (t.AwaitingHumanSince is not { } since) continue;
            var reason = t.AwaitingHumanReason ?? "";
            result.Add(new StageQuestion(QuestionId(t.Id, since), "open", AskedBy(t), since, reason,
                Math.Max(0, (int)(now - since).TotalMinutes), null));
        }
        var open = result.OrderByDescending(q => q.AskedAt).ToList();

        var answered = new List<StageQuestion>();
        if (journal is not null && task.FeatureId is { } featureId && task.OwnerId is { } ownerId)
        {
            var ids = chain.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var entries = journal.Read(ownerId, featureId).Entries;
            foreach (var group in entries.Where(e => e.TaskId is not null && ids.ContainsKey(e.TaskId)
                         && e.Kind is "waiting" or "waiting_cleared").GroupBy(e => e.TaskId!))
            {
                FeatureJournalEntry? pending = null;
                foreach (var e in group.OrderBy(e => e.At))
                {
                    if (e.Kind == "waiting") { pending = e; continue; }
                    if (pending is null) continue;
                    var owner = ids[group.Key];
                    answered.Add(new StageQuestion(QuestionId(group.Key, pending.At), "answered", AskedBy(owner),
                        pending.At, pending.Reason ?? "",
                        Math.Max(0, (int)(e.At - pending.At).TotalMinutes),
                        new StageAnswer(e.Reason ?? "", e.By, e.At)));
                    pending = null;
                }
            }
        }
        var history = answered.OrderByDescending(q => q.Answer!.At).Take(MaxAnsweredQuestions);
        return open.Concat(history).ToList();
    }

    private static string QuestionId(string taskId, DateTime since) =>
        taskId + ":" + new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private StageQuestionAuthor AskedBy(TaskItem t) =>
        new(t.PersonaId, (t.PersonaId is { } pid ? personas?.GetByIdInternal(pid)?.Name : null) ?? t.Title);

    // Ответ человека на открытый вопрос этапа (B04). Только человек (контроллер отсекает сервисный токен).
    // Видимость — как у экрана; вопрос не из этого этапа или уже не открыт — 404; пустой/слишком длинный
    // текст — 400. Снимает ожидание задачи и кладёт ответ в журнал; доставку агенту делает вызывающий.
    public StageResult<StageAnsweredQuestion> AnswerQuestion(string taskId, string questionId, string? text, string actorId)
    {
        if (Visible(taskId, actorId) is not { } task) return StageResult<StageAnsweredQuestion>.NotFound();
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return StageResult<StageAnsweredQuestion>.BadRequest("text: нужен непустой ответ");
        if (trimmed.Length > MaxAnswerChars)
            return StageResult<StageAnsweredQuestion>.BadRequest($"text: не длиннее {MaxAnswerChars} символов");

        var chain = Chain(task);
        var known = BuildQuestions(task, chain).FirstOrDefault(q => q.Id == questionId);
        if (known is null) return StageResult<StageAnsweredQuestion>.NotFound();
        if (known.Status != "open") return StageResult<StageAnsweredQuestion>.Conflict("Вопрос уже отвечен");
        var question = known;
        var targetId = questionId[..questionId.LastIndexOf(':')];
        var cleared = tasks.AnswerAwaitingHuman(targetId, trimmed, actorId);
        if (cleared is null) return StageResult<StageAnsweredQuestion>.NotFound();
        var answered = question with
        {
            Status = "answered",
            Answer = new StageAnswer(trimmed, actorId, DateTime.UtcNow),
        };
        return StageResult<StageAnsweredQuestion>.Ok(new StageAnsweredQuestion(answered, cleared));
    }

    // Метрики этапа (D1b). T берётся только внутри фичи собственной задачи (FeatureId → эпик того же
    // арендатора), R(T) — только из её же задач: чужое в ответ не попадает (И3). Задача без эпика
    // (нет FeatureId, эпик пропал) — metrics = null: считать нечего, экран остальное отдаёт.
    // claimed — ревью, уже учтённые в KPI этапа: общее ревью (ReviewOf из нескольких задач) входит в
    // агрегат ровно один раз, у первой задачи, которая его встретила; null — одиночный экран задачи.
    // all — слепок задач, снятый один раз на экран; null — снимаем сами (одиночный вызов)
    private StageMetrics? BuildMetrics(TaskItem task, HashSet<string>? claimed = null, IReadOnlyList<TaskItem>? all = null)
    {
        if (task.FeatureId is null) return null;
        var epic = tasks.GetById(task.FeatureId);
        if (epic is null || epic.OwnerId is not { } ownerId || ownerId != task.OwnerId) return null;

        var reviews = (all ?? tasks.GetAll())
            .Where(t => t.Role == TaskRoles.Review && t.OwnerId == task.OwnerId && t.FeatureId == task.FeatureId
                && t.ReviewOf?.Contains(task.Id) == true)
            .OrderBy(t => t.CreatedAt)
            .ToList();
        if (claimed is not null)
        {
            reviews = reviews.Where(r => !claimed.Contains(r.Id)).ToList();
            foreach (var r in reviews) claimed.Add(r.Id);
        }

        var read = journal?.Read(ownerId, epic.Id);
        IReadOnlyList<FeatureSpendRecord>? records = null;
        if (spend is not null)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal) { task.Id };
            foreach (var r in reviews) ids.Add(r.Id);
            records = spend.Slice(ownerId, ids, [], [], null).Records;
        }
        return FeatureStageMetrics.Build(task, reviews, read, records, DateTime.UtcNow);
    }

    // Сводка шапки этапа (B07): исполнитель, шаблон, KPI, задачи ветки, соседняя ветка, чат исполнителя.
    // «Этап» — все шаги и доработки фичи с тем же stage.id (слепок), у слепков без id — цепочка шага.
    // Агрегаты считаются по всем его задачам; approx = журнал неполный хотя бы у одной (легаси тоже).
    private StageSummary BuildStageSummary(TaskItem task, string actorId)
    {
        var epic = task.FeatureId is { } fid ? tasks.GetById(fid) : null;
        // Список задач снимаем один раз на экран: метрики, ревью и соседняя ветка считаются по нему
        var all = tasks.GetAll();
        var members = StageMembers(task, all);
        var byStep = members.GroupBy(t => t.StepRef ?? t.Id).ToList();

        var rows = new List<StageSummaryTask>();
        var metrics = new List<StageMetrics>();
        var claimedReviews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in byStep)
        {
            var main = group.OrderBy(t => t.Role == TaskRoles.Step ? 0 : 1).ThenBy(t => t.CreatedAt).First();
            var chain = group.ToList();
            var reviews = ReviewsOf(chain, all);
            var returns = reviews.Count(r => r.Verification?.Verdict == TaskVerdicts.Returned);
            var last = reviews.LastOrDefault(r => r.Verification?.Verdict is not null)?.Verification?.Verdict;
            var verdict = last switch
            {
                TaskVerdicts.Returned => "returned",
                TaskVerdicts.Accepted or TaskVerdicts.AcceptedWithRemarks => "accepted",
                _ => null
            };
            int? minutes = null;
            foreach (var t in chain)
            {
                var m = BuildMetrics(t, claimedReviews, all);
                if (m is null) continue;
                metrics.Add(m);
                if (m.Executor.Minutes is { } em) minutes = (minutes ?? 0) + em;
            }
            var name = PersonaName(main.PersonaId ?? main.Stage?.PersonaId);
            var state = chain.Any(t => t.AwaitingHumanSince is not null) ? "waiting"
                : chain.All(t => t.Status == TaskItemStatus.Done) ? "done"
                : StatusName(chain.Any(t => t.Status == TaskItemStatus.InProgress) ? TaskItemStatus.InProgress : main.Status);
            rows.Add(new StageSummaryTask(main.Id, main.StepRef, main.Title, state,
                name is null ? null : new StageSummaryExecutorName(name), minutes, verdict, returns));
        }

        var tokenParts = metrics.SelectMany(m => new[] { m.Executor.Tokens, m.Review?.Tokens }).Where(t => t is not null).Select(t => t!).ToList();
        // totalMinutes — сумма трёх долей (работа, ревью, ожидание человека), а не длина окна
        var execMinutes = metrics.Sum(m => m.Executor.Minutes ?? 0);
        var reviewMinutes = metrics.Sum(m => m.Review?.Minutes ?? 0);
        var waitedMinutes = metrics.Sum(m => m.Human.Minutes ?? 0);
        var kpi = new StageSummaryKpi(
            execMinutes + reviewMinutes + waitedMinutes, execMinutes, reviewMinutes, waitedMinutes,
            metrics.Sum(m => (m.Executor.Turns ?? 0) + (m.Review?.Turns ?? 0)),
            tokenParts.Count == 0 ? null : new StageSummaryTokens(
                tokenParts.Sum(t => t.Input), tokenParts.Sum(t => t.Output),
                tokenParts.Sum(t => t.CacheRead + t.CacheCreation)),
            metrics.Count == 0 || metrics.Any(m => m.Mode != "journal"));

        return new StageSummary(
            BuildExecutor(task), TemplateName(task, epic), task.Stage?.TemplateVersion ?? 0,
            rows.Sum(r => r.Returns), kpi, rows, BuildParallelNote(task, epic, all), ExecutorSessionId(task, actorId));
    }

    // Задачи этапа: тот же stage.id в одной фиче одного арендатора (шаги и доработки); без id — цепочка шага
    private List<TaskItem> StageMembers(TaskItem task, IReadOnlyList<TaskItem> all)
    {
        var id = task.Stage?.StageId;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(task.FeatureId)) return Chain(task, all);
        var list = new List<TaskItem> { task };
        list.AddRange(all.Where(t => t.Id != task.Id && t.OwnerId == task.OwnerId && t.FeatureId == task.FeatureId
            && t.Role is TaskRoles.Step or TaskRoles.Rework && t.Stage?.StageId == id));
        return list;
    }

    private static List<TaskItem> ReviewsOf(List<TaskItem> chain, IReadOnlyList<TaskItem> all)
    {
        var ids = chain.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return all
            .Where(t => t.Role == TaskRoles.Review && t.OwnerId == chain[0].OwnerId && t.ReviewOf?.Any(ids.Contains) == true)
            .OrderBy(t => t.CreatedAt)
            .ToList();
    }

    private string? PersonaName(string? personaId) =>
        personaId is null ? null : personas?.GetByIdInternal(personaId)?.Name;

    private StageSummaryExecutor? BuildExecutor(TaskItem task)
    {
        var pid = task.PersonaId ?? task.Stage?.PersonaId;
        if (pid is null || personas?.GetByIdInternal(pid) is not { } persona) return null;

        // Эффективный уровень: задача → слепок этапа → персона → средний. Ячейка этапа — «своя»
        var tier = task.ModelTier ?? task.Stage?.DefaultTier ?? persona.ModelTier ?? ModelTier.Medium;
        var stageCell = tier switch
        {
            ModelTier.Strong => task.Stage?.TierStrong, ModelTier.Weak => task.Stage?.TierWeak, _ => task.Stage?.TierMedium
        };
        var personaCell = tier switch
        {
            ModelTier.Strong => persona.TierStrong, ModelTier.Weak => persona.TierWeak, _ => persona.TierMedium
        };
        var own = !string.IsNullOrWhiteSpace(stageCell);
        var label = own ? stageCell!
            : !string.IsNullOrWhiteSpace(personaCell) ? personaCell!
            : !string.IsNullOrWhiteSpace(persona.Model) ? persona.Model!
            : JsonNamingPolicy.CamelCase.ConvertName(tier.ToString());

        int? silence = task.Stage?.StallMinutes is > 0 ? task.Stage.StallMinutes : null;
        var action = silence is null ? null : task.Stage?.StallPolicy switch
        {
            WorkflowStallPolicies.Alert => "notify",
            WorkflowStallPolicies.StopAlert => "stop",
            _ => "nudge"
        };
        return new StageSummaryExecutor(persona.Id, persona.Name, persona.Role, label, own, silence, action);
    }

    private string? TemplateName(TaskItem task, TaskItem? epic)
    {
        if (templates is null || task.Stage is not { TemplateId.Length: > 0 } st) return null;
        var scope = string.Equals(epic?.Workflow?.Scope, "project", StringComparison.Ordinal)
            ? WorkflowTemplateScope.Project : WorkflowTemplateScope.Global;
        return templates.Get(st.TemplateId, scope, task.ProjectId)?.Name;
    }

    // Соседняя ветка: другой шаг маршрута, не связанный с этапом ни предком, ни потомком. Из них —
    // та, где ещё идёт работа, иначе первая; у ветки без задач показывать нечего — null
    private StageSummaryParallel? BuildParallelNote(TaskItem task, TaskItem? epic, IReadOnlyList<TaskItem> all)
    {
        var stages = epic?.Workflow?.Stages;
        var myId = task.Stage?.StageId;
        if (stages is null || string.IsNullOrEmpty(myId) || task.Role is not (TaskRoles.Step or TaskRoles.Rework)) return null;
        var byId = stages.Where(s => s.Id.Length > 0).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        HashSet<string> Ancestors(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([id]);
            while (stack.Count > 0 && byId.TryGetValue(stack.Pop(), out var s))
                foreach (var a in s.After.Where(seen.Add)) stack.Push(a);
            return seen;
        }
        if (!byId.ContainsKey(myId)) return null;
        var mine = Ancestors(myId);

        var candidates = new List<(RouteStage Stage, List<TaskItem> Tasks)>();
        foreach (var s in stages.Where(s => s.Role == TaskRoles.Step && s.Id != myId))
        {
            if (mine.Contains(s.Id) || Ancestors(s.Id).Contains(myId)) continue;
            var own = all.Where(t => t.OwnerId == task.OwnerId && t.FeatureId == task.FeatureId
                && t.Role is TaskRoles.Step or TaskRoles.Rework && t.Stage?.StageId == s.Id).ToList();
            if (own.Count > 0) candidates.Add((s, own));
        }
        if (candidates.Count == 0) return null;
        var pick = candidates.FirstOrDefault(c => c.Tasks.Any(t => t.Status != TaskItemStatus.Done));
        if (pick.Stage is null) pick = candidates[0];

        var steps = pick.Tasks.GroupBy(t => t.StepRef ?? t.Id).ToList();
        var done = steps.Count(g => g.All(t => t.Status == TaskItemStatus.Done));
        var lead = pick.Tasks.OrderBy(t => t.CreatedAt).First();
        return new StageSummaryParallel(pick.Stage.Id, pick.Stage.Title ?? pick.Stage.Role, done, steps.Count,
            PersonaName(lead.PersonaId ?? lead.Stage?.PersonaId));
    }

    // Чат исполнителя — только владельцу задачи (постановка B07): участнику проекта с правом Read
    // sessionId не уходит, даже если транскрипт ему открыт (там своя дверь, строка ниже по файлу)
    private string? ExecutorSessionId(TaskItem task, string actorId)
    {
        var sessionId = task.LinkedSessionId;
        if (string.IsNullOrEmpty(sessionId)) return null;
        return task.OwnerId == actorId ? sessionId : null;
    }

    public StageResult<StageCommitDiff> BuildDiff(string taskId, string? sha, string actorId)
    {
        if (Visible(taskId, actorId) is not { } task) return StageResult<StageCommitDiff>.NotFound();
        sha ??= "";
        if (!ShaPattern.IsMatch(sha))
            return StageResult<StageCommitDiff>.BadRequest("sha — от 7 до 40 символов [0-9a-f]");

        // Дифф — только коммита из списка этого этапа: полный sha берём из списка, а не из запроса
        var commits = CollectCommits(task, Chain(task), out _);
        var matches = commits.Where(c => c.Sha.StartsWith(sha, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) return StageResult<StageCommitDiff>.NotFound();
        var commit = matches[0];

        if (git is null || WorktreeOf(task, commit.TaskId) is not { } path)
            return StageResult<StageCommitDiff>.NotFound();
        var diff = git.CommitDiff(path, commit.Sha, MaxDiffChars);
        if (diff is null) return StageResult<StageCommitDiff>.NotFound();
        return StageResult<StageCommitDiff>.Ok(
            new StageCommitDiff(commit.Sha, commit.Subject, commit.Files, diff.Text, diff.Truncated, MaxDiffChars));
    }

    // Версии файлов этапа (B06): версия файла = коммит этапа, тронувший его; n=1 — самый ранний.
    // afterReturn — коммит сделан после первого возврата ревью этой цепочки
    private List<StageFile> BuildFiles(List<StageCommit> commits, List<TaskItem> chain)
    {
        var returnedAt = ReturnedAt(chain);
        return FilesOf(commits).Select(kv => new StageFile(kv.Key,
            kv.Value.Select((c, i) => new StageFileVersion(i + 1, c.Sha, c.Subject, c.At,
                returnedAt is { } r && c.At is { } at && at > r)).ToList())).ToList();
    }

    // Файл → коммиты, его тронувшие (порядок коммитов — от старого к новому); порядок файлов — по первому появлению
    private static List<KeyValuePair<string, List<StageCommit>>> FilesOf(List<StageCommit> commits)
    {
        var map = new Dictionary<string, List<StageCommit>>(StringComparer.Ordinal);
        foreach (var c in commits)
            foreach (var f in c.Files.Distinct(StringComparer.Ordinal))
            {
                if (!map.TryGetValue(f, out var list)) map[f] = list = [];
                list.Add(c);
            }
        return map.ToList();
    }

    // Момент первого возврата ревью цепочки этапа; null — возвратов не было
    private DateTime? ReturnedAt(List<TaskItem> chain)
    {
        var ids = chain.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var owner = chain[0].OwnerId;
        return tasks.GetAll()
            .Where(t => t.Role == TaskRoles.Review && t.OwnerId == owner
                && t.Verification?.Verdict == TaskVerdicts.Returned && t.ReviewOf?.Any(ids.Contains) == true)
            .Select(t => (DateTime?)t.Verification!.VerifiedAt)
            .Min();
    }

    // Дифф файла между версиями (B06). from/to — номер версии файла или «all» (from=all — состояние до
    // первой версии, to=all — после последней); не заданы — all. Файл — только из списка этапа
    public StageResult<StageFileDiff> BuildFileDiff(string taskId, string? path, string? from, string? to, string actorId)
    {
        if (Visible(taskId, actorId) is not { } task) return StageResult<StageFileDiff>.NotFound();
        if (string.IsNullOrWhiteSpace(path)) return StageResult<StageFileDiff>.BadRequest("path обязателен");
        // Путь — строго внутри дерева задачи: «..», абсолютный и корень — 400 (SafePath), до списка файлов и git
        try { SafePath.JoinBelowRoot(task.WorktreePath is { Length: > 0 } root ? root : Path.GetTempPath(), path); }
        catch (UnauthorizedAccessException) { return StageResult<StageFileDiff>.BadRequest("path вне дерева задачи"); }
        if (!TryVersion(from, out var f)) return StageResult<StageFileDiff>.BadRequest("from — номер версии или all");
        if (!TryVersion(to, out var t)) return StageResult<StageFileDiff>.BadRequest("to — номер версии или all");
        if (f is not null && t is not null && f > t) return StageResult<StageFileDiff>.BadRequest("from не может быть позже to");

        var chain = Chain(task);
        var commits = CollectCommits(task, chain, out _);
        if (FilesOf(commits).FirstOrDefault(kv => kv.Key == path).Value is not { } versions)
            return StageResult<StageFileDiff>.NotFound();
        if (f > versions.Count || t > versions.Count || f < 1 || t < 1) return StageResult<StageFileDiff>.NotFound();

        var fromCommit = versions[(f ?? 1) - 1];
        var toCommit = versions[(t ?? versions.Count) - 1];
        // from = all или from = to — показываем изменение самой версии: от состояния ДО коммита
        var fromRef = f is null || f == t ? fromCommit.Sha + "^" : fromCommit.Sha;
        if (git is null || WorktreeOf(task, toCommit.TaskId) is not { } wt) return StageResult<StageFileDiff>.NotFound();
        var diff = git.FileDiff(wt, fromRef, toCommit.Sha, path, MaxDiffChars);
        if (diff is null) return StageResult<StageFileDiff>.NotFound();
        return StageResult<StageFileDiff>.Ok(new StageFileDiff(path, f?.ToString() ?? "all", t?.ToString() ?? "all",
            diff.Text, diff.Truncated, MaxDiffChars));
    }

    // null в out — «all» (или не задано); false — не число и не all
    private static bool TryVersion(string? raw, out int? n)
    {
        n = null;
        if (string.IsNullOrEmpty(raw) || raw == "all") return true;
        if (!int.TryParse(raw, out var v)) return false;
        n = v;
        return true;
    }

    // Задача фичи, видимая актору, или null. Не-фичевая задача (ни роли, ни featureId) — не предмет
    // экрана этапа: для неё «нет такой»
    private TaskItem? Visible(string taskId, string actorId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrEmpty(actorId)) return null;
        var task = tasks.GetById(taskId);
        if (task is null || task.Role is null && task.FeatureId is null) return null;
        if (task.OwnerId == actorId) return task;
        if (task.ProjectId is { } projectId && access?.Resolve(actorId, projectId, ProjectRight.Read) is not null)
            return task;
        return null;
    }

    // Цепочка версий артефакта: сам шаг и его доработки (rework того же stepRef), в одной фиче одного
    // арендатора. Задача без stepRef — цепочка из неё одной
    private List<TaskItem> Chain(TaskItem task, IReadOnlyList<TaskItem>? all = null)
    {
        var chain = new List<TaskItem> { task };
        if (string.IsNullOrEmpty(task.StepRef) || string.IsNullOrEmpty(task.FeatureId)) return chain;
        chain.AddRange((all ?? tasks.GetAll()).Where(t =>
            t.Id != task.Id && t.OwnerId == task.OwnerId && t.FeatureId == task.FeatureId
            && t.StepRef == task.StepRef && t.Role is TaskRoles.Step or TaskRoles.Rework));
        return chain;
    }

    // Дерево, в котором искать коммиты задачи цепочки: своё, иначе дерево запрошенной задачи
    private static string? WorktreeOf(TaskItem requested, string chainTaskId, IEnumerable<TaskItem>? chain = null)
    {
        var own = chain?.FirstOrDefault(t => t.Id == chainTaskId)?.WorktreePath;
        return !string.IsNullOrWhiteSpace(own) ? own : requested.WorktreePath;
    }

    private string? WorktreeOf(TaskItem requested, string chainTaskId) =>
        WorktreeOf(requested, chainTaskId, Chain(requested));

    private List<StageCommit> CollectCommits(TaskItem task, List<TaskItem> chain, out bool available)
    {
        available = false;
        var found = new List<StageCommit>();
        if (git is null) return found;
        foreach (var t in chain)
        {
            var path = !string.IsNullOrWhiteSpace(t.WorktreePath) ? t.WorktreePath : task.WorktreePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            var read = git.CommitsWithTaskTrailer(path, t.Id, MaxCommitsPerTask);
            if (read is null) continue;
            available = true;
            foreach (var c in read)
                found.Add(new StageCommit(c.Sha, c.Sha[..7], c.Subject, c.At, c.Files, t.Id, t.Role, 0));
        }
        // Версии — по времени коммита от старого к новому; одинаковый sha из двух деревьев — один коммит
        var ordered = found
            .GroupBy(c => c.Sha).Select(g => g.First())
            .OrderBy(c => c.At ?? DateTime.MinValue)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i] = ordered[i] with { Version = i + 1 };
        return ordered;
    }

    private StageInput BuildInput(TaskItem task, string actorId)
    {
        var sessionId = task.LinkedSessionId;
        if (string.IsNullOrEmpty(sessionId) || transcripts is null)
            return new StageInput(task.Stage, null, false, null);

        // Лента чата исполнителя — чужой приватный чат постороннему не отдаём (как маску чужих чатов в карточке)
        var canSeeChat = task.OwnerId == actorId || access?.ResolveSession(actorId, sessionId, ProjectRight.Read) is not null;
        if (!canSeeChat) return new StageInput(task.Stage, null, false, null);

        IReadOnlyList<StoredMessage> history;
        try { history = transcripts.ReadAsync(sessionId).GetAwaiter().GetResult(); }
        catch (Exception) { history = []; }
        return new StageInput(task.Stage, sessionId, true, BuildTranscript(history));
    }

    private static StageTranscript BuildTranscript(IReadOnlyList<StoredMessage> history)
    {
        var prompt = history.OfType<StoredUserMessage>().FirstOrDefault()?.Text;
        if (prompt is not null)
        {
            // Сначала редакция, потом усечение: граница усечения не должна разрезать секрет (PEM без END
            // и токен без хвоста регулярки не опознают). Точные секреты инстанса (InstanceSecretsProvider)
            // живут в вертикали Dossiers, а Tasks от неё зависеть не вправе, поэтому здесь — только
            // регулярки: ключ стороннего провайдера без узнаваемого префикса не вырезается.
            prompt = SecretRedactor.Redact(prompt);
            if (prompt.Length > MaxPromptChars) prompt = prompt[..MaxPromptChars];
        }

        var calls = new List<StageToolCall>();
        var files = new List<string>();
        var total = 0;
        foreach (var use in history.OfType<StoredToolUseMessage>())
        {
            total++;
            var summary = Summarize(use.Input);
            if (use.Name == "Read" && SummaryFilePath(use.Input) is { } path
                && files.Count < MaxFilesRead && !files.Contains(path))
                files.Add(path);
            if (calls.Count >= MaxToolCalls) continue;
            DateTime? at = use.StartedAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
            calls.Add(new StageToolCall(use.Name, summary, at, use.IsError));
        }
        return new StageTranscript(prompt, files, calls, total, total > calls.Count);
    }

    // Аргументы инструмента как JSON-объект; полный ответ инструмента не читается вовсе
    private static JsonElement? InputObject(object? input)
    {
        if (input is null) return null;
        try
        {
            var el = input is JsonElement e ? e : JsonSerializer.SerializeToElement(input);
            return el.ValueKind == JsonValueKind.Object ? el : null;
        }
        catch (Exception) { return null; }
    }

    private static string? SummaryFilePath(object? input) =>
        InputObject(input) is { } obj && obj.TryGetProperty("file_path", out var p) && p.ValueKind == JsonValueKind.String
            ? SecretRedactor.Redact(p.GetString())
            : null;

    private static string? Summarize(object? input)
    {
        if (InputObject(input) is not { } obj) return null;
        foreach (var key in SummaryKeys)
        {
            if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = (value.GetString() ?? "").ReplaceLineEndings(" ").Trim();
            if (text.Length == 0) continue;
            // Редакция до усечения (см. BuildTranscript)
            text = SecretRedactor.Redact(text);
            if (text.Length > MaxSummaryChars) text = text[..MaxSummaryChars] + "…";
            return text;
        }
        return null;
    }

    // Статус в той же форме, что отдаёт остальной API (camelCase)
    private static string StatusName(TaskItemStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    private StageReview BuildReview(TaskItem task)
    {
        var reviews = tasks.GetAll()
            .Where(t => t.Role == TaskRoles.Review && t.OwnerId == task.OwnerId && t.ReviewOf?.Contains(task.Id) == true)
            .OrderBy(t => t.CreatedAt)
            .Select(t => ToReview(t, t.Verification))
            .ToList();
        StageReviewItem? own = task.Verification is null ? null : ToReview(task, task.Verification);
        StageAwaitingHuman? awaiting = task.AwaitingHumanSince is { } since
            ? new StageAwaitingHuman(since, task.AwaitingHumanReason) : null;
        return new StageReview(own, reviews, awaiting, BuildReviewRounds(task));
    }

    // Раунды ревью (B05): по одной записи на ревью-задачу с вердиктом, по времени создания. Эпик без
    // маршрута и без предложения — легаси: разбор блокеров не делаем, структурность не обещаем.
    // pending — отдельная запись-ожидание следующего раунда (verdict/at = null), если последний
    // раунд вернул шаг, а шаг ещё не закрыт.
    private StageReviewRounds BuildReviewRounds(TaskItem task)
    {
        var epic = task.FeatureId is { } fid ? tasks.GetById(fid) : null;
        var legacy = epic is not null && epic.Workflow is null && epic.WorkflowProposal is null;
        var verified = tasks.GetAll()
            .Where(t => t.Role == TaskRoles.Review && t.OwnerId == task.OwnerId && t.ReviewOf?.Contains(task.Id) == true
                && t.Verification?.Verdict is not null)
            .OrderBy(t => t.CreatedAt)
            .ToList();
        var rounds = new List<StageReviewRound>();
        for (var i = 0; i < verified.Count; i++)
        {
            var t = verified[i];
            var v = t.Verification!;
            var (blockers, remarks, structured) = legacy
                ? ([], string.IsNullOrWhiteSpace(v.Notes) ? [] : new List<string> { v.Notes.Trim() }, false)
                : ReviewNotesParser.Parse(v.Notes);
            var pid = v.PersonaId ?? t.PersonaId;
            StageQuestionAuthor? reviewer = pid is null ? null
                : new StageQuestionAuthor(pid, personas?.GetByIdInternal(pid)?.Name ?? t.Title);
            rounds.Add(new StageReviewRound(i + 1, reviewer, v.Verdict, v.ReturnReason, v.VerifiedAt,
                blockers, remarks, structured, false));
        }
        // Последний раунд вернул шаг, а шаг не закрыт — следующий раунд ждёт доработку исполнителя
        // и запустится сам: отдельная запись без ревьюера, вердикта и времени.
        if (verified.Count > 0 && verified[^1].Verification!.Verdict == TaskVerdicts.Returned
            && task.Status != TaskItemStatus.Done)
            rounds.Add(new StageReviewRound(verified.Count + 1, null, null, null, null, [], [], false, true));
        return new StageReviewRounds(rounds, rounds.Any(r => !r.Pending) && rounds.Where(r => !r.Pending).All(r => r.Structured), legacy ? "legacy" : null);
    }

    private static StageReviewItem ToReview(TaskItem t, TaskVerification? v) => new(
        t.Id, t.Title, t.Role, StatusName(t.Status), v?.Verdict, v?.ReturnReason, v?.Notes,
        // Блокер — возврат: шаг не принят, пока замечания не сняты
        v?.Verdict == TaskVerdicts.Returned, v?.VerifiedAt);
}

public enum StageOutcome { Ok, NotFound, BadRequest, Conflict }

public sealed record StageResult<T>(StageOutcome Outcome, T? Value, string? Error) where T : class
{
    public static StageResult<T> Ok(T value) => new(StageOutcome.Ok, value, null);
    public static StageResult<T> NotFound() => new(StageOutcome.NotFound, null, null);
    public static StageResult<T> BadRequest(string error) => new(StageOutcome.BadRequest, null, error);
    public static StageResult<T> Conflict(string error) => new(StageOutcome.Conflict, null, error);
}

// Ответ GET /api/tasks/{id}/stage. Metrics — блок метрик этапа (D1b); null — у задачи нет фичи с эпиком
public sealed record StageScreen(
    string TaskId, string? FeatureId, string? Role, string? StepRef, string Title, string Status,
    StageSnapshot? Stage, StageArtifacts Artifacts, StageInput Input, StageReview Review, StageMetrics? Metrics,
    IReadOnlyList<StageQuestion> Questions, StageSummary StageSummary);

// CommitsAvailable = false — дерево задачи недоступно или git не ответил (список пуст не потому, что коммитов нет)
public sealed record StageArtifacts(
    IReadOnlyList<StageCommit> Commits, bool CommitsAvailable,
    IReadOnlyList<string> LinkedFiles, string? ResultMarkdown,
    IReadOnlyList<StageFile> Files, StageArtifactTotals Totals);

// Файл этапа с версиями (B06): версия = коммит этапа, тронувший файл; N — с 1, от раннего к позднему
public sealed record StageFile(string Path, IReadOnlyList<StageFileVersion> Versions);

public sealed record StageFileVersion(int N, string CommitSha, string Subject, DateTime? At, bool AfterReturn);

public sealed record StageArtifactTotals(int Files, int Commits);

// From/To — номера версий или «all»
public sealed record StageFileDiff(string Path, string From, string To, string Diff, bool Truncated, int MaxChars);

// Version — номер версии артефакта в цепочке шага и доработок (с 1, по времени коммита)
public sealed record StageCommit(
    string Sha, string ShortSha, string Subject, DateTime? At, IReadOnlyList<string> Files,
    string TaskId, string? Role, int Version);

// Transcript = null — чат исполнителя не привязан или закрыт этому читателю (TranscriptAvailable = false)
public sealed record StageInput(StageSnapshot? Stage, string? SessionId, bool TranscriptAvailable, StageTranscript? Transcript);

public sealed record StageTranscript(
    string? Prompt, IReadOnlyList<string> FilesRead, IReadOnlyList<StageToolCall> ToolCalls,
    int ToolCallsTotal, bool Truncated);

public sealed record StageToolCall(string Name, string? Summary, DateTime? At, bool IsError);

public sealed record StageReview(
    StageReviewItem? Own, IReadOnlyList<StageReviewItem> Reviews, StageAwaitingHuman? AwaitingHuman,
    StageReviewRounds ReviewRounds);

// Раунды ревью этапа (B05). Structured — notes всех раундов разобраны по договорённости формата;
// Hint = "legacy" у эпика без шаблона (подробностей нет)
public sealed record StageReviewRounds(IReadOnlyList<StageReviewRound> Rounds, bool Structured, string? Hint);

public sealed record StageReviewRound(
    int Round, StageQuestionAuthor? Reviewer, string? Verdict, string? ReturnReason, DateTime? At,
    IReadOnlyList<StageReviewBlocker> Blockers, IReadOnlyList<string> Remarks, bool Structured, bool Pending);

public sealed record StageReviewBlocker(string Tag, string? File, int? Line, string Text);

public sealed record StageReviewItem(
    string TaskId, string Title, string? Role, string Status, string? Verdict, string? ReturnReason,
    string? Notes, bool Blocker, DateTime? VerifiedAt);

public sealed record StageAwaitingHuman(DateTime Since, string? Reason);

// Вопрос человеку на этапе (B04). Status — open | answered; WaitedMinutes — сколько ждали (для открытого —
// до сих пор, для отвеченного — до ответа); Answer = null у открытого
public sealed record StageQuestion(
    string Id, string Status, StageQuestionAuthor AskedBy, DateTime AskedAt, string Text,
    int? WaitedMinutes, StageAnswer? Answer);

public sealed record StageQuestionAuthor(string? PersonaId, string Name);

public sealed record StageAnswer(string Text, string? By, DateTime At);

// Результат ответа: сам вопрос и задача после снятия ожидания (для рассылки task_changed)
public sealed record StageAnsweredQuestion(StageQuestion Question, TaskItem Task);

public sealed record StageCommitDiff(
    string Sha, string Subject, IReadOnlyList<string> Files, string Diff, bool Truncated, int MaxChars);

// Сводка шапки этапа (B07). Executor = null — у этапа нет персоны-исполнителя; ParallelNote = null —
// соседней ветки нет; ExecutorSessionId = null — чат не привязан или закрыт читателю. Kpi.Approx — журнал
// неполный (легаси), тогда Tokens может быть null; Returns — возвраты ревью по всем шагам этапа.
public sealed record StageSummary(
    StageSummaryExecutor? Executor, string? TemplateName, int TemplateVersion, int Returns, StageSummaryKpi Kpi,
    IReadOnlyList<StageSummaryTask> Tasks, StageSummaryParallel? ParallelNote, string? ExecutorSessionId);

// SilenceAction: notify | nudge | stop (политика простоя слепка); ModelIsOwnCell — модель задана ячейкой этапа
public sealed record StageSummaryExecutor(
    string PersonaId, string Name, string? RoleTitle, string ModelLabel, bool ModelIsOwnCell,
    int? SilenceMinutes, string? SilenceAction);

public sealed record StageSummaryKpi(
    int TotalMinutes, int ExecMinutes, int ReviewMinutes, int WaitedHumanMinutes, int Turns,
    StageSummaryTokens? Tokens, bool Approx);

public sealed record StageSummaryTokens(long Input, long Output, long Cache);

// State: todo | inProgress | waiting | done; Verdict: accepted | returned | null
public sealed record StageSummaryTask(
    string TaskId, string? StepRef, string Title, string State, StageSummaryExecutorName? Executor,
    int? Minutes, string? Verdict, int Returns);

public sealed record StageSummaryExecutorName(string Name);

public sealed record StageSummaryParallel(string StageId, string Title, int DoneCount, int TotalCount, string? PersonaName);
