using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож ключа хранилищ в точках доступа (хвост M-11 карточки 0576ba2b, S1-3a): в
/// <c>Controllers</c>/<c>Hubs</c>, в файлах с <c>[ProjectScope]</c> или <c>IProjectAccess</c>,
/// id ВЫЗЫВАЮЩЕГО (<c>UserId</c>/<c>userId</c>/<c>CallerUserId</c>/<c>CurrentUserId</c>) не должен
/// появляться аргументом вызова сверх снимка. С S2c-6 в словарь входят и id АКТОРА хода
/// (<c>actorUserId</c>/<c>ActorUserId</c>/<c>authorUserId</c>/<c>actor</c> — локальная переменная тулсетов), а область включает платформенные
/// MCP-тулсеты (<c>Services/Mcp/Http/*Toolset*.cs</c>, <c>NotesToolset</c>). В общем проекте по такому ключу хранилища
/// (задачи, заметки, память, досье) молча развели бы данные участника и владельца; ключ
/// проектных действий — только <c>TenantOwnerId</c> из <c>RequestScope</c>.
///
/// Снимок <c>ProjectActionCallerKeyAllowList.txt</c> — по ФАЙЛУ со счётчиком строк (как у
/// <c>OwnerIdInlineGuardTests</c>): новая строка с id вызывающего в известном файле или в новом
/// файле — красное, мёртвая строка снимка — тоже. Сторож текстовый, читает исходники с того
/// же дерева, что собирается; образец — <c>OwnerIdInlineGuardTests</c>.
/// </summary>
public class ProjectActionCallerKeyGuardTests
{
    private const string AllowListPath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/ProjectActionCallerKeyAllowList.txt";

    // id вызывающего целым аргументом: «(UserId)», «(x, UserId, y)», «(UserId, …)»
    private static readonly Regex CallerAsArgument = new(
        @"[(,]\s*(UserId|userId|CallerUserId|callerUserId|CurrentUserId|actorUserId|ActorUserId|authorUserId|actor)\s*[,)]", RegexOptions.Compiled);

    private static readonly Regex ScopeMarker = new(@"ProjectScope|IProjectAccess", RegexOptions.Compiled);

    [Fact]
    public void ПроектныеТочкиДоступа_ИдВызывающегоАргументомВнеСнимка_Отсутствует()
    {
        var root = FindRepoRoot();
        var code = ScanCode(root);
        var allow = ReadAllowList(root);

        // Вакуумно-зелёный сторож хуже отсутствующего: файлов с областью запроса десятки
        code.Count.Should().BeGreaterThan(10, "сторож обязан видеть контроллеры и хабы с ProjectScope");

        var outside = code
            .Where(kv => kv.Value > allow.GetValueOrDefault(kv.Key))
            .Select(kv => $"  • {kv.Key}: в коде {kv.Value}, в снимке {allow.GetValueOrDefault(kv.Key)}")
            .ToList();
        outside.Should().BeEmpty(
            "id вызывающего аргументом в проектном действии — кандидат в ключ хранилищ; ключ — только " +
            "TenantOwnerId из RequestScope. Осознанное личное место — поднять счётчик снимка с вердиктом в дифе:\n"
            + string.Join("\n", outside));
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
        stale.Should().BeEmpty("место переведено на TenantOwnerId — уменьшите счётчик снимка:\n" + string.Join("\n", stale));
    }

    // Платформенные MCP-тулсеты: Services/Mcp/Http/*Toolset*.cs и тулсет заметок вертикали Notes
    private static bool IsMcpToolset(string rel) =>
        (rel.Contains("/Services/Mcp/Http/") && Path.GetFileName(rel).Contains("Toolset"))
        || rel.EndsWith("ClaudeHomeServer.Notes/Services/Mcp/NotesToolset.cs", StringComparison.Ordinal);

    // путь от корня репозитория → число строк с id вызывающего аргументом
    private static Dictionary<string, int> ScanCode(string root)
    {
        var result = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("Tests") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var isMcpToolset = IsMcpToolset(rel);
            if (!isMcpToolset && !rel.Contains("/Controllers/") && !rel.Contains("/Hubs/")) continue;

            var lines = File.ReadAllLines(file).Select(l => Regex.Replace(l, @"//.*$", "")).ToList();
            // MCP-тулсеты (S2c-6) сканируются целиком: они ходят с токеном арендатора, и id актора
            // там тоже не должен стать ключом хранилищ; маркер области для них не требуется
            if (!isMcpToolset && !lines.Any(l => ScopeMarker.IsMatch(l))) continue;
            var count = lines.Count(l => CallerAsArgument.IsMatch(l));
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
