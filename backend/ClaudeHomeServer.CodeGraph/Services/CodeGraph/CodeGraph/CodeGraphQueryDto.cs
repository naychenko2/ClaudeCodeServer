namespace ClaudeHomeServer.Services.CodeGraph;

// DTO тонких запросов к графу (поиск / соседи / хабы). Отдельно от снимка v1:
// эти ответы уезжают в контекст агента через MCP, поэтому несут только то, что ему нужно —
// без Label (дублирует хвост FQN), без отдельных SourceFile/SourceLocation (склеены в Location).

/// <summary>Узел графа в компактном виде: FQN, вид, «файл:строка» и степень связности.</summary>
public sealed class CodeGraphNodeBriefDto
{
    public string Id { get; init; } = "";
    public string Fqn { get; init; } = "";
    /// <summary>Class | Interface | Struct | Enum | Component | Hook | UiPrimitive | Util | Constant</summary>
    public string Kind { get; init; } = "";
    /// <summary>«относительный/путь.cs:42» (строка опускается, если её нет в снимке).</summary>
    public string Location { get; init; } = "";
    /// <summary>Степень связности: входящие + исходящие рёбра.</summary>
    public int Degree { get; init; }
    /// <summary>
    /// Сколько РАЗНЫХ файлов ссылаются на узел (файлы источников входящих рёбер) —
    /// честнее сырого degree для хабов: разворот «файл::*» надувает in-degree
    /// (784 ребра у токена C = всего 332 файла). Заполняется только в хабах.
    /// </summary>
    public int? Files { get; init; }
}

/// <summary>Результат поиска узлов по имени/части FQN.</summary>
public sealed class CodeGraphFindResultDto
{
    public List<CodeGraphNodeBriefDto> Results { get; init; } = new();
    /// <summary>Сколько узлов подошло всего (до применения лимита).</summary>
    public int Total { get; init; }
    public bool IsStale { get; init; }
}

/// <summary>Сосед узла: с кем связан, чем и в какую сторону.</summary>
public sealed class CodeGraphNeighborDto
{
    public string Id { get; init; } = "";
    public string Fqn { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Location { get; init; } = "";
    /// <summary>in — сосед ссылается на узел; out — узел ссылается на соседа.</summary>
    public string Direction { get; init; } = "";
    /// <summary>Calls | Implements | References</summary>
    public string Relation { get; init; } = "";
    /// <summary>Extracted | Inferred</summary>
    public string Confidence { get; init; } = "";
}

/// <summary>Связи узла с разбивкой по направлению и типу отношения.</summary>
public sealed class CodeGraphNeighborsResultDto
{
    public CodeGraphNodeBriefDto Node { get; init; } = new();
    public List<CodeGraphNeighborDto> Neighbors { get; init; } = new();
    /// <summary>Всего связей под фильтром (до лимита).</summary>
    public int Total { get; init; }
    /// <summary>Входящих связей узла (без учёта фильтров).</summary>
    public int TotalIn { get; init; }
    /// <summary>Исходящих связей узла (без учёта фильтров).</summary>
    public int TotalOut { get; init; }
    /// <summary>Сводка по типам отношений под фильтром: Implements → 48, References → 63.</summary>
    public Dictionary<string, int> ByRelation { get; init; } = new();
    public bool IsStale { get; init; }
}

/// <summary>Топ узлов по связности.</summary>
public sealed class CodeGraphHubsResultDto
{
    public List<CodeGraphNodeBriefDto> Hubs { get; init; } = new();
    /// <summary>Всего узлов в графе.</summary>
    public int NodeCount { get; init; }
    /// <summary>Всего рёбер в графе.</summary>
    public int EdgeCount { get; init; }
    public bool IsStale { get; init; }
}

/// <summary>
/// Исход запроса соседей: HasGraph=false — граф не построен (404 + фоновая постройка);
/// Result=null при HasGraph — узел не опознан, Candidates — похожие узлы для подсказки.
/// </summary>
public sealed record CodeGraphNeighborsOutcome(
    bool HasGraph,
    CodeGraphNeighborsResultDto? Result,
    IReadOnlyList<CodeGraphNodeBriefDto> Candidates);
