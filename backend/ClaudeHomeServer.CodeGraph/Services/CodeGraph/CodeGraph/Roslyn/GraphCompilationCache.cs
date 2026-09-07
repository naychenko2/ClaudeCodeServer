using Microsoft.CodeAnalysis;

namespace ClaudeHomeServer.Services.CodeGraph.Roslyn;

/// <summary>
/// In-memory кэш последнего построения графа для rootPath: Compilation-деревья + граф.
/// Позволяет UpdateAsync пересобирать только changedFiles вместо полного rebuild.
/// Не персистентный — живёт пока жив Singleton-провайдер; при рестарте инкремент падает до BuildAsync.
/// </summary>
public sealed class GraphCompilationCache
{
    /// <summary>Последний построенный граф (узлы + рёбра).</summary>
    public required Core.CodeGraph Graph { get; init; }

    /// <summary>Деревья последней Compilation: relPath → SyntaxTree (для переиспользования unchanged).</summary>
    public required IReadOnlyDictionary<string, SyntaxTree> Trees { get; init; }

    /// <summary>Имя сборки Compilation (маркер «своего» проекта).</summary>
    public required string ProjectAssemblyName { get; init; }
}
