namespace ClaudeHomeServer.Services.CodeGraph.Core;

/// <summary>
/// Граф кода: узлы (типы) и рёбра (связи между типами).
/// </summary>
public record CodeGraph
{
    /// <summary>
    /// Все узлы графа (типы).
    /// </summary>
    public required Dictionary<string, CodeGraphNode> Nodes { get; init; } = new();

    /// <summary>
    /// Все рёбра графа (связи между типами).
    /// </summary>
    public required List<CodeGraphEdge> Edges { get; init; } = new();

    /// <summary>
    /// Получить god-узлы (типы с высокой центральностью по degree).
    /// Degree = число входящих + исходящих рёбер.
    /// Константы (чистые данные) исключаются: у них нет исходящих путей, degree делает
    /// их «словарём» (токены дизайн-системы обгоняют координаторов), а не точкой входа в код.
    /// </summary>
    /// <param name="minDegree">Минимальный degree для включения (дефолт 10).</param>
    /// <returns>Список god-узлов, отсортированный по убыванию degree.</returns>
    public IEnumerable<CodeGraphNode> GodNodes(int minDegree = 10)
    {
        var degree = new Dictionary<string, int>();
        foreach (var edge in Edges)
        {
            degree[edge.Source] = degree.GetValueOrDefault(edge.Source) + 1;
            degree[edge.Target] = degree.GetValueOrDefault(edge.Target) + 1;
        }

        return Nodes.Values
            .Where(n => n.Kind != NodeKind.Constant)
            .Where(n => degree.GetValueOrDefault(n.Id) >= minDegree)
            .OrderByDescending(n => degree.GetValueOrDefault(n.Id));
    }

    /// <summary>
    /// Создать пустой граф.
    /// </summary>
    public static CodeGraph Empty => new()
    {
        Nodes = new Dictionary<string, CodeGraphNode>(),
        Edges = new List<CodeGraphEdge>(),
    };
}
