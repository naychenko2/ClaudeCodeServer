using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож прямого вызова чистых функций <c>ProjectAccess.Can/Check</c> и <c>SessionAccess.CanSee</c>
/// (дефект R-S1-3a; <c>CanSee</c> сторож не видел — FINAL-1, Spend.Badge ходил мимо гейта): роль
/// участника общего проекта, рубильник <c>SharedProjects:Enabled</c> и запрет записи участнику в S1
/// решает ТОЛЬКО <c>IProjectAccess</c> (<c>ProjectAccessService</c>). Контроллер или вертикаль, зовущие
/// <c>ProjectAccess.Can</c> напрямую, дали бы редактору запись в проект владельца мимо гейта.
///
/// Allow-list <c>DirectProjectAccessAllowList.txt</c> — по ФАЙЛУ со счётчиком (как у
/// <c>ProjectActionCallerKeyGuardTests</c>): новый вызов в известном или новом файле — красное,
/// мёртвая строка снимка — тоже. Сторож текстовый; комментарии из строк вырезаются.
/// </summary>
public class DirectProjectAccessGuardTests
{
    private const string AllowListPath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/DirectProjectAccessAllowList.txt";

    private static readonly Regex DirectCall = new(@"\b(ProjectAccess\s*\.\s*(Can|Check)|SessionAccess\s*\.\s*CanSee)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void ПрямойВызовProjectAccess_ВнеСнимка_Отсутствует()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var allow = ReadAllowList(root);

        code.Count.Should().BeGreaterThan(3, "сторож обязан видеть известные места прямого вызова");

        var outside = code
            .Where(kv => kv.Value > allow.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в коде {kv.Value}, в снимке {allow.GetValueOrDefault(kv.Key)}")
            .ToList();
        outside.Should().BeEmpty(
            "решение о доступе принимает IProjectAccess.Resolve/ResolveSession (роль, рубильник, запрет записи " +
            "участнику); прямой ProjectAccess.Can/Check обходит гейт:\n" + string.Join("\n", outside));
    }

    [Theory]
    [InlineData("if (!SessionAccess.CanSee(session, project, id))", true)]
    [InlineData("SessionAccess . CanSee (s, null, u)", true)]
    [InlineData("ProjectAccess.Check(project, userId)", true)]
    [InlineData("access.ResolveSession(userId, sessionId)", false)]
    [InlineData("var canSee = CanSee(session)", false)]
    public void Шаблон_ЛовитПрямыеВызовыЧистыхФункцийДоступа(string line, bool expected) =>
        DirectCall.IsMatch(line).Should().Be(expected);

    [Fact]
    public void Снимок_НеСодержитСтрокКоторыхНетВКоде()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var stale = ReadAllowList(root)
            .Where(kv => kv.Value > code.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в снимке {kv.Value}, в коде {code.GetValueOrDefault(kv.Key)}")
            .ToList();
        stale.Should().BeEmpty("место переведено на IProjectAccess — уменьшите счётчик снимка:\n" + string.Join("\n", stale));
    }

    private static Dictionary<string, int> ScanCode(string root)
    {
        var result = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("Tests") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var count = File.ReadAllLines(file)
                .Count(l => DirectCall.IsMatch(Regex.Replace(l, @"//.*$", "")));
            if (count > 0) result[rel] = count;
        }
        return result;
    }

    private static Dictionary<string, int> ReadAllowList(string root)
    {
        var result = new Dictionary<string, int>();
        foreach (var raw in File.ReadAllLines(Path.Combine(root, AllowListPath)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|');
            result[parts[0]] = int.Parse(parts[1]);
        }
        return result;
    }

    // .git бывает файлом (worktree), поэтому проверяем и каталог, и файл
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
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory + " не встретил .git и backend/.");
    }
}
