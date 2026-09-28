namespace ClaudeHomeServer.Services.CodeGraph;

// Тонкий адаптер `IArchitectureCodeSource` → `CodeGraphService` (Viaduct 10.1).
// Режет полный DTO снимка до узлов, рёбер и времени построения — ровно то, что
// генератор стартовой модели Architecture перекладывает в свои нейтральные входы.
public sealed class ArchitectureCodeSource(CodeGraphService codeGraph) : IArchitectureCodeSource
{
    public async Task<ArchitectureCodeSnapshot?> GetSnapshotAsync(string rootPath, CancellationToken ct)
    {
        var snap = await codeGraph.GetSnapshotAsync(rootPath, ct);
        if (snap is null) return null;
        return new ArchitectureCodeSnapshot(
            snap.Nodes.Select(n => new ArchitectureCodeNode(n.Id, n.Label, n.SourceFile, n.Kind)).ToList(),
            snap.Edges.Select(e => new ArchitectureCodeEdge(e.Source, e.Target, e.Relation)).ToList(),
            DateTimeOffset.TryParse(snap.Metadata.BuiltAt, out var builtAt) ? builtAt : null);
    }

    public Task RebuildAsync(string rootPath, CancellationToken ct) => codeGraph.RebuildAsync(rootPath, ct);
}
