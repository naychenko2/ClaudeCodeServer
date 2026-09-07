using System.Collections.Concurrent;
using ClaudeHomeServer.Services.CodeGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClaudeHomeServer.Services.CodeGraph.Roslyn;

/// <summary>
/// Извлекает рёбра графа (Implements/References/Calls) из Compilation через Roslyn symbol API.
/// Все рёбра — только между типами проекта (BCL/внешние отсеиваются по ContainingAssembly).
/// </summary>
public static class EdgeExtractor
{
    /// <summary>
    /// Извлечь рёбра. Если restrictToRelPaths задан — рёбра строятся только для исходящих
    /// из типов этих файлов (source ∈ restrictToRelPaths); используется инкрементом.
    /// </summary>
    public static List<CodeGraphEdge> Extract(
        Compilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> trees,
        ExtractedNodes nodes,
        string projectAssemblyName,
        IReadOnlySet<string>? restrictToRelPaths = null)
    {
        var projectIds = nodes.ProjectTypeIds;
        var edges = new ConcurrentDictionary<string, CodeGraphEdge>(Environment.ProcessorCount, 64);

        // --- Implements + References: symbol-driven, по деревьям ---
        foreach (var (relPath, tree) in trees)
        {
            if (restrictToRelPaths is not null && !restrictToRelPaths.Contains(relPath))
                continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(decl) as INamedTypeSymbol;
                if (symbol is null) continue;
                var sourceId = NodeExtractor.GetId(symbol);
                if (!projectIds.Contains(sourceId)) continue; // не наш тип

                ExtractImplements(symbol, sourceId, projectIds, edges);
                ExtractReferences(symbol, sourceId, projectIds, edges);
            }
        }

        // --- Calls: syntax-driven walker (InvocationExpression / ObjectCreationExpression) ---
        ExtractCalls(compilation, trees, projectIds, restrictToRelPaths, edges);

        return edges.Values.ToList();
    }

    /// <summary>
    /// Implements: прямые интерфейсы и базовый тип. AllInterfaces не берём — транзитив
    /// плодит шум; прямые связи информативнее для графа зависимостей.
    /// </summary>
    private static void ExtractImplements(
        INamedTypeSymbol symbol,
        string sourceId,
        HashSet<string> projectIds,
        ConcurrentDictionary<string, CodeGraphEdge> edges)
    {
        foreach (var iface in symbol.Interfaces)
        {
            AddEdge(edges, sourceId, iface, projectIds, EdgeRelation.Implements);
        }

        // BaseType: не object/ValueType/Enum (они из BCL и отсеются, но проверим явно).
        if (symbol.BaseType is { } baseType && baseType.TypeKind != TypeKind.Error)
        {
            AddEdge(edges, sourceId, baseType, projectIds, EdgeRelation.Implements);
        }
    }

    /// <summary>
    /// References: типы, на которые ссылается тип через свои члены — поля, свойства,
    /// параметры методов, возвращаемые значения. Разворачивает generic-аргументы и массивы.
    /// </summary>
    private static void ExtractReferences(
        INamedTypeSymbol symbol,
        string sourceId,
        HashSet<string> projectIds,
        ConcurrentDictionary<string, CodeGraphEdge> edges)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var member in symbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field:
                    CollectTypes(field.Type, seen);
                    break;
                case IPropertySymbol prop:
                    CollectTypes(prop.Type, seen);
                    break;
                case IEventSymbol ev:
                    CollectTypes(ev.Type, seen);
                    break;
                case IMethodSymbol method when method.MethodKind is MethodKind.Ordinary
                    or MethodKind.Constructor or MethodKind.StaticConstructor:
                    CollectTypes(method.ReturnType, seen);
                    foreach (var p in method.Parameters) CollectTypes(p.Type, seen);
                    break;
            }
        }

        foreach (var target in seen)
        {
            AddEdge(edges, sourceId, target, projectIds, EdgeRelation.References);
        }
    }

    /// <summary>
    /// Calls: для каждого вызова метода/конструктора берём ContainingType целевого символа.
    /// Source = тип, в теле которого стоит вызов (через ancestor-walk по синтаксису — дешевле
    /// GetEnclosingSymbol). Forward-direction: A calls B = A зависит от B — то, что нужно графу.
    /// SymbolFinder.FindCallersAsync даёт reverse (кто вызывает) и дороже O(n) per метод.
    /// </summary>
    private static void ExtractCalls(
        Compilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> trees,
        HashSet<string> projectIds,
        IReadOnlySet<string>? restrictToRelPaths,
        ConcurrentDictionary<string, CodeGraphEdge> edges)
    {
        foreach (var (relPath, tree) in trees)
        {
            if (restrictToRelPaths is not null && !restrictToRelPaths.Contains(relPath))
                continue;

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // Вызовы методов.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                AddCallEdge(model, invocation, projectIds, edges);
            }

            // Создание объектов (конструкторы).
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                AddCallEdge(model, creation, projectIds, edges);
            }
        }
    }

    private static void AddCallEdge(
        SemanticModel model,
        SyntaxNode callNode,
        HashSet<string> projectIds,
        ConcurrentDictionary<string, CodeGraphEdge> edges)
    {
        var sourceDecl = callNode.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (sourceDecl is null) return; // вызов вне типа (top-level) — пропускаем

        var sourceSymbol = model.GetDeclaredSymbol(sourceDecl) as INamedTypeSymbol;
        if (sourceSymbol is null) return;
        var sourceId = NodeExtractor.GetId(sourceSymbol);
        if (!projectIds.Contains(sourceId)) return;

        var info = model.GetSymbolInfo(callNode);
        if (info.Symbol is not IMethodSymbol method) return;
        if (method.ContainingType is null) return;

        AddEdge(edges, sourceId, method.ContainingType, projectIds, EdgeRelation.Calls);
    }

    /// <summary>
    /// Рекурсивно собрать все именованные типы из сигнатуры (generic-аргументы, массивы).
    /// </summary>
    private static void CollectTypes(ITypeSymbol? type, HashSet<INamedTypeSymbol> sink)
    {
        if (type is null) return;
        switch (type)
        {
            case INamedTypeSymbol named:
                // Дедуп constructed-generic к определению (Repo<string> == Repo<int> → Repo<T>).
                if (sink.Add(named.OriginalDefinition))
                {
                    foreach (var arg in named.TypeArguments) CollectTypes(arg, sink);
                }
                break;
            case IArrayTypeSymbol arr:
                CollectTypes(arr.ElementType, sink);
                break;
            case IPointerTypeSymbol ptr:
                CollectTypes(ptr.PointedAtType, sink);
                break;
        }
    }

    private static void AddEdge(
        ConcurrentDictionary<string, CodeGraphEdge> edges,
        string sourceId,
        ITypeSymbol? targetType,
        HashSet<string> projectIds,
        EdgeRelation relation)
    {
        if (targetType is not INamedTypeSymbol named) return;
        if (named.TypeKind == TypeKind.Error) return;

        // Нормализация constructed-generic к определению: Repo<string> → Repo<T>,
        // иначе FQN цели не совпадёт с Id узла-определения.
        var targetId = NodeExtractor.GetId(named.OriginalDefinition);
        if (!projectIds.Contains(targetId)) return; // внешний тип (BCL) — отбрасываем
        if (sourceId == targetId) return; // self-loop не информативен

        var key = $"{sourceId}\x1F{targetId}\x1F{relation}";
        edges[key] = new CodeGraphEdge
        {
            Source = sourceId,
            Target = targetId,
            Relation = relation,
            Confidence = EdgeConfidence.Extracted,
        };
    }
}
