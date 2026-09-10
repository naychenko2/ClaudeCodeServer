namespace ClaudeHomeServer.Services.CodeGraph;

// Тонкий адаптер `ICodeGraphInspector` → `CodeGraphService` (Этап 5, волна 2).
// CodeGraph — вынесенная вертикаль с собственным `CodeGraphService` и полным DTO
// `CodeGraphSnapshotDto`. Адаптер в Main режет DTO до узкого `CodeGraphSnapshot`
// (только BuiltAt + Nodes + IsStale), которого достаточно Dossiers для якорения FQN
// и сигнатуры кеша статусов. Полный DTO остаётся у REST-контроллера и MCP-тулсета
// — им этого шва не нужно.
public sealed class CodeGraphInspector(CodeGraphService codeGraph) : ICodeGraphInspector
{
    public async Task<CodeGraphSnapshot?> GetSnapshotAsync(string rootPath, CancellationToken ct)
    {
        var snap = await codeGraph.GetSnapshotAsync(rootPath, ct);
        if (snap is null) return null;
        return new CodeGraphSnapshot(
            BuiltAt: string.IsNullOrEmpty(snap.Metadata.BuiltAt)
                ? DateTimeOffset.MinValue
                : DateTimeOffset.Parse(snap.Metadata.BuiltAt),
            Nodes: snap.Nodes
                .Select(n => new DossierCodeGraphNode(n.FullyQualifiedName, n.SourceFile))
                .ToList(),
            IsStale: snap.Metadata.IsStale);
    }

    public void StartRebuildIfIdle(string rootPath) => codeGraph.StartRebuildIfIdle(rootPath);
}