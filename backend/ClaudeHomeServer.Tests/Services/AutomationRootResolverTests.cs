using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.TriggerSources;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Резолв корня файловых триггеров: режим проекта (projectId → RootPath) и режим «папка без проекта»
// для глобальных агентов (folder → подпапка основной папки пользователя, guard от traversal).
public class AutomationRootResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProjectManager _projects;
    private readonly AppSettingsService _appSettings;

    public AutomationRootResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rootres_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = _tempDir,
            })
            .Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, users, _appSettings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] kv)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in kv) d[k] = JsonSerializer.SerializeToElement(v);
        return d;
    }

    private static User Alice => new() { Username = "alice" };

    [Fact]
    public void Проект_резолвится_в_RootPath()
    {
        var root = Path.Combine(_tempDir, "myproj");
        var project = _projects.Create("MyProj", root, "u1", "alice", createDirectory: true);
        var resolver = new AutomationRootResolver(_projects, _appSettings);

        var (resolved, label) = resolver.Resolve(Args(("projectId", project.Id)), Alice);

        resolved.Should().Be(root);
        label.Should().Contain("MyProj");
    }

    [Fact]
    public void Несуществующий_проект_даёт_null()
    {
        var resolver = new AutomationRootResolver(_projects, _appSettings);
        var (resolved, _) = resolver.Resolve(Args(("projectId", "нет-такого")), Alice);
        resolved.Should().BeNull();
    }

    [Fact]
    public void Пустая_папка_даёт_домашнюю_папку_пользователя()
    {
        var resolver = new AutomationRootResolver(_projects, _appSettings);
        var (resolved, label) = resolver.Resolve(Args(("folder", "")), Alice);

        resolved.Should().Be(Path.GetFullPath(Path.Combine(_tempDir, "alice")));
        label.Should().Be("домашняя папка");
    }

    [Fact]
    public void Подпапка_резолвится_относительно_домашней()
    {
        var resolver = new AutomationRootResolver(_projects, _appSettings);
        var (resolved, label) = resolver.Resolve(Args(("folder", "sub/dir")), Alice);

        resolved.Should().Be(Path.GetFullPath(Path.Combine(_tempDir, "alice", "sub", "dir")));
        label.Should().Contain("sub/dir");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("../bob")]
    public void Traversal_за_домашнюю_папку_отклоняется(string folder)
    {
        var resolver = new AutomationRootResolver(_projects, _appSettings);
        var (resolved, _) = resolver.Resolve(Args(("folder", folder)), Alice);
        resolved.Should().BeNull();
    }

    [Fact]
    public void Ни_проекта_ни_папки_даёт_null()
    {
        var resolver = new AutomationRootResolver(_projects, _appSettings);
        var (resolved, _) = resolver.Resolve(new Dictionary<string, JsonElement>(), Alice);
        resolved.Should().BeNull();
    }

    [Fact]
    public void Пустой_DefaultProjectsPath_даёт_null()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "empty", "projects.json"),
            })
            .Build();
        var appSettings = new AppSettingsService(config);
        var resolver = new AutomationRootResolver(_projects, appSettings);

        var (resolved, _) = resolver.Resolve(Args(("folder", "sub")), Alice);
        resolved.Should().BeNull();
    }
}
