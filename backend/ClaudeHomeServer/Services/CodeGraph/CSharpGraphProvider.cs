using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.CodeGraph.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Провайдер графа для C#: строит граф типов с рёбрами Calls/Implements/References
/// через Roslyn (CSharpCompilation + SemanticModel). Без MSBuildWorkspace — ради
/// платформонезависимости (CI ubuntu), отсутствия lock'ов и контроля памяти.
/// Инкремент — по in-memory кэшу Compilation; fallback на регексп при &gt;MaxFiles.
/// </summary>
public sealed partial class CSharpGraphProvider : ICodeGraphProvider
{
    private readonly ILogger<CSharpGraphProvider> _logger;
    private readonly SymbolFilterOptions _options = new();

    /// <summary>Порог числа .cs-файлов: выше — fallback на регексп (без Roslyn).</summary>
    private const int MaxFiles = 5000;

    /// <summary>Порог для кэширования Compilation: выше — не держим кэш (память), инкремент падает до rebuild.</summary>
    private const int CacheTreeThreshold = 1000;

    // Кэш последнего построения: rootPath → (граф, деревья, assemblyName). Concurrent — Singleton.
    private readonly ConcurrentDictionary<string, GraphCompilationCache> _cache = new();

    // Блокировка RMW кэша per-rootPath. ConcurrentDictionary атомарен на отдельные операции
    // (TryGetValue/set), но BuildAsync/UpdateAsync — длинный read-modify-write: между чтением
    // кэша и записью другой поток мог бы вписать свой результат и потеряться. Лок сериализует
    // перестроения одного проекта, не блокируя разные проекты (провайдер — Singleton, и при
    // живом REST новые читатели приходят параллельно с фоновым rebuild по дебаунсу).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public CSharpGraphProvider(ILogger<CSharpGraphProvider> logger)
    {
        _logger = logger;
    }

