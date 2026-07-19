using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Маппинг путей хост ↔ sandbox-контейнер: проекты песочницы, профили, temp.
public class DockerPathMapperTests
{
    private static DockerPathMapper Make()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sbx_map_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        return new DockerPathMapper(sandbox);
    }

    [Fact]
    public void ToRuntime_ПроектВКорнеПесочницы_МапитсяВProjects()
    {
        var m = Make();
        var host = Path.Combine(Path.GetTempPath(), "x"); // заглушка, заменим ниже
        _ = host;
        // Берём корень из самого маппера через round-trip известной точки
        var runtime = m.ToRuntime(RootProjectsHost(m));
        runtime.Should().Be("/projects");

        var sub = m.ToRuntime(Path.Combine(RootProjectsHost(m), "alice", "app"));
        sub.Should().Be("/projects/alice/app");
    }

    [Fact]
    public void ToHost_ОбратныйМаппинг_Симметричен()
    {
        var m = Make();
        var host = Path.Combine(RootProjectsHost(m), "bob", "proj");
        var runtime = m.ToRuntime(host);
        m.ToHost(runtime).Should().Be(host);
    }

    [Fact]
    public void ToRuntime_ПутьВнеПесочницы_Бросает()
    {
        var m = Make();
        var act = () => m.ToRuntime(@"C:\Windows\System32\secret.txt");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CanMap_ХостовойПроект_True_Посторонний_False()
    {
        var m = Make();
        m.CanMap(Path.Combine(RootProjectsHost(m), "any")).Should().BeTrue();
        m.CanMap(@"C:\Windows").Should().BeFalse();
    }

    [Fact]
    public void CanMap_ПрефиксСоседнейПапки_False()
    {
        // Правило для «…/ClaudeSandbox» не должно матчить «…/ClaudeSandboxBackup»:
        // StartsWith без границы сегмента пробивал бы инвариант «путь вне монтирований».
        var m = Make();
        var sibling = RootProjectsHost(m) + "Backup";
        m.CanMap(Path.Combine(sibling, "x")).Should().BeFalse();
    }

    [Fact]
    public void ToRuntime_ПрефиксСоседнейПапки_Бросает()
    {
        var m = Make();
        var sibling = RootProjectsHost(m) + "Backup";
        var act = () => m.ToRuntime(Path.Combine(sibling, "secret.txt"));
        act.Should().Throw<InvalidOperationException>();
    }

    // Достаём хостовый корень /projects через ToHost (внутренние правила приватны)
    private static string RootProjectsHost(DockerPathMapper m) => m.ToHost("/projects");
}
