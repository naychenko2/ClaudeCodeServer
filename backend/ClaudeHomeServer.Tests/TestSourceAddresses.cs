using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests;

/// <summary>
/// Адрес «Файл.cs:строка» в тексте исходника: разбор, резолв пути, число строк файла.
/// Общий для двух сторожей: SubsystemDisableMatrixTests (причины KnownViolations обязаны вести
/// в существующее место) и SourceLinkGuardTests (комментарии исходников — хвост ревью e013789e:
/// в копию вернули протухшую ссылку Program.cs:621-622, и ни один тест этого не увидел).
/// Разбор адреса и резолв пути были приватными в SubsystemDisableMatrixTests; вынесены сюда,
/// чтобы второй сторож не писал их копией (постановка S3-СТОРОЖ-ССЫЛКИ).
///
/// Число строк файла кешируется: один и тот же файл зовут несколько строк одного словаря и
/// несколько комментариев дерева; без кеша прогон сторожа перечитывал бы SessionManager.cs
/// десятки раз. Кеши — ConcurrentDictionary: тестовые классы идут параллельно.
/// </summary>
internal static class TestSourceAddresses
{
    /// Полный адрес: путь.cs:строка или путь.cs:строка-строка. Та форма, что встречается в
    /// комментариях исходников, — и единственная, которую мерит SourceLinkGuardTests:
    /// короткая ссылка «Имя (:NN)» в комментарии исходника не имеет владельца
    /// (это может быть и другой файл, и этот же) и мерится нечем.
    public const string FullAddressPattern = @"(?<path>[\w.\-/]+\.cs):(?<line>\d+)(?:-(?<lineTo>\d+))?";

    public static readonly Regex FullAddress = new(FullAddressPattern, RegexOptions.Compiled);

    /// Адрес причины KnownViolations в трёх формах; порядок альтернатив важен: полный адрес
    /// снимается целым до того, как его хвост разберётся короткой ссылкой — вместе со своим
    /// диапазоном «:NN-MM», обе границы которого меряются так же, как у короткой ссылки
    /// (п. 2 хвоста eaf0be70). Голое имя файла — не адрес, а владелец: оно задаёт файл,
    /// к которому относятся идущие после него короткие ссылки «Имя (:NN)» и «Имя (:NN-MM)»
    /// (п. 3 хвоста d3e0ddf8).
    public static readonly Regex DictionaryAddress = new(
        FullAddressPattern + @"|(?<bare>[\w.\-/]+\.cs)|(?<short>[A-Za-z_]\w*)\s*\(:\s*(?<from>\d+)(?:-(?<to>\d+))?",
        RegexOptions.Compiled);

    /// Путь из адреса → файл дерева. Порядок: как записан (от корня репозитория), затем
    /// коротким путём Main-проекта (Controllers/…, Program.cs) — обе формы встречаются в
    /// словарях и комментариях и ведут к одному файлу; затем — единственный файл дерева с таким
    /// путём или с таким именем. Третий шаг нужен сторожу комментариев: они называют чужие
    /// вертикали коротким именем (TurnEventBus.cs, PromptSectionContributor.cs); для
    /// KnownViolations он ничего не меняет — его адреса резолвятся первыми двумя формами.
    /// Имя, по которому в дереве больше одного файла, не резолвится: мерить нечем, адрес красный.
    public static string? Resolve(string path)
    {
        var parts = path.Split('/');
        string?[] direct = [TryRepo(parts), TryRepo(["backend", "ClaudeHomeServer", ..parts])];
        var hit = direct.FirstOrDefault(c => c is not null);
        if (hit is not null) return hit;
        return UniqueOf(TreeByPath.Value.GetValueOrDefault(path))
               ?? UniqueOf(TreeByName.Value.GetValueOrDefault(parts[^1]));
    }

    /// Число строк файла дерева. Полные пути, полученные <see cref="Resolve"/>.
    public static int LineCount(string fullPath) =>
        LineCounts.Value.GetOrAdd(fullPath, static p => File.ReadLines(p).Count());

    private static string? TryRepo(string[] parts)
    {
        try { return TestRepoFiles.RepoFile(parts); }
        catch (InvalidOperationException) { return null; }
    }

    private static string? UniqueOf(List<string>? files) => files is { Count: 1 } ? files[0] : null;

    // Индекс .cs-файлов дерева: по любому суффиксу пути и по имени файла. obj/ и bin/ —
    // порождение сборки (GlobalUsings.g.cs, AssemblyInfo.cs), в индекс не входят: именем из
    // порождения нельзя измерить ссылку комментария. Lazy — построение один раз на процесс,
    // паралельные тесты не строят индекс дважды.
    private static readonly Lazy<Dictionary<string, List<string>>> TreeByPath = new(() => SuffixIndex(BuildTree()));
    private static readonly Lazy<Dictionary<string, List<string>>> TreeByName = new(() => NameIndex(BuildTree()));

    private static readonly Lazy<ConcurrentDictionary<string, int>> LineCounts = new();

    private static List<string> BuildTree()
    {
        var root = TestRepoFiles.RepoRoot;
        var files = new List<string>();
        void Walk(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs")) files.Add(f);
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "obj" or "bin" or ".git" or "node_modules") continue;
                Walk(sub);
            }
        }
        Walk(Path.Combine(root, "backend"));
        return files;
    }

    private static Dictionary<string, List<string>> SuffixIndex(List<string> files)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var parts = f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < parts.Length; i++)
                AddTo(index, string.Join('/', parts[i..]), f);
        }
        return index;
    }

    private static Dictionary<string, List<string>> NameIndex(List<string> files)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in files)
            AddTo(index, Path.GetFileName(f), f);
        return index;
    }

    private static void AddTo(Dictionary<string, List<string>> index, string key, string file)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(file);
    }
}
