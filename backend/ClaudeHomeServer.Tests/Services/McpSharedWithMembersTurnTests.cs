using ClaudeHomeServer.Services.Access;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2a-7: фактическая доставка серверов личного реестра провайдером BuildExternalMcpProvider —
/// той же точкой, которую ClaudeSession зовёт на каждый ход ради --mcp-config.
///
/// Чат общего проекта (рамка Narrow по вердикту GetTurnFrame/ITurnFrameSource — тому же,
/// что входит в сигнатуру запуска): непомеченный сервер личного реестра снимается,
/// помеченный SharedWithMembers — доезжает. Рамка не зависит от актора (TurnFrameTests),
/// поэтому проверка одна на чат: она равна и для владельца, и для участника.
/// Личный проект и чат вне проекта — поведение прежнее: их матрица живёт в McpDeliveryTests,
/// здесь проверяется только отсутствие изменений на этих ветках.
/// </summary>
[Trait("Category", "Host")]
public class McpSharedWithMembersTurnTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public McpSharedWithMembersTurnTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<(string Project, string Owner)> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name });
        resp.EnsureSuccessStatusCode();
        var p = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return (IdOf(p), p.GetProperty("ownerId").GetString()!);
    }

    private async Task<string> CreateChatInProjectAsync(string projectId)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateChatOutsideProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateServerAsync(string key)
    {
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new
        {
            key,
            transport = "stdio",
            command = "node",
            args = new[] { "server.js" },
        });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task MarkAsync(string id, bool shared)
    {
        var resp = await _client.PutAsJsonAsync($"/api/mcp/servers/{id}/shared", new { sharedWithMembers = shared });
        resp.EnsureSuccessStatusCode();
    }

    // Провайдер приватный (его единственная точка вызова — сборка контекста хода). Тест зовёт
    // ту же лямбду, что поднимается на каждый ход: состав --mcp-config проверяется на живой
    // точке, а не на копии формулы. Прецедент прямого вызова приватного члена SessionManager —
    // DrainNextPendingAsync в SessionManagerTests.RunDelegation.
    private List<string> DeliveredKeys(string ownerId, string? projectId, string sessionId)
    {
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var method = typeof(SessionManager).GetMethod("BuildExternalMcpProvider",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var provider = (Func<ExternalMcpContext?>)method.Invoke(sessions, [ownerId, projectId, null, sessionId])!;
        return provider()?.Servers.Select(s => s.Key).ToList() ?? [];
    }

    [Fact]
    public async Task ОбщийПроект_НепомеченныйСнимается_ПомеченныйДоезжает()
    {
        var (project, owner) = await CreateProjectAsync($"Общий-{_tag}");
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(project,
            [new ProjectMember { UserId = "участник-" + _tag, Role = ProjectRole.Editor }]);
        var chat = await CreateChatInProjectAsync(project);

        var off = await CreateServerAsync($"s2a7-off-{_tag}"); // включён в реестр, пометки нет
        var on = await CreateServerAsync($"s2a7-on-{_tag}");
        await MarkAsync(on, true);

        var keys = DeliveredKeys(owner, project, chat);
        keys.Should().Contain($"s2a7-on-{_tag}",
            "помеченный «для участников» доезжает до хода общего проекта — без выдачи в проект и персоне");
        keys.Should().NotContain($"s2a7-off-{_tag}",
            "непомеченный сервер личного реестра не доезжает ни до какого актора общего проекта (Narrow, решение 29.10)");
    }

    [Fact]
    public async Task ЛичныйПроект_ПрежниеОсиРаботаютБезПометки()
    {
        var (project, owner) = await CreateProjectAsync($"Личный-{_tag}");
        var chat = await CreateChatInProjectAsync(project);
        var key = $"s2a7-personal-{_tag}";
        await CreateServerAsync(key); // пометки нет — и не нужно

        // Ось «включён в проекте» — обычная выдача через McpServersOn
        _factory.Services.GetRequiredService<ProjectManager>()
            .Update(project, null, null, mcpServersOn: [key]);

        DeliveredKeys(owner, project, chat).Should().Contain(key,
            "личный проект — Wide: выдача через проект работает как раньше, пометка не требуется");
    }

    [Fact]
    public async Task ВнеПроекта_ПрежняяОсьAllowOutsideProjects_ПометкаНеРешает()
    {
        // id владельца — из любого созданного проекта; сам проект в этой ветке не участвует
        var (_, owner) = await CreateProjectAsync($"Соседний-{_tag}");
        var chat = await CreateChatOutsideProjectAsync();

        var offKey = $"s2a7-outside-off-{_tag}";
        var onKey = $"s2a7-outside-on-{_tag}";
        await CreateServerAsync(offKey); // без AllowOutsideProjects → не едет, как раньше
        var onId = await CreateServerAsync(onKey);
        var put = await _client.PutAsJsonAsync($"/api/mcp/servers/{onId}", new
        {
            key = onKey,
            transport = "stdio",
            command = "node",
            allowOutsideProjects = true,
        });
        put.EnsureSuccessStatusCode();

        var keys = DeliveredKeys(owner, null, chat);
        keys.Should().NotContain(offKey, "вне проектов прежняя ось записи: без AllowOutsideProjects сервер не едет");
        keys.Should().Contain(onKey, "с AllowOutsideProjects — едет, пометка «для участников» здесь ни при чём");
    }
}
