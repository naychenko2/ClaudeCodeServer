using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services.Dossiers;

// Запрос пассивного recall паспортов (этап 2, ADR-004 §5): якоря, известные на старте хода
// (linkedFiles задачи + пути из текста хода + файлы предыдущего хода сессии), плюс сам текст.
// RootPath — EffectiveRoot чата (WorktreePath ?? project.RootPath): у worktree-чата своё дерево.
// TaskId — задача чата (у чата-исполнителя): её LinkedFiles тоже якорь (сервис сам проверит
// принадлежность задаче этого проекта, по образцу guard'а захвата).
public sealed record DossierRecallRequest(
    string ProjectId,
    string? RootPath,
    string? TaskId,
    IReadOnlyList<string> AnchorFiles,
    string TurnText);

// Результат recall'а: markdown-кусок для блока PersonaMemoryService.BuildRecallAsync + записи,
// реально попавшие в блок (для манифеста атрибуции F3). Text=null — подмешивать нечего.
public sealed record DossierRecallResult(string? Text, IReadOnlyList<ChangeDossier> Used);

// Recall паспортов изменений (ADR-004 §5, этап 2). Пассивный канал: до 3 паспортов, каждый
// ≤300 символов, суммарно ≤1 КБ; ранжирование score = семантика × свежесть × статус.
//
// Статусы — лениво, при recall, с кешем на версию снимка графа/HEAD: пока HEAD дерева и
// сигнатура графа не сменились, статусы берутся из кеша и git log не зовётся вовсе (один
// rev-parse HEAD на recall — локальный и дешёвый, «пачки git log» он не образует). При
// промахе кеша ход НЕ тормозится: записи отдаются с сохранённым статусом, а пересчёт по
// кандидатам запускается фоном и наполнит кеш к следующему ходу. Удаления паспортов не
// происходит никогда (решение владельца) — archived лишь выкидывает запись из пассивного
// канала, найти её можно через dossier_lookup.
//
// Не sealed: юнит-тесты подменяют git-вызовы и снимок CodeGraph защищёнными виртуальными
// методами (счётчики вызовов — тест кеша статусов).
public class DossierRecallService(
    DossierStore store,
    TaskManager? tasks = null,
    Git.GitService? git = null,
    CodeGraphService? codeGraph = null,
    ILogger<DossierRecallService>? log = null)
{
    // Бюджет пассивного канала (ADR-004 §5): блок паспортов не должен весить больше
    // остальной памяти вместе взятой (личная память — единицы записей по 240 символов).
    public const int MaxDossiers = 3;
    public const int EntryCharLimit = 300;
    public const int BlockCharLimit = 1024;

    // Сколько кандидатов ранжирования попадает в ленивый пересчёт статусов: пересчёт платит
    // git-вызовами (один git log --numstat на паспорт), поэтому считается только то, что
    // реально имеет шанс попасть в промпт, — не весь стор (до 5000 записей).
    private const int StatusCandidateWindow = 20;

    // Полураспад свежести: за 60 дней вес паспорта падает вдвое. Мягкий спад — паспорт
    // полугодовой давности всё ещё ценен (в истории решений старое ≠ бесполезное).
    private const double FreshnessHalfLifeDays = 60.0;

    // Active (ADR-004 §5): файл после паспорта менялся мало — ≤3 коммитов ИЛИ ≤30% строк.
    internal const int ActiveMaxCommits = 3;
    internal const int ActiveMaxChangedPercent = 30;

    private static readonly Regex PathRx = new(
        @"[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+)+\.[A-Za-z0-9]{1,10}",
        RegexOptions.Compiled);

    // Кеш статусов: owner:project → статусы на версию (HEAD, сигнатура графа). Guard — Lock.
    private sealed class StatusCache
    {
        public string HeadSha = "";
        public string GraphSig = "";
        public Dictionary<string, DossierStatus> ById = new();
    }

    private readonly ConcurrentDictionary<string, StatusCache> _statusCache = new();
    private readonly Lock _cacheLock = new();
    // Фоновые пересчёты: не плодим параллельные по одному проекту (inflight-гейт)
    private readonly HashSet<string> _refreshInflight = [];

    // --- Пассивный канал ---

    public async Task<DossierRecallResult> BuildRecallBlockAsync(string ownerId, DossierRecallRequest req)
    {
        // Изоляция per-owner/per-project — сам стор ключуется парой, чужие записи сюда не попадают
        var snapshot = store.List(ownerId, req.ProjectId).Where(d => !d.SummaryFailed).ToList();
        if (snapshot.Count == 0) return new DossierRecallResult(null, []);

        var anchors = NormalizeAnchors([.. req.AnchorFiles, .. TaskAnchorFiles(req)]);
        var trimmedText = (req.TurnText ?? "").Trim();
        if (anchors.Count == 0 && trimmedText.Length == 0) return new DossierRecallResult(null, []);

        // Семантика: полнотекст по тексту хода (токены паспорта — выжимка + якоря). Dify-retrieve
        // сюда сознательно не заводится: recall идёт на КАЖДОМ ходу персоны, и сетевой вызов
        // поверх уже существующих retrieve памяти и команды удвоил бы стоимость хода. Якорь
        // (файл, который персона правит) — главный сигнал, он закрывает основной сценарий
        // «правлю файл — вижу, зачем его меняли»; векторный поиск — точка роста, если полнотекста
        // не хватит.
        var relevance = MemoryFulltext.Relevance(snapshot, trimmedText,
            d => d.Id, BuildHay, d => [.. d.Files, .. d.Symbols]);

        // Валидный кеш статусов: те же HEAD дерева и сигнатура снимка графа. Один rev-parse
        // на recall; при промахе ниже ход деградирует (сохранённые статусы), без git log.
        var head = await ResolveHeadSafeAsync(ownerId, req.RootPath);
        var graphSig = req.RootPath is null ? "" : GraphSigOf(req.RootPath);
        StatusCache? cache = null;
        if (head is not null)
            lock (_cacheLock)
                cache = _statusCache.TryGetValue(CacheKey(ownerId, req.ProjectId), out var c)
                    && c.HeadSha == head && c.GraphSig == graphSig ? c : null;

        var now = DateTimeOffset.UtcNow;
        var ranked = snapshot
            .Select(d =>
            {
                var st = cache?.ById.GetValueOrDefault(d.Id) ?? d.Status;
                return (d, score: Score(relevance.GetValueOrDefault(d.Id, 0.0), AnchorHit(d, anchors), st, d.CommittedAt, now));
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        // Промах кеша — пересчёт фоном по кандидатам (не по всему стору), кеш наполнится к
        // следующему ходу; текущий ход уже отработан на сохранённых статусах выше
        if (cache is null && head is not null && ranked.Count > 0)
            ScheduleBackgroundRefresh(ownerId, req.ProjectId, req.RootPath!,
                ranked.Take(StatusCandidateWindow).Select(x => x.d).ToList());

        var formatted = FormatBlock(ranked.Take(MaxDossiers).Select(x => x.d).ToList());
        return new DossierRecallResult(formatted.Text, formatted.Included);
    }

    // «Сено» для полнотекстового скоринга: выжимка + subject + якоря паспорта
    private static string BuildHay(ChangeDossier d) =>
        d.Why + " " + d.CommitSubject + " " + string.Join(' ', d.Decisions) + " "
        + string.Join(' ', d.Rejected) + " " + string.Join(' ', d.Pitfalls) + " " + string.Join(' ', d.Invariants);

    // Скоринг ADR-004 §5: семантика × свежесть × статус. Совпадение якоря (файл хода ∈ файлы
    // паспорта) даёт семантике максимум — правка файла и есть главный сценарий канала.
    // Чистая функция — тестируется без стора.
    internal static double Score(double textRelevance, bool anchorHit, DossierStatus status,
        DateTimeOffset committedAt, DateTimeOffset nowUtc)
    {
        var semantic = anchorHit ? 1.0 : textRelevance;
        var ageDays = Math.Max(0, (nowUtc - committedAt).TotalDays);
        var freshness = Math.Pow(0.5, ageDays / FreshnessHalfLifeDays);
        var multiplier = status switch
        {
            DossierStatus.Active => 1.0,
            DossierStatus.Degraded => 0.5,
            DossierStatus.Archived => 0.0,   // в пассивный канал не попадает вовсе
            _ => 1.0,
        };
        return semantic * freshness * multiplier;
    }

    private static bool AnchorHit(ChangeDossier d, IReadOnlySet<string> anchors) =>
        anchors.Count > 0 && d.Files.Any(anchors.Contains);

    // Якоря-файлы задачи чата (LinkedFiles): только задача ЭТОГО проекта (guard по образцу
    // захвата — чужая/удалённая задача якорей не даёт). TaskManager'а в тестах может не быть.
    private IReadOnlyList<string> TaskAnchorFiles(DossierRecallRequest req)
    {
        if (tasks is null || string.IsNullOrEmpty(req.TaskId)) return [];
        var task = tasks.GetById(req.TaskId);
        return task is not null && task.ProjectId == req.ProjectId ? task.LinkedFiles : [];
    }

    // --- Форматирование блока (бюджет 3 × 300 символов, суммарно ≤1 КБ) ---

    internal sealed record FormattedBlock(string? Text, IReadOnlyList<ChangeDossier> Included);

    internal static FormattedBlock FormatBlock(IReadOnlyList<ChangeDossier> top)
    {
        var content = top.Where(HasContent).ToList();
        var entries = content.Select(d => FormatEntry(d)).ToList();
        // Суммарный бюджет: не влезает — роняем записи с конца (худшие по ранжированию)
        while (entries.Count > 0 && Header.Length + entries.Sum(e => e.Length + 1) > BlockCharLimit)
            entries.RemoveAt(entries.Count - 1);
        if (entries.Count == 0) return new FormattedBlock(null, []);
        return new FormattedBlock(Header + "\n" + string.Join("\n", entries),
            content.Take(entries.Count).ToList());
    }

    private const string Header =
        "По коду из этого хода уже есть история решений (зачем меняли и что при этом отвергли; " +
        "подробности — mcp__memory__dossier_get(id)):";

    // Запись без содержания (нет ни пунктов, ни «зачем») в блок не идёт — пустая строка со
    // ссылкой бесполезна; сам паспорт остаётся доступен через dossier_lookup.
    private static bool HasContent(ChangeDossier d) =>
        d.Rejected.Count > 0 || d.Pitfalls.Count > 0 || d.Decisions.Count > 0 || !string.IsNullOrWhiteSpace(d.Why);

    // Одна запись ≤300 символов. Порядок ценности (замер прода: «что отвергли» и «грабли» —
    // уникальная ценность паспорта, в коммите их нет никогда): пункты rejected → pitfalls →
    // decisions (до двух), при усечении РЕЖЕТСЯ «зачем» первым, пункт 2 — затем, пункт 1 —
    // последним (усекается, но не выпадает).
    internal static string FormatEntry(ChangeDossier d, DossierStatus? status = null)
    {
        var degraded = (status ?? d.Status) == DossierStatus.Degraded;
        var degradedMark = degraded ? " [код с тех пор менялся]" : "";

        var prefix = $"- {ShortSha(d.CommitSha)}: ";
        var suffix = degradedMark + $" Подробнее: mcp__memory__dossier_get({d.Id})";
        var budget = EntryCharLimit - prefix.Length - suffix.Length;

        var (p1, p2) = PickPoints(d);
        var why = string.IsNullOrWhiteSpace(d.Why) ? "" : d.Why.Trim().Replace('\n', ' ');

        string Core(string? a, string? b, string w)
        {
            var points = new[] { a, b }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            var s = string.Join("; ", points);
            if (w.Length > 0) s += (s.Length > 0 ? "." : "") + " Зачем: " + w;
            return s;
        }

        // 1. Полная сборка
        if (Core(p1, p2, why).Length <= budget) return prefix + Core(p1, p2, why) + suffix;

        // 2. Режем «зачем» — первым: персоне важнее «это уже пробовали и отвергли», чем пересказ
        //    сообщения коммита (его она и так прочитает в git log)
        if (why.Length > 0)
        {
            var withoutWhy = Core(p1, p2, "");
            var room = budget - withoutWhy.Length - ". Зачем: ".Length;
            var whyCut = room >= 20 ? CutWord(why, room) : "";
            var v = Core(p1, p2, whyCut);
            if (v.Length <= budget) return prefix + v + suffix;
            why = "";
        }

        // 3. Совсем без «зачем»
        if (Core(p1, p2, "").Length <= budget) return prefix + Core(p1, p2, "") + suffix;

        // 4. Режем пункт 2 (отказ/грабли держим до последнего из пунктов)
        if (p2 is not null)
        {
            var room = budget - Core(p1, null, "").Length - "; ".Length;
            p2 = room >= 20 ? CutWord(p2, room) : null;
            if (Core(p1, p2, "").Length <= budget) return prefix + Core(p1, p2, "") + suffix;
            p2 = null;
        }

        // 5. Только пункт 1, урезанный по границе слова (не выпадает никогда)
        return prefix + CutWord(p1 ?? "", Math.Max(budget, 10)) + suffix;
    }

    // Пункты записи: до двух, в порядке ценности rejected → pitfalls → decisions
    private static (string? P1, string? P2) PickPoints(ChangeDossier d)
    {
        var pool = new List<string>();
        pool.AddRange(d.Rejected.Select(s => "отвергли: " + s));
        pool.AddRange(d.Pitfalls.Select(s => "грабли: " + s));
        pool.AddRange(d.Decisions.Select(s => "решили: " + s));
        return (pool.Count > 0 ? pool[0] : null, pool.Count > 1 ? pool[1] : null);
    }

    // Усечь по границе слова до ≤max символов (многоточие занимает последний символ)
    private static string CutWord(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max <= 1) return s[..max];
        var cut = s[..(max - 1)];
        var sp = cut.LastIndexOf(' ');
        if (sp > max / 2) cut = cut[..sp];
        return cut.TrimEnd() + "…";
    }

    private static string ShortSha(string sha) => sha.Length <= 7 ? sha : sha[..7];

    // --- Якоря хода ---

    // Нормализация якорей к формату паспорта/git: прямые слэши, lowercase (SessionChangedPaths.Normalize)
    internal static IReadOnlySet<string> NormalizeAnchors(IReadOnlyList<string> files)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var t = f?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            set.Add(SessionChangedPaths.Normalize(t));
        }
        return set;
    }

    // Пути файлов из текста хода («правлю backend/Services/Foo.cs», «…в api.ts:123»): токен
    // с ≥1 разделителем пути и расширением. Лишние совпадения не страшны — якорь лишь сигнал
    // ранжирования, точный матч делает OrdinalIgnoreCase-сравнение с якорями паспорта.
    internal static IReadOnlyList<string> ExtractPathsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        foreach (Match m in PathRx.Matches(text))
        {
            var p = m.Value.Replace('\\', '/');
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            result.Add(p);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // --- Статусы (ленивый пересчёт, ADR-004 §5) ---

    // Пересчёт статусов кандидатов (публичный — им же пользуется фоновый запуск и тесты).
    // Один git log --numstat на паспорт; снимок графа грузится один раз на партию.
    public async Task RefreshStatusesAsync(string ownerId, string projectId, string root,
        IReadOnlyList<ChangeDossier> candidates)
    {
        if (candidates.Count == 0) return;
        var head = await ResolveHeadSafeAsync(ownerId, root);
        if (head is null) return;

        var fqns = await LoadAliveFqnsSafeAsync(root);

        var changed = false;
        var statuses = new Dictionary<string, DossierStatus>();
        foreach (var d in candidates)
        {
            var st = await ClassifyAsync(ownerId, root, d, fqns);
            statuses[d.Id] = st ?? d.Status;
            if (st is { } s && d.Status != s) { d.Status = s; changed = true; }
        }
        if (changed) store.PersistStatuses(ownerId, projectId);

        lock (_cacheLock)
        {
            _statusCache[CacheKey(ownerId, projectId)] = new StatusCache
            {
                HeadSha = head,
                GraphSig = GraphSigOf(root),
                ById = statuses,
            };
        }
    }

    // Классификация одного паспорта (§5): archived — все символы исчезли из снимка графа;
    // иначе git log {sha}..HEAD --numstat по файлам паспорта: ≤3 коммитов ИЛИ ≤30% строк —
    // Active, существенная переписка — Degraded. null — сигнала нет (git не ответил / нет
    // снимка) — статус записи не трогаем.
    private async Task<DossierStatus?> ClassifyAsync(string ownerId, string root,
        ChangeDossier d, IReadOnlySet<string>? aliveFqns)
    {
        if (d.Symbols.Count > 0 && aliveFqns is not null && !d.Symbols.Any(aliveFqns.Contains))
            return DossierStatus.Archived;
        if (d.Files.Count == 0) return DossierStatus.Active;

        var log = await GitLogNumstatSafeAsync(ownerId, root, d.CommitSha, d.Files);
        if (log is null) return null;
        var (commits, changedLines) = ParseNumstat(log);
        var linesNow = d.Files.Sum(f => CountLines(root, f));
        return ClassifyFile(commits, changedLines, linesNow);
    }

    // Чистая логика файлового статуса: мало менялся (≤3 коммитов ИЛИ ≤30% строк) — Active.
    internal static DossierStatus ClassifyFile(int commits, long changedLines, long linesNow) =>
        commits <= ActiveMaxCommits || (linesNow > 0 && changedLines * 100 <= linesNow * ActiveMaxChangedPercent)
            ? DossierStatus.Active
            : DossierStatus.Degraded;

    // Разбор `git log --format=%H --numstat <sha>..HEAD -- <files>`: строки-sha (40 hex) —
    // коммиты, строки `added\tdeleted\tpath` — суммарная переписка строк.
    internal static (int Commits, long ChangedLines) ParseNumstat(string output)
    {
        var commits = 0;
        long changed = 0;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length == 40 && line.All(char.IsAsciiHexDigit)) { commits++; continue; }
            var tabs = line.Split('\t');
            if (tabs.Length >= 3 && long.TryParse(tabs[0], out var add) && long.TryParse(tabs[1], out var del))
                changed += add + del;
        }
        return (commits, changed);
    }

    // Фоновый пересчёт после промаха кеша: ход не ждёт — кеш наполнится к следующему ходу.
    // Inflight-гейт не плодит параллельные пересчёты одного проекта.
    private void ScheduleBackgroundRefresh(string ownerId, string projectId, string root,
        IReadOnlyList<ChangeDossier> candidates)
    {
        var key = CacheKey(ownerId, projectId);
        lock (_cacheLock)
        {
            if (_refreshInflight.Contains(key)) return;
            _refreshInflight.Add(key);
        }
        _ = Task.Run(async () =>
        {
            try { await RefreshStatusesAsync(ownerId, projectId, root, candidates); }
            catch (Exception ex) { log?.LogDebug(ex, "dossiers: фоновый пересчёт статусов {Project}", projectId); }
            finally { lock (_cacheLock) _refreshInflight.Remove(key); }
        });
    }

    private static string CacheKey(string ownerId, string projectId) => $"{ownerId}:{projectId}";

    // Дешёвая сигнатура снимка графа (mtime graph.json): пока не менялась, граф не перестраивался
    private string GraphSigOf(string rootPath) =>
        codeGraph?.GetCacheSignature(rootPath)?.ToString("O") ?? "";

    // --- Точки подмены для тестов (счётчики git-вызовов, фейковые снимки графа) ---

    protected virtual async Task<string?> ResolveHeadAsync(string ownerId, string root)
    {
        if (git is null || !Git.GitService.IsGitRepo(root)) return null;
        var r = await git.RunAsync(ownerId, root, ["rev-parse", "HEAD"]);
        return r.Ok ? r.Stdout.Trim() : null;
    }

    protected virtual async Task<string?> GitLogNumstatAsync(
        string ownerId, string root, string sha, IReadOnlyList<string> files)
    {
        var r = await git!.RunAsync(ownerId, root,
            ["log", "--format=%H", "--numstat", $"{sha}..HEAD", "--", .. files]);
        return r.Ok ? r.Stdout : null;
    }

    // Живые FQN типов из снимка графа дерева; null — снимка нет (символьный статус не считаем)
    protected virtual async Task<IReadOnlySet<string>?> LoadAliveFqnsAsync(string root)
    {
        if (codeGraph is null) return null;
        var snap = await codeGraph.GetSnapshotAsync(root, CancellationToken.None);
        if (snap is null) return null;
        return new HashSet<string>(snap.Nodes.Select(n => n.FullyQualifiedName), StringComparer.Ordinal);
    }

    // Размер файла в строках (для «≤30% строк»); отсутствующий файл — 0 (полная переписка)
    protected virtual int CountLines(string root, string relFile)
    {
        try
        {
            var abs = Path.Combine([root, .. relFile.Replace('\\', '/').Split('/')]);
            if (!File.Exists(abs)) return 0;
            return File.ReadLines(abs).Count();
        }
        catch { return 0; }
    }

    private async Task<string?> ResolveHeadSafeAsync(string ownerId, string? root)
    {
        if (root is null) return null;
        try { return await ResolveHeadAsync(ownerId, root); }
        catch (Exception ex) { log?.LogDebug(ex, "dossiers: rev-parse HEAD {Root}", root); return null; }
    }

    private async Task<string?> GitLogNumstatSafeAsync(
        string ownerId, string root, string sha, IReadOnlyList<string> files)
    {
        try { return await GitLogNumstatAsync(ownerId, root, sha, files); }
        catch (Exception ex) { log?.LogDebug(ex, "dossiers: git log --numstat {Sha}", sha); return null; }
    }

    private async Task<IReadOnlySet<string>?> LoadAliveFqnsSafeAsync(string root)
    {
        try { return await LoadAliveFqnsAsync(root); }
        catch (Exception ex) { log?.LogDebug(ex, "dossiers: снимок CodeGraph {Root}", root); return null; }
    }

    // --- Активный канал: поиск для MCP dossier_lookup (в том числе archived) ---

    public IReadOnlyList<ChangeDossier> Lookup(string ownerId, string projectId,
        string? path = null, string? symbol = null, string? query = null, int limit = 20)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return store.Find(ownerId, projectId, file: path).Take(limit).ToList();
        if (!string.IsNullOrWhiteSpace(symbol))
            return store.Find(ownerId, projectId, symbol: symbol).Take(limit).ToList();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // Полнотекст по стору (graceful degradation без Dify) — MemoryFulltext.Rank
            var list = store.List(ownerId, projectId);
            return [.. MemoryFulltext.Rank(list, query, limit, BuildHay)];
        }
        return store.List(ownerId, projectId).OrderByDescending(d => d.CommittedAt).Take(limit).ToList();
    }
}
