using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Архив чатов в http-тулсете рабочего пространства (wsp): признак и фильтр в chats_list,
/// пометка о возврате из архива в chats_send и сам инструмент chats_archive — один на оба
/// направления, как REST PUT /api/chats/{id}/archived.
///
/// Оси: архивные чаты по умолчанию не отдаются (как в интерфейсе, где архив живёт за
/// отдельным режимом списка), гейт живости отдаётся текстом сервиса как есть, чужой чат
/// не архивируется (та же формула владения, что у соседей по тулсету).
/// </summary>
public class WorkspaceHttpChatArchiveTests : IDisposable
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

    private static async Task ArchiveViaRestAsync(HttpClient client, string sessionId)
    {
        var resp = await client.PutAsJsonAsync($"/api/chats/{sessionId}/archived", new { archived = true });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId,
        string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement rpc) =>
        rpc.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    // Подмена адаптера чата занятым (образец — ChatsArchivedEndpointTests): гейт архивации
    // смотрит на живой прогон, а не на Status
    private void SetBusyAdapter(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)field.GetValue(Sessions)!;
        var entry = map[sessionId]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.HasTrackedBg).Returns(false);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    [Fact]
    public async Task ChatsList_ПоУмолчаниюБезАрхивных_СIncludeArchived_ОтдаётВсеСПризнаком()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var archived = await CreateChatAsync(client);
        await ArchiveViaRestAsync(client, archived);

        var plain = await CallToolAsync(client, caller, "chats_list", new { });
        IsError(plain).Should().BeFalse(TextOf(plain));
        var plainItems = JsonSerializer.Deserialize<JsonElement>(TextOf(plain));
        plainItems.EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .Should().Contain(caller, "живой чат в списке")
            .And.NotContain(archived, "архивные по умолчанию не отдаются");
        plainItems.EnumerateArray().Should().OnlyContain(
            i => i.GetProperty("isArchived").GetBoolean() == false,
            "признак архива есть у каждого элемента");

        var full = await CallToolAsync(client, caller, "chats_list", new { includeArchived = true });
        IsError(full).Should().BeFalse(TextOf(full));
        var fullItems = JsonSerializer.Deserialize<JsonElement>(TextOf(full));
        fullItems.EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .Should().Contain([caller, archived]);
        fullItems.EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == archived)
            .GetProperty("isArchived").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ChatsArchive_УбираетИВозвращает()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);

        var hide = await CallToolAsync(client, caller, "chats_archive",
            new { sessionId = target, archived = true });
        IsError(hide).Should().BeFalse(TextOf(hide));
        var hidden = JsonSerializer.Deserialize<JsonElement>(TextOf(hide));
        hidden.GetProperty("id").GetString().Should().Be(target);
        hidden.GetProperty("isArchived").GetBoolean().Should().BeTrue();
        Sessions.GetById(target)!.IsArchived.Should().BeTrue();

        var back = await CallToolAsync(client, caller, "chats_archive",
            new { sessionId = target, archived = false });
        IsError(back).Should().BeFalse(TextOf(back));
        JsonSerializer.Deserialize<JsonElement>(TextOf(back))
            .GetProperty("isArchived").GetBoolean().Should().BeFalse();
        Sessions.GetById(target)!.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChatsArchive_БезНаправления_Отказ()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);

        var result = await CallToolAsync(client, caller, "chats_archive", new { sessionId = target });

        IsError(result).Should().BeTrue("пропущенный archived нельзя трактовать как «вернуть»");
        TextOf(result).Should().Contain("archived");
        Sessions.GetById(target)!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ChatsArchive_ЧужойЧат_Отказ()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var foreign = await CreateChatAsync(stranger);

        var result = await CallToolAsync(client, caller, "chats_archive",
            new { sessionId = foreign, archived = true });

        IsError(result).Should().BeTrue("чат другого владельца недоступен");
        TextOf(result).Should().Contain("не найдена");
        Sessions.GetById(foreign)!.IsArchived.Should().BeFalse("чужой чат остался нетронутым");
    }

    [Fact]
    public async Task ChatsArchive_ХодВПолёте_ОтказТекстомСервиса()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);
        SetBusyAdapter(target);

        var result = await CallToolAsync(client, caller, "chats_archive",
            new { sessionId = target, archived = true });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("идёт ход", "человеческий текст сервиса отдаём как есть");
        Sessions.GetById(target)!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ChatsSend_ВАрхивныйЧат_ПроходитИНесётПометку()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);
        await ArchiveViaRestAsync(client, target);

        var result = await CallToolAsync(client, caller, "chats_send",
            new { sessionId = target, text = "привет из архива", wait = "none" });

        IsError(result).Should().BeFalse(TextOf(result));
        var body = JsonSerializer.Deserialize<JsonElement>(TextOf(result));
        body.GetProperty("restoredFromArchive").GetBoolean().Should().BeTrue();
        body.GetProperty("archiveHint").GetString().Should().Contain("архив");
        Sessions.GetById(target)!.IsArchived.Should().BeFalse("активность возвращает чат из архива");
    }

    [Fact]
    public async Task ChatsSend_ВЖивойЧат_БезПометкиОбАрхиве()
    {
        var client = Client;
        var caller = await CreateChatAsync(client);
        var target = await CreateChatAsync(client);

        var result = await CallToolAsync(client, caller, "chats_send",
            new { sessionId = target, text = "обычное сообщение", wait = "none" });

        IsError(result).Should().BeFalse(TextOf(result));
        JsonSerializer.Deserialize<JsonElement>(TextOf(result))
            .TryGetProperty("restoredFromArchive", out _)
            .Should().BeFalse("пометка появляется только у чата, который был в архиве");
    }

    [Fact]
    public void ChatsArchive_ЕстьВСоставеСекцииChats_Статически()
    {
        var tools = WorkspaceToolset.ToolsForSections(
            new HashSet<string>(StringComparer.Ordinal) { "chats" }, "контекст");

        tools.Select(t => t.Name).Should().Contain("chats_archive",
            "состав не зависит от свойств хода — инструмент статичен");
        WorkspaceToolset.ToolSection["chats_archive"].Should().Be("chats");
    }
}
