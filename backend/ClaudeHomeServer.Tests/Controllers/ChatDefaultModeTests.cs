using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Умолчание режима прав у нового чата: чат с персоной — AcceptEdits (без серверного классификатора
/// «Авто» на каждый вызов инструмента), явный режим уважается, чат без персоны — прежнее Auto.
/// </summary>
[Trait("Category", "Host")]
public class ChatDefaultModeTests : IClassFixture<ChatDefaultModeTests.Hosts>
{
    /// <summary>
    /// Хост на весь класс (IClassFixture), а не на тест: раньше класс поднимал полный хост на каждый
    /// тест (шесть, по 2–4 с под загрузкой) — это и был вес класса под параллелью, а не проверка.
    /// Пауз/опросов в классе нет и не было. Общее состояние тесты не делят: каждый заводит свою
    /// персону и свой чат и читает только свой id — ни одна проверка не зависит от того, что оставил
    /// предыдущий тест, поэтому сброс общего стора в конструкторе не нужен. Клиент (и вход) —
    /// свои на тест-экземпляр: конструктор xUnit выполняется перед каждым тестом.
    /// </summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChatDefaultModeTests(Hosts hosts)
    {
        _factory = hosts;
        _client = _factory.CreateAuthenticatedClient();
    }

    private T Svc<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private string OwnerId => Svc<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    // Персона на тест: имена уникальны внутри класса — порядок прогона не создаёт коллизий,
    // каждая проверка обращается только к своей персоне и созданному ею чату.
    private string NewPersona(string name) => Svc<PersonaManager>().Create(
        OwnerId, name, null, null, null, null, null, PersonaScope.Global, null, null, null, false).Id;

    private async Task<string> PostAsync(string url, object body)
    {
        var resp = await _client.PostAsJsonAsync(url, body);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private ClaudeMode ModeOf(string sessionId) => Svc<SessionManager>().GetById(sessionId)!.Mode;

    [Fact]
    public async Task ЧатСПерсонойБезРежима_AcceptEdits()
    {
        var id = await PostAsync("/api/chats", new { personaId = NewPersona("П1") });

        ModeOf(id).Should().Be(ClaudeMode.AcceptEdits);
    }

    [Fact]
    public async Task ЧатСПерсонойЯвныйAuto_Auto()
    {
        var id = await PostAsync("/api/chats", new { personaId = NewPersona("П2"), mode = "auto" });

        ModeOf(id).Should().Be(ClaudeMode.Auto);
    }

    [Fact]
    public async Task ЧатПерсоныЧерезPersonasБезРежима_AcceptEdits()
    {
        var id = await PostAsync($"/api/personas/{NewPersona("П3")}/chats", new { });

        ModeOf(id).Should().Be(ClaudeMode.AcceptEdits);
    }

    [Fact]
    public async Task ГрупповойЧатБезРежима_AcceptEdits()
    {
        var id = await PostAsync("/api/chats/group", new { personaIds = new[] { NewPersona("Г1"), NewPersona("Г2") } });

        ModeOf(id).Should().Be(ClaudeMode.AcceptEdits);
    }

    [Fact]
    public void ЧатБезПерсоныБезРежима_ПрежнееAuto()
    {
        ClaudeModeExtensions.ForNewChat(null, hasPersona: false).Should().Be(ClaudeMode.Auto);
        ClaudeModeExtensions.ForNewChat("auto", hasPersona: false).Should().Be(ClaudeMode.Auto);
        ClaudeModeExtensions.ForNewChat("plan", hasPersona: true).Should().Be(ClaudeMode.Plan);
    }

    [Fact]
    public async Task McpChatsCreate_СПерсоной_AcceptEdits()
    {
        var caller = await PostAsync("/api/chats", new { personaId = NewPersona("Вызывающая") });
        var resp = await _client.PostAsJsonAsync($"/mcp/wsp/{caller}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "chats_create", arguments = new { personaId = NewPersona("Целевая") } },
        });
        resp.EnsureSuccessStatusCode();
        var rpc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var text = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var id = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;

        ModeOf(id).Should().Be(ClaudeMode.AcceptEdits);
    }
}
