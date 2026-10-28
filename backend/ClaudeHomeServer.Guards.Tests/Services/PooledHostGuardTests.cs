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
        var violations = new List<string>();
        var pooled = 0;
        foreach (var dll in HostCategoryGuardTests.TestAssemblies())
            foreach (var cls in HostScan.Hosted(dll, PooledHostName).Where(c => !Exempt.Contains(c.Name)))
            {
                pooled++;
                var used = HostScan.MembersUsedBy(dll, cls.Name);
                violations.AddRange(Forbidden.Where(used.Contains).Select(f => $"{cls.Name}: {f.Split("::")[1]}"));
            }
        pooled.Should().BeGreaterThanOrEqualTo(2, "образцы пула (TasksControllerTests, SessionsControllerTests) обязаны найтись — иначе сторож прошёл вакуумно");
        Assert.True(violations.Count == 0, "Класс в пуле общих хостов меняет конфиг хоста — ему нужен свой хост "
            + "(IClassFixture<свой подкласс TestWebApplicationFactory>):\n" + string.Join("\n", violations));
    }
}
