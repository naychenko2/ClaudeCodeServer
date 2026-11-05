using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Services;

// persona_ask обязан запускать one-shot от имени владельца: по ownerId раннер выбирает среду
// исполнения (local / песочница). Раньше effort персоны позиционно вставал на место ownerId —
// ход container-владельца уходил на хост мимо песочницы, а effort терялся.
[Trait("Category", "Host")]
public class PersonaAskServiceTests
{
    private sealed class CapturingOneShot : IOneShotRunner
    {
        public string? OwnerId;
        public string? Effort;
        public int Calls;

        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default,
            string? ownerId = null, string? effort = null, string? label = null)
        {
            Calls++;
            OwnerId = ownerId;
            Effort = effort;
            return Task.FromResult("ответ персоны");
        }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default,
            string? ownerId = null, string? effort = null, string? label = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task ВладелецИУсилие_УходятВРаннерНаСвоиМеста()
    {
        var fake = new CapturingOneShot();
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.RemoveAll<IOneShotRunner>();
                s.AddSingleton<IOneShotRunner>(fake);
            }
        };
        var svc = factory.Services.GetRequiredService<PersonaAskService>();
        // Память выключена: recall и autolearn тест не касаются
        var persona = new Persona
        {
            OwnerId = "owner-42", Name = "Светлана", Handle = "svetlana",
            Effort = "high", MemoryEnabled = false,
        };

        var answer = await svc.AskAsync("owner-42", persona, "Как дела?", context: null,
            ct: default, callerIsPrivate: false);

        answer.Should().Be("ответ персоны");
        fake.Calls.Should().Be(1);
        fake.OwnerId.Should().Be("owner-42");
        fake.Effort.Should().Be("high");
    }

    // --- Сквозная цепочка persona_ask (MCP CallAsync → AskAsync → LearnFromConsultation) ---

    private static readonly string LongQuestion = new('в', 450);
    private static readonly string LongAnswer = new('о', 300);

    private sealed class LongAnswerOneShot : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null) =>
            Task.FromResult(LongAnswer);
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null) =>
            throw new NotSupportedException();
    }

    // Счётчик LLM-извлечения фактов автообучения: единственный потребитель ICheapTextRunner
    // на пути persona_ask — LearnFromConsultation
    private sealed class CountingCheap : ICheapTextRunner
    {
        public int Calls;
        public readonly TaskCompletionSource Done = new();
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Done.TrySetResult();
            return Task.FromResult("[]");
        }
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Done.TrySetResult();
            return Task.FromResult<string?>("[]");
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Done.TrySetResult();
            return Task.FromResult(new OneShotResult("[]", null, 0));
        }
    }

    // Вызывает persona_ask через настоящий MCP-маршрут. headerSession: "route" — тот же чат,
    // "ghost" — несуществующая сессия в X-Caller-Session-Id. Возвращает (ответ, счётчик извлечения).
    private static async Task<(string Text, int ExtractCalls)> AskViaMcpAsync(
        bool routePrivate, string headerSession, bool expectExtraction)
    {
        var cheap = new CountingCheap();
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.RemoveAll<IOneShotRunner>();
                s.AddSingleton<IOneShotRunner>(new LongAnswerOneShot());
                s.RemoveAll<ICheapTextRunner>();
                s.AddSingleton<ICheapTextRunner>(cheap);
            }
        };
        var client = factory.CreateAuthenticatedClient();
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"pa-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var created = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        created.EnsureSuccessStatusCode();
        var chatId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (routePrivate)
            factory.Services.GetRequiredService<SessionManager>().GetOwned(chatId, ownerId)!.PrivateToUserId = ownerId;

        var target = factory.Services.GetRequiredService<PersonaManager>().Create(ownerId,
            "Цель" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            PersonaScope.Project, projectId, null, null, true);
        target.MemoryEnabled = true;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/personas/{chatId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "persona_ask", arguments = new { personaId = target.Id, question = LongQuestion } },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Caller-Session-Id", headerSession == "route" ? chatId : headerSession);
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;

        if (expectExtraction) await Task.WhenAny(cheap.Done.Task, Task.Delay(5000));
        else await Task.Delay(400);
        return (text, cheap.Calls);
    }

    [Fact]
    public async Task PersonaAsk_ПриватныйВызыватель_ОтветЕстьИзвлечениеНоль()
    {
        var (text, calls) = await AskViaMcpAsync(routePrivate: true, headerSession: "route", expectExtraction: false);
        text.Should().Be(LongAnswer);
        calls.Should().Be(0, "факты приватного чата не уходят в общую полку персоны");
    }

    /// <summary>
    /// Несуществующая сессия в X-Caller-Session-Id: актор хода неизвестен, и по правилу F2-O2
    /// («неизвестный актор не получает прав») persona_ask отвергается гейтом актора до тулсета —
    /// ответ не «ответ персоны», а отказ; до автообучения путь не доходит, извлечения нет.
    /// </summary>
    [Fact]
    public async Task PersonaAsk_НесуществующаяСессияВЗаголовке_ОтказИзвлечениеНоль()
    {
        var (text, calls) = await AskViaMcpAsync(routePrivate: false, headerSession: "ghost-session", expectExtraction: false);
        text.Should().Be(ActorRightGate.DeniedText, "актор неизвестен — прав нет, вызов отсекается до тулсета");
        calls.Should().Be(0, "отказ до тулсета — извлечение не запускается");
    }

    [Fact]
    public async Task PersonaAsk_ОбщийЧат_ИзвлечениеОдинВызов()
    {
        var (text, calls) = await AskViaMcpAsync(routePrivate: false, headerSession: "route", expectExtraction: true);
        text.Should().Be(LongAnswer);
        calls.Should().Be(1, "контроль: путь живой, нули выше не тривиальны");
    }
}
