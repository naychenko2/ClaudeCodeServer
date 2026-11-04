using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож ключа хранилищ VideoEditor (хвост M-11 шага S0-5d): в контроллерах модуля «Видео» id вызывающего
/// (<c>UserId</c>) допустим только в гейте области (<c>gate.*</c> — проверка права) и в личной рабочей
/// папке кадров (<c>_workspace.*Frame*</c>). Нити, префы, задачи, фильмы и клипы ключуются
/// <c>scope.TenantOwnerId</c> (через <c>Tenant(scope)</c>): в общем проекте участник и владелец расходятся,
/// и возврат к <c>UserId</c> молча разведёт их данные при зелёных тестах «владелец = вызывающий».
/// Сторож текстовый: читает исходник с того же дерева, что собирается.
/// </summary>
public class VideoEditorTenantKeyGuardTests
{
    private const string ControllersPath = "backend/ClaudeHomeServer.VideoEditor/Controllers";

    private static readonly Regex UserIdUse = new(@"\bUserId\b", RegexOptions.Compiled);

    [Fact]
    public void КонтроллерыVideoEditor_UserId_ТолькоВГейтеИЛичныхКадрах()
    {
        var dir = Path.Combine(FindRepoRoot(), ControllersPath);
        var violations = new List<string>();
        var allowed = 0;

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], @"//.*$", "");
                if (!UserIdUse.IsMatch(code)) continue;
                // Вызов с ключом-арендатором мог перенестись: строка продолжения допустима,
                // если строка вызова содержит Tenant(scope) (тот же приём, что у аудио-сторожа)
                var prev = i > 0 ? Regex.Replace(lines[i - 1], @"//.*$", "") : "";
                if (Regex.IsMatch(prev, @"\bTenant\(scope\)")) { allowed++; continue; }
                if (IsAllowed(code)) { allowed++; continue; }
                violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        // Вакуумно-зелёный сторож хуже отсутствующего: объявления UserId и вызовы гейта есть всегда
        allowed.Should().BeGreaterThanOrEqualTo(10, "сторож обязан видеть допустимые вхождения UserId");
        violations.Should().BeEmpty("ключ хранилищ — Tenant(scope), а не UserId вызывающего");
    }

    // Объявление свойства, вызов гейта (право доступа) и личная папка кадров владельца.
    // F2-P2e/F2-P2f (98dc1bd57/826d45c07, принято ревью 9d84ad50 → 08e5a4c1): сервисы вертикали
    // получают (ключ-арендатор, актор приватности): id вызывающего допустим аргументом ПОСЛЕ
    // ключа арендатора Tenant(scope) на той же строке (или на строке продолжения вызова,
    // как в AudioEditorTenantKeyGuardTests) — ключ хранилищ остаётся арендатор.
    private static bool IsAllowed(string code) =>
        Regex.IsMatch(code, @"\bUserId\s*=>")
        || Regex.IsMatch(code, @"\bgate\.Try\w+\(\s*UserId\b")
        || Regex.IsMatch(code, @"\b_workspace\.(SaveFrame|FindFrame|FindFrameName)\(\s*UserId\b")
        || AfterTenantKey(code);

    private static bool AfterTenantKey(string code) {
        var t = Regex.Match(code, @"\bTenant\(scope\)");
        var u = Regex.Match(code, @"\bUserId\b");
        return t.Success && u.Success && u.Index > t.Index;
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
