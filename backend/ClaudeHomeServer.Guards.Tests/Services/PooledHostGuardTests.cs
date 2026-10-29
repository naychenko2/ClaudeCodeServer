using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож пула общих хостов (фича «Ускорение тестов», Э5): класс в пуле (<c>PooledHost</c> или его
/// наследник-фикстура) не меняет конфиг общего хоста — ни сам, ни своей фикстурой. Правка
/// <c>ExtraServices</c>/<c>ExtraConfig</c> после сборки хоста молча не действует, до сборки — утекает
/// во все классы пула; <c>WithWebHostBuilder</c> поднимает второй хост мимо пула. Такому классу
/// нужен свой хост. Скан — по метаданным собранных тестовых сборок (HostScan).
/// </summary>
public class PooledHostGuardTests
{
    private const string PooledHostName = "ClaudeHomeServer.Tests.Helpers.PooledHost";

    internal static readonly string[] Forbidden =
    [
        "ClaudeHomeServer.Tests.Helpers.TestWebApplicationFactory::set_ExtraServices",
        "ClaudeHomeServer.Tests.Helpers.TestWebApplicationFactory::get_ExtraConfig",
        "Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory`1::WithWebHostBuilder",
    ];

    // Тест самого пула правит конфиг НАРОЧНО и на своём частном экземпляре SharedHostPool,
    // а не на общем хосте процесса — проверяет, что пул такую правку отвергает
    private static readonly HashSet<string> Exempt = ["ClaudeHomeServer.Tests.Fixtures.SharedHostPoolTests"];

    [Fact]
    public void КлассВПуле_НеМеняетКонфигХоста()
    {
        // Сборки независимы — параллельно, как в стороже Host
        var scans = HostCategoryGuardTests.TestAssemblies().ToList().AsParallel().Select(dll =>
            HostScan.Hosted(dll, PooledHostName).Where(c => !Exempt.Contains(c.Name)).Select(cls =>
                (cls.Name, Used: HostScan.MembersUsedBy(dll, cls.Name))).ToList()).ToList();
        var pooled = scans.Sum(s => s.Count);
        var violations = scans.SelectMany(s => s)
            .SelectMany(c => Forbidden.Where(c.Used.Contains).Select(f => $"{c.Name}: {f.Split("::")[1]}")).ToList();
        pooled.Should().BeGreaterThanOrEqualTo(2, "образцы пула (TasksControllerTests, SessionsControllerTests) обязаны найтись — иначе сторож прошёл вакуумно");
        Assert.True(violations.Count == 0, "Класс в пуле общих хостов меняет конфиг хоста — ему нужен свой хост "
            + "(IClassFixture<свой подкласс TestWebApplicationFactory>):\n" + string.Join("\n", violations));
    }
}
