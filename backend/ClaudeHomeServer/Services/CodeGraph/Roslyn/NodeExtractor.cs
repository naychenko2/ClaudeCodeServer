using ClaudeHomeServer.Services.CodeGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClaudeHomeServer.Services.CodeGraph.Roslyn;

/// <summary>
/// Результат извлечения узлов: словарь Id→узел (partial-merge) и множество Id для O(1)-фильтра целей.
/// </summary>
public sealed record ExtractedNodes
{
    public required Dictionary<string, CodeGraphNode> Nodes { get; init; }
    public required HashSet<string> ProjectTypeIds { get; init; }
}

/// <summary>
/// Извлекает узлы-типы из Compilation. Узел = объявление типа (не метод), как требует мандат:
/// методы фрагментируют граф (спайк Graphify). Partial-классы сливаются в один узел по FQN.
/// </summary>
public static class NodeExtractor
{
    // Стабильный FQN с generic-параметрами; один и тот же формат для Id узла и цели ребра,
    // чтобы source/target совпадали посимвольно.
    private static readonly SymbolDisplayFormat QualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    /// Извлечь узлы из деревьев. Если restrictToRelPaths задан — обходятся только эти файлы
    /// (используется инкрементом).
    /// </summary>
    public static ExtractedNodes Extract(
        Compilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> trees,
        SymbolFilterOptions options,
        IReadOnlySet<string>? restrictToRelPaths = null)
    {
        var nodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        // Обход в стабильном порядке relPath (Ordinal): при partial-merge выживает «первый
        // встреченный» узел, и его SourceFile обязан быть детерминированным. Итерация словаря
        // (особенно ConcurrentDictionary из CompilationBuilder) порядка не гарантирует —
        // без сортировки SourceFile partial-типа прыгал от прогона к прогону.
        foreach (var relPath in trees.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var tree = trees[relPath];
            if (restrictToRelPaths is not null && !restrictToRelPaths.Contains(relPath))
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // BaseTypeDeclarationSyntax покрывает class/interface/struct/enum — все четыре.
            foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var symbol = semanticModel.GetDeclaredSymbol(decl) as INamedTypeSymbol;
                if (symbol is null) continue;
                if (CodeSymbolFilter.IsAnonymousOrSynthetic(symbol)) continue;

                // Вложенные типы тоже индексируем как самостоятельные узлы (у них свой FQN).
                var id = GetId(symbol);
                if (ids.Contains(id)) continue; // partial-merge: первый встреченный — основной

                var node = new CodeGraphNode
                {
                    Id = id,
                    Label = symbol.Name,
                    FullyQualifiedName = GetId(symbol), // FQN без global::
                    SourceFile = relPath,
                    SourceLocation = $"line {GetLine(decl.Identifier.GetLocation(), tree)}",
                    Kind = ToKind(symbol.TypeKind),
                };

                nodes[id] = node;
                ids.Add(id);
            }
        }

        return new ExtractedNodes { Nodes = nodes, ProjectTypeIds = ids };
    }

    /// <summary>
    /// Id/FQN символа в едином формате (используется и для узла, и для цели ребра).
    /// </summary>
    public static string GetId(INamedTypeSymbol symbol) =>
        symbol.ToDisplayString(QualifiedFormat);

    private static int GetLine(Location location, SyntaxTree tree)
    {
        try
        {
            var span = location.GetLineSpan();
            return span.StartLinePosition.Line + 1; // 1-based
        }
        catch
        {
            return 0;
        }
    }

    private static NodeKind ToKind(TypeKind kind) => kind switch
    {
        TypeKind.Class => NodeKind.Class,
        TypeKind.Interface => NodeKind.Interface,
        TypeKind.Struct => NodeKind.Struct,
        TypeKind.Enum => NodeKind.Enum,
        _ => NodeKind.Class,
    };
}
