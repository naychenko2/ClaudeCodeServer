using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Сторож синхронности двух списков: бэкенд (ProxyController.AllowedHosts) и фронтенд
// (MarkdownContent.PROXY_ALLOWED_HOSTS). Списки живут в разных языках и раньше
// синхронизировались по комментарию «синхронизировать с AllowedHosts» — следующий домен
// неизбежно добавлялся в одном месте. Тест читает оба файла, вытаскивает строки в
// кавычках между [ и ] после имени переменной/поля и сравнивает множества. Расхождение
// роняет тест с понятным diff'ом.
public class ProxyAllowedHostsSyncTests
{
    private static readonly string[] BackendCandidateRoots =
    [
        // из bin/Debug/netX.0/ в корне репо
        "..", "..", "..", "..", "..",
    ];

    private static string RepoRoot()
    {
        // AppContext.BaseDirectory = .../backend/ClaudeHomeServer.Tests/bin/Debug/netX.0/
        // Корень репо — на 5 уровней выше по структуре солюшна, но надёжнее искать по маркеру.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        dir.Should().NotBeNull("не нашли корень репо по CLAUDE.md от AppContext.BaseDirectory");
        return dir!.FullName;
    }

    private static string BackendListPath() =>
        Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Controllers", "ProxyController.cs");

    private static string FrontendListPath() =>
        Path.Combine(RepoRoot(), "frontend", "src", "components", "chat", "MarkdownContent.tsx");

    [Fact]
    public void BackendAllowedHosts_иFrontedProxyAllowedHosts_синхронны()
    {
        var backend = ExtractHosts(BackendListPath(),
            @"private\s+static\s+readonly\s+string\[\]\s+AllowedHosts\s*=\s*\[(.+?)\];",
            "AllowedHosts");
        var frontend = ExtractHosts(FrontendListPath(),
            @"const\s+PROXY_ALLOWED_HOSTS\s*=\s*\[(.+?)\];",
            "PROXY_ALLOWED_HOSTS");

        backend.Should().NotBeEmpty($"в {BackendListPath()} должен быть непустой список AllowedHosts");
        frontend.Should().NotBeEmpty($"в {FrontendListPath()} должен быть непустой список PROXY_ALLOWED_HOSTS");

        var backendSet = new HashSet<string>(backend, StringComparer.OrdinalIgnoreCase);
        var frontendSet = new HashSet<string>(frontend, StringComparer.OrdinalIgnoreCase);

        var onlyInBackend = backendSet.Except(frontendSet).OrderBy(h => h).ToArray();
        var onlyInFrontend = frontendSet.Except(backendSet).OrderBy(h => h).ToArray();

        var diff = string.Empty;
        if (onlyInBackend.Length > 0)
            diff += "\nТолько в backend (ProxyController.AllowedHosts):\n  - " + string.Join("\n  - ", onlyInBackend);
        if (onlyInFrontend.Length > 0)
            diff += "\nТолько во frontend (PROXY_ALLOWED_HOSTS):\n  - " + string.Join("\n  - ", onlyInFrontend);

        diff.Should().BeEmpty(
            "списки AllowedHosts (бэкенд) и PROXY_ALLOWED_HOSTS (фронтенд) должны совпадать как множества." + diff);
    }

    // Хелпер: вытащить все строковые литералы в кавычках между [ и ] после заданной переменной/поля.
    // Устойчив к многострочности и однострочным комментариям внутри блока.
    private static List<string> ExtractHosts(string path, string declarationPattern, string fieldName)
    {
        var text = File.ReadAllText(path);
        var match = Regex.Match(text, declarationPattern, RegexOptions.Singleline);
        match.Success.Should().BeTrue(
            $"не нашли объявление {fieldName} в {path} — паттерн устарел, обнови тест");

        var body = match.Groups[1].Value;
        var hosts = new List<string>();
        foreach (Match m in Regex.Matches(body, "[\"']([^\"']+)[\"']"))
            hosts.Add(m.Groups[1].Value);

        return hosts;
    }
}
