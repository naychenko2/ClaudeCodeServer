using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Per-ход провайдер slice Code Graph в системный промпт хода (ADR вариант A — per-ход
/// slice в --append-system-prompt): две секции — «хабы» (точки входа в код) и «словарь»
/// (константы, импортируемые повсюду, свёрнутые по файлам). Граф кода иначе невидим
/// Claude CLI; compact-список закрывает «холодный старт понимания кода».
///
/// Источник — CodeGraphService.GetSnapshotAsync. Кэш slice по сигнатуре (mtime graph.json,
/// 1:1 со сменой BuiltAt): граф не грузится каждый ход, slice пересчитывается только при
/// перестроении. isStale — дешёвый mtime-чек на каждый вызов. Ошибки → null (ход идёт без блока).
/// </summary>
public sealed class CodeGraphPromptProvider
{
    private readonly CodeGraphService _graphs;
    private readonly ILogger<CodeGraphPromptProvider> _log;

    private const int TopN = 10;

    // Секция «словарь»: порог degree тот же, что у god-узлов; рамки — до 5 файлов по 6 имён,
    // чтобы словарь не разворачивался обратно в многострочный топ.
    private const int DictionaryMinDegree = 10;
    private const int DictionaryMaxFiles = 5;
    private const int DictionaryMaxNames = 6;

    // Per-rootPath кэш slice. Сигнатура (mtime graph.json) та же → граф не перестраивался →
    // sliceBase актуален; isStale досчитывается дёшево при каждом вызове (см. GetSliceAsync).
    private sealed record CachedSlice(DateTimeOffset Signature, DateTimeOffset BuiltAt, string SliceBase);
    private readonly ConcurrentDictionary<string, CachedSlice> _cache = new();

    // Счётчик загрузок снимка (для диагностики и тестов кэша: 2 вызова с той же сигнатурой
    // не должны давать более одной загрузки).
    internal int SnapshotLoads;

    public CodeGraphPromptProvider(CodeGraphService graphs, ILogger<CodeGraphPromptProvider> log)
    {
        _graphs = graphs;
        _log = log;
    }

    /// <summary>
    /// Сформировать compact slice top-10 god-nodes для системного промпта. null — граф не
    /// построен, god-узлов нет или rootPath пустой; в этом случае блок в промпт не попадает.
    ///
    /// fallbackRootPath — корень проекта для чата с отдельным worktree: пока свой граф дерева
    /// не построен (строится при первом обращении инструментов), отдаём slice главной ветки
    /// с явной пометкой. Пустой промпт хуже приблизительного: без блока агент не знает даже
    /// про существование графа и инструментов к нему (ADR-003).
    /// </summary>
    public async Task<string?> GetSliceAsync(string? rootPath, string? fallbackRootPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;

        var slice = await SliceForAsync(rootPath, ct);
        if (slice is not null) return slice;

        if (string.IsNullOrWhiteSpace(fallbackRootPath)
            || WorkspaceKnowledgeStore.NormalizePath(fallbackRootPath)
               == WorkspaceKnowledgeStore.NormalizePath(rootPath))
            return null;

        var fromMain = await SliceForAsync(fallbackRootPath, ct);
        return fromMain is null ? null : fromMain + MainTreeNote;
    }

    // Slice конкретного дерева; null — граф для него не построен либо god-узлов нет.
    private async Task<string?> SliceForAsync(string rootPath, CancellationToken ct)
    {
        var key = WorkspaceKnowledgeStore.NormalizePath(rootPath);

        try
        {
            // Дешёвая сигнатура: пока graph.json не менялся, BuiltAt тот же — переиспользуем slice.
            var signature = _graphs.GetCacheSignature(key);
            if (signature is null) return null; // граф ещё не построен

            if (_cache.TryGetValue(key, out var cached) && cached.Signature == signature)
            {
                // Граф не перестраивался — slice актуален; isStale проверим дёшево (mtime).
                return _graphs.IsStaleFor(key, cached.BuiltAt)
                    ? AppendStaleMarker(cached.SliceBase)
                    : cached.SliceBase;
            }

            // Сигнатура сменилась — перезагружаем снимок и пересчитываем slice.
            var snap = await _graphs.GetSnapshotAsync(key, ct);
            SnapshotLoads++;
            if (snap is null) return null;

            var sliceBase = RenderSlice(snap);
            if (sliceBase is null) return null;

            var builtAt = ParseBuiltAt(snap.Metadata.BuiltAt);
            _cache[key] = new CachedSlice(signature.Value, builtAt, sliceBase);

            return snap.Metadata.IsStale ? AppendStaleMarker(sliceBase) : sliceBase;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось собрать slice Code Graph для {Path}", key);
            return null;
        }
    }

    // Склеить slice из снимка: две секции — «хабы» (top god-узлов с метрикой «используют
    // N файлов») и «словарь» (константы одной строкой на файл) + ссылка на полный граф.
    // God-узлы берём из готового поля снимка (отсортированы по degree, константы исключены
    // в CodeGraph.GodNodes); degree и файлы-импортёры досчитываем по рёбрам (контракт REST
    // v1 не несёт degree наружу).
    private static string? RenderSlice(CodeGraphSnapshotDto snap)
    {
        var degree = new Dictionary<string, int>(snap.Edges.Count, StringComparer.Ordinal);
        foreach (var e in snap.Edges)
        {
            degree[e.Source] = degree.GetValueOrDefault(e.Source) + 1;
            degree[e.Target] = degree.GetValueOrDefault(e.Target) + 1;
        }

        var nodeById = new Dictionary<string, GraphNodeDto>(snap.Nodes.Count, StringComparer.Ordinal);
        foreach (var n in snap.Nodes)
            nodeById[n.Id] = n;

        var rows = new List<(GraphNodeDto Node, int Degree, int Files)>();
        foreach (var id in snap.GodNodes)
        {
            if (rows.Count >= TopN) break;
            if (!nodeById.TryGetValue(id, out var node)) continue;
            rows.Add((node, degree.GetValueOrDefault(id), ImporterFiles(id, snap, nodeById)));
        }

        // Словарь: константы с высоким degree сворачиваем по файлам — знание «цвета только
        // из design.ts» полезно модели, но не должно занимать строки топа хабов.
        var dictionary = snap.Nodes
            .Where(n => NodeKinds.IsConstant(n.Kind) && degree.GetValueOrDefault(n.Id) >= DictionaryMinDegree)
            .GroupBy(n => n.SourceFile)
            .OrderByDescending(g => g.Sum(n => degree.GetValueOrDefault(n.Id)))
            .Take(DictionaryMaxFiles)
            .ToList();

        if (rows.Count == 0 && dictionary.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Структура кода проекта (Code Graph)");
        if (rows.Count > 0)
        {
            sb.AppendLine("Хабы (точки входа в код):");
            foreach (var (node, deg, files) in rows)
            {
                var name = string.IsNullOrWhiteSpace(node.FullyQualifiedName)
                    ? node.Label
                    : node.FullyQualifiedName;
                // «используют N файлов» честнее сырого degree: разворот «файл::*» надувает
                // in-degree (784 ребра у токена C = всего 332 файла). У хаба без входящих — degree.
                var metric = files > 0
                    ? $"используют {files} {PluralFiles(files)}"
                    : $"{deg} связей";
                sb.AppendLine($"• {name} ({node.SourceFile}) — {metric}");
            }
        }
        if (dictionary.Count > 0)
        {
            sb.AppendLine("Словарь (импортируется повсюду, навигации нет):");
            foreach (var file in dictionary)
            {
                var names = string.Join("/", file.Select(n => n.Label).Take(DictionaryMaxNames));
                var rest = file.Count() - DictionaryMaxNames;
                if (rest > 0) names += $" +{rest}";
                sb.AppendLine($"{Path.GetFileName(file.Key)} ({names})");
            }
        }
        // Куда идти за остальным графом. Раньше здесь стояли панель «Граф» и REST-эндпоинт —
        // обе двери для агента закрыты (панель для человека, ключа к REST у него нет).
        sb.AppendLine("Когда звать граф вместо Grep:");
        sb.AppendLine("• «где объявлен X» — codegraph_find: отдаёт файл со строкой и вид типа, без текстового шума совпадений;");
        sb.AppendLine("• «что сломается, если правлю X» — codegraph_neighbors: входящие связи с типом (Calls/Implements/References);");
        sb.AppendLine("• «с чего начать в незнакомой подсистеме» — codegraph_hubs.");
        sb.Append("Grep остаётся для текстовых вхождений и файлов вне графа (конфиги, .md, разметка).");
        return sb.ToString();
    }

    /// <summary>
    /// Уникальные файлы-импортёры узла по входящим рёбрам: у TS id источника «файл::имя»
    /// (файл — префикс), у C# — SourceFile узла-источника. Считается на лету, хранить нечего.
    /// </summary>
    private static int ImporterFiles(string nodeId, CodeGraphSnapshotDto snap,
        Dictionary<string, GraphNodeDto> nodeById)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in snap.Edges)
        {
            if (!string.Equals(e.Target, nodeId, StringComparison.Ordinal)) continue;
            if (nodeById.TryGetValue(e.Source, out var src) && !string.IsNullOrEmpty(src.SourceFile))
                files.Add(src.SourceFile);
            else if (e.Source.Contains("::"))
                files.Add(e.Source[..e.Source.LastIndexOf("::")]);
            else
                files.Add(e.Source);
        }
        return files.Count;
    }

    // Русская плюрализация: 1 файл / 2 файла / 5 файлов.
    private static string PluralFiles(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return "файл";
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "файла";
        return "файлов";
    }

    // Пометка «slice не от твоего дерева»: чат в отдельном worktree правит свою ветку, а
    // структура показана от главной — пока свой граф не построен.
    private const string MainTreeNote =
        "\n[этот срез — от ГЛАВНОЙ ветки проекта: в чате отдельное рабочее дерево, "
        + "его собственный граф ещё строится — вызови codegraph_hubs/codegraph_find, "
        + "они уже отвечают по твоему дереву]";

    private static string AppendStaleMarker(string sliceBase) =>
        sliceBase + "\n[может быть устаревшим — файлы изменились]";

    private static DateTimeOffset ParseBuiltAt(string builtAt)
        => string.IsNullOrWhiteSpace(builtAt) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(builtAt);
}
