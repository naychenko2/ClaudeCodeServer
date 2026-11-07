using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож человеческих путей владения чатом (разрез 4bc3d04f, строка 5; S2d-3d, после S2d-3a–3c):
/// в <c>/Controllers/</c> и <c>/Hubs/</c> прямой вызов <c>GetOwned(</c> запрещён — владение чатом
/// на человеческом пути обязано проверяться вместе с видимостью (<c>GetOwnedVisible</c> =
/// <c>GetOwned</c> + <c>SessionAccess.CanSee</c>, <c>SessionManager.cs:11233</c>): на чужом приватном
/// чате владелец проекта должен получать отказ, неотличимый от «нет такого чата» (решение 28.10).
/// Default-deny на новые ручки: любой новый <c>GetOwned(</c> в области — красный, пока под него
/// нет причины в снимке. Сам <c>GetOwned</c> сторож не меняет: машинные пути (тулсеты своей
/// сессии-вызывателя, дайджест, продолжение) законно работают через арендатора — но они живут
/// вне Controllers/Hubs.
///
/// Снимок <c>HumanSessionAccessAllowList.txt</c> — по ФАЙЛУ со счётчиком (как у
/// <c>ProjectActionCallerKeyGuardTests</c>): новый вызов в известном или новом файле — красное,
/// мёртвая строка снимка — тоже. Сторож текстовый; комментарии из строк вырезаются.
/// Невакуумность: область подтверждается местами, заменёнными в S2d-3a–3c, — сторож обязан
/// видеть <c>GetOwnedVisible(</c> в контроллерах и хабах; ноль значит сломанную область.
/// </summary>
public class HumanSessionAccessGuardTests
{
    private const string AllowListPath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/HumanSessionAccessAllowList.txt";

    // Ровный вызов GetOwned( — GetOwnedVisible( не ловится: после «GetOwned» идёт «Visible», а не «(».
    private static readonly Regex GetOwnedCall = new(@"\bGetOwned\s*\(", RegexOptions.Compiled);

    private static readonly Regex GetOwnedVisibleCall = new(@"\bGetOwnedVisible\s*\(", RegexOptions.Compiled);

    [Fact]
    public void КонтроллерыИХабы_ПрямойGetOwned_ВнеСнимка_Отсутствует()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var allow = ReadAllowList(root);

        // Вакуумно-зелёный сторож хуже отсутствующего: реальность области подтверждают
        // человеческие ручки, переведённые на GetOwnedVisible в S2d-3a–3c.
        CountVisible(root).Should().BeGreaterThan(10,
            "сторож обязан видеть места, заменённые на GetOwnedVisible в S2d-3a–3c: " +
            "ноль значит сломанную область, и зелёный сторож при этом ничего не значит");

        var outside = code
            .Where(kv => kv.Value > allow.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в коде {kv.Value}, в снимке {allow.GetValueOrDefault(kv.Key)}")
            .ToList();
        outside.Should().BeEmpty(
            "GetOwned( в контроллере/хабе сверяет только арендатора: на чужом приватном чате " +
            "владелец проходит молча. Человеческий путь — GetOwnedVisible (GetOwned + CanSee). " +
            "Осознанное исключение — поднять счётчик снимка с вердиктом в дифе:\n"
            + string.Join("\n", outside));
    }

    [Theory]
    [InlineData("sessions.GetOwned(sid, UserId)", true)]
    [InlineData("spheres.GetOwned(id, UserId)", true)]
    [InlineData("GetOwned (sid, userId)", true)]
    [InlineData("sessions.GetOwnedVisible(sid, UserId)", false)]
    [InlineData("var getOwned = GetOwned;", false)]
    [InlineData("GetOwnership(id)", false)]
    public void Шаблон_ЛовитПрямойGetOwned_НеЛовитGetOwnedVisible(string line, bool expected) =>
        GetOwnedCall.IsMatch(line).Should().Be(expected);

    [Fact]
    public void Снимок_НеСодержитСтрокКоторыхНетВКоде()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var stale = ReadAllowList(root)
            .Where(kv => kv.Value > code.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в снимке {kv.Value}, в коде {code.GetValueOrDefault(kv.Key)}")
            .ToList();
        stale.Should().BeEmpty("место переведено на GetOwnedVisible или удалено — уменьшите счётчик снимка:\n" + string.Join("\n", stale));
    }

    // Файлы области: production-код под /Controllers/ или /Hubs/, без тестов и артефактов сборки
    private static IEnumerable<string> EnumerateScope(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("Tests") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (!rel.Contains("/Controllers/") && !rel.Contains("/Hubs/")) continue;
            yield return file;
        }
    }

    // путь от корня репозитория → число строк с прявым вызовом GetOwned(
    private static Dictionary<string, int> ScanCode(string root)
    {
        var result = new Dictionary<string, int>();
        foreach (var file in EnumerateScope(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var count = File.ReadAllLines(file)
                .Count(l => GetOwnedCall.IsMatch(Regex.Replace(l, @"//.*$", "")));
            if (count > 0) result[rel] = count;
        }
        return result;
    }

    // число строк с GetOwnedVisible( в области — доказательство, что область не пуста
    private static int CountVisible(string root)
    {
        var count = 0;
        foreach (var file in EnumerateScope(root))
            count += File.ReadAllLines(file)
                .Count(l => GetOwnedVisibleCall.IsMatch(Regex.Replace(l, @"//.*$", "")));
        return count;
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
