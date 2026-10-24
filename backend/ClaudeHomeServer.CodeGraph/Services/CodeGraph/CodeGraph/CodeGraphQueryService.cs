using System.Collections.Concurrent;
using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Тонкие запросы к графу кода (поиск узла, соседи, хабы) — то, чем агент пользуется
/// через MCP-сервер codegraph. Полный снимок для этого не годится: graph.json проекта
/// ~1 МБ, тянуть его в контекст на каждый вопрос расточительно.
///
/// Кэш индекса — по сигнатуре снимка (mtime graph.json, 1:1 со сменой BuiltAt), как в
/// <see cref="CodeGraphPromptProvider"/>: пока граф не перестраивался, degree/смежность
/// считаются один раз. isStale — дешёвый mtime-чек на каждый запрос.
/// </summary>
public sealed class CodeGraphQueryService(CodeGraphService graphs)
{
    // Дефолтные и предельные лимиты выдачи: ответы уезжают в контекст агента, поэтому
    // «отдать 20 и честно написать, сколько всего» лучше, чем вывалить сотню.
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    private sealed record Index(
        DateTimeOffset Signature,
        DateTimeOffset BuiltAt,
        Dictionary<string, GraphNodeDto> Nodes,
        Dictionary<string, int> Degree,
        Dictionary<string, List<GraphEdgeDto>> Outgoing,
        Dictionary<string, List<GraphEdgeDto>> Incoming,
        List<string> Hubs,
        int EdgeCount);

    private readonly ConcurrentDictionary<string, Index> _cache = new();

