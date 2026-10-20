using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож зоны персоны: единственная точка правды — <c>PersonaZone</c> (бэк) и
/// <c>lib/personaZone.ts</c> (фронт). Ручные сравнения <c>Scope</c> с Project/Sphere
/// вне неё не плодим: словарь — файл и ТОЧНОЕ число строк на сегодня, рост краснеет,
/// падение подсказывает уменьшить лимит. Бэкенд-словарь пуст: всё вне exempt — дефект.
/// </summary>
public class PersonaZoneGuardTests
{
    private const string Scope = @"(?:[A-Za-z_][A-Za-z0-9_]*\.)*PersonaScope\.(Project|Sphere)";

    private static readonly Regex BackendPattern = new(
        $@"(==|!=)\s*{Scope}\b|\b{Scope}\s*(==|!=)|\bis\s+(not\s+)?{Scope}\b|\bcase\s+{Scope}\b|=>\s*{Scope}\b|\bScope\s*:\s*{Scope}\b",
        RegexOptions.Compiled);

    private static readonly Regex FrontendPattern = new(
        @"\bscope\s*(===|!==)\s*'(project|sphere)'|'(project|sphere)'\s*(===|!==)\s*[A-Za-z_.]*scope\b",
        RegexOptions.Compiled);

    // Сама точка правды и CRUD — вне счёта
    private static readonly HashSet<string> BackendExempt =
    [
        "backend/ClaudeHomeServer.Core/Models/PersonaZone.cs",
        "backend/ClaudeHomeServer/Services/PersonaManager.cs",
        "backend/ClaudeHomeServer/Services/PersonasCrudService.cs",
    ];

    private const string FrontendExempt = "frontend/src/lib/personaZone.ts";

    private static readonly Dictionary<string, int> BackendAllowed = new()
    {
    };

    private static readonly Dictionary<string, int> FrontendAllowed = new()
    {
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    private static bool IsSkipped(string relative)
    {
        var parts = relative.Split('/');
        return parts.Any(p => p is "bin" or "obj" or "node_modules" or "__tests__")
            || parts.Any(p => p.EndsWith(".Tests", StringComparison.Ordinal))
            || relative.Contains(".test.", StringComparison.Ordinal);
    }

    // Относительный путь → число СТРОК с совпадением
    private static Dictionary<string, int> Scan(string root, string dir, string[] exts, Regex pattern,
        Func<string, bool> exempt)
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
        {
            if (!exts.Contains(Path.GetExtension(file))) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsSkipped(relative) || exempt(relative)) continue;
            var count = File.ReadLines(file).Count(l => pattern.IsMatch(l));
            if (count > 0) hits[relative] = count;
        }
        return hits;
    }

    private static Dictionary<string, int> ScanBackend() =>
        Scan(RepoRoot(), "backend", [".cs"], BackendPattern, BackendExempt.Contains);

    private static Dictionary<string, int> ScanFrontend() =>
        Scan(RepoRoot(), Path.Combine("frontend", "src"), [".ts", ".tsx"], FrontendPattern,
            r => r == FrontendExempt);

    private static void Check(Dictionary<string, int> hits, Dictionary<string, int> allowed, string what)
    {
        var violations = hits
            .Where(h => h.Value > allowed.GetValueOrDefault(h.Key))
            .Select(h => $"{h.Key}: {h.Value} (лимит {allowed.GetValueOrDefault(h.Key)})")
            .ToList();
        violations.Should().BeEmpty(
            $"зону персоны ({what}) сравнивают только через единую точку правды; "
            + "новое место — не плодить, а звать PersonaZone");

        var shrunk = allowed
            .Where(a => hits.GetValueOrDefault(a.Key) < a.Value)
            .Select(a => $"{a.Key}: лимит {a.Value}, найдено {hits.GetValueOrDefault(a.Key)}")
            .ToList();
        // Меньше лимита — не ошибка, но дыру на будущее надо закрыть
        if (shrunk.Count > 0)
            Console.WriteLine("Уменьшите лимит в allow-list: " + string.Join("; ", shrunk));
    }

    [Fact]
    public void Бэкенд_СравненийЗоныНеБольшеЛимита() =>
        Check(ScanBackend(), BackendAllowed, "бэкенд");

    [Fact]
    public void Фронт_СравненийЗоныНеБольшеЛимита() =>
        Check(ScanFrontend(), FrontendAllowed, "фронт");

    [Theory]
    [InlineData("var x = p.Scope == PersonaScope.Project;")]
    [InlineData("if (p.Scope != PersonaScope.Sphere) { }")]
    [InlineData("if (p.Scope is PersonaScope.Project) { }")]
    [InlineData("case PersonaScope.Sphere:")]
    [InlineData("if (p is { Scope: PersonaScope.Project }) { }")]
    [InlineData("if (p is not { Scope: ClaudeHomeServer.Models.PersonaScope.Sphere, SphereId: not null }) { }")]
    [InlineData("var z = k switch { 1 => PersonaScope.Sphere, _ => x };")]
    [InlineData("=> p.Scope == ClaudeHomeServer.Models.PersonaScope.Project;")]
    public void Шаблон_ЛовитСравнениеНаБэке(string line) =>
        BackendPattern.IsMatch(line).Should().BeTrue();

    [Theory]
    [InlineData("if (p.scope === 'project') {}")]
    [InlineData("const a = scope !== 'sphere';")]
    public void Шаблон_ЛовитСравнениеНаФронте(string line) =>
        FrontendPattern.IsMatch(line).Should().BeTrue();
}
