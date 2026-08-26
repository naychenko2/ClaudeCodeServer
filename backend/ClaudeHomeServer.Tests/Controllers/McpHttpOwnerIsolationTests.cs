using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Изоляция владельцев на MCP-over-HTTP (ADR-012). Эндпоинт торчит наружу вместе со всем
/// Kestrel (на бою :80/:443 с публичным доменом), поэтому владелец берётся ТОЛЬКО из claim
/// sub сервисного JWT. Для widgets это безобидно — данных у него нет; проверяем на тестовом
/// тулсете, который возвращает свой ownerId, потому что в фазе 2 сюда переедет tasks, и
/// анонимный или подменённый владелец означал бы чтение чужих задач.
/// </summary>
public class McpHttpOwnerIsolationTests : IDisposable
{
    // Тулсет-эхо: единственный инструмент возвращает владельца, каким его увидел контроллер
    private sealed class WhoAmIToolset : IMcpToolset
    {
        public string Name => "test-whoami";
        public string Version => "0.0.1";
        public IReadOnlyList<McpToolSchema> Tools { get; } =
        [
            new McpToolSchema("whoami", "Возвращает владельца вызова",
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        ];

        public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
            McpToolCallContext context, CancellationToken ct) =>
            Task.FromResult(new McpToolCallResult(context.OwnerId));
    }

    private readonly TestWebApplicationFactory _factory = new()
    {
        ExtraServices = s => s.AddSingleton<IMcpToolset, WhoAmIToolset>(),
    };

    public void Dispose() => _factory.Dispose();

    private static async Task<string> WhoAmIAsync(HttpClient client, object? @params = null)
    {
        var resp = await client.PostAsJsonAsync("/mcp/test-whoami", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = @params ?? new { name = "whoami", arguments = new { } },
        });
        resp.EnsureSuccessStatusCode();
        var answer = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task РазныеТокены_РазныеВладельцы()
    {
        var a = _factory.CreateAuthenticatedClient();
        var b = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var ownerA = await WhoAmIAsync(a);
        var ownerB = await WhoAmIAsync(b);

        ownerA.Should().NotBeNullOrEmpty();
        ownerB.Should().NotBeNullOrEmpty();
        ownerB.Should().NotBe(ownerA, "владелец берётся из токена, а не из запроса");
    }

    /// <summary>
    /// Подмена владельца снаружи невозможна: ни телом вызова, ни заголовком контекста.
    /// Заголовок X-Caller-Session-Id при http шлёт КЛИЕНТ, поэтому доверять ему как
    /// источнику прав нельзя — он влияет только на журнал и гейты по сессии.
    /// </summary>
    [Fact]
    public async Task ПодменаВладельца_ТеломИлиЗаголовком_НеРаботает()
    {
        var a = _factory.CreateAuthenticatedClient();
        var b = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var ownerA = await WhoAmIAsync(a);
        var ownerB = await WhoAmIAsync(b);

        a.DefaultRequestHeaders.Add("X-Caller-Session-Id", "чужая-сессия");
        a.DefaultRequestHeaders.Add("X-Owner-Id", ownerB);
        var viaHeaders = await WhoAmIAsync(a);
        var viaBody = await WhoAmIAsync(a, new
        {
            name = "whoami",
            arguments = new { ownerId = ownerB, sub = ownerB },
        });

        viaHeaders.Should().Be(ownerA);
        viaBody.Should().Be(ownerA);
    }
}
