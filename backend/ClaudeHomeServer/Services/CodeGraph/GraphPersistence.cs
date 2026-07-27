using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Персистентность графа кода: хранение graph.json по ключу Project.RootPath.
/// Ключ — SHA256 от нормализованного RootPath (без userId, как WorkspaceKnowledge).
/// Путь: data/code-graphs/{rootPathHash}/graph.json
/// </summary>
public sealed class GraphPersistence
{
    private readonly string _baseDir;
    private readonly ILogger<GraphPersistence> _logger;

    public GraphPersistence(string dataDir, ILogger<GraphPersistence> logger)
    {
        var baseDir = dataDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        _baseDir = Path.Combine(baseDir, "code-graphs");
        _logger = logger;
    }

    /// <summary>
    /// Снимок графа из кэша: сам граф + метаданные построения (когда построен, сколько файлов).
    /// </summary>
    public sealed record GraphSnapshot(Core.CodeGraph Graph, DateTimeOffset BuiltAt, int FileCount);

    /// <summary>
    /// Загрузить граф из кэша (null если нет).
    /// </summary>
    public async Task<Core.CodeGraph?> LoadAsync(string rootPath, CancellationToken ct)
    {
        var snap = await LoadSnapshotAsync(rootPath, ct);
        return snap?.Graph;
    }

    /// <summary>
    /// Загрузить снимок графа (граф + метаданные) из кэша. null — граф не построен.
    /// </summary>
    public Task<GraphSnapshot?> LoadSnapshotAsync(string rootPath, CancellationToken ct)
    {
        var hash = GetHash(rootPath);
        var dir = Path.Combine(_baseDir, hash);
        var file = Path.Combine(dir, "graph.json");

        if (!File.Exists(file))
            return Task.FromResult<GraphSnapshot?>(null);

        try
        {
            var json = File.ReadAllText(file);
            var dto = JsonSerializer.Deserialize<CodeGraphDto>(json);
            if (dto is null) return Task.FromResult<GraphSnapshot?>(null);

            var graph = ToModel(dto);
            // У старых graph.json (до метаданных) BuiltAt пуст — считаем эпохой, isStale тогда true.
            var builtAt = string.IsNullOrWhiteSpace(dto.BuiltAt)
                ? DateTimeOffset.MinValue
                : DateTimeOffset.Parse(dto.BuiltAt);
            return Task.FromResult<GraphSnapshot?>(new GraphSnapshot(graph, builtAt, dto.FileCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить граф для {Path}", rootPath);
            return Task.FromResult<GraphSnapshot?>(null);
        }
    }

    /// <summary>
    /// Сохранить граф в кэш (атомарно: temp + rename).
    /// </summary>
    public Task SaveAsync(string rootPath, Core.CodeGraph graph, CancellationToken ct)
    {
        var hash = GetHash(rootPath);
        var dir = Path.Combine(_baseDir, hash);
        var file = Path.Combine(dir, "graph.json");

        try
        {
            Directory.CreateDirectory(dir);

            var dto = ToDto(graph);
            dto.BuiltAt = DateTimeOffset.UtcNow.ToString("O");
            dto.FileCount = CountSourceFiles(rootPath);
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            // Атомарная запись: temp-файл + rename
            var tempFile = Path.Combine(dir, $"graph.{Guid.NewGuid()}.tmp");
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, file, overwrite: true);

            _logger.LogDebug("Граф сохранён для {Path}: {Nodes} узлов, {Edges} рёбер",
                rootPath, graph.Nodes.Count, graph.Edges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить граф для {Path}", rootPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Дешёвая сигнатура кэша графа: mtime graph.json (null — граф не построен).
    /// mtime меняется только при SaveAsync (атомарный temp+move), то есть 1:1 со сменой
    /// BuiltAt, — годится как proxy для инвалидации slice-кэша промпта без загрузки самого графа.
    /// </summary>
    public DateTimeOffset? GetCacheSignature(string rootPath)
    {
        var hash = GetHash(rootPath);
        var file = Path.Combine(_baseDir, hash, "graph.json");
        try
        {
            if (!File.Exists(file)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Удалить граф из кэша.
    /// </summary>
    public void Delete(string rootPath)
    {
        var hash = GetHash(rootPath);
        var dir = Path.Combine(_baseDir, hash);

        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить граф для {Path}", rootPath);
        }
    }

    /// <summary>
    /// Вычислить SHA256 от нормализованного RootPath (ключ stor-а).
    /// </summary>
    private static string GetHash(string rootPath)
    {
        var normalized = WorkspaceKnowledgeStore.NormalizePath(rootPath);
        using var hash = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hashBytes = hash.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// DTO для сериализации (Nodes как словарь, Edges как список + метаданные построения).
    /// </summary>
    private sealed class CodeGraphDto
    {
        public Dictionary<string, CodeGraphNodeDto> Nodes { get; set; } = new();
        public List<CodeGraphEdgeDto> Edges { get; set; } = new();
        /// <summary>ISO-время построения графа (для isStale и metadata REST).</summary>
        public string BuiltAt { get; set; } = "";
        /// <summary>Число исходных файлов на момент построения.</summary>
        public int FileCount { get; set; }
    }

    /// <summary>
    /// Посчитать исходные файлы (.cs и прочие поддержанные расширения) в проекте.
    /// Дёшево, для metadata REST; ошибки подсчёта не роняют сохранение.
    /// </summary>
    private static int CountSourceFiles(string rootPath)
    {
        try
        {
            if (!Directory.Exists(rootPath)) return 0;
            return Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    private sealed class CodeGraphNodeDto
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string FullyQualifiedName { get; set; } = "";
        public string SourceFile { get; set; } = "";
        public string SourceLocation { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    private sealed class CodeGraphEdgeDto
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string Relation { get; set; } = "";
        public string Confidence { get; set; } = "";
    }

    private static CodeGraphDto ToDto(Core.CodeGraph graph) => new()
    {
        Nodes = graph.Nodes.ToDictionary(
            kvp => kvp.Key,
            kvp => new CodeGraphNodeDto
            {
                Id = kvp.Value.Id,
                Label = kvp.Value.Label,
                FullyQualifiedName = kvp.Value.FullyQualifiedName,
                SourceFile = kvp.Value.SourceFile,
                SourceLocation = kvp.Value.SourceLocation,
                Kind = kvp.Value.Kind.ToString(),
            }),
        Edges = graph.Edges.Select(e => new CodeGraphEdgeDto
        {
            Source = e.Source,
            Target = e.Target,
            Relation = e.Relation.ToString(),
            Confidence = e.Confidence.ToString(),
        }).ToList(),
    };

    private static Core.CodeGraph ToModel(CodeGraphDto dto) => new()
    {
        Nodes = dto.Nodes.ToDictionary(
            kvp => kvp.Key,
            kvp => new CodeGraphNode
            {
                Id = kvp.Value.Id,
                Label = kvp.Value.Label,
                FullyQualifiedName = kvp.Value.FullyQualifiedName,
                SourceFile = kvp.Value.SourceFile,
                SourceLocation = kvp.Value.SourceLocation,
                Kind = Enum.Parse<NodeKind>(kvp.Value.Kind),
            }),
        Edges = dto.Edges.Select(e => new CodeGraphEdge
        {
            Source = e.Source,
            Target = e.Target,
            Relation = Enum.Parse<EdgeRelation>(e.Relation),
            Confidence = Enum.Parse<EdgeConfidence>(e.Confidence),
        }).ToList(),
    };
}
