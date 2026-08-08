using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Фильтр захвата (§объём/стоимость): порог «короткое сообщение» для однофайловых правок,
    // где выжимать нечего. Список пропускаемых conventional-типов — из конфига (Dossiers:SkipCommitTypes).
    private const int SkipSingleFileMessageChars = 100;
    private static readonly string[] DefaultSkipCommitTypes = ["style", "chore", "build", "ci"];

    // Окно реплик по времени коммита (блокер «зачем про чужое дело»): сессия живёт сутками и
    // ведёт несколько дел подряд — «последние N реплик» подобрали бы соседнее. Окно вокруг
    // CommittedAt + отсечка по паузе (тишина > gap = граница дела).
    private static readonly CommitWindowOptions WindowOpts = new(
        BeforeMs: 2 * 60 * 60 * 1000L,   // потолок взгляда назад (длинный ход)
        AfterMs: 15 * 60 * 1000L,        // хвост финальных реплик того же хода после коммита
        GapMs: 30 * 60 * 1000L);         // пауза, разделяющая дела

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly TaskManager _tasks;
    private readonly FileService _files;
    private readonly Git.GitService _git;
    private readonly DossierStore _store;
    private readonly DossierCaptureState _state;
    private readonly ICheapTextRunner _cheap;
    private readonly CodeGraph.CodeGraphService _codeGraph;
    private readonly InstanceSecretsProvider _secrets;
    private readonly HashSet<string> _skipTypes;
    private readonly ILogger<DossierCaptureService> _log;

    public DossierCaptureService(SessionManager sessions, ProjectManager projects, TaskManager tasks,
        FileService files, Git.GitService git, DossierStore store, DossierCaptureState state,
        ICheapTextRunner cheap, CodeGraph.CodeGraphService codeGraph,
        InstanceSecretsProvider secrets, IConfiguration config, ILogger<DossierCaptureService> log)
    {
        _sessions = sessions;
        _projects = projects;
        _tasks = tasks;
        _files = files;
        _git = git;
        _store = store;
        _state = state;
        _cheap = cheap;
        _codeGraph = codeGraph;
        _secrets = secrets;
        _log = log;
        // Dossiers:SkipCommitTypes — массив conventional-типов, чьи паспорта не снимаются
        // (выжимать нечего). Меняется без пересборки; дефолт — служебные типы обслуживания.
        var configured = config.GetSection("Dossiers:SkipCommitTypes").Get<string[]>();
        _skipTypes = (configured is { Length: > 0 } ? configured : DefaultSkipCommitTypes)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
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
        // §1 guard + §6 opt-out: трейлер — заявка; fail-closed по принадлежности, и чат мог явно
        // попросить «не сохранять решения» (ExcludeFromDossiers) — паспорт тогда молча не создаётся.
        var belongs = SessionBelongsToProject(sessionOwnerId, session.ProjectId, ownerId, project.Id);
        if (!ShouldCaptureSession(belongs, session.ExcludeFromDossiers)) return;

        string? taskId = null;
        var taskIdRaw = CommitTrailers.ExtractTaskId(fullMessage);
        if (taskIdRaw is not null && TranscriptMigrator.IsSafeSessionId(taskIdRaw))
        {
            var task = _tasks.GetById(taskIdRaw);
            if (task is not null && task.ProjectId == project.Id) taskId = taskIdRaw;
        }

        // Идемпотентность (§4, §7): коммит уже представлен записью (сам или через supersededSha)
        if (_store.FindByAnyCommitSha(ownerId, project.Id, commit.Sha) is not null) return;

        // Переякорение squash (§7): среди прежних досье ищем одно, чей subject присутствует в новом
        // сообщении ОТДЕЛЬНОЙ строкой (не произвольной подстрокой — иначе «fix bug» ложно матчит
        // «fix bug in x/y»), И чей sha недостижим от HEAD этого дерева. Переякоряется РОВНО ОДНО —
        // с самым длинным (специфичным) subject: несколько похожих subject'ов при squash не должны
        // дать несколько записей на одном commitSha (инвариант ADR-004 §4 «один коммит — один паспорт»).
        // Совпадение трейлера само по себе ничего не значит — он не уникален per-коммит.
        var existing = _store.List(ownerId, project.Id);
        var bestSubject = PickReanchorSubject(existing.Select(d => d.CommitSubject).ToList(), fullMessage);
        if (bestSubject is not null)
        {
            ChangeDossier? target = null;
            foreach (var d in existing)
            {
                if (!string.Equals(d.CommitSubject, bestSubject, StringComparison.Ordinal)) continue;
                // §7: ОБА условия вместе — subject (уже сматчен в PickReanchorSubject) И sha недостижим.
                if (!ShouldReanchor(subjectMatch: true, await IsReachableAsync(ownerId, root, d.CommitSha))) continue;
                target = d;
                break;   // переякоряем первое подходящее досье с самым специфичным subject
            }
            if (target is not null)
            {
                target.SupersededSha.Add(target.CommitSha);
                target.CommitSha = commit.Sha;
                target.CommitSubject = commit.Subject;
                target.CommittedAt = commit.Date;
                var rFiles = await GetChangedFilesAsync(ownerId, root, commit.Sha);
                target.Files = rFiles;
                target.Symbols = await AnchorSymbolsAsync(root, rFiles, ownerId);
                _store.Reanchor(target);
                return;   // переякорение поглотило коммит — новый паспорт не создаём
            }
        }

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
        var files = await GetChangedFilesAsync(ownerId, root, commit.Sha);
        var parentCount = await GetParentCountAsync(ownerId, root, commit.Sha);

        // Фильтр захвата (объём/стоимость): merge-коммиты (2+ родителя), служебные типы и
        // однофайловые правки с коротким сообщением пропускаем молчаливо — выжимать там нечего.
        // Применяется ТОЛЬКО на новом захвате: переякорение при squash (выше) фильтру не
        // подчиняется — перепись истории объединяет в теле и feat, и chore, и терять якорь
        // паспорта из-за типа/merge нельзя.
        if (ShouldSkipCommit(commit.Subject, commit.Body, files.Count, _skipTypes,
            SkipSingleFileMessageChars, parentCount))
            return;

        var exactSecrets = _secrets.GetExactSecrets();

        var diffStat = TakeHead(await GetDiffStatAsync(ownerId, root, commit.Sha), MaxDiffStatChars);
        var transcript = TakeTail(await BuildTranscriptAsync(session.Id, commit.Date), MaxTranscriptChars);
        var taskCard = TakeHead(BuildTaskCard(taskId is null ? null : _tasks.GetById(taskId)), MaxTaskCardChars);

        // Редакция ДО модели
        diffStat = SecretRedactor.Redact(diffStat, exactSecrets);
        transcript = SecretRedactor.Redact(transcript, exactSecrets);
        taskCard = SecretRedactor.Redact(taskCard, exactSecrets);

        var symbols = await AnchorSymbolsAsync(root, files, ownerId);

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

    // §6 opt-out + §1 guard (композит): паспорт создаётся, только если сессия принадлежит проекту
    // этого владельца И чат не помечен «не сохранять решения» (ExcludeFromDossiers, ADR-004 §6).
    // Тестируется без тяжёлых зависимостей — как и SessionBelongsToProject.
    internal static bool ShouldCaptureSession(bool belongsToProject, bool optedOut) =>
        belongsToProject && !optedOut;

    // §7: переякорение при squash — ОБА условия вместе. subject старого коммита есть в новом
    // сообщении И старый sha недостижим от HEAD. Одной недостижимости мало (коммит в невлитой
    // ветке от HEAD текущего дерева недостижим, но жив и ничем не переписан), одного subject-матча
    // мало (git commit --amend без переписи предков, ручной повтор сообщения). subjectMatch уже
    // вычислен вызывающим (дёшево), oldReachable — результат git merge-base --is-ancestor.
    internal static bool ShouldReanchor(bool subjectMatch, bool oldReachable) => subjectMatch && !oldReachable;

    // §7: среди прежних subject'ов выбираем один для переякорения — присутствующий в новом
    // сообщении ОТДЕЛЬНОЙ строкой (squash выкладывает заголовки исходных коммитов строками), а не
    // произвольной подстрокой (иначе «fix bug» ложно матчит «fix bug in x/y»), и самый длинный
    // (специфичный). null — ни один не матчится. Reachable-фильтр остаётся на вызывающем: здесь
    // только чистая текстовая логика выбора, тестируемая без git/стора.
    internal static string? PickReanchorSubject(IReadOnlyList<string> oldSubjects, string fullMessage)
    {
        var lines = new HashSet<string>(
            fullMessage.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0),
            StringComparer.Ordinal);
        string? best = null;
        foreach (var s in oldSubjects)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!lines.Contains(s.Trim())) continue;
            if (best is null || s.Length > best.Length) best = s;
        }
        return best;
    }

    private async Task<string> GetDiffStatAsync(string ownerId, string root, string sha)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["show", "--stat", "--pretty=format:", sha]);
            return r.Ok ? r.Stdout.Trim() : "";
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: git show --stat {Sha}", sha); return ""; }
    }

    // Реплики хода отбираются по времени коммита, а не «последние N»: сессия живёт сутками и
    // ведёт несколько дел подряд, и хвост подобрал бы соседнее (блокер «зачем про чужое дело»).
    // Окно вокруг CommittedAt + отсечка по паузе; не привязалось по времени → пусто (промпт
    // обойдётся коммитом и diff — это безопаснее «всего чата»). Усечение — своё, ниже (TakeTail).
    private async Task<string> BuildTranscriptAsync(string sessionId, DateTimeOffset commitTime)
    {
        try
        {
            var history = await _sessions.GetHistoryAsync(sessionId);
            var window = SelectCommitWindow(history, commitTime.ToUnixTimeMilliseconds(), WindowOpts);
            if (window.Count == 0) return "";
            return SessionSummaryService.BuildTranscript(window, int.MaxValue);
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: транскрипт сессии {Session}", sessionId); return ""; }
    }

    // Параметры окна реплик вокруг коммита. Before — потолок взгляда назад (типичный длинный ход);
    // After — короткий хвост финальных реплик того же хода после коммита; Gap — пауза, через
    // которую назад не переходим (тишина дольше gap = граница между делами).
    internal sealed record CommitWindowOptions(long BeforeMs, long AfterMs, long GapMs);

    private static long? TimestampOf(StoredMessage m) => m switch
    {
        StoredUserMessage u => u.Timestamp,
        StoredTextMessage t => t.Timestamp,
        _ => null,
    };

    // Чистая логика выбора кластера реплик «одного дела» вокруг коммита. Опорные точки —
    // сообщения с Timestamp (только у user/text оно есть); из них строим окно [commit-before,
    // commit+after] и расширяем НАЗАД через малые паузы (≤ gap = та же работа), вперёд не
    // расширяем (хвост после коммита ограничен after, чтобы не затянуть следующее дело).
    // Возвращаем все сообщения между границами кластера — включая без Timestamp (tool_use,
    // file_changed): они контекст между репликами и важны для выжимки.
    internal static IReadOnlyList<StoredMessage> SelectCommitWindow(
        IReadOnlyList<StoredMessage> messages, long commitUnixMs, CommitWindowOptions opts)
    {
        var anchors = new List<(int Idx, long Ts)>();
        for (int i = 0; i < messages.Count; i++)
        {
            var ts = TimestampOf(messages[i]);
            if (ts.HasValue) anchors.Add((i, ts.Value));
        }
        if (anchors.Count == 0) return [];

        var lo = commitUnixMs - opts.BeforeMs;
        var hi = commitUnixMs + opts.AfterMs;
        var byTime = anchors.OrderBy(a => a.Ts).ToList();

        // Опорные точки в окне коммита (упорядочены по времени — byTime.Where сохраняет порядок).
        var window = byTime.Where(a => a.Ts >= lo && a.Ts <= hi).ToList();
        if (window.Count == 0) return [];

        // Расширение назад от самой ранней опоры окна: пока предыдущая опора отстоит ≤ gap.
        var include = new HashSet<int>(window.Select(a => a.Idx));
        int k = byTime.FindIndex(a => a.Idx == window[0].Idx);
        while (k > 0)
        {
            var cur = byTime[k];
            var prev = byTime[k - 1];
            if (cur.Ts - prev.Ts > opts.GapMs) break;
            include.Add(prev.Idx);
            k--;
        }

        var ordered = include.Order().ToArray();
        int minIdx = ordered[0];
        int maxIdx = ordered[^1];
        var result = new List<StoredMessage>(maxIdx - minIdx + 1);
        for (int i = minIdx; i <= maxIdx; i++) result.Add(messages[i]);
        return result;
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

    // Якорение (§3, §7 ADR-004). Файлы — git show --name-only (нужно и фильтру захвата, и якорю,
    // поэтому получаются отдельно и раньше выжимки). Символы — по СНИМКУ CodeGraph (снимок, не
    // пересборка): тик коммита не должен триггерить дорогой Roslyn-анализ. Не-C# файлы / граф
    // ещё не построен для дерева → символов нет, честный файловый уровень (см. ADR-004 §7 —
    // известное ограничение v1). Форматы путей совместимы: и git, и граф хранят относительные
    // пути от rootPath через '/', поэтому матчится напрямую.
    private async Task<List<string>> GetChangedFilesAsync(string ownerId, string root, string sha)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["show", "--name-only", "--pretty=format:", sha]);
            if (r.Ok)
                return [.. r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: git show --name-only {Sha}", sha); }
        return [];
    }

    // Число родителей коммита — надёжный признак merge (2+), в отличие от текста subject:
    // стандартные автомержи «Merge branch …» conventional-типом не разбираются и фильтром по
    // типу не ловятся. %P — родительские sha через пробел (0 = корневой, 1 = обычный, 2+ = merge).
    // Ошибка вызова → консервативно 1 (обычный): ложный паспорт на merge безопаснее потери коммита.
    private async Task<int> GetParentCountAsync(string ownerId, string root, string sha)
    {
        try
        {
            var r = await _git.RunAsync(ownerId, root, ["show", "-s", "--format=%P", sha]);
            if (!r.Ok) return 1;
            return r.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch { return 1; }
    }

    private async Task<List<string>> AnchorSymbolsAsync(string root, List<string> files, string ownerId)
    {
        if (files.Count == 0) return [];
        try
        {
            var snap = await _codeGraph.GetSnapshotAsync(root, CancellationToken.None);
            if (snap is null)
            {
                // Граф для этого дерева ещё не построен (строится лениво по запросу к API). Лучший
                // ход — не задерживать захват, а фоном запросить построение: следующие коммиты
                // этого дерева получат символы. Дедуп по idle (StartRebuildIfIdle), неблокирующе.
                _codeGraph.StartRebuildIfIdle(root);
                return [];
            }
            var normFiles = new HashSet<string>(files.Select(f => f.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            return [.. snap.Nodes
                .Where(n => normFiles.Contains(n.SourceFile.Replace('\\', '/')))
                .Select(n => n.FullyQualifiedName)
                .Distinct()];
        }
        catch (Exception ex) { _log.LogDebug(ex, "dossiers: снимок CodeGraph {Root}", root); return []; }
    }

    // Фильтр захвата (объём/стоимость, ADR-004 §2-§3). Пропускает merge-коммиты (2+ родителя —
    // признак из git, не из текста), коммиты служебных conventional-типов (style/chore/build/ci —
    // список из Dossiers:SkipCommitTypes; merge сюда НЕ добавляем — иначе обычный коммит с таким
    // префиксом молча потеряется) и однофайловые правки с коротким сообщением. Чистая логика —
    // тестируется без git/конфига. parentCount по умолчанию 1 (обычный коммит).
    internal static bool ShouldSkipCommit(string subject, string body, int filesCount,
        IReadOnlySet<string> skipTypes, int minMessageChars, int parentCount = 1)
    {
        if (parentCount >= 2) return true;
        var type = ParseConventionalType(subject);
        if (type is not null && skipTypes.Contains(type)) return true;
        if (filesCount == 1 && MessageContentLength(subject, body) < minMessageChars) return true;
        return false;
    }

    // Conventional-тип из subject: «feat(scope): …», «feat!: …», «feat: …» → «feat». null, если
    // subject не следует конвенции (merge, свободный текст, тип с пробелом).
    internal static string? ParseConventionalType(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        int end = -1;
        foreach (var ch in subject)
        {
            if (ch is '(' or '!' or ':') { end = subject.IndexOf(ch); break; }
        }
        if (end <= 0) return null;
        var type = subject[..end].Trim().ToLowerInvariant();
        if (type.Length == 0) return null;
        foreach (var ch in type) if (char.IsWhiteSpace(ch)) return null;   // тип без пробелов
        return type;
    }

    // Служебные трейлеры коммита — не содержательный текст (CCS-Session/Task, соавторство), их
    // длина не должна спасать короткое сообщение от фильтра. Считаем длину subject+body без них.
    private static readonly Regex TrailerLineRx = new(
        @"(?im)^\s*(CCS-Session|CCS-Task|Co-Authored-By|Co-authored-by):.*$",
        RegexOptions.Compiled);

    internal static int MessageContentLength(string subject, string body) =>
        TrailerLineRx.Replace(subject + "\n" + (body ?? ""), "").Trim().Length;

    private static string BuildSummaryPrompt(GitCommitRaw commit, string diffStat, string transcript, string taskCard)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты пишешь ПАСПОРТ ИЗМЕНЕНИЯ — короткую выжимку «зачем, что решили, что отвергли, какие " +
                      "грабли» для чтения вместе с кодом позже, без доступа к этому разговору. Материал ниже: " +
                      "сообщение коммита, статистика diff" + (string.IsNullOrWhiteSpace(taskCard) ? "" : ", карточка задачи") +
                      " и реплики рабочего хода. Главный якорь — КОММИТ И DIFF: выжимка обязана объяснять " +
                      "именно эти изменения в этих файлах. В одном чате могло вестись несколько дел — если " +
                      "реплики хода говорят про другое, игнорируй их и опирайся на сообщение коммита и diff.");
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
            "отказы, грабли или инварианты, которых нет в материале выше (большинство коммитов — исполнение по " +
            "плану, и пустые rejected/pitfalls там нормальны).");
        sb.AppendLine();
        sb.AppendLine("Значения полей:");
        sb.AppendLine("why — коротко, ЗАЧЕМ сделано изменение; обязано объяснять именно этот коммит (файлы и " +
                      "diff выше), а не соседнее дело из разговора.");
        sb.AppendLine("pitfalls (грабли) — на что РЕАЛЬНО наступили в процессе: сломалось, не завелось, вело " +
                      "себя неожиданно, пришлось обходить. НЕ инструкции и ограничения из задачи («не добавлять " +
                      "X», «не забыть сделать Y») — это указания постановщика, а не грабли.");
        sb.AppendLine("invariants (инварианты) — правила системы, которые нельзя нарушать впредь и которые " +
                      "прояснило это изменение. НЕ результаты прогона тестов («46/46 проходят»), НЕ метрики сборки.");
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
