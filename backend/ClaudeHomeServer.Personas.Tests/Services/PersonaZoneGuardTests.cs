using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож зоны персоны: единственная точка правды — <c>PersonaZone</c> (бэк) и
/// <c>lib/personaZone.ts</c> (фронт). Ручные сравнения <c>Scope</c> с Project/Sphere
/// вне неё не плодим: словарь — файл и ТОЧНОЕ число строк на сегодня, рост краснеет,
/// падение подсказывает уменьшить лимит. Бэкенд-словарь пуст: всё вне exempt — дефект.
/// На бэке домен назван типом (<c>PersonaScope.Project</c>), на фронте — нет: слово
/// <c>'project'</c> живёт и в других областях (область шаблона процесса —
/// <c>WorkflowTemplateScope = 'global' | 'project'</c>), и по одной строке
/// <c>scope === 'project'</c> неотличимо от зоны персоны. Поэтому домен решает файл:
/// сравнение <c>scope</c> с <c>'sphere'</c> — зона персоны всегда (среди операндов с именем
/// <c>scope</c> 'sphere' встречается только в ней: у вики своё <c>kind</c>, его паттерн
/// не берёт), сравнение с <c>'project'</c> — только в файле, который называет саму зону
/// (её путь, её тип, её точка правды или её третье значение). Файл, который сравнивает
/// зону персоны, не назвав ни одного из этих признаков, сторож не увидит: это сознательный
/// пропуск детекции, а не дыра в allow-list — allow-list пуста.
/// </summary>
public class PersonaZoneGuardTests
{
    private const string Scope = @"(?:[A-Za-z_][A-Za-z0-9_]*\.)*PersonaScope\.(Project|Sphere)";

    private static readonly Regex BackendPattern = new(
        $@"(==|!=)\s*{Scope}\b|\b{Scope}\s*(==|!=)|\bis\s+(not\s+)?{Scope}\b|\bcase\s+{Scope}\b|=>\s*{Scope}\b|\bScope\s*:\s*{Scope}\b",
        RegexOptions.Compiled);

    // Операнд фронт-сравнения: имя поля зоны или доступ к нему — с любой стороны.
    private const string FrontLeft = @"\bscope";
    private const string FrontRight = @"[A-Za-z_.]*scope\b";

    private static readonly Regex FrontSphereLine = new(
        $@"{FrontLeft}\s*(===|!==)\s*'sphere'|'sphere'\s*(===|!==)\s*{FrontRight}",
        RegexOptions.Compiled);

    private static readonly Regex FrontProjectLine = new(
        $@"{FrontLeft}\s*(===|!==)\s*'project'|'project'\s*(===|!==)\s*{FrontRight}",
        RegexOptions.Compiled);

    // Домен зоны персоны на фронте: сам раздел, её тип, её точка правды или её третье
    // значение. Ничего из этого в файле нет — 'project' в нём читается как чужая область.
    private const string FrontZonePath = "frontend/src/features/personas/";
    private static readonly string[] FrontZoneWords = ["'sphere'", "PersonaScope", "personaZone"];

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

    // Строки одного файла → сколько из них сравнений зоны персоны. Чистая функция:
    // её проверяют тесты ниже на кусках реальных файлов, без обхода репозитория.
    private static int CountFrontZoneLines(string relative, string text)
    {
        var lines = text.Split('\n');
        var isZoneDomain = relative.StartsWith(FrontZonePath, StringComparison.Ordinal)
            || FrontZoneWords.Any(text.Contains);
        return lines.Count(l => FrontSphereLine.IsMatch(l)
            || (isZoneDomain && FrontProjectLine.IsMatch(l)));
    }