    // Кэш результата isStale: полный mtime-обход исходников на КАЖДЫЙ вызов любого
    // инструмента (find/neighbors/hubs) — лишние тысячи stat'ов на ход; и slice-промпт
    // зовёт свою проверку тоже. TTL 5с много дешевле повтора обхода, а задержка запуска
    // перестроения на ≤5с ничто против дебаунса 15с.
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, bool Stale)> _staleCache = new();
    private static readonly TimeSpan StaleCacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Поиск узлов по имени типа или части FQN. null — граф не построен.
    /// </summary>
    public async Task<CodeGraphFindResultDto?> FindAsync(
        string rootPath, string query, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(rootPath, ct);
        if (index is null) return null;

        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return new CodeGraphFindResultDto { IsStale = StaleAndRefresh(rootPath, index) };

        var matched = new List<(GraphNodeDto Node, int Rank)>();
        foreach (var node in index.Nodes.Values)
        {
            var rank = Rank(node, q);
            if (rank >= 0) matched.Add((node, rank));
        }

        var results = matched
            .OrderBy(m => m.Rank)
            .ThenByDescending(m => index.Degree.GetValueOrDefault(m.Node.Id))
            .ThenBy(m => m.Node.FullyQualifiedName, StringComparer.Ordinal)
            .Take(Clamp(limit))
            .Select(m => Brief(m.Node, index))
            .ToList();

        return new CodeGraphFindResultDto
        {
            Results = results,
            Total = matched.Count,
            IsStale = StaleAndRefresh(rootPath, index),
        };
    }

    /// <summary>
    /// Связи узла: входящие/исходящие, с типом отношения и confidence.
    /// direction — in | out | both; relation — Calls | Implements | References (null — все).
    /// </summary>
    public async Task<CodeGraphNeighborsOutcome> NeighborsAsync(
        string rootPath, string node, string? direction, string? relation, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(rootPath, ct);
        if (index is null) return new CodeGraphNeighborsOutcome(false, null, []);

        var (target, candidates) = Resolve(index, node);
        if (target is null)
            return new CodeGraphNeighborsOutcome(true, null, candidates);

        var wantIn = !string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase);
        var wantOut = !string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase);
        var relFilter = string.IsNullOrWhiteSpace(relation) ? null : relation.Trim();

        var outgoing = index.Outgoing.GetValueOrDefault(target.Id) ?? [];
        var incoming = index.Incoming.GetValueOrDefault(target.Id) ?? [];

        var rows = new List<(CodeGraphNeighborDto Dto, int Degree)>();
        var byRelation = new Dictionary<string, int>(StringComparer.Ordinal);

        void Collect(List<GraphEdgeDto> edges, bool inbound)
        {
            foreach (var e in edges)
            {
                if (relFilter is not null && !e.Relation.Equals(relFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var otherId = inbound ? e.Source : e.Target;
                // Цель ребра может отсутствовать среди узлов (внешний тип) — показываем как есть.
                index.Nodes.TryGetValue(otherId, out var other);
                byRelation[e.Relation] = byRelation.GetValueOrDefault(e.Relation) + 1;
                rows.Add((new CodeGraphNeighborDto
                {
                    Id = otherId,
                    Fqn = other?.FullyQualifiedName is { Length: > 0 } fqn ? fqn : otherId,
                    Kind = other?.Kind ?? "",
                    Location = other is null ? "" : Location(other),
                    Direction = inbound ? "in" : "out",
                    Relation = e.Relation,
                    Confidence = e.Confidence,
                }, index.Degree.GetValueOrDefault(otherId)));
            }
        }

        if (wantIn) Collect(incoming, inbound: true);
        if (wantOut) Collect(outgoing, inbound: false);

        // Самые связные соседи первыми: при жёстком лимите они полезнее алфавитного среза.
        var neighbors = rows
            .OrderByDescending(r => r.Degree)
            .ThenBy(r => r.Dto.Fqn, StringComparer.Ordinal)
            .Take(Clamp(limit))
            .Select(r => r.Dto)
            .ToList();

        return new CodeGraphNeighborsOutcome(true, new CodeGraphNeighborsResultDto
        {
            Node = Brief(target, index),
            Neighbors = neighbors,
            Total = rows.Count,
            TotalIn = incoming.Count,
            TotalOut = outgoing.Count,
            ByRelation = byRelation,
            IsStale = StaleAndRefresh(rootPath, index),
        }, []);
    }

    /// <summary>
    /// Связи файла: входящие связи всех типов, объявленных в файле, сгруппированные по файлу-потребителю
    /// (хук PreToolUse «потребители правленого файла»). Форма выхода та же, что у node: каждая строка
    /// Neighbors — один файл-потребитель (Fqn/Location — путь файла, Id — он же), Total — число
    /// файлов-потребителей; Node описывает сам файл. file — путь от корня дерева. Результата нет
    /// (Result = null), если в графе нет ни одного типа из этого файла; кандидатов у режима нет.
    /// </summary>
    public async Task<CodeGraphNeighborsOutcome> NeighborsByFileAsync(
        string rootPath, string file, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(rootPath, ct);
        if (index is null) return new CodeGraphNeighborsOutcome(false, null, []);

        var wanted = NormalizeFile(file);
        if (wanted.Length == 0) return new CodeGraphNeighborsOutcome(true, null, []);

        var declared = index.Nodes.Values
            .Where(n => NormalizeFile(n.SourceFile).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (declared.Count == 0) return new CodeGraphNeighborsOutcome(true, null, []);

        var declaredIds = declared.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var consumers = new Dictionary<string, (int Count, HashSet<string> Relations)>(StringComparer.OrdinalIgnoreCase);
        var totalIn = 0;
        foreach (var id in declaredIds)
        {
            foreach (var e in index.Incoming.GetValueOrDefault(id) ?? [])
            {
                // Связь внутри самого файла потребителем не считается
                if (declaredIds.Contains(e.Source)) continue;
                var from = index.Nodes.TryGetValue(e.Source, out var src) && !string.IsNullOrEmpty(src.SourceFile)
                    ? src.SourceFile
                    : e.Source.Contains("::") ? e.Source[..e.Source.LastIndexOf("::", StringComparison.Ordinal)] : e.Source;
                var key = NormalizeFile(from);
                if (key.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                totalIn++;
                if (!consumers.TryGetValue(key, out var acc)) consumers[key] = acc = (0, new HashSet<string>(StringComparer.Ordinal));
                acc.Relations.Add(e.Relation);
                consumers[key] = (acc.Count + 1, acc.Relations);
            }
        }

        var byRelation = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, acc) in consumers)
            foreach (var r in acc.Relations) byRelation[r] = byRelation.GetValueOrDefault(r) + 1;

        var neighbors = consumers
            .OrderByDescending(c => c.Value.Count)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(Clamp(limit))
            .Select(c => new CodeGraphNeighborDto
            {
                Id = c.Key,
                Fqn = c.Key,
                Kind = "File",
                Location = c.Key,
                Direction = "in",
                Relation = string.Join("+", c.Value.Relations.Order(StringComparer.Ordinal)),
                Confidence = "",
            })
            .ToList();

        return new CodeGraphNeighborsOutcome(true, new CodeGraphNeighborsResultDto
        {
            Node = new CodeGraphNodeBriefDto { Id = wanted, Fqn = wanted, Kind = "File", Location = wanted, Degree = totalIn },
            Neighbors = neighbors,
            Total = consumers.Count,
            TotalIn = totalIn,
            TotalOut = 0,
            ByRelation = byRelation,
            IsStale = StaleAndRefresh(rootPath, index),
        }, []);
    }

    /// <summary>
    /// Зависимости файла: исходящие связи Calls|References типов, объявленных в файле, на узлы ДРУГИХ
    /// файлов (хвост «Контракты» первого хода). Каждая строка Neighbors — один символ-цель с «файл:строка»
    /// объявления; Total — число уникальных целей. Цели без узла в графе (внешние типы) пропускаются: без
    /// объявления строки нет. Результата нет (Result = null), если файла нет в графе.
    /// </summary>
    public async Task<CodeGraphNeighborsOutcome> DependenciesByFileAsync(
        string rootPath, string file, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(rootPath, ct);
        if (index is null) return new CodeGraphNeighborsOutcome(false, null, []);

        var wanted = NormalizeFile(file);
        if (wanted.Length == 0) return new CodeGraphNeighborsOutcome(true, null, []);

        var declaredIds = index.Nodes.Values
            .Where(n => NormalizeFile(n.SourceFile).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (declaredIds.Count == 0) return new CodeGraphNeighborsOutcome(true, null, []);

        var targets = new Dictionary<string, (GraphNodeDto Node, HashSet<string> Relations)>(StringComparer.Ordinal);
        foreach (var id in declaredIds)
        {
            foreach (var e in index.Outgoing.GetValueOrDefault(id) ?? [])
            {
                if (!e.Relation.Equals("Calls", StringComparison.OrdinalIgnoreCase)
                    && !e.Relation.Equals("References", StringComparison.OrdinalIgnoreCase)) continue;
                if (declaredIds.Contains(e.Target) || !index.Nodes.TryGetValue(e.Target, out var target)) continue;
                if (NormalizeFile(target.SourceFile).Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (!targets.TryGetValue(e.Target, out var acc))
                    targets[e.Target] = acc = (target, new HashSet<string>(StringComparer.Ordinal));
                acc.Relations.Add(e.Relation);
            }
        }

        var neighbors = targets.Values
            .OrderByDescending(t => index.Degree.GetValueOrDefault(t.Node.Id))
            .ThenBy(t => t.Node.FullyQualifiedName, StringComparer.Ordinal)
            .Take(Clamp(limit))
            .Select(t => new CodeGraphNeighborDto
            {
                Id = t.Node.Id,
                Fqn = string.IsNullOrWhiteSpace(t.Node.FullyQualifiedName) ? t.Node.Label : t.Node.FullyQualifiedName,
                Kind = t.Node.Kind,
                Location = Location(t.Node),
                Direction = "out",
                Relation = string.Join("+", t.Relations.Order(StringComparer.Ordinal)),
                Confidence = "",
            })
            .ToList();

        return new CodeGraphNeighborsOutcome(true, new CodeGraphNeighborsResultDto
        {
            Node = new CodeGraphNodeBriefDto { Id = wanted, Fqn = wanted, Kind = "File", Location = wanted, Degree = targets.Count },
            Neighbors = neighbors,
            Total = targets.Count,
            TotalIn = 0,
            TotalOut = targets.Count,
            IsStale = StaleAndRefresh(rootPath, index),
        }, []);
    }

    private static string NormalizeFile(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }

    /// <summary>
    /// Топ узлов по связности (те же хабы, что в slice системного промпта, но по запросу).
    /// null — граф не построен.
    /// </summary>
    public async Task<CodeGraphHubsResultDto?> HubsAsync(string rootPath, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(rootPath, ct);
        if (index is null) return null;

        var hubs = index.Hubs
            .Take(Clamp(limit))
            .Select(id => Brief(index.Nodes[id], index, importerFiles: true))
            .ToList();

        return new CodeGraphHubsResultDto
        {
            Hubs = hubs,
            NodeCount = index.Nodes.Count,
            EdgeCount = index.EdgeCount,
            IsStale = StaleAndRefresh(rootPath, index),
        };
    }

    // Ранг совпадения: 0 — точное имя типа или FQN, 1 — начинается с запроса,
    // 2 — вхождение в FQN; -1 — не подошёл. Регистр не учитываем: модель пишет имя как помнит.
    private static int Rank(GraphNodeDto node, string q)
    {
        if (node.Label.Equals(q, StringComparison.OrdinalIgnoreCase)
            || node.FullyQualifiedName.Equals(q, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (node.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (node.FullyQualifiedName.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        return -1;
    }

    // Резолв узла по тому, что назвала модель: id/FQN как есть → хвост FQN («…ServerMessage»)
    // → короткое имя типа. Неоднозначность не угадываем: возвращаем кандидатов, чтобы
    // инструмент показал их и модель уточнила, а не получила связи случайного однофамильца.
    private static (GraphNodeDto? Node, IReadOnlyList<CodeGraphNodeBriefDto> Candidates) Resolve(
        Index index, string node)
    {
        var q = (node ?? "").Trim();
        if (q.Length == 0) return (null, []);

        if (index.Nodes.TryGetValue(q, out var exact)) return (exact, []);

        var byFqn = index.Nodes.Values
            .Where(n => n.FullyQualifiedName.Equals(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byFqn.Count == 1) return (byFqn[0], []);

        var suffix = "." + q;
        var byName = index.Nodes.Values
            .Where(n => n.Label.Equals(q, StringComparison.OrdinalIgnoreCase)
                        || n.FullyQualifiedName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1) return (byName[0], []);

        var candidates = (byFqn.Count > 1 ? byFqn : byName)
            .OrderByDescending(n => index.Degree.GetValueOrDefault(n.Id))
            .Take(5)
            .Select(n => Brief(n, index))
            .ToList();
        return (null, candidates);
    }

    private static CodeGraphNodeBriefDto Brief(GraphNodeDto node, Index index, bool importerFiles = false) => new()
    {
        Id = node.Id,
        Fqn = string.IsNullOrWhiteSpace(node.FullyQualifiedName) ? node.Label : node.FullyQualifiedName,
        Kind = node.Kind,
        Location = Location(node),
        Degree = index.Degree.GetValueOrDefault(node.Id),
        Files = importerFiles ? CountImporterFiles(node.Id, index) : null,
    };

    /// <summary>
    /// Уникальные файлы-импортёры узла по входящим рёбрам: у TS id источника «файл::имя»
    /// (файл — префикс), у C# — SourceFile узла-источника. Считается на лету, хранить нечего.
    /// </summary>
    private static int CountImporterFiles(string nodeId, Index index)
    {
        var incoming = index.Incoming.GetValueOrDefault(nodeId);
        if (incoming is null || incoming.Count == 0) return 0;

        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in incoming)
        {
            if (index.Nodes.TryGetValue(e.Source, out var src) && !string.IsNullOrEmpty(src.SourceFile))
                files.Add(src.SourceFile);
            else if (e.Source.Contains("::"))
                files.Add(e.Source[..e.Source.LastIndexOf("::")]);
            else
                files.Add(e.Source);
        }
        return files.Count;
    }

    // «файл:строка» одной строкой: SourceLocation снимка — «line 42» (см. NodeExtractor).
    private static string Location(GraphNodeDto node)
    {
        var line = ParseLine(node.SourceLocation);
        return line > 0 ? $"{node.SourceFile}:{line}" : node.SourceFile;
    }

    private static int ParseLine(string? location)
    {
        if (string.IsNullOrEmpty(location)) return 0;
        var digits = new string(location.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var line) ? line : 0;
    }

    private static int Clamp(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    // Граф несвежий — просим фоновое перестроение (guard «один rebuild на проект» внутри),
    // как делает GET снимка: агент работает без панели, и без этого читал бы устаревший граф
    // сколь угодно долго. Ответ отдаётся из текущего снимка с пометкой isStale.
    private bool StaleAndRefresh(string rootPath, Index index)
    {
        var key = PathNormalizer.NormalizePath(rootPath);
        var now = DateTimeOffset.UtcNow;

        if (_staleCache.TryGetValue(key, out var cached) && now - cached.At < StaleCacheTtl)
            return cached.Stale;

        var stale = graphs.IsStaleFor(rootPath, index.BuiltAt);
        if (stale)
            graphs.StartRebuildIfIdle(rootPath);

        _staleCache[key] = (now, stale);
        return stale;
    }

    // Индекс графа из кэша либо из снимка. null — граф ещё не построен.
    private async Task<Index?> GetIndexAsync(string rootPath, CancellationToken ct)
    {
        var key = PathNormalizer.NormalizePath(rootPath);

        var signature = graphs.GetCacheSignature(key);
        if (signature is null) return null;

        if (_cache.TryGetValue(key, out var cached) && cached.Signature == signature)
            return cached;

        var snapshot = await graphs.GetSnapshotAsync(key, ct);
        if (snapshot is null) return null;

        var index = BuildIndex(snapshot, signature.Value);
        _cache[key] = index;
        return index;
    }

    private static Index BuildIndex(CodeGraphSnapshotDto snapshot, DateTimeOffset signature)
    {
        var nodes = new Dictionary<string, GraphNodeDto>(snapshot.Nodes.Count, StringComparer.Ordinal);
        foreach (var n in snapshot.Nodes) nodes[n.Id] = n;

        var degree = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<GraphEdgeDto>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<GraphEdgeDto>>(StringComparer.Ordinal);
        foreach (var e in snapshot.Edges)
        {
            degree[e.Source] = degree.GetValueOrDefault(e.Source) + 1;
            degree[e.Target] = degree.GetValueOrDefault(e.Target) + 1;
            if (!outgoing.TryGetValue(e.Source, out var outs)) outgoing[e.Source] = outs = [];
            outs.Add(e);
            if (!incoming.TryGetValue(e.Target, out var ins)) incoming[e.Target] = ins = [];
            ins.Add(e);
        }

        var hubs = nodes.Values
            // Константы — словарь, не точка входа в код: исключаем из топа, как GodNodes
            // (в codegraph_find они остаются — поиск по имени обязан их находить).
            .Where(n => !NodeKinds.IsConstant(n.Kind))
            .Where(n => degree.GetValueOrDefault(n.Id) > 0)
            .OrderByDescending(n => degree.GetValueOrDefault(n.Id))
            .ThenBy(n => n.FullyQualifiedName, StringComparer.Ordinal)
            .Select(n => n.Id)
            .ToList();

        var builtAt = string.IsNullOrWhiteSpace(snapshot.Metadata.BuiltAt)
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Parse(snapshot.Metadata.BuiltAt);

        return new Index(signature, builtAt, nodes, degree, outgoing, incoming, hubs, snapshot.Edges.Count);
    }
}
