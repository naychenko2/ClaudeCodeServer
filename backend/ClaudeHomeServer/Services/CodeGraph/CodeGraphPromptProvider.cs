using System.Collections.Concurrent;
using System.Text;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Per-ход провайдер slice top-10 god-nodes Code Graph в системный промпт хода
/// (ADR вариант A — per-ход slice в --append-system-prompt). Граф кода иначе невидим
/// Claude CLI; compact-список хабов по связности закрывает «холодный старт понимания кода».
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
    /// </summary>
    public async Task<string?> GetSliceAsync(string? rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;
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

    // Склеить slice из снимка: top-N god-узлов (FQN + sourceFile + degree) + ссылка на полный граф.
    // God-узлы берём из готового поля снимка (отсортированы по degree; порог minDegree задан графом);
    // degree для отображения досчитываем по рёбрам (контракт REST v1 не несёт degree наружу).
    private static string? RenderSlice(CodeGraphSnapshotDto snap)
    {
        if (snap.GodNodes.Count == 0) return null;

        var degree = new Dictionary<string, int>(snap.Edges.Count, StringComparer.Ordinal);
        foreach (var e in snap.Edges)
        {
            degree[e.Source] = degree.GetValueOrDefault(e.Source) + 1;
            degree[e.Target] = degree.GetValueOrDefault(e.Target) + 1;
        }

        var nodeById = new Dictionary<string, GraphNodeDto>(snap.Nodes.Count, StringComparer.Ordinal);
        foreach (var n in snap.Nodes)
            nodeById[n.Id] = n;

        var rows = new List<(GraphNodeDto Node, int Degree)>();
        foreach (var id in snap.GodNodes)
        {
            if (rows.Count >= TopN) break;
            if (!nodeById.TryGetValue(id, out var node)) continue;
            rows.Add((node, degree.GetValueOrDefault(id)));
        }
        if (rows.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Структура кода проекта (Code Graph)");
        sb.AppendLine($"Базовые хабы по связности (top-{rows.Count}):");
        foreach (var (node, deg) in rows)
        {
            var name = string.IsNullOrWhiteSpace(node.FullyQualifiedName)
                ? node.Label
                : node.FullyQualifiedName;
            sb.AppendLine($"• {name} ({node.SourceFile}) — {deg} связей");
        }
        sb.Append("Полный граф и поиск: панель «Граф» или GET /api/projects/{id}/code-graph.");
        return sb.ToString();
    }

    private static string AppendStaleMarker(string sliceBase) =>
        sliceBase + "\n[может быть устаревшим — файлы изменились]";

    private static DateTimeOffset ParseBuiltAt(string builtAt)
        => string.IsNullOrWhiteSpace(builtAt) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(builtAt);
}
