using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож ключа хранилищ ImageEditor (хвост M-11): в контроллерах редактора id вызывающего
/// (<c>UserId</c>) допустим только для гейта доступа, флагов пользователя и кэша лиц; нити, задачи,
/// префы, шаги, образцы, персонажи и обучение ключуются арендатором — <c>scope.Tenant</c> или
/// <c>project.OwnerId</c>. Иначе в общем проекте данные участника и владельца молча разойдутся,
/// а поведенческий тест с «владелец = вызывающий» такую мутацию не краснит.
/// Сторож текстовый: читает исходник с того же дерева, что собирается.
/// </summary>
public class ImageEditorTenantKeyGuardTests
{
    private const string ControllersPath = "backend/ClaudeHomeServer.ImageEditor/Controllers";

    // Файлы, где UserId целиком личный: админская отмена обучения и сам гейт (он сверяет доступ вызывающего)
    private static readonly string[] Exempt = ["LoraTrainAdminController.cs", "ImageEditScopeGate.cs"];

    private static readonly Regex UserIdUse = new(@"\bUserId\b", RegexOptions.Compiled);

    // Допустимые употребления: объявление, гейт доступа, флаг пользователя, кэш лиц
    private static readonly Regex Allowed = new(
        @"string UserId\s*=>|\bgate\.Try\w+\(\s*UserId\b|\b[Ff]lags\?\.IsEnabled\(\s*UserId\b|\bGetOrDetectAsync\(\s*UserId\b",
        RegexOptions.Compiled);

    [Fact]
    public void КонтроллерыImageEditor_ХранилищаНеКлючуютсяОтВызывающего()
    {
        var dir = Path.Combine(FindRepoRoot(), ControllersPath);
        var files = Directory.GetFiles(dir, "*.cs").Where(f => !Exempt.Contains(Path.GetFileName(f))).ToList();
        files.Count.Should().BeGreaterThanOrEqualTo(6, "сторож обязан видеть контроллеры редактора");

        var violations = new List<string>();
        var seen = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], @"//.*$", "");
                if (!UserIdUse.IsMatch(code)) continue;
                seen++;
                if (!Allowed.IsMatch(code))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        seen.Should().BeGreaterThan(0, "сторож не должен проходить вакуумно: гейты и флаги используют UserId");
        violations.Should().BeEmpty("проектные действия ключуются scope.Tenant / project.OwnerId, а не UserId вызывающего");
    }

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
