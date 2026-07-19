using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Домашняя папка пользователя: по умолчанию {база по среде}/{логин}, но её можно
// переопределить абсолютным путём через Projects:UserHomeOverrides (однопользовательский инстанс).
public class UserHomeResolverTests : IDisposable
{
    private readonly string _tempDir;

    public UserHomeResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "userhome_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserHomeResolver Build(params (string User, string Path)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = _tempDir,
        };
        foreach (var (user, path) in overrides)
            values[$"Projects:UserHomeOverrides:{user}"] = path;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new UserHomeResolver(config, new AppSettingsService(config));
    }

    // Резолвер с настроенной песочницей: база container-пользователей — sandboxRoot
    private UserHomeResolver BuildWithSandbox(string sandboxRoot, params (string User, string Path)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = _tempDir,
            ["Sandbox:ProjectsRoot"] = sandboxRoot,
        };
        foreach (var (user, path) in overrides)
            values[$"Projects:UserHomeOverrides:{user}"] = path;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(
            config, NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new UserHomeResolver(config, new AppSettingsService(config), sandbox);
    }

    private static User Alice => new() { Username = "alice" };

    private static User Isolated => new()
    {
        Username = "alice",
        ExecutionEnvironment = ExecutionEnvironments.Container,
    };

    [Fact]
    public void Без_override_папка_логина_внутри_базы()
    {
        Build().Resolve(Alice).Should().Be(Path.Combine(_tempDir, "alice"));
    }

    [Fact]
    public void Override_заменяет_папку_логина()
    {
        var custom = Path.Combine(_tempDir, "flat");

        Build(("alice", custom)).Resolve(Alice).Should().Be(Path.GetFullPath(custom));
    }

    [Fact]
    public void Override_ищется_без_учёта_регистра_логина()
    {
        var custom = Path.Combine(_tempDir, "flat");

        Build(("ALICE", custom)).Resolve(Alice).Should().Be(Path.GetFullPath(custom));
    }

    [Fact]
    public void Override_чужого_логина_не_затрагивает_остальных()
    {
        var resolver = Build(("bob", Path.Combine(_tempDir, "flat")));

        resolver.Resolve(Alice).Should().Be(Path.Combine(_tempDir, "alice"));
    }

    [Fact]
    public void Container_без_настроенной_песочницы_даёт_null()
    {
        // Sandbox:ProjectsRoot не задан (SandboxManager отсутствует) — база неизвестна,
        // DefaultProjectsPath для изолированного пользователя не подходит
        var user = new User { Username = "alice", ExecutionEnvironment = ExecutionEnvironments.Container };

        Build().Resolve(user).Should().BeNull();
    }

    // Изоляция container-пользователей: их дом обязан лежать СТРОГО внутри Sandbox:ProjectsRoot,
    // иначе процессы пользователя папку просто не увидят (в контейнер монтируется только корень),
    // а дом, равный самому корню, открыл бы соседей по песочнице

    [Fact]
    public void Container_override_внутри_песочницы_принимается()
    {
        var sandbox = Path.Combine(_tempDir, "sandbox");
        var resolver = BuildWithSandbox(sandbox, ("alice", Path.Combine(sandbox, "flat")));

        resolver.Resolve(Isolated).Should().Be(Path.GetFullPath(Path.Combine(sandbox, "flat")));
    }

    [Fact]
    public void Container_override_вне_песочницы_игнорируется()
    {
        var sandbox = Path.Combine(_tempDir, "sandbox");
        var resolver = BuildWithSandbox(sandbox, ("alice", Path.Combine(_tempDir, "outside")));

        resolver.Resolve(Isolated).Should().Be(Path.Combine(sandbox, "alice"));
    }

    [Fact]
    public void Container_override_в_соседнюю_папку_с_общим_префиксом_игнорируется()
    {
        // «…\sandbox2» не вложен в «…\sandbox», хотя и начинается так же
        var sandbox = Path.Combine(_tempDir, "sandbox");
        var resolver = BuildWithSandbox(sandbox, ("alice", Path.Combine(_tempDir, "sandbox2")));

        resolver.Resolve(Isolated).Should().Be(Path.Combine(sandbox, "alice"));
    }

    [Fact]
    public void Container_override_в_сам_корень_песочницы_игнорируется()
    {
        // Корень общий для всех изолированных пользователей — домом одного он быть не может
        var sandbox = Path.Combine(_tempDir, "sandbox");
        var resolver = BuildWithSandbox(sandbox, ("alice", sandbox));

        resolver.Resolve(Isolated).Should().Be(Path.Combine(sandbox, "alice"));
    }

    [Fact]
    public void Относительный_override_игнорируется()
    {
        // Резолвился бы от рабочей папки процесса — молча уехавший дом хуже игнора
        Build(("alice", "GIT")).Resolve(Alice).Should().Be(Path.Combine(_tempDir, "alice"));
    }

    [Fact]
    public void Пустая_база_даёт_null()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "empty", "projects.json"),
            })
            .Build();
        var resolver = new UserHomeResolver(config, new AppSettingsService(config));

        resolver.Resolve(Alice).Should().BeNull();
    }

    [Fact]
    public void Пользователь_без_логина_даёт_null()
    {
        Build().Resolve(new User { Username = "" }).Should().BeNull();
        Build().Resolve((User?)null).Should().BeNull();
    }
}
