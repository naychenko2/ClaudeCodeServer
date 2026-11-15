using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож явного доступа к проекту «Личное» (ADR-040 §2.3, шаг Ф2-1). «Личного» нет в
/// <c>ProjectManager</c>, и пользовательский id до него не доходит; единственные пути к нему —
/// <c>GetByIdIncludingPersonal</c> и <c>GetAllPersonal</c>. Новый вызов вне allow-list — красное:
/// его надо осознанно добавить, подтвердив, что id приходит из серверного состояния.
/// Устройство как у <see cref="DirectProjectAccessGuardTests"/>: файл со счётчиком, текстовый скан.
/// </summary>
public class PersonalProjectAccessGuardTests
{
    private const string AllowListPath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/PersonalProjectAccessAllowList.txt";

    // Точка обязательна: объявления методов (без точки) вызовами не считаются. Скобки не требуем: группа методов
    // («personal.GetAllPersonal» как делегат) — тоже путь к «Личному». \s пересекает перенос строки, поэтому
    // скан идёт по тексту файла целиком: вызов, разорванный по строкам («personal\n    .GetAllPersonal()»), виден
    private static readonly Regex Call = new(
        @"\.\s*(GetByIdIncludingPersonal|GetAllPersonal)\b", RegexOptions.Compiled);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void ВызовыДоступаКЛичному_ВнеСнимка_Отсутствуют()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var allow = ReadAllowList(root);

        var outside = code
            .Where(kv => kv.Value > allow.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в коде {kv.Value}, в снимке {allow.GetValueOrDefault(kv.Key)}")
            .ToList();
        outside.Should().BeEmpty(
            "«Личное» достижимо только из осознанных мест (ADR-040 §2.1); новый вызов добавляется в " +
            "PersonalProjectAccessAllowList.txt вместе с вердиктом, откуда берётся id:\n" + string.Join("\n", outside));
    }

    [Fact]
    public void Снимок_НеСодержитСтрокКоторыхНетВКоде()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var stale = ReadAllowList(root)
            .Where(kv => kv.Value > code.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в снимке {kv.Value}, в коде {code.GetValueOrDefault(kv.Key)}")
            .ToList();
        stale.Should().BeEmpty("вызова больше нет — уменьшите счётчик снимка:\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("personal.GetByIdIncludingPersonal(id)", true)]
    [InlineData("_personal . GetAllPersonal ()", true)]
    [InlineData("Project? GetByIdIncludingPersonal(string id);", false)]
    [InlineData("public IReadOnlyCollection<Project> GetAllPersonal()", false)]
    [InlineData("personal.GetPersonalOf(ownerId)", false)]
    [InlineData("var all = personal.GetAllPersonal;", true)]
    [InlineData("personal\n    .GetByIdIncludingPersonal(id)", true)]
    [InlineData("personal.\n    GetAllPersonal\n    ()", true)]
    [InlineData("personal?.GetAllPersonal()", true)]
    [InlineData("// personal.GetAllPersonal()", false)]
    [InlineData("/* personal\n.GetAllPersonal() */", false)]
    public void Шаблон_ЛовитВызовыНоНеОбъявления(string text, bool expected) =>
        CountCalls(text).Should().Be(expected ? 1 : 0);

    // Вызовы в тексте файла целиком, без комментариев (в том числе блочных, растянутых на несколько строк)
    private static int CountCalls(string text) =>
        Call.Matches(LineComment.Replace(BlockComment.Replace(text, ""), "")).Count;

    private static Dictionary<string, int> ScanCode(string root)
    {
        var result = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("Tests") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var count = CountCalls(File.ReadAllText(file));
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