    private static IEnumerable<string> FilesOf(string root, string dir, string[] exts, Func<string, bool> exempt)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
        {
            if (!exts.Contains(Path.GetExtension(file))) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsSkipped(relative) || exempt(relative)) continue;
            yield return file;
        }
    }

    // Относительный путь → число СТРОК с совпадением
    private static Dictionary<string, int> ScanBackend()
    {
        var root = RepoRoot();
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in FilesOf(root, "backend", [".cs"], BackendExempt.Contains))
        {
            var count = File.ReadLines(file).Count(BackendPattern.IsMatch);
            if (count > 0) hits[Path.GetRelativePath(root, file).Replace('\\', '/')] = count;
        }
        return hits;
    }

    private static Dictionary<string, int> ScanFrontend()
    {
        var root = RepoRoot();
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in FilesOf(root, Path.Combine("frontend", "src"), [".ts", ".tsx"],
                     r => r == FrontendExempt))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var count = CountFrontZoneLines(relative, File.ReadAllText(file));
            if (count > 0) hits[relative] = count;
        }
        return hits;
    }

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
    [InlineData("if (p.scope === 'sphere') {}")]
    [InlineData("const a = scope !== 'sphere';")]
    public void Шаблон_ЛовитСравненияЗоныНаФронте(string line) =>
        FrontSphereLine.IsMatch(line).Should().BeTrue();

    [Theory]
    [InlineData("if (p.scope === 'project') {}")]
    [InlineData("const a = scope !== 'project';")]
    public void Шаблон_ЛовитСравненияПроектаНаФронте(string line) =>
        FrontProjectLine.IsMatch(line).Should().BeTrue();

    // Сравнение scope с 'sphere' — сама зона персоны: оно и есть её признак, файл
    // считается файлом зоны и его строки идут в счёт (в чужой области scope с 'sphere' нет).
    [Theory]
    [InlineData("x/Other.tsx", "if (p.scope === 'sphere') return 1;")]
    public void Домен_СферУСчётомВезде(string relative, string text) =>
        CountFrontZoneLines(relative, text).Should().Be(1);

    // 'project' вне файла зоны — чужая область (область шаблона процесса, C1b):
    // строки WorkflowTemplatesEditor.tsx и workflowEditorLogic.ts в счёт не идут.
    [Theory]
    [InlineData("frontend/src/features/workflowTemplates/WorkflowTemplatesEditor.tsx",
        "const scope = sel.scope;\n"
        + "save: t => workflowTemplatesApi.saveTemplate(t, scope === 'project' ? projectId : undefined),\n"
        + "if (scope === 'project') setLocals(drop); else { setGlobals(drop); }")]
    [InlineData("frontend/src/features/workflowTemplates/workflowEditorLogic.ts",
        "const locals = entries.filter(e => e.scope === 'project').map(e => e.template);")]
    public void Домен_ОбластиШаблонаНеСчитаютсяЗоной(string relative, string text) =>
        CountFrontZoneLines(relative, text).Should().Be(0);

    // Файл, назвавший зону (тип / точка правды / значение 'sphere' / свой раздел),
    // сравнивает её руками — это и есть то, чего не должно быть: считаем.
    [Theory]
    [InlineData("x/ExecutorPicker.tsx", "import { isProjectPersona } from '../lib/personaZone';\nif (p.scope === 'project') return false;")]
    [InlineData("x/AnyFeature.tsx", "const scope: PersonaScope = draft.scope;\nif (scope === 'project') return false;")]
    [InlineData("x/AnyFeature.tsx", "const kind: 'global' | 'sphere' = pick;\nif (p.scope === 'project') return false;")]
    [InlineData("frontend/src/features/personas/PersonaForm.tsx", "if (draft.scope === 'project') setZone('project');")]
    public void Домен_ФайлЗоныСчитаетсяВесь(string relative, string text) =>
        CountFrontZoneLines(relative, text).Should().Be(1);

    // Оба значения в файле зоны — обе строки в счёте.
    [Fact]
    public void Домен_ФайлЗоныСчитаетОбеВетви()
    {
        const string text = """
            import { isProjectPersona } from '../../lib/personaZone';
            if (p.scope === 'project') return 1;
            if (p.scope === 'sphere') return 2;
            """;
        CountFrontZoneLines("x/AnyFeature.tsx", text).Should().Be(2);
    }
}
