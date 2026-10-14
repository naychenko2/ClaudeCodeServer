using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Шлюз MCP на боевом Program: маршрут смаплен, JWT владельца выпускает настоящий
/// JwtService, а MCP-over-HTTP бэкенда отвечает шлюзу тем же составом, что и серверному
/// ходу (сторож G9). Звонок «в свой Kestrel» уводится в TestServer того же хоста.
/// </summary>
public sealed class McpGatewayIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new()
    {
        ExtraServices = s => s.AddHttpClient(McpGatewayEndpoints.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler()),
    };

    public void Dispose() => _factory.Dispose();

    // Устройство в боевом реестре хоста: вход шлюза пускает токен хода только с его учёткой
    private GatewayTestDevice Device(string ownerId) =>
        new(ownerId, _factory.Services.GetRequiredService<DeviceRegistry>());

    private const string ToolsList = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ПрямойЗаходНаMcpБэкендаБезJwt_401()
    {
        var anonymous = _factory.CreateClient();

        var resp = await anonymous.PostAsync($"/mcp/{McpEndpoints.WidgetsName}", Json(ToolsList));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // httpPorts — как ASPNETCORE_HTTP_PORTS образа aspnet: хост кладёт в адреса сервера
    // http://*:порт, и шлюз обязан звонить в бэкенд по loopback, а не падать на Uri
    [Theory]
    [InlineData(null)]
    [InlineData("8080")]
    public async Task ToolsListЧерезШлюз_ТотЖеЧтоУСерверногоХода(string? httpPorts)
    {
        using var factory = httpPorts is null
            ? null
            : _factory.WithWebHostBuilder(b => b.UseSetting(WebHostDefaults.HttpPortsKey, httpPorts));
        var host = (WebApplicationFactory<Program>?)factory ?? _factory;

        var ownerId = host.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        using var dev = new GatewayTestDevice(ownerId, host.Services.GetRequiredService<DeviceRegistry>());
        var turn = host.Services.GetRequiredService<TurnTokenService>().Issue(ownerId, "chat-1", dev.Id);

        var direct = host.CreateClient();
        direct.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            host.Services.GetRequiredService<JwtService>().IssueServiceToken(ownerId));
        var viaServer = await direct.PostAsync($"/mcp/{McpEndpoints.WidgetsName}", Json(ToolsList));

        var device = host.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{turn.Grant.TurnId}/mcp/{McpEndpoints.WidgetsName}")
        {
            Content = Json(ToolsList),
        };
        req.Headers.Add(TurnTokenEndpointFilter.HeaderName, turn.Token);
        dev.Sign(req);
        var viaGateway = await device.SendAsync(req);

        viaServer.StatusCode.Should().Be(HttpStatusCode.OK);
        viaGateway.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = JsonNode.Parse(await viaServer.Content.ReadAsStringAsync())!;
        var actual = JsonNode.Parse(await viaGateway.Content.ReadAsStringAsync())!;
        expected["result"]!["tools"]!.AsArray().Should().NotBeEmpty();
        JsonNode.DeepEquals(actual, expected).Should().BeTrue("шлюз не меняет состав tools/list");
    }

    // Wildcard-хост адреса прослушивания уходит в loopback, конкретный хост не трогается
    [Theory]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://+:8080/", "http://localhost:8080")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
    [InlineData("http://[::]:5000", "http://localhost:5000")]
    [InlineData("https://*:443/base", "https://localhost:443/base")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("http://host.docker.internal:5000", "http://host.docker.internal:5000")]
    public void АдресПрослушивания_WildcardВLoopback(string address, string expected)
    {
        var url = SessionManager.LoopbackApiUrl(address);

        url.Should().Be(expected);
        Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue();
    }

    [Fact]
    public async Task ЧужойХвостНаБоевомХосте_403_БезТокена_401()
    {
        using var dev = Device("owner-x");
        var turn = _factory.Services.GetRequiredService<TurnTokenService>().Issue("owner-x", "chat-1", dev.Id);
        var device = _factory.CreateClient();

        var foreign = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{turn.Grant.TurnId}/mcp/tasks/chat-2") { Content = Json(ToolsList) };
        foreign.Headers.Add(TurnTokenEndpointFilter.HeaderName, turn.Token);
        dev.Sign(foreign);
        (await device.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await device.SendAsync(dev.Sign(new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{turn.Grant.TurnId}/mcp/tasks/chat-1")
            { Content = Json(ToolsList) }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Токен хода без учётки устройства на боевом хосте тоже не проходит
        var bare = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{turn.Grant.TurnId}/mcp/tasks/chat-1") { Content = Json(ToolsList) };
        bare.Headers.Add(TurnTokenEndpointFilter.HeaderName, turn.Token);
        (await device.SendAsync(bare)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Токен серверного хода провайдера (ADR-016 §2) годится только LLM-шлюзу: MCP-шлюз остаётся
    // за устройствами, серверный CLI ходит в MCP напрямую со своим сервисным JWT
    [Fact]
    public async Task ТокенСерверногоХодаПровайдера_401()
    {
        var server = _factory.Services.GetRequiredService<TurnTokenService>().Issue("owner-x", "chat-1", null,
            new GatewayRoute(GatewayUpstreamKind.Provider, "m", ProviderKey: "minimax"), TurnTokenLifetime.Process);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{server.Grant.TurnId}/mcp/tasks/chat-1") { Content = Json(ToolsList) };
        req.Headers.Add(TurnTokenEndpointFilter.HeaderName, server.Token);

        (await _factory.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
