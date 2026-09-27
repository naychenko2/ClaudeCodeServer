namespace ClaudeHomeServer.Services.CodeGraph;

// Узкий шов графа кода для раздела «Архитектура» (Viaduct 10.1, разрез Architecture↔CodeGraph).
// Контракт одного намерения — «снимок графа для генератора стартовой C4-модели».
// Прецедент — соседний `ICodeGraphInspector` (шов Dossiers↔CodeGraph): контракт живёт
// в Core, чтобы вертикаль Architecture не получала запрещённую сторожем границ связь
// «вертикаль → вертикаль». Отдельный интерфейс, а не расширение инспектора: Dossiers
// рёбра не нужны, а генератору не годится неблокирующий `StartRebuildIfIdle` —
// кнопка «Собрать из кода» строит граф в том же запросе.
//
// Реализация — `ArchitectureCodeSource` (Main, тонкий форвардер на `CodeGraphService`).
// Регистрируется только при включённой CodeGraph: у контроллера Architecture шов —
// необязательная зависимость (нет → 503 `graph_unavailable`).

// Узел графа: id, короткое имя, файл (как отдал граф — абсолютный или относительный), вид.
public sealed record ArchitectureCodeNode(string Id, string Label, string SourceFile, string Kind);

// Ребро графа между двумя узлами (Calls | Implements | References).
public sealed record ArchitectureCodeEdge(string Source, string Target, string Relation);

// Снимок графа; BuiltAt — время построения (null, если граф его не отдал).
public sealed record ArchitectureCodeSnapshot(
    IReadOnlyList<ArchitectureCodeNode> Nodes,
    IReadOnlyList<ArchitectureCodeEdge> Edges,
    DateTimeOffset? BuiltAt);

public interface IArchitectureCodeSource
{
    // Снимок графа для дерева. null — граф ещё не построен.
    Task<ArchitectureCodeSnapshot?> GetSnapshotAsync(string rootPath, CancellationToken ct);

    // Блокирующее перестроение графа: возвращается, когда граф построен.
    Task RebuildAsync(string rootPath, CancellationToken ct);
}
