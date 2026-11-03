using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Место на именованном локальном движке (LocalEngines:gemma) в настройках: route «local» плюс
// подпись движка. Без подписи UI показывал модель основного LocalLlm (01.11.2026: в настройках
// у мест Gemma стояло «Локальная · qwen3.8-27b»). Мутация: не отдавать engine — первый тест краснеет;
// отдавать его и месту основного движка — второй.
[Trait("Category", "Host")]
public class LocalActionsEngineLabelTests : IClassFixture<LocalActionsEngineLabelTests.EngineHost>
{
    public sealed class EngineHost : TestWebApplicationFactory
    {
        public EngineHost()
        {
            ExtraConfig["LocalEngines:gemma:BaseUrl"] = "http://127.0.0.1:1";
            ExtraConfig["LocalEngines:gemma:Model"] = "gemma-4-26b-a4b";
            ExtraConfig["LocalEngines:gemma:Title"] = "Gemma 4";
            ExtraConfig["LocalEngines:gemma:Actions:0"] = LocalActionCatalog.NotesTags;
        }
    }

    private readonly HttpClient _admin;

    public LocalActionsEngineLabelTests(EngineHost host) => _admin = host.CreateAuthenticatedClient();

    private async Task<JsonElement> UsageAction(string key)
    {
        var usage = await _admin.GetFromJsonAsync<JsonElement>("/api/usage");
        return usage.GetProperty("ollama").GetProperty("actions").EnumerateArray()
            .First(a => a.GetProperty("key").GetString() == key);
    }

    [Fact]
    public async Task МестоДвижка_ВUsage_ПодписьДвижка()
    {
        var action = await UsageAction(LocalActionCatalog.NotesTags);
        action.GetProperty("route").GetString().Should().Be("local");
        action.GetProperty("source").GetString().Should().Be("config");
        action.GetProperty("engine").GetString().Should().Be("Gemma 4");
    }

    [Fact]
    public async Task МестоНеНаДвижке_БезПодписи()
    {
        var action = await UsageAction(LocalActionCatalog.ChatTitle);
        action.TryGetProperty("engine", out var engine).Should().BeTrue();
        engine.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
