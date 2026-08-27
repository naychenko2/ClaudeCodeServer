using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Изоляция владельцев на http-тулсете персон (ADR-012, фаза 2 волна 2 — пункт приёмки:
/// «токен A не видит и не правит персон владельца B»). PersonasToolset умеет писать персон
/// (manage-модуль), поэтому чужая сессия в хвосте обязана закрывать доступ ЦЕЛИКОМ —
/// ни состава, ни вызова: персона из чужого воркспейса не то что не редактируется —
/// её существование не подтверждается.
/// </summary>
public class PersonasHttpOwnerIsolationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<string> CreateSessionAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"pers-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Токен B с хвостом сессии A: пустой tools/list и отказ вызова — доступ к персонам
    /// закрывается на уровне сессии-вызывателя, а не «фильтрацией чужих персон из списка».
    /// </summary>
    [Fact]
    public async Task ЧужойТокен_НиСоставаНиВызова()
    {
        var sessionIdA = await CreateSessionAsync(Client);
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var list = await clientB.PostAsJsonAsync($"/mcp/personas/{sessionIdA}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        list.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").GetArrayLength()
            .Should().Be(0, "чужая сессия — пустой состав (fail-closed)");

        var call = await clientB.PostAsJsonAsync($"/mcp/personas/{sessionIdA}", new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "personas_list", arguments = new { } },
        });
        call.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = JsonSerializer.Deserialize<JsonElement>(await call.Content.ReadAsStringAsync());
        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("другому владельцу");
    }

    /// <summary>
    /// Живой путь приёмки: personas_list на хвосте своей сессии отдаёт список персон
    /// владельца (минимум провижн-ассистент) — ядро сервера работает без процесса node.
    /// </summary>
    [Fact]
    public async Task ЖивойПуть_СписокПерсон_Работает()
    {
        var sessionId = await CreateSessionAsync(Client);

        var call = await Client.PostAsJsonAsync($"/mcp/personas/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "personas_list", arguments = new { scope = "all" } },
        });
        call.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = JsonSerializer.Deserialize<JsonElement>(await call.Content.ReadAsStringAsync());
        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();
        var text = answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        text.Should().StartWith("[").And.Contain("id",
            "personas_list(scope=all) отдаёт JSON-массив персон владельца");
    }
}
