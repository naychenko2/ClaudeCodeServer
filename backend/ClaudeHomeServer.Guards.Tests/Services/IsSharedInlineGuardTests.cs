using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож инлайновых копий признака «проект общий».
///
/// Зачем: «общий проект» определяется ОДНИМ местом — <c>ProjectAccess.IsShared</c>
/// (ProjectAccess.cs:95): по нему различают личное и общее пространство (TurnFrame) и по нему же
/// отказывают «Приватным можно сделать только чат общего проекта». Инлайновая копия признака
/// (<c>Members is { Count: > 0 }</c>) читается отдельно от определения: смена определения
/// (например «общим считать проект с несколькими участниками») оставила бы её прежней, и в одном
/// месте проекта правило действовало бы по-старому. Шаги W4-T8…W4-T10 свели копии к точке
/// (GitController, GitAutoCommitService, HumanEditCommitter, ProjectMembershipService,
/// McpServersController, WorkspaceToolset); сторож не даёт им вернуться.
///
/// Что ищется: <c>Members is { Count: > 0 }</c> и <c>Members is not { Count: > 0 }</c> в
/// <c>backend/**/*.cs</c> вне тестовых проектов, с ГРАНИЦЕЙ СЛОВА перед <c>Members</c>:
/// <c>FormerMembers is { Count: > 0 }</c> — свой список со своей формой (Project.cs:201-207),
/// не признак общего проекта; <c>Members?.Count > 0</c> — не форма определения. Комментарии из
/// строк вырезаются (по образцу DirectProjectAccessGuardTests).
///
/// Сама точка исключена из копий по имени файла, но её присутствие обязательно: «копий нет» без
/// определения в коде значило бы только то, что сторож ничего не видит.
/// </summary>
public class IsSharedInlineGuardTests
{
    private const string DefinitionPath =
        "backend/ClaudeHomeServer.Core/Services/Access/ProjectAccess.cs";

    private static readonly Regex InlineCopy = new(
        @"\bMembers is (not )?\{ Count: > 0 \}", RegexOptions.Compiled);

    // Сколько примеров показать при падении: человеку нужен образец, а не весь список.
    private const int MaxExamples = 10;

    [Fact]
    public void ИнлайновыеКопииПризнакаОбщегоПроекта_ВнеТочки_Отсутствуют()
    {
        var root = FindRepoRoot();
        var findings = ScanCode(root);

        findings.Should().NotBeEmpty("сторож обязан читать настоящее дерево репозитория");
        findings.Any(f => f.Path == DefinitionPath).Should().BeTrue(
            $"определение обязано оставаться в точке ({DefinitionPath}): «копий нет» без неё " +
            "означало бы только то, что сторож ничего не видит");

        var copies = findings.Where(f => f.Path != DefinitionPath).ToList();
        var sb = new StringBuilder();
        foreach (var f in copies.Take(MaxExamples))
            sb.AppendLine($"  • {f.Path}:{f.Line} {f.Text}");
        if (copies.Count > MaxExamples)
            sb.AppendLine($"  … ещё {copies.Count - MaxExamples}, показаны не все.");

        copies.Should().BeEmpty(
            "«проект общий» определяется одним местом — ProjectAccess.IsShared. Инлайновая копия " +
            "признака живёт отдельно от определения и переживёт его смену молча. Копию можно " +
            "вернуть только вместе с определением — то есть в самой точке.\n" + sb);
    }

    [Theory]
    [InlineData("if (p.Members is not { Count: > 0 }) return null;", true)]
    [InlineData("p.Members is { Count: > 0 } && p.Members.Any(m => m.UserId == UserId)", true)]
    [InlineData("if (!ProjectAccess.IsShared(p)) return null;", false)]
    [InlineData("var former = p.FormerMembers is { Count: > 0 } f", false)]
    [InlineData("if (project?.Members?.Count > 0)", false)]
    [InlineData("// Members is { Count: > 0 } — здесь было до W4-T10", false)]
    public void Шаблон_ЛовитКопиюНоНеСоседниеФормы(string line, bool expected) =>
        InlineCopy.IsMatch(StripComment(line)).Should().Be(expected);

    private static List<Finding> ScanCode(string root)
    {
        var result = new List<Finding>();
        foreach (var file in WalkFiles(Path.Combine(root, "backend")))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!InScope(rel)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StripComment(lines[i]);
                foreach (Match m in InlineCopy.Matches(line))
                    result.Add(new Finding(rel, i + 1, m.Value));
            }
        }
        return result;
    }

    // Тестовые проекты и TestKit вне охвата: их строки с образцом — не код продукта.
    private static bool InScope(string rel)
    {
        if (!rel.EndsWith(".cs", StringComparison.Ordinal)) return false;
        foreach (var seg in rel.Split('/'))
        {
            if (seg is "obj" or "bin" or "node_modules") return false;
            if (seg is "TestKit" or "Tests" || seg.EndsWith(".Tests", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // Комментарий: всё после `//` на строке (та же отсечка, что у DirectProjectAccessGuardTests).
    private static string StripComment(string line) => Regex.Replace(line, @"//.*$", "");

    public sealed record Finding(string Path, int Line, string Text);

    // .git бывает и файлом (git worktree) — исполнители работают именно в worktree.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
            " не встретил каталога с .git и backend/. Сторож работает на настоящем дереве.");
    }

    // Плановый обход (не Directory.EnumerateFiles: в worktree он заходит в репозиторий по
    // симлинкам и натыкался на битые пути вне дерева — образец OwnerIdInlineGuardTests).
    private static IEnumerable<string> WalkFiles(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) yield return f;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (name is "obj" or "bin" or "node_modules") continue;
            foreach (var f in WalkFiles(d)) yield return f;
        }
    }
}
