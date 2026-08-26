using System.Reflection;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Как виджеты объявляются ходу после переезда на MCP-over-HTTP (ADR-012): узел конфига,
/// адрес, заголовки и отпечаток транспорта в сигнатуре запуска. Конфиг строим настоящим
/// BuildTurnMcpConfig — текстовая проверка исходника тут ничего не доказала бы: ошибка в
/// узле видна только тем, что инструмент МОЛЧА исчезает у модели.
///
/// Сторожа стабильности состава (McpToolsetStabilityTests) это не заменяет и не трогает:
/// там инвариант «состав не зависит от хода», здесь — форма объявления сервера.
/// </summary>
public class McpHttpTransportConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-mcp-http-config-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];

    public McpHttpTransportConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { /* уборка best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Настоящий BuildTurnMcpConfig сессии: путь temp-конфига + строка сигнатуры серверов
    private (JsonObject Servers, string ServerKeys) BuildConfig(WidgetsMcpContext widgets)
    {
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            WidgetsMcp: widgets);
        var session = new ClaudeSession(new Session(), context);

        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        // У ValueTuple элементы — ПОЛЯ, а не свойства
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        // Пустой путь возможен только в stdio-ветке и только вне дерева репозитория
        // (index.js сервера не найден) — это тоже валидный исход: главное, что ход не
        // поехал по негодному http-адресу
        if (string.IsNullOrEmpty(path)) return ([], keys);
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        return ((JsonObject)doc["mcpServers"]!, keys);
    }

    [Fact]
    public void ГодныйАдрес_ВиджетыОбъявленыHttpЭндпоинтомСТокеномВладельца()
    {
        var (servers, keys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", "tok-A", UseHttp: true));

        var widgets = servers["widgets"]!.AsObject();
        widgets["type"]!.GetValue<string>().Should().Be("http");
        widgets["url"]!.GetValue<string>().Should().Be("http://localhost:5000/mcp/widgets");
        widgets.ContainsKey("command").Should().BeFalse("процесс node ради виджетов не поднимается");
        // alwaysLoad у http остаётся: без него первый вызов в ходе падает «No such tool
        // available», а в режиме deferred-tools сервер и вовсе прячет инструменты
        widgets["alwaysLoad"]!.GetValue<bool>().Should().BeTrue();

        var headers = widgets["headers"]!.AsObject();
        headers["Authorization"]!.GetValue<string>().Should().Be("Bearer tok-A",
            "сервисный JWT владельца уезжает обычным заголовком — эндпоинт проверяет его сам");
        // При http заголовки шлёт CLI, а не наш код: что не положено статикой в конфиг,
        // до бэкенда не доедет. На этом заголовке держатся [DenyOnDelegatedTurn] и журнал
        // GET /api/mcp/calls — в фазе 2 его потеря вернула бы платный цикл делегирования.
        headers.ContainsKey("X-Caller-Session-Id").Should().BeTrue();
    }

    /// <summary>
    /// Не-http адрес (документированное лечение HTTPS-деплоя через McpTasksApiUrl) — сервер
    /// объявляется по-старому, через node. Именно fail-closed: молча остаться без инструмента
    /// нельзя, а по https CLI до локального Kestrel не доедет.
    /// </summary>
    [Fact]
    public void АдресНеHttp_FailClosed_ВиджетыЕдутПрежнимStdioСервером()
    {
        var (servers, keys) = BuildConfig(
            new WidgetsMcpContext("https://naychenko.me", "tok-A", UseHttp: false));

        if (servers["widgets"] is { } node)
        {
            var widgets = node.AsObject();
            widgets.ContainsKey("type").Should().BeFalse("http-узла быть не должно");
            widgets["command"]!.GetValue<string>().Should().Be("node",
                "инструмент обязан остаться у модели: тихая потеря недопустима");
            widgets["args"]![0]!.GetValue<string>().Should().EndWith("index.js");
            keys.Should().Contain("widgets:t:stdio");
        }
        else
        {
            // Сборка вне дерева репозитория: index.js stdio-сервера не найден. Тогда
            // сервера в конфиге нет вовсе — и это всё равно НЕ выход по http.
            keys.Should().NotContain("widgets", "негодный адрес не смеет протащить http-узел");
        }
    }

    /// <summary>
    /// Транспорт входит в сигнатуру запуска: переключение рубильника обязано пробить живой
    /// процесс доживания. Иначе рубильник отката «применится когда-нибудь потом» — то есть
    /// не будет работать ровно тогда, когда он понадобится.
    /// </summary>
    [Fact]
    public void ТранспортВходитВСигнатуруЗапуска()
    {
        var (_, httpKeys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", "tok-A", UseHttp: true));
        var (_, otherKeys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", "tok-A", UseHttp: false));

        httpKeys.Should().Contain("widgets:t:http");
        // Вне дерева репозитория вторая сигнатура вырождается в пустую — она всё равно другая,
        // то есть переключение рубильника обязано убить живой процесс доживания
        otherKeys.Should().NotBe(httpKeys, "смена транспорта меняет сигнатуру прогона");
    }

    [Theory]
    // Годный адрес: обычный loopback Kestrel и мост песочницы
    [InlineData("http://localhost:5000", true, true)]
    [InlineData("http://host.docker.internal:5000", true, true)]
    [InlineData("http://192.168.7.65", true, true)]
    // https — молчаливая смерть инструмента (ERR_TLS_CERT_ALTNAME_INVALID): fail-closed
    [InlineData("https://naychenko.me", true, false)]
    // Рубильник отката сильнее годного адреса
    [InlineData("http://localhost:5000", false, false)]
    // Мусор в McpTasksApiUrl — тоже не повод ехать по http
    [InlineData("не-адрес", true, false)]
    [InlineData("", true, false)]
    public void ГейтТранспорта_РешаетСхемаАдресаИРубильник(string url, bool enabled, bool expected) =>
        McpHttpTransport.Usable(url, enabled).Should().Be(expected);

    [Fact]
    public void АдресЭндпоинта_КлеитсяБезДвойногоСлеша() =>
        McpHttpTransport.EndpointFor("http://localhost:5000/", "widgets")
            .Should().Be("http://localhost:5000/mcp/widgets");
}
