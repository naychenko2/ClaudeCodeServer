using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
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

    [Fact]
    public async Task ToolsListЧерезШлюз_ТотЖеЧтоУСерверногоХода()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        using var dev = Device(ownerId);
        var turn = _factory.Services.GetRequiredService<TurnTokenService>().Issue(ownerId, "chat-1", dev.Id);

        var direct = _factory.CreateClient();
        direct.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<JwtService>().IssueServiceToken(ownerId));
        var viaServer = await direct.PostAsync($"/mcp/{McpEndpoints.WidgetsName}", Json(ToolsList));

        var device = _factory.CreateClient();
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
}
