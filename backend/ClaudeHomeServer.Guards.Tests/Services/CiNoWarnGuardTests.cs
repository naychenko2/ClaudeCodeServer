using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож: в .github/workflows/ci.yml (step Build) не используется -p:NoWarn= —
/// глобальное свойство MSBuild, которое перебивает per-project
/// &lt;NoWarn&gt;$(NoWarn);…&lt;/NoWarn&gt; в .csproj. Env-переменная NoWarn — обычное
/// свойство, per-project коды (CA1822/CA1805/CS1591/CA2255) дописываются сами,
/// поэтому дублировать их в CI-флаге не нужно.
/// </summary>
public class CiNoWarnGuardTests
{
    [Fact]
    public void BuildStep_НеИспользуетГлобальныйФлагNoWarn()
    {
        var repoRoot = FindRepoRoot()
            ?? throw new InvalidOperationException(
                "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
                " не встретил каталога с .git и backend/");

        var ciPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        File.Exists(ciPath).Should().BeTrue("workflow ci.yml обязан существовать");

        var lines = File.ReadAllLines(ciPath);

        // -p:NoWarn — глобальное свойство MSBuild: перебивает per-project <NoWarn>
        // в .csproj. Env-переменная NoWarn — обычное свойство, per-project коды
        // дописываются сами. Если кто-то заменит env на -p:NoWarn= в команде сборки,
        // per-project подавления слетят и под -warnaserror сборка падает.
        // Ищем только в non-comment строках: в комментариях -p:NoWarn= может
        // упоминаться как объяснение, но не как активный флаг.
        var offending = lines
            .Where(l => !l.TrimStart().StartsWith('#') && l.Contains("-p:NoWarn"))
            .ToList();

        offending.Should().BeEmpty(
            "в ci.yml (step Build) не должен использоваться -p:NoWarn= — " +
            "это глобальное свойство MSBuild, которое перебивает per-project " +
            "<NoWarn>$(NoWarn);…</NoWarn> в .csproj. Env-переменная NoWarn — " +
            "обычное свойство, per-project коды дописываются сами. " +
            "Нашли: " + string.Join(" | ", offending));
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
