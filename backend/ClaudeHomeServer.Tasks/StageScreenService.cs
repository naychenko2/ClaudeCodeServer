using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Dossiers;

namespace ClaudeHomeServer.Services.Tasks;

// Данные экрана этапа (D1a, разрез ai-fabrika-workflow-cut-2026-11.md §8, строка D1): три блока —
// артефакты (коммиты с трейлером CCS-Task, linkedFiles, resultMarkdown), вход и транскрипт
// (слепок этапа, постановка исполнителю, прочитанные файлы и вызванные инструменты), ревью
// (вердикт, ревью-задачи, ожидание человека). Четвёртый блок — метрики — зарезервирован полем
// metrics = null (D1b). Сервис только читает: ни задач, ни git, ни ленты чата не меняет.
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
    IChatTranscriptReader? transcripts = null)
{
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
            Artifacts: new StageArtifacts(commits, commitsAvailable, task.LinkedFiles.ToList(), task.ResultMarkdown),
            Input: BuildInput(task, actorId),
            Review: BuildReview(task),
            Metrics: null);
        return StageResult<StageScreen>.Ok(screen);
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
    private List<TaskItem> Chain(TaskItem task)
    {
        var chain = new List<TaskItem> { task };
        if (string.IsNullOrEmpty(task.StepRef) || string.IsNullOrEmpty(task.FeatureId)) return chain;
        chain.AddRange(tasks.GetAll().Where(t =>
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
            prompt = SecretRedactor.Redact(prompt.Length > MaxPromptChars ? prompt[..MaxPromptChars] : prompt);

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
            if (text.Length > MaxSummaryChars) text = text[..MaxSummaryChars] + "…";
            return SecretRedactor.Redact(text);
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
        return new StageReview(own, reviews, awaiting);
    }

    private static StageReviewItem ToReview(TaskItem t, TaskVerification? v) => new(
        t.Id, t.Title, t.Role, StatusName(t.Status), v?.Verdict, v?.ReturnReason, v?.Notes,
        // Блокер — возврат: шаг не принят, пока замечания не сняты
        v?.Verdict == TaskVerdicts.Returned, v?.VerifiedAt);
}

public enum StageOutcome { Ok, NotFound, BadRequest }

public sealed record StageResult<T>(StageOutcome Outcome, T? Value, string? Error) where T : class
{
    public static StageResult<T> Ok(T value) => new(StageOutcome.Ok, value, null);
    public static StageResult<T> NotFound() => new(StageOutcome.NotFound, null, null);
    public static StageResult<T> BadRequest(string error) => new(StageOutcome.BadRequest, null, error);
}

// Ответ GET /api/tasks/{id}/stage. Metrics зарезервирован под D1b (всегда null в D1a)
public sealed record StageScreen(
    string TaskId, string? FeatureId, string? Role, string? StepRef, string Title, string Status,
    StageSnapshot? Stage, StageArtifacts Artifacts, StageInput Input, StageReview Review, object? Metrics);

// CommitsAvailable = false — дерево задачи недоступно или git не ответил (список пуст не потому, что коммитов нет)
public sealed record StageArtifacts(
    IReadOnlyList<StageCommit> Commits, bool CommitsAvailable,
    IReadOnlyList<string> LinkedFiles, string? ResultMarkdown);

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
    StageReviewItem? Own, IReadOnlyList<StageReviewItem> Reviews, StageAwaitingHuman? AwaitingHuman);

public sealed record StageReviewItem(
    string TaskId, string Title, string? Role, string Status, string? Verdict, string? ReturnReason,
    string? Notes, bool Blocker, DateTime? VerifiedAt);

public sealed record StageAwaitingHuman(DateTime Since, string? Reason);

public sealed record StageCommitDiff(
    string Sha, string Subject, IReadOnlyList<string> Files, string Diff, bool Truncated, int MaxChars);
