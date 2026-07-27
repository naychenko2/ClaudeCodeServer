using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Services.CodeGraph;

// DTO контракта REST v1 (GET /api/projects/{id}/code-graph). Внутренние детали
// (enum'ы NodeKind/EdgeRelation/EdgeConfidence) наружу не утекают — только строки.

/// <summary>
/// Снимок графа кода для REST-ответа: узлы, рёбра, god-узлы и метаданные.
/// </summary>
public sealed class CodeGraphSnapshotDto
{
    public List<GraphNodeDto> Nodes { get; init; } = new();
    public List<GraphEdgeDto> Edges { get; init; } = new();
    public List<string> GodNodes { get; init; } = new();
    public CodeGraphMetadataDto Metadata { get; init; } = new();
}

/// <summary>Узел графа — тип (класс/интерфейс/структура/enum).</summary>
public sealed class GraphNodeDto
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string FullyQualifiedName { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string SourceLocation { get; init; } = "";
    public string Kind { get; init; } = "";
}

/// <summary>Ребро графа — связь между двумя типами.</summary>
public sealed class GraphEdgeDto
{
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    /// <summary>Calls | Implements | References</summary>
    public string Relation { get; init; } = "";
    /// <summary>Extracted | Inferred</summary>
    public string Confidence { get; init; } = "";
}

/// <summary>Метаданные снимка графа.</summary>
public sealed class CodeGraphMetadataDto
{
    /// <summary>ISO-время построения графа.</summary>
    public string BuiltAt { get; init; } = "";
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int FileCount { get; init; }
    /// <summary>true — исходные файлы изменились после BuiltAt (граф несвежий).</summary>
    public bool IsStale { get; init; }
}

/// <summary>
/// Маппер модели графа в DTO контракта REST v1.
/// </summary>
public static class CodeGraphSnapshotMapper
{
    public static CodeGraphSnapshotDto ToDto(
        Core.CodeGraph graph,
        DateTimeOffset builtAt,
        int fileCount,
        bool isStale) => new()
        {
            Nodes = graph.Nodes.Values
            .Select(n => new GraphNodeDto
            {
                Id = n.Id,
                Label = n.Label,
                FullyQualifiedName = n.FullyQualifiedName,
                SourceFile = n.SourceFile,
                SourceLocation = n.SourceLocation,
                Kind = n.Kind.ToString(),
            })
            .OrderBy(n => n.Label, StringComparer.Ordinal)
            .ThenBy(n => n.FullyQualifiedName, StringComparer.Ordinal)
            .ToList(),
            Edges = graph.Edges
            .Select(e => new GraphEdgeDto
            {
                Source = e.Source,
                Target = e.Target,
                Relation = e.Relation.ToString(),
                Confidence = e.Confidence.ToString(),
            })
            .ToList(),
            GodNodes = graph.GodNodes().Select(n => n.Id).ToList(),
            Metadata = new CodeGraphMetadataDto
            {
                BuiltAt = builtAt == DateTimeOffset.MinValue ? "" : builtAt.ToString("O"),
                NodeCount = graph.Nodes.Count,
                EdgeCount = graph.Edges.Count,
                FileCount = fileCount,
                IsStale = isStale,
            },
        };
}
