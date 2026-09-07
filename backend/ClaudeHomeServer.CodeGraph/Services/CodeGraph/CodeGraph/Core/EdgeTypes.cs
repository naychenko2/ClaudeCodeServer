namespace ClaudeHomeServer.Services.CodeGraph.Core;

/// <summary>
/// Тип отношения между узлами графа.
/// </summary>
public enum EdgeRelation
{
    /// <summary>
    /// Вызовы методов/конструкторов (Calls).
    /// </summary>
    Calls,

    /// <summary>
    /// Реализация интерфейса (Implements).
    /// </summary>
    Implements,

    /// <summary>
    /// Ссылки на тип (в полях, параметрах, возвращаемых значениях).
    /// </summary>
    References,
}

/// <summary>
/// Уверенность в отношении: извлечён из кода или выведен эвристикой.
/// </summary>
public enum EdgeConfidence
{
    /// <summary>
    /// Явно извлечено из синтаксиса/символов (Roslyn SymbolFinder).
    /// </summary>
    Extracted,

    /// <summary>
    /// Выведено эвристикой (например, по текстовым матчам без символов).
    /// </summary>
    Inferred,
}

/// <summary>
/// Ребро графа кода — связь между двумя типами.
/// </summary>
public record CodeGraphEdge
{
    /// <summary>
    /// Исходный узел (от кого связь).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Целевой узел (кому связь).
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// Тип отношения.
    /// </summary>
    public required EdgeRelation Relation { get; init; }

    /// <summary>
    /// Уверенность в отношении.
    /// </summary>
    public required EdgeConfidence Confidence { get; init; }
}