    private SemaphoreSlim LockFor(string rootPath) =>
        _locks.GetOrAdd(rootPath, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Построить полный граф для всех .cs-файлов проекта через Roslyn.
    /// </summary>
    public async Task<Core.CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
    {
        var sem = LockFor(rootPath);
        await sem.WaitAsync(ct);
        try
        {
            return await BuildLockedAsync(rootPath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Внутреннее построение, уже под блокировкой rootPath.</summary>
    private async Task<Core.CodeGraph> BuildLockedAsync(string rootPath, CancellationToken ct)
    {
        var build = CompilationBuilder.Build(rootPath, _options, MaxFiles, ct);

        if (!build.IsComplete)
        {
            _logger.LogWarning(
                "CSharpGraphProvider: {Count} .cs-файлов превышает порог {Max} — fallback на регексп для {Path}",
                build.TotalFilesFound, MaxFiles, rootPath);
            return await BuildWithRegexFallback(rootPath, ct);
        }

        var nodes = NodeExtractor.Extract(build.Compilation, build.Trees, _options);
        var edges = EdgeExtractor.Extract(
            build.Compilation, build.Trees, nodes,
            CompilationBuilder.ProjectAssemblyName, restrictToRelPaths: null);

        var graph = new Core.CodeGraph
        {
            Nodes = nodes.Nodes,
            Edges = edges,
        };

        // Кэш для инкремента — только если проект умещается в порог по памяти.
        if (build.Trees.Count <= CacheTreeThreshold)
        {
            _cache[rootPath] = new GraphCompilationCache
            {
                Graph = graph,
                Trees = build.Trees,
                ProjectAssemblyName = CompilationBuilder.ProjectAssemblyName,
            };
        }

        _logger.LogInformation(
            "CSharpGraphProvider: Roslyn-граф для {Path} — {Nodes} узлов, {Edges} рёбер ({Files} файлов)",
            rootPath, graph.Nodes.Count, graph.Edges.Count, build.Trees.Count);

        return graph;
    }

    /// <summary>
    /// Инкрементальное обновление по изменившимся файлам.
    /// Стратегия: переиспользуем unchanged-деревья из кэша, перестраиваем только changedFiles;
    /// инвалидация — source-side (рёбра из changed-файлов) + orphan-cleanup по обоим концам.
    /// Limitation: target-side по членам unchanged-файла не отслеживается (документировано в мандате
    /// как реалистичный fallback). Если кэша нет — полный BuildAsync.
    /// RMW кэша сериализован per-rootPath локом: иначе параллельный rebuild/REST-чтение терял обновление.
    /// </summary>
    public async Task<Core.CodeGraph> UpdateAsync(
        string rootPath,
        IEnumerable<string> changedFiles,
        CancellationToken ct)
    {
        var changed = changedFiles
            .Select(f => CompilationBuilder.Rel(rootPath, f))
            // Escape-гард (SafeJoin-аналог): rel-путь с ".." или абсолютный другого корня
            // указывал бы вне rootPath, и UpdateAsync читал бы чужие файлы с диска.
            .Where(r => !r.StartsWith("../", StringComparison.Ordinal)
                        && !r.Equals("..", StringComparison.Ordinal)
                        && !Path.IsPathRooted(r))
            .Where(r => !CodeSymbolFilter.IsExcludedFile(r, _options))
            .Distinct()
            .ToList();

        // Пустой список изменений — полный rebuild с собственным локом.
        if (changed.Count == 0)
            return await BuildAsync(rootPath, ct);

        var sem = LockFor(rootPath);
        await sem.WaitAsync(ct);
        try
        {
            // Все fallback-точки под локом зовут BuildLockedAsync напрямую, без повторного
            // взятия лока — иначе SemaphoreSlim(1,1) дедлокнулся бы на реентерабельном вызове.
            if (!_cache.TryGetValue(rootPath, out var cache) || cache.Trees.Count > CacheTreeThreshold)
            {
                _logger.LogDebug("CSharpGraphProvider.UpdateAsync: нет кэша для {Path} — полный rebuild", rootPath);
                return await BuildLockedAsync(rootPath, ct);
            }

            // Если изменилась значительная часть — дешевле полный rebuild.
            if (changed.Count >= cache.Trees.Count)
                return await BuildLockedAsync(rootPath, ct);

            ct.ThrowIfCancellationRequested();

            // 1. Обновляем map деревьев: перечитываем changedFiles, удаляем пропавшие.
            var trees = new Dictionary<string, SyntaxTree>(cache.Trees, StringComparer.Ordinal);
            foreach (var rel in changed)
            {
                var abs = Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs))
                {
                    trees.Remove(rel);
                    continue;
                }

                try
                {
                    var text = await File.ReadAllTextAsync(abs, ct);
                    trees[rel] = CSharpSyntaxTree.ParseText(text, path: abs);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "UpdateAsync: не прочитать {File}", abs);
                }
            }

            ct.ThrowIfCancellationRequested();

            // 2. Перестраиваем Compilation из обновлённого набора деревьев.
            var compilation = CSharpCompilation.Create(
                cache.ProjectAssemblyName,
                trees.Values,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithNullableContextOptions(NullableContextOptions.Enable));

            var changedSet = new HashSet<string>(changed, StringComparer.Ordinal);

            // 3. Узлы: удалить changed-файлы, извлечь заново только для них.
            var nodes = new Dictionary<string, CodeGraphNode>(cache.Graph.Nodes, StringComparer.Ordinal);
            var toDropNodes = nodes
                .Where(kvp => changedSet.Contains(kvp.Value.SourceFile))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var id in toDropNodes) nodes.Remove(id);

            var freshNodes = NodeExtractor.Extract(compilation, trees, _options, restrictToRelPaths: changedSet);
            foreach (var (id, node) in freshNodes.Nodes) nodes[id] = node;

            var extracted = new ExtractedNodes { Nodes = nodes, ProjectTypeIds = new HashSet<string>(nodes.Keys, StringComparer.Ordinal) };

            // 4. Рёбра: выкинуть changed-source (перестроится EdgeExtractor'ом) и повисшие
            //    orphan-концы — и source, и target. SourceFile для changed-детекта берём из
            //    СТАРОГО кэша: e.Source мог уже исчезнуть из новых nodes (тип удалён/переименован),
            //    и тогда lookup в новых nodes дал бы null — ребро ошибочно прошло бы changed-фильтр
            //    и выжило с висящим Source. Orphan-cleanup по обоим концам такие рёбра гасит.
            var oldNodes = cache.Graph.Nodes;
            var edges = cache.Graph.Edges
                .Where(e =>
                {
                    if (oldNodes.TryGetValue(e.Source, out var oldSrc) && changedSet.Contains(oldSrc.SourceFile))
                        return false;
                    return nodes.ContainsKey(e.Source) && nodes.ContainsKey(e.Target);
                })
                .ToList();

            var freshEdges = EdgeExtractor.Extract(
                compilation, trees, extracted,
                cache.ProjectAssemblyName, restrictToRelPaths: changedSet);
            edges.AddRange(freshEdges);

            // Дедуп (могли остаться дубли после слияния).
            var dedup = edges.DistinctBy(e => $"{e.Source}\x1F{e.Target}\x1F{e.Relation}").ToList();

            var graph = new Core.CodeGraph { Nodes = nodes, Edges = dedup };

            _cache[rootPath] = new GraphCompilationCache
            {
                Graph = graph,
                Trees = trees,
                ProjectAssemblyName = cache.ProjectAssemblyName,
            };

            _logger.LogInformation(
                "CSharpGraphProvider: инкремент для {Path}, changed={Changed} — итого {Nodes} узлов, {Edges} рёбер",
                rootPath, changed.Count, graph.Nodes.Count, graph.Edges.Count);

            return graph;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Fallback на регекспах: для проектов &gt; MaxFiles, где Roslyn-Compilation неприемлем по памяти.
    /// Даёт god-nodes (узлы-типы) без resolution рёбер — лучше, чем ничего для огромных репо.
    /// </summary>
    private async Task<Core.CodeGraph> BuildWithRegexFallback(string rootPath, CancellationToken ct)
    {
        var nodes = new Dictionary<string, CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        var regex = TypeDeclRegex();

        // Тот же обход, что и в Roslyn-пути: мусорные каталоги (IgnoredDirectories) уже отсечены,
        // иначе regex-fallback индексировал бы .claude/bin/obj… (баг прода).
        foreach (var file in CompilationBuilder.EnumerateCsFiles(rootPath))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var text = await File.ReadAllTextAsync(file, ct);
                var relPath = CompilationBuilder.Rel(rootPath, file);
                if (CodeSymbolFilter.IsExcludedFile(relPath, _options)) continue;

                foreach (Match match in regex.Matches(text))
                {
                    var kindStr = match.Groups[1].Value.ToLowerInvariant();
                    var name = match.Groups[2].Value;
                    var id = $"{relPath}:{name}";

                    if (nodes.ContainsKey(id)) continue;
                    var line = CountNewLines(text.AsSpan(0, match.Index)) + 1;
                    nodes[id] = new CodeGraphNode
                    {
                        Id = id,
                        Label = name,
                        FullyQualifiedName = name,
                        SourceFile = relPath,
                        SourceLocation = $"line {line}",
                        Kind = kindStr switch
                        {
                            "interface" => NodeKind.Interface,
                            "struct" => NodeKind.Struct,
                            "enum" => NodeKind.Enum,
                            _ => NodeKind.Class,
                        },
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Regex-fallback: ошибка обработки файла {File}", file);
            }
        }

        _logger.LogInformation("CSharpGraphProvider: regex-fallback — {Nodes} типов для {Path}", nodes.Count, rootPath);
        return new Core.CodeGraph { Nodes = nodes, Edges = edges };
    }

    private static int CountNewLines(ReadOnlySpan<char> span)
    {
        int count = 0;
        foreach (var c in span) if (c == '\n') count++;
        return count;
    }

    [GeneratedRegex(@"\b(class|interface|struct|enum)\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclRegex();
}
