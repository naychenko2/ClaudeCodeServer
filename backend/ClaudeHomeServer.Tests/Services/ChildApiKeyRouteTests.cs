using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// И2 (ADR-041): точки шлюза и песочницы. Сам запуск хода ребёнка — в Llm.Tests/ChildApiKeyRouteTests.
[Collection(TestCollections.ProcessGlobalState)]
public class ChildApiKeyRouteTests
{
    private sealed class StubPolicy(params string[] children) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) =>
            children.Contains(ownerId) ? new(ownerId, new ChildProfile { Enabled = true }, false) : null;
    }

    private static UpstreamSelector Selector(IChildModePolicy policy)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:st1:OAuthToken"] = "tok-st1",
        }).Build();
        var opts = new MutableOptionsMonitor<LlmGatewayOptions>(
            new LlmGatewayOptions { Enabled = true, AllowSubscriptions = true, AnthropicBaseUrl = "https://anthropic.test" });
        return new UpstreamSelector(new ClaudeSubscriptionPool(cfg), new LlmProviderRegistry(cfg), opts, policy);
    }

    [Fact]
    public void Шлюз_Ребёнку_МаршрутПодпискиНеВыдаётся()
    {
        var start = Selector(new StubPolicy("kid")).StartTurn(new TurnTokenService(new TurnEventBus()), "kid", "chat", null, "sonnet");

        start.Token.Should().BeNull();
        start.FailureText.Should().Be(ChildApiKeyRoute.RefusedText);
    }

    [Fact]
    public void Шлюз_ПустойВладелец_Отказ()
    {
        var start = Selector(new StubPolicy()).StartTurn(new TurnTokenService(new TurnEventBus()), "", "chat", null, "sonnet");

        start.Token.Should().BeNull("неопознанный владелец — fail-closed, не подписка");
    }

    [Fact]
    public void Шлюз_ВзрослыйВладелец_ПодпискаВыдаётся()
    {
        var start = Selector(new StubPolicy("kid")).StartTurn(new TurnTokenService(new TurnEventBus()), "adult", "chat", null, "sonnet");

        start.Token.Should().NotBeNull();
    }

    private static DockerProcessRunner Runner()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "ccs-child-" + Guid.NewGuid().ToString("N"), "projects.json"),
        }).Build();
        return new DockerProcessRunner(new SandboxManager(config, NullLogger<SandboxManager>.Instance), "owner-1");
    }

    [Fact]
    public void Песочница_МеткаРебёнка_ТокенПодпискиНеДоставляется()
    {
        var prev = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "токен-подписки");
        try
        {
            var kid = Runner().BuildTurnEnv(new Dictionary<string, string> { [ChildTurnMarker.EnvKey] = "1" });
            var adult = Runner().BuildTurnEnv(null);

            kid.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
            adult["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("токен-подписки");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", prev); }
    }
}
