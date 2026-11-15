using System.Text;
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

    // Комментарий вырезается только ВНЕ строковых литералов: «/*» внутри строки не открывает блок,
    // «*/» внутри строки его не закрывает, «//» внутри строки не начинает строковый комментарий.
    // Иначе литерал «"**/*"» открывал «блочный комментарий» и стирал код до следующей «*/» — вызов
    // под таким литералом пропадал из снимка. Сам литерал остаётся в тексте скана: он не комментарий.

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
    // Строковый литерал не маскирует вызов под ним: «/*» внутри строки не открывает блок, и код
    // до следующей «*/» не стирается. Спрятать вызов в настоящем комментарии по-прежнему можно
    [InlineData("var g = \"**/*\";\npersonal.GetAllPersonal();", true)]
    [InlineData("var g = \"**/*\";\npersonal.GetAllPersonal();\n/* настоящий блок */", true)]
    [InlineData("var u = \"https://example.com\"; personal.GetAllPersonal();", true)]
    [InlineData("var g = @\"C:\\images\\**/*\"; personal.GetAllPersonal(); /* блок */", true)]
    [InlineData("var g = \"**/*\";\n// personal.GetAllPersonal()", false)]
    public void Шаблон_ЛовитВызовыНоНеОбъявления(string text, bool expected) =>
        CountCalls(text).Should().Be(expected ? 1 : 0);

    // Вызовы в тексте файла целиком, без комментариев (в том числе блочных, растянутых на несколько строк)
    private static int CountCalls(string text) => Call.Matches(StripComments(text)).Count;

    // Текст без комментариев, строковые литералы — как были. Однопроходный сканер, а не регулярка:
    // у регулярки нет способа отличить «/*» в строке от начала блока, не разбирая саму строку
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (text[i] is '@' or '$' or '"' && TryStringLiteral(text, i, out var end))
            {
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // i — на начале литерала («@», «$» или «"»); при true end — за его закрывающей кавычкой.
    // Обычный "...", verbatim @"..."", raw """…""" (и их префиксы $). Не закрытый литерал — false:
    // строка остаётся в скане как код, дальше скан идёт с того же символа
    private static bool TryStringLiteral(string text, int i, out int end)
    {
        end = i;
        var p = i;
        var verbatim = false;
        while (p < text.Length && (text[p] == '@' || text[p] == '$'))
        {
            verbatim |= text[p] == '@';
            p++;
        }
        if (p >= text.Length || text[p] != '"') return false;

        var quotes = 0;
        while (p < text.Length && text[p] == '"')
        {
            quotes++;
            p++;
        }
        if (quotes >= 3)
        {
            var close = text.IndexOf(new string('"', quotes), p, StringComparison.Ordinal);
            if (close < 0) return false;
            end = close + quotes;
            return true;
        }

        while (p < text.Length)
        {
            if (text[p] == '"')
            {
                if (verbatim && p + 1 < text.Length && text[p + 1] == '"')
                {
                    p += 2;
                    continue;
                }
                end = p + 1;
                return true;
            }
            if (!verbatim && text[p] == '\\')
            {
                p += 2;
                continue;
            }
            p++;
        }
        return false;
    }

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
