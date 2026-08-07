using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Dossiers;

// Hosted service захвата паспортов изменений (ADR-004 §2-§3, §7). Детект коммитов —
// тик 60 с (по образцу ChatExpiryService) + внеочередная проверка по завершении хода
// (подписка на SessionManager.OnSessionMessage, по образцу TeamMemoryAutolearnService).
// Единица наблюдения — РАБОЧЕЕ ДЕРЕВО (не проект): worktree-чат коммитит в свою ветку,
// и HEAD корня проекта такого коммита не увидит (major-правка Глеба №3).
public sealed class DossierCaptureService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private const int CommitScanLimit = 50;
    private const int MaxTranscriptChars = 12_000;
    private const int MaxDiffStatChars = 4_000;
    private const int MaxTaskCardChars = 2_000;
    private const int MaxSummaryItemChars = 200;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly TaskManager _tasks;
    private readonly FileService _files;
    private readonly Git.GitService _git;
    private readonly DossierStore _store;
    private readonly DossierCaptureState _state;
    private readonly ICheapTextRunner _cheap;
    private readonly FeatureFlagService _flags;
    private readonly CodeGraph.CodeGraphService _codeGraph;
    private readonly InstanceSecretsProvider _secrets;
    private readonly ILogger<DossierCaptureService> _log;

    public DossierCaptureService(SessionManager sessions, ProjectManager projects, TaskManager tasks,
        FileService files, Git.GitService git, DossierStore store, DossierCaptureState state,
        ICheapTextRunner cheap, FeatureFlagService flags, CodeGraph.CodeGraphService codeGraph,
        InstanceSecretsProvider secrets, ILogger<DossierCaptureService> log)
    {
        _sessions = sessions;
        _projects = projects;
        _tasks = tasks;
        _files = files;
        _git = git;
        _store = store;
        _state = state;
        _cheap = cheap;
        _flags = flags;
        _codeGraph = codeGraph;
        _secrets = secrets;
        _log = log;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage += OnSessionMessageAsync;
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage -= OnSessionMessageAsync;
        return base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAllAsync(ct); }
                catch (Exception ex) { _log.LogError(ex, "dossiers: ошибка фонового тика"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Внеочередная проверка по завершении хода — только по EffectiveRoot ЭТОЙ сессии,
    // не по корню проекта (worktree-чат мог коммитить только в своё дерево).
    private Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage || string.IsNullOrEmpty(session.ProjectId)) return Task.CompletedTask;
        var project = _projects.GetById(session.ProjectId);
        if (project is null || string.IsNullOrEmpty(project.OwnerId)) return Task.CompletedTask;
        if (!_flags.IsEnabled(project.OwnerId, FeatureFlagKeys.ChangeDossiers)) return Task.CompletedTask;

        var root = session.WorktreePath ?? project.RootPath;
        _ = Task.Run(() => TickRootSafeAsync(project, root));
        return Task.CompletedTask;
    }

    // Публичный для тестов: один проход по всем проектам с включённым флагом — корень
    // проекта плюс деревья живых worktree-чатов этого проекта.
    public async Task TickAllAsync(CancellationToken ct = default)
    {
        foreach (var project in _projects.GetAll())
        {
            if (string.IsNullOrEmpty(project.OwnerId)) continue;
            if (!_flags.IsEnabled(project.OwnerId, FeatureFlagKeys.ChangeDossiers)) continue;

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.RootPath };
            foreach (var s in _sessions.GetAll())
                if (s.ProjectId == project.Id && !string.IsNullOrEmpty(s.WorktreePath))
                    roots.Add(s.WorktreePath!);

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                await TickRootSafeAsync(project, root);
            }
        }
    }

    private async Task TickRootSafeAsync(Project project, string root)
    {
        try { await TickRootAsync(project, root); }
        catch (Exception ex) { _log.LogWarning(ex, "dossiers: тик по дереву {Root} проекта {Project}", root, project.Id); }
    }

    public async Task TickRootAsync(Project project, string root)
    {
        if (!Git.GitService.IsGitRepo(root)) return;
        var ownerId = project.OwnerId!;
        var key = DossierCaptureState.RootKey(ownerId, project.Id, root);

        var commits = _files.GetCommitsRaw(root, limit: CommitScanLimit);
        if (commits.Count == 0) return;
        var head = commits[0].Sha;
        var lastSeen = _state.Get(key);

        // Первое наблюдение дерева — только фиксируем HEAD, backfill истории не делаем
        if (lastSeen is null) { _state.Set(key, head); return; }
        if (lastSeen == head) return;

        var fresh = new List<GitCommitRaw>();
        foreach (var c in commits)
        {
            if (c.Sha == lastSeen) break;
            fresh.Add(c);
        }
        _state.Set(key, head);
        if (fresh.Count == 0) return;   // lastSeen вне окна CommitScanLimit — редкий случай, пропускаем

        fresh.Reverse();   // от старых к новым — важно для порядка переякорения при нескольких коммитах
        foreach (var c in fresh)
            await ProcessCommitAsync(project, root, ownerId, c);
    }

    private async Task ProcessCommitAsync(Project project, string root, string ownerId, GitCommitRaw commit)
    {
        var fullMessage = commit.Subject + "\n" + commit.Body;

        // §1: трейлер — не доверенный ввод. Формат + принадлежность, fail-closed.
        var sessionIdRaw = CommitTrailers.ExtractSessionId(fullMessage);
        if (sessionIdRaw is null || !TranscriptMigrator.IsSafeSessionId(sessionIdRaw)) return;

        var session = _sessions.GetById(sessionIdRaw);
        if (session is null) return;
        var sessionOwnerId = _sessions.ResolveOwnerId(session);
        // §1: трейлер — заявка, а не ключ. fail-closed: совпадение владельца и проекта обязательно.
        if (!SessionBelongsToProject(sessionOwnerId, session.ProjectId, ownerId, project.Id)) return;

        string? taskId = null;
        var taskIdRaw = CommitTrailers.ExtractTaskId(fullMessage);
        if (taskIdRaw is not null && TranscriptMigrator.IsSafeSessionId(taskIdRaw))
        {
            var task = _tasks.GetById(taskIdRaw);
            if (task is not null && task.ProjectId == project.Id) taskId = taskIdRaw;
        }

        // Идемпотентность (§4, §7): коммит уже представлен записью (сам или через supersededSha)
        if (_store.FindByAnyCommitSha(ownerId, project.Id, commit.Sha) is not null) return;

        // Переякорение squash (§7): ОБА условия — старый sha недостижим от HEAD этого дерева
        // И subject старого коммита есть в новом сообщении. Совпадение трейлера само по себе
        // ничего не значит — он не уникален per-коммит (тот же чат коммитит регулярно).
        var reanchored = false;
        foreach (var d in _store.List(ownerId, project.Id))
        {
            // Дешёвые гард-проверки до git-вызова: subject пуст или не упоминается — не переякориваем
            var subjectMatch = !string.IsNullOrEmpty(d.CommitSubject)
                && fullMessage.Contains(d.CommitSubject, StringComparison.Ordinal);
            if (!subjectMatch) continue;
            // §7: ОБА условия вместе (ShouldReanchor) — subject старого в новом И старый sha недостижим.
            if (!ShouldReanchor(subjectMatch, await IsReachableAsync(ownerId, root, d.CommitSha))) continue;

            d.SupersededSha.Add(d.CommitSha);
            d.CommitSha = commit.Sha;
            d.CommitSubject = commit.Subject;
            d.CommittedAt = commit.Date;
            var (rFiles, rSymbols) = await AnchorAsync(root, commit.Sha, ownerId);
            d.Files = rFiles;
            d.Symbols = rSymbols;
            _store.Reanchor(d);
            reanchored = true;
        }
        if (reanchored) return;

        await CaptureNewAsync(project, root, ownerId, session, taskId, commit);
    }

    // Недостижимость — оба сигнала дают один и тот же ExitCode != 0 (несуществующий объект
    // после gc тоже не «ancestor»). Ошибка вызова git (таймаут/сеть) → консервативно true
    // (достижим): ложный отказ от переякорения безопаснее ложного слияния двух паспортов.
    private async Task<bool> IsReachableAsync(string ownerId, string root, string sha)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["merge-base", "--is-ancestor", sha, "HEAD"]);
            return r.Ok;
        }
        catch { return true; }
    }

    private async Task CaptureNewAsync(Project project, string root, string ownerId, Session session,
        string? taskId, GitCommitRaw commit)
    {
        var exactSecrets = _secrets.GetExactSecrets();

        var diffStat = TakeHead(await GetDiffStatAsync(ownerId, root, commit.Sha), MaxDiffStatChars);
        var transcript = TakeTail(await BuildTranscriptAsync(session.Id), MaxTranscriptChars);
        var taskCard = TakeHead(BuildTaskCard(taskId is null ? null : _tasks.GetById(taskId)), MaxTaskCardChars);

        // Редакция ДО модели
        diffStat = SecretRedactor.Redact(diffStat, exactSecrets);
        transcript = SecretRedactor.Redact(transcript, exactSecrets);
        taskCard = SecretRedactor.Redact(taskCard, exactSecrets);

        var (files, symbols) = await AnchorAsync(root, commit.Sha, ownerId);

        var dossier = new ChangeDossier
        {
            OwnerId = ownerId,
            ProjectId = project.Id,
            CommitSha = commit.Sha,
            CommitSubject = commit.Subject,
            CommittedAt = commit.Date,
            SessionId = session.Id,
            TaskId = taskId,
            PersonaId = session.PersonaId,
            Files = files,
            Symbols = symbols,
        };

        try
        {
            var prompt = BuildSummaryPrompt(commit, diffStat, transcript, taskCard);
            var raw = await _cheap.RunAsync(LocalActionCatalog.DossierSummary, prompt, "haiku", ownerId, jsonFormat: "json");
            // Редакция ПОСЛЕ модели — она могла процитировать то, что просочилось мимо первого прохода
            var redacted = SecretRedactor.Redact(raw, exactSecrets);
            var parsed = ParseSummary(redacted) ?? throw new InvalidOperationException("пустой/невалидный JSON ответа");
            dossier.Why = parsed.Why;
            dossier.Decisions = parsed.Decisions;
            dossier.Rejected = parsed.Rejected;
            dossier.Pitfalls = parsed.Pitfalls;
            dossier.Invariants = parsed.Invariants;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dossiers: выжимка не удалась для {Sha} — сохраняю скелет", ShortSha(commit.Sha));
            dossier.SummaryFailed = true;
            dossier.Why = commit.Subject;
        }

        _store.Add(dossier);
    }

    private static string ShortSha(string sha) => sha.Length <= 7 ? sha : sha[..7];

    // §1 guard: трейлер принимается, только если сессия принадлежит тому же владельцу и проекту.
    // fail-closed — null-владелец/проект (глобальный/личный чат) или несовпадение => чужой чат,
    // паспорта нет. Чистый предикат — тестируется без тяжёлых зависимостей (общая папка двух
    // владельцев: у каждого свой ownerId, чужой трейлер здесь отбрасывается).
    internal static bool SessionBelongsToProject(string? sessionOwnerId, string? sessionProjectId,
        string ownerId, string projectId) =>
        sessionOwnerId == ownerId && sessionProjectId == projectId;

    // §7: переякорение при squash — ОБА условия вместе. subject старого коммита есть в новом
    // сообщении И старый sha недостижим от HEAD. Одной недостижимости мало (коммит в невлитой
    // ветке от HEAD текущего дерева недостижим, но жив и ничем не переписан), одного subject-матча
    // мало (git commit --amend без переписи предков, ручной повтор сообщения). subjectMatch уже
    // вычислен вызывающим (дёшево), oldReachable — результат git merge-base --is-ancestor.
    internal static bool ShouldReanchor(bool subjectMatch, bool oldReachable) => subjectMatch && !oldReachable;

    private async Task<string> GetDiffStatAsync(string ownerId, string root, string sha)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["show", "--stat", "--pretty=format:", sha]);
            return r.Ok ? r.Stdout.Trim() : "";
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: git show --stat {Sha}", sha); return ""; }
    }

    private async Task<string> BuildTranscriptAsync(string sessionId)
    {
        try
        {
            var history = await _sessions.GetHistoryAsync(sessionId);
            // Без внутреннего усечения BuildTranscript — своё урезание применяем ниже (TakeTail:
            // финальные ответы важнее, обрезка с начала, а не гибрид голова+хвост как у сводки)
            return SessionSummaryService.BuildTranscript(history, int.MaxValue);
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: транскрипт сессии {Session}", sessionId); return ""; }
    }

    private static string BuildTaskCard(TaskItem? task)
    {
        if (task is null) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(task.Title)) sb.AppendLine(task.Title);
        if (!string.IsNullOrWhiteSpace(task.Description)) sb.AppendLine(task.Description);
        if (!string.IsNullOrWhiteSpace(task.ResultMarkdown)) sb.AppendLine(task.ResultMarkdown);
        return sb.ToString();
    }

    // Якорение (§7 ADR-004): файлы — git show --name-only; символы — по СНИМКУ CodeGraph
    // (снимок, не пересборка — тик коммита не должен триггерить дорогой Roslyn-анализ).
    // Не-C# файлы / граф ещё не построен для дерева → символов нет, честный файловый уровень.
    private async Task<(List<string> Files, List<string> Symbols)> AnchorAsync(string root, string sha, string ownerId)
    {
        var files = new List<string>();
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["show", "--name-only", "--pretty=format:", sha]);
            if (r.Ok)
                files = [.. r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: git show --name-only {Sha}", sha); }

        var symbols = new List<string>();
        if (files.Count > 0)
        {
            try
            {
                var snap = await _codeGraph.GetSnapshotAsync(root, CancellationToken.None);
                if (snap is not null)
                {
                    var normFiles = new HashSet<string>(files.Select(f => f.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                    symbols = [.. snap.Nodes
                        .Where(n => normFiles.Contains(n.SourceFile.Replace('\\', '/')))
                        .Select(n => n.FullyQualifiedName)
                        .Distinct()];
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "dossiers: снимок CodeGraph {Root}", root); }
        }
        return (files, symbols);
    }

    private static string BuildSummaryPrompt(GitCommitRaw commit, string diffStat, string transcript, string taskCard)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты пишешь ПАСПОРТ ИЗМЕНЕНИЯ — короткую выжимку «зачем, что решили, что отвергли, какие " +
                      "грабли» для чтения вместе с кодом позже, без доступа к этому разговору. Материал ниже: " +
                      "сообщение коммита, статистика diff" + (string.IsNullOrWhiteSpace(taskCard) ? "" : ", карточка задачи") +
                      " и реплики рабочего хода.");
        sb.AppendLine();
        sb.AppendLine("Коммит: " + commit.Subject);
        if (!string.IsNullOrWhiteSpace(commit.Body)) sb.AppendLine(commit.Body);
        sb.AppendLine();
        sb.AppendLine("Статистика diff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(diffStat) ? "(нет данных)" : diffStat);
        if (!string.IsNullOrWhiteSpace(taskCard))
        {
            sb.AppendLine();
            sb.AppendLine("Карточка задачи:");
            sb.AppendLine(taskCard);
        }
        sb.AppendLine();
        sb.AppendLine("Ход разговора:");
        sb.AppendLine(string.IsNullOrWhiteSpace(transcript) ? "(нет данных)" : transcript);
        sb.AppendLine();
        sb.AppendLine("Ответь СТРОГО JSON вида " +
            "{\"why\":\"…\",\"decisions\":[\"…\"],\"rejected\":[\"…\"],\"pitfalls\":[\"…\"],\"invariants\":[\"…\"]}. " +
            "Каждый пункт — не длиннее 200 символов, по-русски. Пустые списки — валидный ответ: НЕ ВЫДУМЫВАЙ " +
            "отказы, грабли или инварианты, которых нет в материале выше. why — коротко, зачем сделано изменение.");
        return sb.ToString();
    }

    private sealed record SummaryDto(string? Why, List<string>? Decisions, List<string>? Rejected,
        List<string>? Pitfalls, List<string>? Invariants);

    internal sealed record ParsedSummary(string Why, List<string> Decisions, List<string> Rejected,
        List<string> Pitfalls, List<string> Invariants);

    // Публичный для тестов контракта JSON-ответа
    internal static ParsedSummary? ParseSummary(string raw)
    {
        var json = Memory.MemoryLlmParsing.ExtractBalanced(raw, '{', '}');
        if (json is null) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<SummaryDto>(json, JsonOpts);
            if (parsed is null) return null;
            return new ParsedSummary(Cap(parsed.Why), CapList(parsed.Decisions), CapList(parsed.Rejected),
                CapList(parsed.Pitfalls), CapList(parsed.Invariants));
        }
        catch (JsonException) { return null; }
    }

    private static string Cap(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        return t.Length <= MaxSummaryItemChars ? t : t[..MaxSummaryItemChars].Trim();
    }

    private static List<string> CapList(List<string>? items) =>
        items is null ? [] : [.. items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Cap)];

    private static string TakeHead(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    private static string TakeTail(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[^max..];
}
