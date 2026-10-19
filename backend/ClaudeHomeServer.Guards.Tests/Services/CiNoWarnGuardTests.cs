using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож: все per-project NoWarn-коды из backend/**/*.csproj обязаны присутствовать
/// в CI-флаге NoWarn (env-переменная в .github/workflows/ci.yml, step Build).
///
/// Причина: MSBuild -p:NoWarn= задаёт NoWarn как ГЛОБАЛЬНОЕ свойство, которое перебивает
/// per-project &lt;NoWarn&gt;$(NoWarn);…&lt;/NoWarn&gt; в .csproj. Если per-project код
/// (CA1822/CA1805/CS1591/CA2255) не включён в CI-флаг, per-project подавление слетает,
/// и под -warnaserror сборка падает. Тест ловит это на стадии коммита, а не на CI.
/// </summary>
public class CiNoWarnGuardTests
{
    [Fact]
    public void PerProjectNoWarn_КодыПрисутствуютВCiФлаге()
    {
        // 1. Собираем множество всех per-project NoWarn-кодов из backend/**/*.csproj
        var repoRoot = FindRepoRoot()
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
                " не встретил каталога с .git и backend/");

        var csprojFiles = Directory.GetFiles(Path.Combine(repoRoot, "backend"), "*.csproj", SearchOption.AllDirectories);
        csprojFiles.Should().NotBeEmpty("в backend/ обязан быть хотя бы один .csproj");

        // Regex: <NoWarn>…CODE1;CODE2…</NoWarn> — извлекаем коды, исключая $(NoWarn)
        var noWarnRegex = new Regex(@"<NoWarn>(.*?)</NoWarn>", RegexOptions.Singleline);
        var perProjectCodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in csprojFiles)
        {
            var content = File.ReadAllText(file);
            foreach (Match m in noWarnRegex.Matches(content))
            {
                var value = m.Groups[1].Value;
                // Убираем $(NoWarn) — это ссылка на родительское значение, не код
                value = value.Replace("$(NoWarn)", "");
                foreach (var code in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (code.Length > 0)
                        perProjectCodes.Add(code);
                }
            }
        }

        perProjectCodes.Should().NotBeEmpty(
            "в backend/**/*.csproj обязаны быть per-project NoWarn-коды " +
            "(сейчас: CA1822/CA1805/CS1591 в HandsBridge, CA2255 в TestKit)");

        // 2. Читаем .github/workflows/ci.yml и достаём значение NoWarn из env-блока step Build
        var ciPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        File.Exists(ciPath).Should().BeTrue("workflow ci.yml обязан существовать");

        var ciContent = File.ReadAllText(ciPath);
        // Ищем строку NoWarn: "..." в env-блоке (YAML: NoWarn: "VALUE")
        var envMatch = Regex.Match(ciContent, @"NoWarn:\s*[""]?(.*?)[""]?\s*$", RegexOptions.Multiline);
        envMatch.Success.Should().BeTrue(
            "в ci.yml (step Build) обязан быть env-блок с NoWarn — " +
            "иначе per-project коды не подавлены и сборка падает");

        var ciNoWarnValue = envMatch.Groups[1].Value;
        var ciCodes = new HashSet<string>(
            ciNoWarnValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        // 3. Каждый per-project код обязан присутствовать в CI-флаге
        var missing = perProjectCodes.Where(c => !ciCodes.Contains(c)).ToList();
        missing.Should().BeEmpty(
            $"per-project NoWarn-коды, отсутствующие в CI-флаге (env NoWarn в ci.yml): " +
            string.Join(", ", missing) +
            $"\nCI-флаг: {ciNoWarnValue}" +
            $"\nPer-project коды: {string.Join(", ", perProjectCodes.OrderBy(x => x))}" +
            "\nПричина: -p:NoWarn= задаёт NoWarn глобально и перебивает per-project <NoWarn> в .csproj; " +
            "без этих кодов в флаге per-project подавления слетают, и под -warnaserror сборка падает.");
    }

    // Подъём от каталога сборки тестов до корня репозитория (тот же приём, что в
    // ProjectMapHygieneGuardTests.RepoScan и McpToolsetStabilityTests).
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
