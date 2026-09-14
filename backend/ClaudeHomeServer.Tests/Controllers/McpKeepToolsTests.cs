using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Белый список инструментов профиля провайдера (<c>LlmProviderConfig.KeepMcpTools</c>):
/// сервер остаётся целиком, а из его состава едет ровно перечисленное. Нужен там, где сервер
/// без вариантов нужен, а отдельные его инструменты — нет: у tasks это tasks_delete и
/// tasks_run_executor, у memory — team_memory_* (запись в общее знание команды).
///
/// Проверяем ОБА гейта. Фильтр только в tools/list был бы косметическим: имя инструмента
/// модель знает из прошлого опыта или угадывает, поэтому вызов вырезанного обязан отвечать
/// отказом и НЕ доходить до тулсета (fail-closed). Тулсет-стенд считает свои вызовы — это
/// и есть доказательство «не выполнился», текста ответа для него мало.
/// </summary>
public class McpKeepToolsTests : IDisposable
{
    private const string Server = "test-keep";

    // Модели трёх профилей: с фильтром, без фильтра (обратная совместимость) и с фильтром,
    // где рядом с настоящим именем стоит несуществующее
    private const string FilteredModel = "keeptools-filtered-model";
    private const string PlainModel = "keeptools-plain-model";
    private const string UnknownModel = "keeptools-unknown-model";

    /// <summary>Тулсет-стенд: три инструмента и журнал фактических вызовов.</summary>
    private sealed class DemoToolset : IMcpStaticToolset
    {
        public List<string> Calls { get; } = [];

        public string Name => Server;
        public string Version => "0.0.1";

        private static McpToolSchema Schema(string name) => new(name, $"Стенд: {name}",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

        public IReadOnlyList<McpToolSchema> Tools { get; } =
            [Schema("demo_read"), Schema("demo_write"), Schema("demo_delete")];

        public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
            McpToolCallContext context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(tool);
            // Как настоящие тулсеты: неизвестное имя — исключение, контроллер обернёт его в
            // content-ошибку. Журнал пишем ДО отказа: тест fail-closed доказывает, что вызов
            // сюда вовсе не дошёл
            if (!Tools.Any(t => t.Name == tool))
                throw new ArgumentException($"Неизвестный инструмент: {tool}");
            return Task.FromResult(new McpToolCallResult($"выполнено: {tool}"));
        }
    }

    private readonly DemoToolset _toolset = new();
    private readonly TestWebApplicationFactory _factory;

