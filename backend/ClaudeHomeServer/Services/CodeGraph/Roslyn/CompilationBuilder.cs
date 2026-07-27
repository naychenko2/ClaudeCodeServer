using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ClaudeHomeServer.Services.CodeGraph.Roslyn;

/// <summary>
/// Результат сборки Compilation: деревья, привязка relPath→tree и флаг полноты.
/// IsComplete=false означает превышение порога (нужен fallback).
/// </summary>
public sealed record CompilationBuildResult
{
    /// <summary>Compilation проекта (пустая, если IsComplete=false).</summary>
    public required Compilation Compilation { get; init; }

    /// <summary>relPath (через '/') → SyntaxTree. Только включённые файлы.</summary>
    public required IReadOnlyDictionary<string, SyntaxTree> Trees { get; init; }

    /// <summary>Включённые relPath в стабильном порядке.</summary>
    public required IReadOnlyList<string> OrderedRelPaths { get; init; }

    /// <summary>true — Compilation построена полностью; false — превышен порог, нужен fallback.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>Число .cs-файлов найдено всего (до фильтра).</summary>
    public int TotalFilesFound { get; init; }
}

/// <summary>
/// Собирает CSharpCompilation из папки проекта без MSBuild-стека:
/// парсит все .cs-файлы в SyntaxTree через CSharpSyntaxTree.ParseText и создаёт Compilation.
/// MetadataReferences подключаются best-effort (для качества resolution внешних типов),
/// но intra-project resolution работает и без них — фильтр BCL идёт по ContainingAssembly.
/// </summary>
public static class CompilationBuilder
{
    /// <summary>
    /// Имя сборки Compilation — маркер «своего» проекта (SymbolFilter.IsProjectType).
    /// Фиксированная строка: детерминирована и не конфликтует с реальными сборками.
    /// </summary>
    public const string ProjectAssemblyName = "CodeGraph.AnalyzedProject";

    /// <summary>
    /// Построить Compilation из всех .cs папки rootPath.
    /// </summary>
    /// <param name="maxFiles">Порог числа файлов; при превышении IsComplete=false.</param>
    public static CompilationBuildResult Build(
        string rootPath,
        SymbolFilterOptions options,
        int maxFiles,
        CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            return new CompilationBuildResult
            {
                Compilation = EmptyCompilation(),
                Trees = new Dictionary<string, SyntaxTree>(),
                OrderedRelPaths = Array.Empty<string>(),
                IsComplete = false,
                TotalFilesFound = 0,
            };
        }

        // Собираем .cs, отсекая obj/bin/generated по relPath (нормализованному через '/').
        var candidates = new List<string>();
        foreach (var file in EnumerateCsFiles(rootPath))
        {
            if (ct.IsCancellationRequested) break;
            var rel = Rel(rootPath, file);
            if (CodeSymbolFilter.IsExcludedFile(rel, options)) continue;
            candidates.Add(file);
        }

        if (candidates.Count > maxFiles)
        {
            return new CompilationBuildResult
            {
                Compilation = EmptyCompilation(),
                Trees = new Dictionary<string, SyntaxTree>(),
                OrderedRelPaths = Array.Empty<string>(),
                IsComplete = false,
                TotalFilesFound = candidates.Count,
            };
        }

        // Парсинг параллельно: адаптивный DOF, память растёт от деревьев.
        var dof = Math.Max(2, Math.Min(8, candidates.Count / 64 + 2));
        var trees = new ConcurrentDictionary<string, SyntaxTree>(Environment.ProcessorCount, candidates.Count);

        Parallel.ForEach(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = dof, CancellationToken = ct },
            file =>
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var rel = Rel(rootPath, file);
                    var tree = CSharpSyntaxTree.ParseText(text, path: file);
                    trees[rel] = tree;
                }
                catch (IOException)
                {
                    // Файл мог удалиться/заблокироваться — пропускаем, не роняя сборку.
                }
            });

        var ordered = trees.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var orderedTrees = ordered.Select(k => trees[k]).Cast<SyntaxTree>().ToList();

        var compilation = CSharpCompilation.Create(
            ProjectAssemblyName,
            orderedTrees,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // Best-effort metadata references: улучшают resolution внешних базовых типов
        // (нас это напрямую не волнует — BCL фильтруется по assembly), но не мешают.
        var refs = ResolveBaseReferences();
        if (refs.Count > 0)
        {
            compilation = compilation.AddReferences(refs);
        }

        return new CompilationBuildResult
        {
            Compilation = compilation,
            Trees = trees,
            OrderedRelPaths = ordered,
            IsComplete = true,
            TotalFilesFound = candidates.Count,
        };
    }

    /// <summary>
    /// Построить Compilation из конкретного набора (relPath → текст).
    /// Используется тестами и инкрементом: детерминировано, без чтения с диска.
    /// </summary>
    public static Compilation BuildFromSources(
        IEnumerable<(string RelPath, string Text)> sources,
        bool includeBaseReferences = true)
    {
        var trees = new List<SyntaxTree>();
        foreach (var (rel, text) in sources)
        {
            trees.Add(CSharpSyntaxTree.ParseText(text, path: rel));
        }

        var compilation = CSharpCompilation.Create(
            ProjectAssemblyName,
            trees,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        if (includeBaseReferences)
        {
            var refs = ResolveBaseReferences();
            if (refs.Count > 0)
                compilation = compilation.AddReferences(refs);
        }

        return compilation;
    }

    /// <summary>
    /// relPath от rootPath, нормализованный через '/' (стабильно cross-platform).
    /// </summary>
    public static string Rel(string rootPath, string file)
    {
        var rel = Path.GetRelativePath(rootPath, file);
        return rel.Replace('\\', '/');
    }

    private static IEnumerable<string> EnumerateCsFiles(string rootPath)
    {
        // Перечисление через стек — устойчивее к lock-ам каталога, чем рекурсия AllDirectories.
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir, "*.cs");
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var f in files) yield return f;
            foreach (var sd in subdirs) stack.Push(sd);
        }
    }

    private static List<MetadataReference> ResolveBaseReferences()
    {
        var refs = new List<MetadataReference>();
        // System.Private.CoreLib — всегда в рантайме .NET.
        TryAdd(refs, typeof(object).Assembly.Location);
        // System.Runtime / netstandard — best-effort.
        TryAddByName(refs, "System.Runtime");
        TryAddByName(refs, "netstandard");
        return refs;
    }

    private static void TryAdd(List<MetadataReference> refs, string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            refs.Add(MetadataReference.CreateFromFile(path));
    }

    private static void TryAddByName(List<MetadataReference> refs, string assemblyName)
    {
        try
        {
            var asm = System.Reflection.Assembly.Load(assemblyName);
            if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
                refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }
        catch (Exception)
        {
            // На CI/минимальном окружении сборки может не быть — не критично.
        }
    }

    private static Compilation EmptyCompilation() =>
        CSharpCompilation.Create(ProjectAssemblyName);
}
