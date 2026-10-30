using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Инструмент chats_continue http-тулсета wsp. Из своего хода агент не может дождаться конца
/// этого же хода, поэтому вызов без sessionId ставит продолжение в очередь и отвечает scheduled;
/// новый чат появляется только после TurnCompleted. Настоящую отправку (процесс CLI) подменяет
/// шов ChatContinueService.SendOverride.
/// </summary>
[Trait("Category", "Host")]
public class WorkspaceHttpChatsContinueTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task<string> CreateChatAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CallRpcAsync(HttpClient client, string sessionId, string method, object? args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method, @params = args,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // Подкладываем ход агента в накопитель сессии: GetHistoryAsync читает живую запись
    private void SeedAgentTurn(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entry = ((System.Collections.IDictionary)field.GetValue(Sessions)!)[sessionId]!;
        List<StoredMessage> turns =
        [
            new StoredUserMessage("вопрос"), new StoredTextMessage("ответ"), new StoredResultMessage("success", 1, 1),
        ];
        entry.GetType().GetField("Accumulator")!.SetValue(entry, new TurnAccumulator(turns, "csid-" + sessionId));
    }

    private TaskCompletionSource<string> ReplyWithNote()
    {
        var firstMessageTo = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Services.GetRequiredService<ChatContinueService>().SendOverride = (sid, _, _, sys) =>
        {
            if (!sys) firstMessageTo.TrySetResult(sid);
            return Task.FromResult<SendAndWaitResult>(sys
                ? new SendAndWaitResult.Completed(new TurnResult("## Цель\nпродолжить", 1, null, Succeeded: true))
                : new SendAndWaitResult.Running());
        };
        return firstMessageTo;
    }

    [Fact]
    public async Task СписокИнструментов_СодержитChatsContinue_БезОбязательныхПараметров()
    {
        var client = Client;
        var chat = await CreateChatAsync(client);

        var rpc = await CallRpcAsync(client, chat, "tools/list", new { });

        var tool = rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "chats_continue");
        tool.GetProperty("inputSchema").TryGetProperty("required", out var required)
            .Should().BeFalse("sessionId опционален: без него продолжается текущий чат");
        tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("note", out _).Should().BeTrue();
    }

    [Fact]
    public async Task СвойЧат_СтавитсяВОчередь_ИСтартуетПослеКонцаХода()
    {
        var client = Client;
        var chat = await CreateChatAsync(client);
        SeedAgentTurn(chat);
        var firstMessage = ReplyWithNote();

        var rpc = await CallRpcAsync(client, chat, "tools/call",
            new { name = "chats_continue", arguments = new { note = "начни с тестов" } });

        TextOf(rpc).Should().Contain("\"status\":\"scheduled\"");
        Sessions.GetById(chat)!.ContinuedInSessionId.Should().BeNull("пока ход не кончился, нового чата нет");

        await _factory.Services.GetRequiredService<ITurnEventBus>().PublishAsync(
            new TurnCompleted(new TurnContext(chat, Sessions.ResolveOwnerId(Sessions.GetById(chat)!), 1, 0), "success"));

        var done = await Task.WhenAny(firstMessage.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().BeSameAs(firstMessage.Task, "после конца хода продолжение обязано стартовать");
        Sessions.GetById(chat)!.ContinuedInSessionId.Should().Be(await firstMessage.Task);
    }

    [Fact]
    public async Task ПовторныйВызов_ПоКоторомуУжеСтоитОчередь_AlreadyScheduled()
    {
        var client = Client;
        var chat = await CreateChatAsync(client);
        SeedAgentTurn(chat);
        ReplyWithNote();
        await CallRpcAsync(client, chat, "tools/call", new { name = "chats_continue", arguments = new { } });

        var rpc = await CallRpcAsync(client, chat, "tools/call", new { name = "chats_continue", arguments = new { } });

        TextOf(rpc).Should().Contain("already_scheduled");
    }

    [Fact]
    public async Task НесуществующийSessionId_ОтказНеНайдена()
    {
        var client = Client;
        var chat = await CreateChatAsync(client);

        var rpc = await CallRpcAsync(client, chat, "tools/call",
            new { name = "chats_continue", arguments = new { sessionId = "no-such-session" } });

        rpc.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        TextOf(rpc).Should().Contain("не найдена");
    }
}