    public McpKeepToolsTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraConfig =
            {
                // Профиль с белым списком: из трёх инструментов стенда остаётся один
                ["LlmProviders:keeptools-filtered:AnthropicBaseUrl"] = "https://example.invalid",
                ["LlmProviders:keeptools-filtered:ApiKey"] = "sk-test",
                ["LlmProviders:keeptools-filtered:Models:0:Id"] = FilteredModel,
                [$"LlmProviders:keeptools-filtered:KeepMcpTools:{Server}:0"] = "demo_read",

                // Профиль без KeepMcpTools — прежнее поведение
                ["LlmProviders:keeptools-plain:AnthropicBaseUrl"] = "https://example.invalid",
                ["LlmProviders:keeptools-plain:ApiKey"] = "sk-test",
                ["LlmProviders:keeptools-plain:Models:0:Id"] = PlainModel,

                // Профиль, где в списке есть несуществующее имя: состав — пересечение,
                // а не объединение, и ход от этого не падает
                ["LlmProviders:keeptools-unknown:AnthropicBaseUrl"] = "https://example.invalid",
                ["LlmProviders:keeptools-unknown:ApiKey"] = "sk-test",
                ["LlmProviders:keeptools-unknown:Models:0:Id"] = UnknownModel,
                [$"LlmProviders:keeptools-unknown:KeepMcpTools:{Server}:0"] = "demo_read",
                [$"LlmProviders:keeptools-unknown:KeepMcpTools:{Server}:1"] = "demo_такого_нет",
            },
        };
        _factory.ExtraServices = s => s.AddSingleton<IMcpToolset>(_toolset);
    }

    public void Dispose() => _factory.Dispose();

    // Чат владельца с заданной моделью: провайдер профиля резолвится из НЕЁ (свойство сессии)
    private async Task<HttpClient> CallerAsync(string model)
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/chats", new { mode = "auto", model });
        created.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());
        chat.GetProperty("model").GetString().Should().Be(model,
            "профиль резолвится по модели чата — без неё тест проверял бы не то");
        // Заголовок сессии-вызывателя кладёт в конфиг хода ClaudeSession; здесь повторяем его руками
        client.DefaultRequestHeaders.Add("X-Caller-Session-Id", chat.GetProperty("id").GetString()!);
        return client;
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object? @params = null)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/{Server}", @params is null
            ? new { jsonrpc = "2.0", id = 1, method }
            : (object)new { jsonrpc = "2.0", id = 1, method, @params });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<IReadOnlyList<string>> ToolsAsync(HttpClient client) =>
        [.. (await RpcAsync(client, "tools/list")).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];

    [Fact]
    public async Task СписокЗадан_ToolsList_ОтдаётРовноЕго()
    {
        var tools = await ToolsAsync(await CallerAsync(FilteredModel));

        tools.Should().Equal("demo_read");
        tools.Should().NotContain("demo_write").And.NotContain("demo_delete",
            "не перечисленные в KeepMcpTools инструменты в состав не попадают");
    }

    [Fact]
    public async Task СписокНеЗадан_СерверЦеликом()
    {
        var tools = await ToolsAsync(await CallerAsync(PlainModel));

        tools.Should().BeEquivalentTo(new[] { "demo_read", "demo_write", "demo_delete" },
            "у провайдера без KeepMcpTools поведение прежнее — обратная совместимость");
    }

    /// <summary>
    /// Второй гейт: вызов вырезанного инструмента — отказ, а не выполнение. Мутационная
    /// проверка задачи: уберите фильтр из ветки tools/call — этот тест обязан покраснеть
    /// (тулсет запишет demo_delete в журнал вызовов).
    /// </summary>
    [Fact]
    public async Task ОтфильтрованныйИнструмент_ToolsCall_Отказ_АНеВыполнение()
    {
        var client = await CallerAsync(FilteredModel);

        var answer = await RpcAsync(client, "tools/call",
            new { name = "demo_delete", arguments = new { } });

        answer.TryGetProperty("error", out _).Should().BeFalse(
            "отказ инструмента — content-ошибка, а не разрыв протокола");
        var result = answer.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("demo_delete").And.Contain("недоступен");
        lock (_toolset.Calls)
            _toolset.Calls.Should().BeEmpty("fail-closed: до тулсета вызов доходить не должен");
    }

    [Fact]
    public async Task РазрешённыйИнструмент_ToolsCall_Выполняется()
    {
        var client = await CallerAsync(FilteredModel);

        var text = (await RpcAsync(client, "tools/call", new { name = "demo_read", arguments = new { } }))
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();

        text.Should().Be("выполнено: demo_read");
        lock (_toolset.Calls) _toolset.Calls.Should().Equal("demo_read");
    }

    [Fact]
    public async Task НеизвестноеИмяВСписке_НеРоняетХод_ИНичегоНеДобавляет()
    {
        var client = await CallerAsync(UnknownModel);

        var tools = await ToolsAsync(client);
        tools.Should().Equal(new[] { "demo_read" },
            "состав — пересечение списка с реальным набором сервера");

        // И вызов несуществующего имени остаётся обычной ошибкой инструмента, а не 500
        var answer = await RpcAsync(client, "tools/call",
            new { name = "demo_такого_нет", arguments = new { } });
        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Запрос не от хода (заголовка сессии нет) профиля не даёт — сервер отдаётся целиком.
    /// Это не дыра: белый список живёт в конфиге провайдера и режет ход локальной модели,
    /// а «резать всем по умолчанию» — другая ось (TrimMcpServers, целыми серверами).
    /// </summary>
    [Fact]
    public async Task БезСессииВызывателя_ФильтраНет()
    {
        var tools = await ToolsAsync(_factory.CreateAuthenticatedClient());

        tools.Should().BeEquivalentTo("demo_read", "demo_write", "demo_delete");
    }

    /// <summary>
    /// Инвариант стабильности состава (McpToolsetStabilityTests, но на поверхности http):
    /// фильтр зависит от СЕССИИ, а не от хода — заголовки и тело запроса его не двигают.
    /// Мерцание состава между ходами перезапускает процесс CLI со всеми MCP-серверами
    /// («Stream closed», «No such tool available»).
    /// </summary>
    [Fact]
    public async Task СоставСФильтром_НеЗависитОтЗаголовковИТелаХода()
    {
        var client = await CallerAsync(FilteredModel);

        var plain = await ToolsAsync(client);
        client.DefaultRequestHeaders.Add("X-Mcp-Tool", "demo_write");
        var loaded = (await RpcAsync(client, "tools/list", new { cursor = "чушь", agentDepth = 3 }))
            .GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

        // Сначала закрепляем, что фильтр вообще работает: без этой строки тест зелёный и
        // при полностью удалённом фильтре (оба списка станут одинаково полными), то есть
        // проверял бы идемпотентность вместо инвариантности состава (ревью 2026-09-06, L-4)
        plain.Should().Equal("demo_read");
        loaded.Should().Equal(plain);
    }
}
