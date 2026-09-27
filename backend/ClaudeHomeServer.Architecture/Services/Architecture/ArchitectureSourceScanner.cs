using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Читает дерево проекта: проекты-маркеры (.csproj с их ProjectReference, package.json)
/// и заголовки подсистем (<c>Title =&gt; "…"</c> у реализаций IAppSubsystem).
/// Служебные каталоги отсекаются тем же правилом, что и в свёртке
/// (<see cref="ArchitecturePathFolding.IsIgnored"/>).
/// </summary>
public static partial class ArchitectureSourceScanner
{
    /// <summary>Потолок обхода — защита от гигантских деревьев (монорепы, случайный home).</summary>
    public const int MaxDirectories = 20_000;

    /// <summary>
    /// Симлинк или junction: в обход не идём — петля (ссылка на предка) гоняла бы обход до
    /// потолка, а ссылка наружу утащила бы чужое дерево в модель проекта.
    /// Не прочитались атрибуты — тоже не идём.
    /// </summary>
    public static bool IsReparsePoint(string dir)
    {
        try
        {
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Имя интерфейса-маркера подсистемы (контракт CCS; в чужих проектах просто не найдётся).</summary>
    public const string SubsystemMarkerName = "IAppSubsystem";

    public static IReadOnlyList<SourceUnit> ScanUnits(string root, CancellationToken ct = default)
    {
        var fullRoot = Path.GetFullPath(root);
        var units = new List<SourceUnit>();
        var stack = new Stack<string>();
        stack.Push(fullRoot);
        var visited = 0;

        while (stack.Count > 0 && visited++ < MaxDirectories)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var relDir = ArchitecturePathFolding.Normalize(Path.GetRelativePath(fullRoot, dir));
            if (relDir == ".") relDir = "";

            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    units.Add(ReadCsproj(fullRoot, relDir, file));
                else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && relDir.Length > 0)
                    units.Add(ReadPackageJson(relDir, file));
            }

            foreach (var sub in subdirs)
            {
                var relSub = ArchitecturePathFolding.Normalize(Path.GetRelativePath(fullRoot, sub));
                if (!ArchitecturePathFolding.IsIgnored(relSub) && !IsReparsePoint(sub)) stack.Push(sub);
            }
        }

        // Два маркера в одном каталоге (csproj + package.json) — одна единица: .NET главнее.
        return units
            .GroupBy(u => u.RelDir, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(u => u.Technology == "csharp" ? 0 : 1).First())
            .OrderBy(u => u.RelDir, StringComparer.Ordinal)
            .ToList();
    }

    private static SourceUnit ReadCsproj(string fullRoot, string relDir, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var refs = new List<string>();
        try
        {
            var doc = XDocument.Load(file);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = el.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                // Include пишется с «\» и на Linux — нормализуем до склейки.
                var target = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(file)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                var targetDir = ArchitecturePathFolding.Normalize(
                    Path.GetRelativePath(fullRoot, Path.GetDirectoryName(target)!));
                if (!targetDir.StartsWith("..", StringComparison.Ordinal)) refs.Add(targetDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Битый csproj — единица остаётся, просто без ссылок.
        }

        var isTest = ArchitecturePathFolding.IsTestPath(relDir.Length > 0 ? relDir + "/" + name : name);
        return new SourceUnit(relDir, name, "csharp", isTest,
            refs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList());
    }

    private static SourceUnit ReadPackageJson(string relDir, string file)
    {
        var name = ArchitecturePathFolding.DisplayName(relDir);
        var technology = "javascript";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var rootEl = doc.RootElement;
            if (rootEl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString()))
                name = n.GetString()!;
            if (HasDependency(rootEl, "react")) technology = "react";
            else if (HasDependency(rootEl, "typescript")) technology = "typescript";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Нечитаемый package.json — единица с именем каталога.
        }
        return new SourceUnit(relDir, name, technology, ArchitecturePathFolding.IsTestPath(relDir), []);
    }

    private static bool HasDependency(JsonElement root, string package) =>
        new[] { "dependencies", "devDependencies", "peerDependencies" }.Any(section =>
            root.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object
            && deps.TryGetProperty(package, out _));

    /// <summary>
    /// Заголовки подсистем: типы, реализующие маркер (в том числе через интерфейсы-наследники
    /// вроде IAppPhaseSubsystem), и их <c>Title =&gt; "…"</c> из исходника.
    /// Файл читается через <paramref name="readFile"/> (относительный путь → текст или null).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadSubsystemTitles(
        CodeSnapshotInput snapshot, Func<string, string?> readFile)
    {
        var byId = snapshot.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var implements = snapshot.Edges.Where(e => e.Relation == "Implements").ToList();

        var markers = snapshot.Types
            .Where(t => t.Name == SubsystemMarkerName && t.Kind == "Interface")
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (markers.Count == 0) return new Dictionary<string, string>();

        // Интерфейсы-наследники маркера — тоже маркеры (до неподвижной точки).
        bool grew;
        do
        {
            grew = false;
            foreach (var e in implements)
                if (markers.Contains(e.Target) && byId.TryGetValue(e.Source, out var s)
                    && s.Kind == "Interface" && markers.Add(s.Id))
                    grew = true;
        } while (grew);

        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in implements.Where(e => markers.Contains(e.Target)))
        {
            if (!byId.TryGetValue(e.Source, out var type) || type.Kind == "Interface") continue;
            if (titles.ContainsKey(type.Id) || readFile(type.SourceFile) is not { } text) continue;
            var m = TitleRegex().Match(text);
            if (m.Success) titles[type.Id] = m.Groups[1].Value;
        }
        return titles;
    }

    [GeneratedRegex("""\bTitle\s*=>\s*"([^"\r\n]+)"\s*;""")]
    private static partial Regex TitleRegex();
}
