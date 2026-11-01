using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// context_list в http-тулсете рабочего пространства (wsp): входит в tools/list всегда
/// (состав не зависит ни от хода, ни от содержимого контекста), читает закрепки ТОЛЬКО чата-
/// вызывателя из хвоста маршрута, чужой чат отбивается.
/// </summary>
[Trait("Category", "Host")]
public class WorkspaceHttpContextListTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string> CreateChatAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string sessionId, string method,
        object? args = null)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = args,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static List<string> ToolNames(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    [Fact]
    public async Task ToolsList_СодержитContextList_ИСоставНеЗависитОтКонтекста()
    {
        var client = _factory.CreateAuthenticatedClient();
        var chat = await CreateChatAsync(client);

        var before = ToolNames(await RpcAsync(client, chat, "tools/list"));
        await Sessions.SetContextAsync(chat,
            [new SessionContextEntry { Type = SessionContextTypes.Url, Id = "https://example.com", Title = "Пример" }]);
        var after = ToolNames(await RpcAsync(client, chat, "tools/list"));

        before.Should().Contain("context_list");
        after.Should().Equal(before, "состав tools/list не зависит от содержимого контекста чата");
    }

    [Fact]
    public void ToolsForSections_ContextList_ЕстьПриПустыхИПолныхСекциях()
    {
        var empty = WorkspaceToolset.ToolsForSections(new HashSet<string>(), "x").Select(t => t.Name);
        var full = WorkspaceToolset.ToolsForSections(
            WorkspaceToolset.ToolSection.Values.ToHashSet(), "x").Select(t => t.Name);

        empty.Should().Equal("context_list");
        full.Should().Contain("context_list");
    }

    [Fact]
    public async Task ContextList_ОтдаётЗакрепкиСвоейСессии_СПредупреждениемОбОтсутствующем()
    {
        var client = _factory.CreateAuthenticatedClient();
        var chat = await CreateChatAsync(client);
        await Sessions.SetContextAsync(chat,
        [
            new SessionContextEntry { Type = SessionContextTypes.Url, Id = "https://example.com", Title = "Пример" },
            new SessionContextEntry { Type = SessionContextTypes.File, Id = "нет/такого.md" },
        ]);

        var rpc = await RpcAsync(client, chat, "tools/call", new { name = "context_list", arguments = new { } });
        var result = rpc.GetProperty("result");
        result.TryGetProperty("isError", out var err).Should().BeFalse(err.ToString());
        var data = JsonSerializer.Deserialize<JsonElement>(
            result.GetProperty("content")[0].GetProperty("text").GetString()!);

        var entries = data.GetProperty("entries");
        entries.GetArrayLength().Should().Be(1);
        entries[0].GetProperty("id").GetString().Should().Be("https://example.com");
        data.GetProperty("warnings").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ContextList_ЧужаяСессия_Отказ()
    {
        var owner = _factory.CreateAuthenticatedClient();
        var foreign = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var chat = await CreateChatAsync(owner);
        await Sessions.SetContextAsync(chat,
            [new SessionContextEntry { Type = SessionContextTypes.Url, Id = "https://secret.example", Title = "Секрет" }]);

        var rpc = await RpcAsync(foreign, chat, "tools/call", new { name = "context_list", arguments = new { } });
        var result = rpc.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().NotContain("secret.example");
    }
}
