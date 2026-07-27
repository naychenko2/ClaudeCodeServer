using Microsoft.CodeAnalysis;

namespace ClaudeHomeServer.Services.CodeGraph.Roslyn;

/// <summary>
/// Настройки фильтрации символов: какие сборки считать «внешними» (BCL/фреймворк),
/// какие файлы пропускать как сгенерированные.
/// </summary>
public sealed class SymbolFilterOptions
{
    /// <summary>
    /// Префиксы имён сборок, считаемых внешними (BCL/фреймворк) — не становятся узлами/целями рёбер.
    /// Дефолт: System.*, Microsoft.* и имена corelib/netstandard.
    /// </summary>
    public HashSet<string> ExcludedAssemblyPrefixes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Microsoft",
        "netstandard",
        "mscorlib",
        "WindowsBase",
    };

    /// <summary>
    /// Подстроки в относительном пути файла, по которым он считается сгенерированным
    /// (дизайнеры, source generators, временный артефакт сборки).
    /// </summary>
    public HashSet<string> ExcludedPathFragments { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "/obj/",
        "/bin/",
        "\\obj\\",
        "\\bin\\",
        ".designer.cs",
        ".generated.cs",
        ".g.cs",
        ".assemblyattributes.cs",
    };
}

/// <summary>
/// Фильтр символов/файлов: определение «свой» тип проекта или внешний (BCL),
/// отсев сгенерированных файлов и анонимных/синтетических типов.
/// Стабилен на обеих платформах — работает только с relPath (нормализован через '/').
/// </summary>
public static class CodeSymbolFilter
{
    /// <summary>
    /// Принадлежит ли тип нашему проекту: ContainingAssembly совпадает с целевой сборкой Compilation.
    /// Это надёжнее строковых префиксов: любой тип, объявленный в наших деревьях, помечен именем Compilation.
    /// </summary>
    public static bool IsProjectType(ITypeSymbol? symbol, string projectAssemblyName)
    {
        if (symbol is null) return false;
        // ErrorType — тип не резолвился (нет metadata reference): внешняя зависимость, отбрасываем.
        if (symbol.TypeKind == TypeKind.Error) return false;
        var containing = symbol.ContainingAssembly?.Name;
        if (string.IsNullOrEmpty(containing)) return false;
        return string.Equals(containing, projectAssemblyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя сборки попадает в BCL/фреймворк-исключения (System.*/Microsoft.* и пр.).
    /// </summary>
    public static bool IsExcludedAssembly(string? assemblyName, SymbolFilterOptions options)
    {
        if (string.IsNullOrEmpty(assemblyName)) return true;
        foreach (var prefix in options.ExcludedAssemblyPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Файл сгенерирован/лежит в artefact-папке — пропуск.
    /// relPath ожидается нормализованным с прямым слешом.
    /// </summary>
    public static bool IsExcludedFile(string relPath, SymbolFilterOptions options)
    {
        var lower = relPath.ToLowerInvariant();
        foreach (var frag in options.ExcludedPathFragments)
        {
            if (lower.Contains(frag.ToLowerInvariant())) return true;
        }
        return false;
    }

    /// <summary>
    /// Тип анонимный/синтетический — не индексируем (шум, не имеет стабильного имени).
    /// </summary>
    public static bool IsAnonymousOrSynthetic(INamedTypeSymbol symbol) =>
        symbol.IsAnonymousType
        || symbol.IsTupleType
        || symbol.TypeKind == TypeKind.Error
        || string.IsNullOrEmpty(symbol.Name);
}
