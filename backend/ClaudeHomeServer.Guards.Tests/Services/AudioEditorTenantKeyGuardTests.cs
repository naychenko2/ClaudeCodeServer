using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож ключа хранилищ модуля «Звук»: в контроллерах <c>AudioEditor/Controllers</c> идентификатор
/// вызывающего (<c>UserId</c>) допустим только как аргумент гейта (<c>gate.Try…(UserId, …)</c>) и в
/// объявлении самого свойства. Нити, префы, задачи, рабочая папка ключуются <c>Owner(scope)</c>
/// (TenantOwnerId области): в общем проекте участник и владелец расходятся, и возврат к
/// <c>UserId</c> молча разведёт их данные при зелёных тестах «владелец = вызывающий».
/// Сторож текстовый: читает исходник с того же дерева, что собирается.
/// </summary>
public class AudioEditorTenantKeyGuardTests
{
    private const string ControllersDir = "backend/ClaudeHomeServer.AudioEditor/Controllers";

    private static readonly Regex UserIdUse = new(@"\bUserId\b", RegexOptions.Compiled);
    private static readonly Regex Declaration = new(@"\bUserId\s*=>\s*User\.FindFirstValue", RegexOptions.Compiled);
    private static readonly Regex GateArgument = new(@"\bgate\.Try\w+\(\s*UserId\b", RegexOptions.Compiled);

    [Fact]
    public void КонтроллерыЗвука_UserId_ТолькоВГейте()
    {
        var files = Directory.GetFiles(Path.Combine(FindRepoRoot(), ControllersDir), "*.cs");
        // Вакуумно-зелёный сторож хуже отсутствующего: контроллеров в папке больше трёх
        files.Length.Should().BeGreaterThan(3, "сторож обязан видеть контроллеры модуля");

        var gateCalls = 0;
        foreach (var file in files)
        {
            var lines = StripComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!UserIdUse.IsMatch(line) || Declaration.IsMatch(line)) continue;
                if (GateArgument.IsMatch(line)) { gateCalls++; continue; }
                // Вызов гейта мог перенестись: допустим аргумент на строке сразу после «gate.Try…(»
                if (i > 0 && Regex.IsMatch(lines[i - 1], @"\bgate\.Try\w+\(\s*$")) { gateCalls++; continue; }
                Assert.Fail($"{Path.GetFileName(file)}:{i + 1} — UserId вне гейта: «{line.Trim()}». " +
                            "Ключ хранилищ — Owner(scope), а не вызывающий");
            }
        }
        gateCalls.Should().BeGreaterThanOrEqualTo(4, "сторож обязан видеть вызовы гейта");
    }

    private static string StripComments(string text) =>
        Regex.Replace(text, @"//[^\r\n]*", "");

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
