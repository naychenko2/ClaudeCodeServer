using ClaudeHomeServer.Services.ProjectServices;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Порт-политика и аргументы запуска сервисов проекта (этап 0 плана
/// docs/research/build-stand-progress-2026-10.md): `--urls` для dotnet, запрет 80/443/8080,
/// автопорт из 55xx–56xx, сверка с «Now listening on:».
/// </summary>
public class DevServerLaunchPolicyTests
{
    private const string Url = "http://localhost:5593";

    [Fact]
    public void BuildArgs_DotnetRunFromLaunchSettings_AppendsUrlsAfterSeparator()
    {
        string[] args = ["run", "--project", "src/Api/Api.csproj", "--launch-profile", "http"];

        DevServerLaunchPolicy.BuildArgs("dotnet", args, Url).Should().Equal(
            "run", "--project", "src/Api/Api.csproj", "--launch-profile", "http", "--", "--urls", Url);
    }

    [Fact]
    public void BuildArgs_DotnetRunWithExistingSeparator_DoesNotDuplicateIt()
    {
        string[] args = ["run", "--", "--verbose"];

        DevServerLaunchPolicy.BuildArgs("dotnet", args, Url).Should().Equal("run", "--", "--verbose", "--urls", Url);
    }

    [Fact]
    public void BuildArgs_DotnetDll_AppendsUrlsDirectly()
    {
        DevServerLaunchPolicy.BuildArgs("dotnet", ["bin/App.dll"], Url).Should().Equal("bin/App.dll", "--urls", Url);
    }

    [Fact]
    public void BuildArgs_DotnetWatch_AppendsUrls()
    {
        DevServerLaunchPolicy.BuildArgs("dotnet.exe", ["watch", "run"], Url)
            .Should().Equal("watch", "run", "--", "--urls", Url);
    }

    [Theory]
    [InlineData("--urls")]
    [InlineData("--urls=http://localhost:7000")]
    public void BuildArgs_UrlsAlreadyGiven_KeepsArgs(string urls)
    {
        string[] args = urls == "--urls" ? ["run", "--", urls, "http://localhost:7000"] : ["run", "--", urls];

        DevServerLaunchPolicy.BuildArgs("dotnet", args, Url).Should().Equal(args);
    }

    [Theory]
    [InlineData("npm", new[] { "run", "dev" })]
    [InlineData("dotnet", new[] { "build" })]
    [InlineData("dotnet", new string[0])]
    public void BuildArgs_NotADotnetApp_KeepsArgs(string command, string[] args)
    {
        DevServerLaunchPolicy.BuildArgs(command, args, Url).Should().Equal(args);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(8080)]
    public void IsForbidden_ProdAndDifyPorts(int port) =>
        DevServerLaunchPolicy.IsForbidden(port).Should().BeTrue();

    [Theory]
    [InlineData(5000)]
    [InlineData(5173)]
    [InlineData(5593)]
    public void IsForbidden_DevPorts_Allowed(int port) =>
        DevServerLaunchPolicy.IsForbidden(port).Should().BeFalse();

    [Fact]
    public void PickAutoPort_TakesFirstFreeFromDevRange()
    {
        DevServerLaunchPolicy.PickAutoPort([5500, 5501, 5000]).Should().Be(5502);
    }

    [Fact]
    public void PickAutoPort_RangeExhausted_ReturnsNull()
    {
        var all = Enumerable.Range(DevServerLaunchPolicy.AutoPortFirst,
            DevServerLaunchPolicy.AutoPortLast - DevServerLaunchPolicy.AutoPortFirst + 1);

        DevServerLaunchPolicy.PickAutoPort(all).Should().BeNull();
    }

    [Fact]
    public void ListenUrl_SandboxBindsAllInterfaces_HostBindsLocalhost()
    {
        DevServerLaunchPolicy.ListenUrl(42000, sandboxed: true).Should().Be("http://0.0.0.0:42000");
        DevServerLaunchPolicy.ListenUrl(5593, sandboxed: false).Should().Be("http://localhost:5593");
    }

    [Theory]
    [InlineData("info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: http://0.0.0.0:5000", 5000)]
    [InlineData("      Now listening on: http://localhost:5593", 5593)]
    [InlineData("      Now listening on: https://[::]:7001", 7001)]
    [InlineData("Local:   http://localhost:5173/", null)]
    public void ParseNowListening(string line, int? expected) =>
        DevServerLaunchPolicy.ParseNowListening(line).Should().Be(expected);

    [Theory]
    [InlineData("  ➜  Local:   http://localhost:8080/", 8080)]
    [InlineData("      Now listening on: http://localhost:5593", 5593)]
    // Строка ошибки привязки называет ЧУЖОЙ адрес — это не «слушаю»
    [InlineData("System.IO.IOException: Failed to bind to address http://127.0.0.1:8080: address already in use.", null)]
    [InlineData("Error: listen EADDRINUSE: address already in use http://localhost:8080", null)]
    [InlineData("Building...", null)]
    public void PortFromOutput(string line, int? expected) =>
        DevServerLaunchPolicy.PortFromOutput(line).Should().Be(expected);

    [Theory]
    [InlineData("dotnet", new[] { "run", "--project", "Api.csproj" }, 300)]
    [InlineData("dotnet.exe", new[] { "watch", "run" }, 300)]
    [InlineData("dotnet", new[] { "bin/App.dll" }, 120)]
    [InlineData("npm", new[] { "run", "dev" }, 120)]
    public void ReadyTimeoutFor_DotnetRunWaitsLonger(string command, string[] args, int seconds) =>
        DevServerLaunchPolicy.ReadyTimeoutFor(command, args).Should().Be(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void ReadyTimeoutReason_NamesCeilingAndBuildHint()
    {
        DevServerLaunchPolicy.ReadyTimeoutReason(TimeSpan.FromSeconds(300), builds: true)
            .Should().StartWith("Таймаут 300 с").And.Contain("сборка");
        DevServerLaunchPolicy.ReadyTimeoutReason(TimeSpan.FromSeconds(120), builds: false)
            .Should().Be("Таймаут 120 с: сервис не начал слушать порт.");
    }

    [Fact]
    public void PortMismatch_AppListensElsewhere_ExplainsWhere()
    {
        DevServerLaunchPolicy.PortMismatch(5593, [5000], appStarted: true)
            .Should().Be("Сервис слушает :5000 вместо :5593.");
    }

    [Fact]
    public void PortMismatch_ExpectedAmongAnnounced_IsNull()
    {
        DevServerLaunchPolicy.PortMismatch(5593, [7001, 5593], appStarted: true).Should().BeNull();
    }

    [Fact]
    public void PortMismatch_BeforeApplicationStarted_IsTooEarlyToJudge()
    {
        // Kestrel печатает адреса по одному: судить можно только после «Application started»
        DevServerLaunchPolicy.PortMismatch(5593, [7001], appStarted: false).Should().BeNull();
    }
}
