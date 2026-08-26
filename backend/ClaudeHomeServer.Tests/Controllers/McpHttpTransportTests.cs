using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// MCP-over-HTTP (ADR-012): рукопожатие, состав и вызов инструмента по POST /mcp/{name}.
/// Проверяем ровно то, на что опирается конфиг хода — иначе поломка транспорта видна только
/// тем, что инструмент МОЛЧА исчезает у модели.
/// </summary>
public class McpHttpTransportTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    private readonly TestWebApplicationFactory _factory = factory;

    private async Task<JsonElement> RpcAsync(string method, object? @params = null, int id = 1)
    {
        var body = @params is null
            ? new { jsonrpc = "2.0", id, method }
            : (object)new { jsonrpc = "2.0", id, method, @params };
        var resp = await _client.PostAsJsonAsync("/mcp/widgets", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Initialize_ОтдаётИмяСервераИЭхоВерсииПротокола()
    {
        var answer = await RpcAsync("initialize", new { protocolVersion = "2025-06-18" });

        answer.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        var result = answer.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().Should().Be("2025-06-18");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("widgets");
    }

    [Fact]
    public async Task ToolsList_ОтдаётWidgetShowСоСхемой()
    {
        var tools = (await RpcAsync("tools/list")).GetProperty("result").GetProperty("tools");

        tools.GetArrayLength().Should().Be(1);
        var tool = tools[0];
        tool.GetProperty("name").GetString().Should().Be("widget_show");
        // Ключи JSON Schema не смеет переписать политика сериализации приложения
        var schema = tool.GetProperty("inputSchema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").TryGetProperty("html", out _).Should().BeTrue();
        schema.GetProperty("required")[0].GetString().Should().Be("html");
    }

    [Fact]
    public async Task ToolsCall_ПоказВиджета_ВозвращаетПодтверждение()
    {
        var answer = await RpcAsync("tools/call", new
        {
            name = "widget_show",
            arguments = new { html = "<div>привет</div>", title = "Сводка" },
        });

        var result = answer.GetProperty("result");
        result.TryGetProperty("isError", out _).Should().BeFalse("вызов удался");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        text.Should().Contain("Сводка").And.Contain("показан пользователю");
    }

    [Fact]
    public async Task ToolsCall_ПустойHtml_ЭтоОтказИнструмента_АНеОшибкаПротокола()
    {
        var answer = await RpcAsync("tools/call", new
        {
            name = "widget_show",
            arguments = new { html = "   " },
        });

        answer.TryGetProperty("error", out _).Should().BeFalse("протокол не при чём — виноват input");
        var result = answer.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("html");
    }

    [Fact]
    public async Task Ping_ОтвечаетПустымРезультатом()
    {
        var answer = await RpcAsync("ping");
        answer.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
    }

    /// <summary>
    /// CLI зондирует сервер нестандартным server/discover ДО initialize (разведка фазы 0):
    /// ответ -32601 его устраивает, а вот 500 или разрыв — нет.
    /// </summary>
    [Fact]
    public async Task ServerDiscover_ОтвечаетМетодНеПоддерживается()
    {
        var answer = await RpcAsync("server/discover");

        answer.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    /// <summary>Уведомление (без id) ответа не имеет — CLI ждёт 202 после рукопожатия.</summary>
    [Fact]
    public async Task Уведомление_БезId_Отвечает202БезТела()
    {
        var resp = await _client.PostAsJsonAsync("/mcp/widgets",
            new { jsonrpc = "2.0", method = "notifications/initialized" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// SSE не реализуем: GET по маршруту — 405, и CLI это переживает. Проба идёт на КАЖДОМ
    /// подключении сервера, поэтому отказом инструмента она считаться не должна — иначе
    /// таблица GET /api/mcp/calls и алерт 04-mcp-errors получают ложный отказ каждый ход.
    /// </summary>
    [Fact]
    public async Task Get_ПоМаршруту_Отвечает405_ИНеСчитаетсяОтказомИнструмента()
    {
        var probe = new HttpRequestMessage(HttpMethod.Get, "/mcp/widgets");
        probe.Headers.Add("X-Caller-Session-Id", "live-probe");
        var resp = await _client.SendAsync(probe);
        resp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        var calls = await _client.GetFromJsonAsync<JsonElement>("/api/mcp/calls");
        calls.GetProperty("recentFailures").EnumerateArray()
            .Should().NotContain(f => f.GetProperty("path").GetString() == "/mcp/widgets"
                && f.GetProperty("statusCode").GetInt32() == 405,
                "штатная проба SSE — не сбой");
    }

    [Fact]
    public async Task БезТокена_Отказ401()
    {
        using var anonymous = _factory.CreateClient();
        var resp = await anonymous.PostAsync("/mcp/widgets",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Состав tools/list константен: заголовки хода, тело запроса и состояние сессии на него
    /// не влияют. Это тот же инвариант, что McpToolsetStabilityTests держит для stdio-серверов
    /// (сигнатура запуска CLI), но на новой поверхности — состав отдаёт эндпоинт.
    /// </summary>
    [Fact]
    public async Task СоставИнструментов_НеЗависитОтЗаголовковТелаИСессии()
    {
        static string Fingerprint(JsonElement tools) => string.Join('\n', tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() + "|" + t.GetProperty("inputSchema").GetRawText()));

        var plain = Fingerprint((await RpcAsync("tools/list")).GetProperty("result").GetProperty("tools"));

        using var withContext = _factory.CreateAuthenticatedClient();
        withContext.DefaultRequestHeaders.Add("X-Caller-Session-Id", Guid.NewGuid().ToString());
        withContext.DefaultRequestHeaders.Add("X-Mcp-Tool", "widget_show");
        var resp = await withContext.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "tools/list",
            @params = new { cursor = "чушь", agentDepth = 3 },
        });
        var loaded = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        Fingerprint(loaded.GetProperty("result").GetProperty("tools")).Should().Be(plain);
    }

    /// <summary>
    /// Тулсеты не имеют доступа к HttpContext и SessionManager: единственный вход — параметры
    /// CallAsync. Иначе состав или поведение инструмента незаметно привяжется к ходу.
    /// </summary>
    [Fact]
    public void Тулсеты_НеЗависятОтHttpContextИSessionManager()
    {
        var toolsets = typeof(ClaudeHomeServer.Services.Mcp.Http.IMcpToolset).Assembly.GetTypes()
            .Where(t => typeof(ClaudeHomeServer.Services.Mcp.Http.IMcpToolset).IsAssignableFrom(t)
                && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        toolsets.Should().NotBeEmpty("хотя бы один тулсет обязан существовать");
        foreach (var type in toolsets)
        foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            new[] { "IHttpContextAccessor", "HttpContext", "SessionManager" }
                .Should().NotContain(parameter.ParameterType.Name,
                    $"тулсет {type.Name} не смеет заглядывать в состояние хода");
    }

    [Fact]
    public async Task НеизвестныйСервер_Отказ404()
    {
        var resp = await _client.PostAsJsonAsync("/mcp/такого-нет",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
