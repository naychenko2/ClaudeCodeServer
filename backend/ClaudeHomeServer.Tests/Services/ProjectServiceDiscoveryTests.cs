using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>Тесты инференса запускаемых сервисов из манифестов проекта.</summary>
public class ProjectServiceDiscoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly ProjectServiceDiscovery _svc;
    private readonly LaunchConfigService _launch;

    public ProjectServiceDiscoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "psd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _launch = new LaunchConfigService(new Mock<ILogger<LaunchConfigService>>().Object);
        _svc = new ProjectServiceDiscovery(_launch, new Mock<ILogger<ProjectServiceDiscovery>>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private Project Project() => new() { RootPath = _dir, OwnerId = "u", Name = "t" };

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_dir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task PackageJson_ServerScriptsOnly()
    {
        Write("package.json", """{ "scripts": { "dev": "vite", "build": "vite build", "predev": "x", "serve": "vite preview" } }""");
        var svcs = await _svc.DiscoverAsync(Project());

        svcs.Should().Contain(s => s.Source == "npm" && s.Args.Contains("dev"));
        svcs.Should().Contain(s => s.Source == "npm" && s.Args.Contains("serve"));
        svcs.Should().NotContain(s => s.Args.Contains("build"));
        svcs.Should().NotContain(s => s.Args.Contains("predev"));
        svcs.Where(s => s.Source == "npm").Should().OnlyContain(s => s.Command == "npm");
    }

    [Fact]
    public async Task PackageJson_DetectsPnpm()
    {
        Write("package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        var svcs = await _svc.DiscoverAsync(Project());

        var dev = svcs.First(s => s.Source == "npm");
        dev.Command.Should().Be("pnpm");
        dev.Args.Should().Equal("dev"); // pnpm <script>, без "run"
    }

    [Fact]
    public async Task LaunchSettings_ExtractsHttpPort()
    {
        Write("src/Api/Api.csproj", "<Project></Project>");
        Write("src/Api/Properties/launchSettings.json", """
        {
          "profiles": {
            "http": { "commandName": "Project", "applicationUrl": "https://localhost:7001;http://localhost:5005" },
            "IIS": { "commandName": "IISExpress" }
          }
        }
        """);
        var svcs = await _svc.DiscoverAsync(Project());

        var dotnet = svcs.Where(s => s.Source == "dotnet").ToList();
        dotnet.Should().ContainSingle(); // только commandName=Project
        dotnet[0].Command.Should().Be("dotnet");
        dotnet[0].SuggestedPort.Should().Be(5005); // http предпочтительнее https
        dotnet[0].Args.Should().Contain("--project");
    }

    [Fact]
    public async Task Compose_ExtractsFirstHostPort()
    {
        Write("docker-compose.yml", """
        services:
          web:
            image: nginx
            ports:
              - "8080:80"
          db:
            image: postgres
            ports:
              - "127.0.0.1:5433:5432"
        """);
        var svcs = await _svc.DiscoverAsync(Project());

        var compose = svcs.Where(s => s.Source == "docker-compose").ToList();
        compose.Should().Contain(s => s.Name.StartsWith("web") && s.SuggestedPort == 8080);
        compose.Should().Contain(s => s.Name.StartsWith("db") && s.SuggestedPort == 5433);
        compose.Should().OnlyContain(s => s.Command == "docker");
    }

    [Fact]
    public async Task Procfile_ParsesProcessTypes()
    {
        Write("Procfile", "web: node server.js\nworker: node worker.js");
        var svcs = await _svc.DiscoverAsync(Project());

        var proc = svcs.Where(s => s.Source == "procfile").ToList();
        proc.Should().Contain(s => s.Command == "node" && s.Args.Contains("server.js"));
    }

    [Fact]
    public async Task Makefile_ServerTargetsOnly()
    {
        Write("Makefile", "run:\n\tdotnet run\nbuild:\n\tdotnet build\ndev-server:\n\tnpm run dev\n");
        var svcs = await _svc.DiscoverAsync(Project());

        var make = svcs.Where(s => s.Source == "makefile").ToList();
        make.Should().Contain(s => s.Args.Contains("run"));
        make.Should().Contain(s => s.Args.Contains("dev-server"));
        make.Should().NotContain(s => s.Args.Contains("build"));
    }

    [Fact]
    public async Task SavedLaunchConfig_MarkedSaved_AndPreferredOverInferred()
    {
        Write("package.json", """{ "scripts": { "dev": "vite" } }""");
        await _launch.WriteAsync(Project(), new List<LaunchConfigEntry>
        {
            new() { Name = "custom-web", RuntimeExecutable = "npm", RuntimeArgs = ["run", "dev"], Port = 4000 }
        });
        _svc.Invalidate(Project().Id); // на всякий (разные Id у Project() — кэш по Id)

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().Contain(s => s.Saved && s.Name == "custom-web" && s.SuggestedPort == 4000);
        // Инференсный "npm run dev" имеет ту же сигнатуру → отброшен в пользу saved.
        svcs.Count(s => s.Command == "npm" && s.Args.SequenceEqual(new[] { "run", "dev" })).Should().Be(1);
    }
}
