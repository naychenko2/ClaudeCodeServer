using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Тулсеты не имеют доступа к HttpContext: единственный вход — параметры CallAsync и DI.
    /// Иначе состав или поведение инструмента незаметно привяжется к ходу (заголовки, путь,
    /// пользователь HTTP-запроса). SessionManager из запретов волны 2 исключён: тулсетам
    /// задач/заметок/персон он нужен легально — изоляция сессии-вызывателя из хвоста
    /// (GetOwned), анти-рекурсия делегирования (DelegatedTurnGate) и живые формулы прав
    /// (TasksMcpEnabled/PersonasEnabled) — всё это свойства СЕССИИ, а не хода (ADR-012).
    /// </summary>
    [Fact]
    public void Тулсеты_НеЗависятОтHttpContext()
    {
        var toolsets = typeof(ClaudeHomeServer.Services.Mcp.Http.IMcpToolset).Assembly.GetTypes()
            .Where(t => typeof(ClaudeHomeServer.Services.Mcp.Http.IMcpToolset).IsAssignableFrom(t)
                && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        toolsets.Should().NotBeEmpty("хотя бы один тулсет обязан существовать");
        foreach (var type in toolsets)
        foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            new[] { "IHttpContextAccessor", "HttpContext" }
                .Should().NotContain(parameter.ParameterType.Name,
                    $"тулсет {type.Name} не смеет заглядывать в HTTP-контекст запроса");
    }

    [Fact]
    public async Task НеизвестныйСервер_Отказ404()
    {
        var resp = await _client.PostAsJsonAsync("/mcp/такого-нет",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Промах мимо шаблона mcp/{name} — fail-closed 404, а не 200 c index.html из
    /// SPA-фолбэка: клиент JSON-RPC обязан видеть ошибку, а не HTML-страницу. Вложенные
    /// пути разбирает шаблон {name}/{**route} (ADR-012, фаза 2: хвост параметризованных
    /// тулсетов) — неизвестное имя честно называет себя unknown_mcp_server; маршрут вовсе
    /// без имени — catch-all RouteMiss. GET-проба на вложенном пути — тот же NoSse-405,
    /// что и на одно-сегментном (шаблон GET ловит хвост тоже).
    /// </summary>
    [Fact]
    public async Task ПромахМаршрутаВложенныйПуть_Отдаёт404АНеSPA()
    {
        var post = await _client.PostAsJsonAsync("/mcp/a/b",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await post.Content.ReadAsStringAsync()).Should().Contain("unknown_mcp_server",
            "вложенный путь разбирает шаблон {name}/{**route} — честный 404 с телом, не SPA-HTML");

        var deeper = await _client.PostAsJsonAsync("/mcp/a/b/c",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        deeper.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "catch-all-хвост не выпускает промах в SPA-фолбэк");

        (await _client.GetAsync("/mcp/a/b")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed,
            "GET-проба на вложенном пути — тот же NoSse, что и на одно-сегментном");
        // Сам маршрут без имени — тот же промах
        var bare = await _client.PostAsync("/mcp", null);
        bare.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bare.Content.ReadAsStringAsync()).Should().Contain("mcp_route_not_found");
    }

    /// <summary>
    /// Имя вызова приходит из ТЕЛА JSON-RPC — лимит заголовков Kestrel его больше не режет.
    /// Строка в 200 000 символов с CRLF не должна становиться ни ключом McpCallLog, ни
    /// подстановкой в логи: негодное по форме (ASCII, ≤64) схлопывается в «(прочее)».
    /// </summary>
    [Fact]
    public async Task ГигантскоеИмяСCRLF_НеПопадаетВЖурналВызовов()
    {
        var evilName = new string('a', 200_000) + "\r\nX-Injected: 1";
        var evilMethod = new string('b', 200_000) + "\r\n";

        // Журнал пишет только запросы хода (X-Caller-Session-Id лежит в конфиге хода) —
        // без него проверять нечего: строка из тела до McpCallLog не доходит вовсе
        using var caller = _factory.CreateAuthenticatedClient();
        caller.DefaultRequestHeaders.Add("X-Caller-Session-Id", "evil-name-probe");

        var call = await caller.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = evilName, arguments = new { html = "<b/>" } },
        });
        call.StatusCode.Should().Be(HttpStatusCode.OK, "отказ инструмента — content-ошибка, не протокольная");

        var method = await caller.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0", id = 2, method = evilMethod,
        });
        method.StatusCode.Should().Be(HttpStatusCode.OK, "-32601 — протокольная ошибка в теле, не HTTP-отказ");

        var calls = await _client.GetFromJsonAsync<JsonElement>("/api/mcp/calls");
        var tools = calls.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("tool").GetString()).ToList();
        tools.Should().NotContain(t => t != null && t.Length > 64,
            "длинные строки из тела — не ключи журнала");
        tools.Should().NotContain(t => t != null && (t.Contains('\n') || t.Contains('\r')),
            "CRLF из тела — не подстановка в логи");
        tools.Should().Contain("(прочее)", "мусор схлопывается в общую строку переполнения");
    }

    /// <summary>
    /// Настоящий внутренний сбой диспетчера (тулсет-«бомба») — протокольная -32603, а не
    /// голый HTTP 500: клиент MCP, получивший 500, снимает ВЕСЬ набор инструментов сервера
    /// до конца жизни процесса CLI — молчаливый отказ, против которого здесь fail-closed.
    /// </summary>
    [Fact]
    public async Task ВнутреннийСбойТулсета_Отвечает32603_АНе500()
    {
        using var boom = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset>(
                new ExplodingToolset()),
        };

        var client = boom.CreateAuthenticatedClient();
        var answer = await client.PostAsJsonAsync("/mcp/boom",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        answer.StatusCode.Should().Be(HttpStatusCode.OK, "протокольная ошибка едет в теле, не HTTP-кодом");
        var payload = JsonSerializer.Deserialize<JsonElement>(await answer.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32603);
    }

    /// <summary>
    /// Находка ревью: позиционные params (массив/скаляр) разрешены спецификацией JSON-RPC,
    /// а индексатор JsonNode[string] на таком бросает InvalidOperationException — диспетчер
    /// падал голым HTTP 500. Теперь — протокольная -32602 на любом методе.
    /// </summary>
    [Fact]
    public async Task ParamsМассивИлиСкаляр_ПротокольнаяОшибка32602_АНе500()
    {
        var initialize = await _client.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0", id = 1, method = "initialize", @params = new object[] { 1, 2 },
        });
        initialize.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = JsonSerializer.Deserialize<JsonElement>(await initialize.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602,
            "позиционные params мы не поддерживаем — но это ошибка запроса, не сервера");

        var call = await _client.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call", @params = "не объект",
        });
        call.StatusCode.Should().Be(HttpStatusCode.OK);
        payload = JsonSerializer.Deserialize<JsonElement>(await call.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    /// <summary>
    /// Кривое тело: авто-400 [ApiController] раньше отбивал его problem+json ДО контроллера
    /// (ветка -32700 была недостижима), и этот 400 ложился в журнал MCP отказом инструмента.
    /// Тело разбираем вручную — клиент получает честный -32700.
    /// </summary>
    [Fact]
    public async Task КривоеТело_Отвечает32700_АНеAutomatic400()
    {
        var resp = await _client.PostAsync("/mcp/widgets",
            new StringContent("""{"jsonrpc": "2.0", "id": 1, "method" """, Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "ошибка разбора — протокольный код, не HTTP 400");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "JSON-RPC-клиенту не нужен problem+json");
        var payload = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);

        var empty = await _client.PostAsync("/mcp/widgets",
            new StringContent("   ", Encoding.UTF8, "application/json"));
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        payload = JsonSerializer.Deserialize<JsonElement>(await empty.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600,
            "пустое тело — Invalid Request, а не Parse error");
    }

    /// <summary>
    /// Батч по спецификации JSON-RPC 2.0: пустой массив и невалидные элементы — -32600
    /// (раньше молча пропускались и клиент не дожидался ответов), батч из одних уведомлений —
    /// 202 без тела.
    /// </summary>
    [Fact]
    public async Task Батч_ПоСпецификации_ПустойИМусор_32600_Уведомления_202()
    {
        var empty = await _client.PostAsync("/mcp/widgets",
            new StringContent("[]", Encoding.UTF8, "application/json"));
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = JsonSerializer.Deserialize<JsonElement>(await empty.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600,
            "пустой батч — единый ответ с ошибкой, не 202");

        var garbage = await _client.PostAsync("/mcp/widgets",
            new StringContent("[1, 2, 3]", Encoding.UTF8, "application/json"));
        garbage.StatusCode.Should().Be(HttpStatusCode.OK);
        payload = JsonSerializer.Deserialize<JsonElement>(await garbage.Content.ReadAsStringAsync());
        payload.ValueKind.Should().Be(JsonValueKind.Array);
        payload.GetArrayLength().Should().Be(3, "на каждый невалидный элемент — свой -32600");
        payload.EnumerateArray().Should().OnlyContain(e =>
            e.GetProperty("error").GetProperty("code").GetInt32() == -32600);

        var notifications = await _client.PostAsync("/mcp/widgets",
            new StringContent("""[{"jsonrpc":"2.0","method":"notifications/initialized"}]""",
                Encoding.UTF8, "application/json"));
        notifications.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await notifications.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Батч без потолка собирал бы ответ DeepClone'ами схем на каждый элемент: массив из
    /// сотен тысяч мелких запросов укладывается в 30-МБ дефолт Kestrel, а ответ растёт в
    /// гигабайтную строку — OOM инстанса гасит ходы всех пользователей. Потолок режет
    /// ДО входа в цикл.
    /// </summary>
    [Fact]
    public async Task БатчСвышеПотолка_Отвечает32600ДоЦикла()
    {
        var items = string.Join(",", Enumerable.Range(0, 200)
            .Select(i => $"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"tools/list\"}}"));
        var resp = await _client.PostAsync("/mcp/widgets",
            new StringContent($"[{items}]", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "протокольная ошибка едет в теле, не HTTP-кодом");
        var payload = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        payload.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        payload.GetProperty("error").GetProperty("message").GetString()!
            .Should().Contain("потолок 100", "в сообщении видно, почему отказ и где предел");
    }

    /// <summary>
    /// Потолок тела запроса: TestServer не исполняет IRequestSizePolicy (это фича Kestrel),
    /// поэтому проверяем атрибут — та же практика, что ПотолокТелаРезультата у DeviceCalls.
    /// Без предела ReadToEndAsync честно прочитает дефолтные 30 МБ в память.
    /// </summary>
    [Fact]
    public void ПотолокТелаЗапроса_1МБНаЭкшенеПоста()
    {
        var attribute = typeof(ClaudeHomeServer.Controllers.McpTransportController)
            .GetMethod(nameof(ClaudeHomeServer.Controllers.McpTransportController.Handle))!
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), false)
            .Cast<RequestSizeLimitAttribute>()
            .Should().ContainSingle().Subject;

        // У атрибута нет публичного свойства со значением — его читает конвейер через
        // IRequestSizeLimitMetadata, оттуда же берём его в проверке
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)attribute).MaxRequestBodySize
            .Should().Be(1024 * 1024,
                "аргументы инструментов несопоставимо меньше (html виджета ≤64 КБ), 1 МБ — запас на фазу 2");
    }

    /// <summary>
    /// Санитизация имени обязана держаться и в ЛОГЕ, не только в таблице вызовов:
    /// TimestampedConsoleWriter ставит настоящий UTC-таймстемп после каждого \n, и
    /// CRLF-вброс визуально неотличим от подлинных записей бэкенда (CWE-117). Имя печатает
    /// и контроллер (Warning об упавшем инструменте), и McpCallLogMiddleware (Debug/Warning
    /// по каждому запросу хода).
    /// </summary>
    [Fact]
    public async Task ГигантскоеИмяСCRLF_НеПопадаетВЛогБэкенда()
    {
        var sink = new CollectingLogSink();
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILoggerProvider>(sink),
        };

        using var caller = factory.CreateAuthenticatedClient();
        caller.DefaultRequestHeaders.Add("X-Caller-Session-Id", "evil-log-probe");
        var call = await caller.PostAsJsonAsync("/mcp/widgets", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new
            {
                name = new string('a', 200_000) + "\r\nX-Injected: 1",
                arguments = new { html = "<b/>" },
            },
        });
        call.StatusCode.Should().Be(HttpStatusCode.OK, "отказ инструмента — content-ошибка, не протокольная");

        var entries = sink.Entries.Where(e => e.Text.Contains("MCP")).ToList();
        entries.Should().NotBeEmpty("упавший инструмент обязан оставить след в логе");
        entries.Should().NotContain(e => e.Text.Contains("X-Injected"),
            "CRLF-вброс не должен попадать в записи лога");
        entries.Should().OnlyContain(e => e.Text.Length <= 2_000,
            "имя в сотни КБ не должно раздувать записи лога");
    }

    // Сборщик записей лога: у TestServer нет консольного вывода, а проверить нужно сам текст,
    // который поедет в TimestampedConsoleWriter (переносы внутри записи там рисуют
    // поддельные строки с настоящими таймстемпами)
    private sealed class CollectingLogSink : ILoggerProvider
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CollectingLogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink.Entries) sink.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // Тулсет-«бомба»: tools/list падает исключением за пределами catch tools/call —
    // проверка, что диспетчер превращает внутренний сбой в -32603, а не в HTTP 500
    private sealed class ExplodingToolset : ClaudeHomeServer.Services.Mcp.Http.IMcpToolset
    {
        public string Name => "boom";
        public string Version => "0.0.1";
        public IReadOnlyList<ClaudeHomeServer.Services.Mcp.Http.McpToolSchema> Tools =>
            throw new InvalidOperationException("тестовый взрыв тулсета");

        public Task<ClaudeHomeServer.Services.Mcp.Http.McpToolCallResult> CallAsync(string tool,
            System.Text.Json.Nodes.JsonObject arguments,
            ClaudeHomeServer.Services.Mcp.Http.McpToolCallContext context, CancellationToken ct)
            => throw new InvalidOperationException("тестовый взрыв тулсета");
    }
}
