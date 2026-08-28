using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Параметр preempt у chats_send (http-тулсет wsp): агент выбирает, прерывать ли идущий
/// ход получателя. Дефолт true — прежнее поведение (обратная совместимость контракта:
/// существующие wait=turn-агенты не должны начать получать queued вместо ответа),
/// false — сообщение встаёт в очередь без Interrupt (Kill процесса убивал фоновых
/// сабагентов, работающих внутри чужого хода — диагностика обрывов 28.08).
///
/// Оси: параметр виден в tools/list (состав схем не режется — инвариант ADR-12),
/// сквозная проводка false → без прерывания, дефолт → прерывание. Поведение очереди
/// по result покрыто юнит-тестами SessionManagerTests (PreemptFalse_*).
/// </summary>
public class WorkspaceHttpChatsSendPreemptTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private static async Task<string> CreateChatAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string sessionId, object body)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId,
        string tool, object args) =>
        await RpcAsync(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = args },
        });

    private static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement rpc) =>
        rpc.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    // Занятый получатель: статус Working + подставной адаптер на месте процесса —
    // Interrupt верифицируется на моке, реальный CLI не поднимается
    private Mock<ILlmSessionAdapter> SetWorkingAdapter(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)field.GetValue(Sessions)!;
        var entry = map[sessionId]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        Sessions.GetById(sessionId)!.Status = SessionStatus.Working;
        return adapter;
    }

    [Fact]
    public async Task ToolsList_СхемаChatsSend_ОтдаётПараметрPreempt()
    {
        var caller = await CreateChatAsync(Client);

        var rpc = await RpcAsync(Client, caller, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var send = rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "chats_send");
        var schema = send.GetProperty("inputSchema");
        var preempt = schema.GetProperty("properties").GetProperty("preempt");
        preempt.GetProperty("type").GetString().Should().Be("boolean");
        preempt.GetProperty("description").GetString().Should().Contain("НЕ прерывать",
            "описание честно про семантику false — иначе модель не узнает про очередь");
        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(n => n.GetString()).ToList() : [];
        required.Should().NotContain("preempt",
            "параметр опционален: дефолт true держит обратную совместимость контракта");
    }

    [Fact]
    public async Task ChatsSend_PreemptFalse_ЗанятыйЧат_НеПрерываетХод()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);
        var adapter = SetWorkingAdapter(target);

        var result = await CallToolAsync(client, caller, "chats_send",
            new { sessionId = target, text = "доклад без прерывания", wait = "none", preempt = false });

        IsError(result).Should().BeFalse(TextOf(result));
        TextOf(result).Should().Contain("queued");
        adapter.Verify(a => a.Interrupt(), Times.Never(),
            "preempt=false не рубит идущий ход получателя");
        Sessions.GetVisiblePending(target).Should().ContainSingle()
            .Which.Text.Should().Be("доклад без прерывания");
    }

    [Fact]
    public async Task ChatsSend_БезПараметра_ЗанятыйЧат_ПрерываетХод()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);
        var adapter = SetWorkingAdapter(target);

        var result = await CallToolAsync(client, caller, "chats_send",
            new { sessionId = target, text = "срочный доклад", wait = "none" });

        IsError(result).Should().BeFalse(TextOf(result));
        TextOf(result).Should().Contain("queued");
        adapter.Verify(a => a.Interrupt(), Times.Once(),
            "дефолт (параметр не передан) — прежнее поведение: ход прерывается");
    }

    [Fact]
    public async Task ChatsSend_PreemptTrueЯвно_ЗанятыйЧат_ПрерываетХод()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);
        var adapter = SetWorkingAdapter(target);

        var result = await CallToolAsync(client, caller, "chats_send",
            new { sessionId = target, text = "срочный доклад", wait = "none", preempt = true });

        IsError(result).Should().BeFalse(TextOf(result));
        adapter.Verify(a => a.Interrupt(), Times.Once(),
            "явный preempt=true — то же, что дефолт");
    }
}
