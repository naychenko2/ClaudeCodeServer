using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// Экран «MCP-серверы» разносит наблюдаемые серверы по группам (кто подключил и кто
// управляет): сервисы продукта, интеграции, память персон и наследство CLI. Плюс
// гигиена: наблюдения pmem_* по исчезнувшим персонам не должны пугать человека.
public class McpServersBuiltinTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public McpServersBuiltinTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Builtin_РаздаётГруппы_ИПрячетПамятьУдалённойПерсоны()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var persona = personas.Create(ownerId, "Mcp Builtin Test", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: true);
        var liveKey = PersonaConsultantToolset.PmemServerKey(persona.Handle);

        _factory.Services.GetRequiredService<McpStatusStore>().RecordFromInit(ownerId, "sess-mcp-builtin",
        [
            new McpServerInfo("tasks", "connected"),
            new McpServerInfo("dify", "connected"),
            new McpServerInfo("markitdown", "connected"),
            new McpServerInfo(liveKey, "connected"),
            new McpServerInfo("pmem_ghost", "connected"), // персоны нет — хвост из старых ходов
        ]);

        var client = _factory.CreateAuthenticatedClient();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/mcp/servers/builtin");
        var items = list.EnumerateArray().ToList();

        items.Select(Key).Should().NotContain("pmem_ghost",
            "наблюдение по удалённой персоне больше никогда не обновится — показывать его нельзя");
        items.Select(Key).Should().Contain(new[] { "tasks", "dify", "markitdown", liveKey });
        Group(items, "tasks").Should().Be(McpBuiltinGroups.Product);
        Group(items, "dify").Should().Be(McpBuiltinGroups.Integration);
        Group(items, liveKey).Should().Be(McpBuiltinGroups.PersonaMemory);
        Group(items, "markitdown").Should().Be(McpBuiltinGroups.External);
    }

    [Fact]
    public async Task Builtin_ПерсонаСВыключеннойПамятью_ЕёСерверНеПоказывается()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        // Персона жива, но память выключена — pmem-сервер в конфиг хода не попадает
        var persona = personas.Create(ownerId, "Mcp Builtin Muted", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);
        var mutedKey = PersonaConsultantToolset.PmemServerKey(persona.Handle);

        _factory.Services.GetRequiredService<McpStatusStore>().RecordFromInit(ownerId, "sess-mcp-builtin-2",
        [
            new McpServerInfo(mutedKey, "connected"),
        ]);

        var client = _factory.CreateAuthenticatedClient();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/mcp/servers/builtin");

        list.EnumerateArray().Select(Key).Should().NotContain(mutedKey);
    }

    private static string Key(JsonElement item) => item.GetProperty("key").GetString()!;

    private static string Group(List<JsonElement> items, string key) =>
        items.First(i => Key(i) == key).GetProperty("group").GetString()!;
}
