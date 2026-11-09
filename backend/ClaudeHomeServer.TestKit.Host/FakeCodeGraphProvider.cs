using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Лёгкий провайдер CodeGraph для интеграционных тестов: строит граф из .cs-файлов
/// регекспом (без Roslyn). Нужен, чтобы POST-build и FileWatcher→Rebuild действительно
/// материализовали graph.json без тяжёлого CSharpGraphProvider (Roslyn — отдельно, в Acceptance).
/// </summary>
public sealed class FakeCsGraphProvider : ICodeGraphProvider
{
    private static readonly Regex TypeDecl = new(@"\b(class|interface|struct|enum)\s+(\w+)", RegexOptions.Compiled);

    public Task<CodeGraph> BuildAsync(string rootPath, CancellationToken ct) =>
        Task.FromResult(BuildFromDir(rootPath));

    public Task<CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct) =>
        BuildAsync(rootPath, ct);

    private static CodeGraph BuildFromDir(string rootPath)
    {
        var nodes = new Dictionary<string, CodeGraphNode>();
        if (!Directory.Exists(rootPath))
            return new CodeGraph { Nodes = nodes, Edges = new List<CodeGraphEdge>() };

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            foreach (Match m in TypeDecl.Matches(text))
            {
                var kindStr = m.Groups[1].Value.ToLowerInvariant();
                var name = m.Groups[2].Value;
                var id = rel + ":" + name;
                if (nodes.ContainsKey(id)) continue;
                nodes[id] = new CodeGraphNode
                {
                    Id = id,
                    Label = name,
                    FullyQualifiedName = name,
                    SourceFile = rel,
                    SourceLocation = "L1",
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

        return new CodeGraph { Nodes = nodes, Edges = new List<CodeGraphEdge>() };
    }
}
