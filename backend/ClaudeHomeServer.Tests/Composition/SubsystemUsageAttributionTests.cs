using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.Services.Gateway;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Composition;

// Учёт обращений по ключу подсистемы: карта «сборка → ключ», мидлвар на живом хосте,
// REST-поля SubsystemInfo, сброс на диск и подъём после рестарта. Хост — один на класс;
// сквозной сценарий со счётом «ровно» и рестартом поднимает два своих хоста (счёт должен
// начинаться с нуля, а общий хост копит обращения соседних тестов).
[Trait("Category", "Host")]
public class SubsystemUsageAttributionTests(SubsystemUsageAttributionTests.Hosts hosts)
    : IClassFixture<SubsystemUsageAttributionTests.Hosts>
{
    // Создание хостов — только внутри фабрики-наследника (храповик «хост на класс»)
    public sealed class Hosts : TestWebApplicationFactory
    {
        public static Hosts Fresh() => new();

        // Новый хост на ТОМ ЖЕ каталоге данных — «рестарт» процесса
        public Hosts RestartOnSameData()
        {
            var next = new Hosts();
            next.ExtraConfig["DataPath"] = Path.Combine(TempDir, "projects.json");
            return next;
        }

        // Хост, у которого звонок шлюза «в свой Kestrel» уходит в TestServer этого же хоста —
        // тот же подвох, что в боевой интеграции шлюза (McpGatewayIntegrationTests.Hosts):
        // без него внутренний запрос шлюза не доезжает до мидлвара этого же процесса.
        public static Hosts GatewayLoopback() => new()
        {
            ExtraServices = s => s.AddHttpClient(McpGatewayEndpoints.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler()),
        };
    }

    private readonly Hosts _factory = hosts;

    private static SubsystemUsageStore StoreOf(TestWebApplicationFactory f) =>
        f.Services.GetRequiredService<SubsystemUsageStore>();

    private static int HitsOf(SubsystemUsageStore store, string key) =>
        store.Snapshot(DateTimeOffset.UtcNow).FirstOrDefault(e => e.Key == key)?.Last24h ?? 0;

    private static async Task<JsonNode> AdminRowAsync(HttpClient client, string key)
    {
        var rows = JsonNode.Parse(await client.GetStringAsync("/api/admin/subsystems"))!.AsArray();
        return rows.Single(n => n!["key"]!.GetValue<string>() == key)!;
    }

    // Тулсет notes параметризован: хвост маршрута — id сессии-вызывателя (чужой хвост даёт
    // пустой состав, но маршрут найден и обращение к подсистеме засчитывается)
    private static async Task McpListAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/mcp/notes/" + Guid.NewGuid().ToString("N"),
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ВызовКонтроллераВертикали_СчитаетсяПоКлючуПодсистемы()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(StoreOf(_factory), "notes");

        var resp = await client.GetAsync("/api/notes");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        HitsOf(StoreOf(_factory), "notes").Should().Be(before + 1);
    }

    [Fact]
    public async Task ВызовБезТокена_НеСчитается()
    {
        using var anon = _factory.CreateClient();
        var before = HitsOf(StoreOf(_factory), "notes");

        var resp = await anon.GetAsync("/api/notes");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        HitsOf(StoreOf(_factory), "notes").Should().Be(before);
    }

    [Fact]
    public async Task РучкаЯдра_НеСчитаетсяНиЗаКакуюПодсистему()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = StoreOf(_factory).Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h);

        (await client.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        // SubsystemsController тоже лежит в Main — ядро
        (await client.GetAsync("/api/admin/subsystems")).StatusCode.Should().Be(HttpStatusCode.OK);

        StoreOf(_factory).Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h).Should().Be(before);
    }

    [Fact]
    public async Task Сценарий_СчётHTTPиMCP_СбросНаДиск_ПодъёмПослеРестарта()
    {
        // ХОСТ А: счёт с нуля
        using var a = Hosts.Fresh();
        using var clientA = a.CreateAuthenticatedClient();

        for (var i = 0; i < 3; i++) await clientA.GetAsync("/api/notes");

        var row = await AdminRowAsync(clientA, "notes");
        row["usedLast24h"]!.GetValue<int>().Should().Be(3);
        row["lastUsedAt"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        // ручка ядра счёт не меняет
        (await clientA.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        HitsOf(StoreOf(a), "notes").Should().Be(3);
        StoreOf(a).Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h).Should().Be(3);

        // MCP: POST /mcp/notes → ровно +1 тому же ключу
        await McpListAsync(clientA);
        HitsOf(StoreOf(a), "notes").Should().Be(4);

        // остановка сервиса сброса пишет файл
        var flush = a.Services.GetServices<IHostedService>().OfType<SubsystemUsageFlushService>().Single();
        await flush.StopAsync(CancellationToken.None);

        var file = Path.Combine(a.TempDir, SubsystemUsageFlushService.FileName);
        File.Exists(file).Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(file))!["entries"]!.AsArray()
            .Single(e => e!["key"]!.GetValue<string>() == "notes")!["counts"]!.AsArray()
            .Sum(c => c!.GetValue<int>()).Should().Be(4);

        // ХОСТ Б на том же каталоге данных: тот же usedLast24h после старта
        using var b = a.RestartOnSameData();
        using var clientB = b.CreateAuthenticatedClient();
        (await AdminRowAsync(clientB, "notes"))["usedLast24h"]!.GetValue<int>().Should().Be(4);
    }

    private static int TotalOf(SubsystemUsageStore store) =>
        store.Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h);

    [Fact]
    public async Task КонтроллерЯдраСборкиMain_СПривязкой_СчитаетсяПодсистеме()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(StoreOf(_factory), "tasks");

        var resp = await client.GetAsync("/api/tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        HitsOf(StoreOf(_factory), "tasks").Should().Be(before + 1);
    }

    // Ручки llm (Usage/Providers/Models) привязаны атрибутом: обращение к расходу —
    // использование вертикали (тот же путь KeyOf, что у tasks выше)
    [Fact]
    public async Task РучкаLlmСПривязкой_СчитаетсяКлючуLlm()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(StoreOf(_factory), "llm");

        var resp = await client.GetAsync("/api/usage");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        HitsOf(StoreOf(_factory), "llm").Should().Be(before + 1);
    }

    [Fact]
    public async Task McpТулсетMain_СПривязкой_СчитаетсяПодсистеме()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(StoreOf(_factory), "tasks");

        var resp = await client.PostAsJsonAsync("/mcp/tasks/" + Guid.NewGuid().ToString("N"),
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        HitsOf(StoreOf(_factory), "tasks").Should().Be(before + 1);
    }

    [Fact]
    public async Task РучкаЯдра_НеСчитаетсяНикому()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = TotalOf(StoreOf(_factory));

        var resp = await client.GetAsync("/api/projects");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        TotalOf(StoreOf(_factory)).Should().Be(before);
    }

    [Fact]
    public void КартаСборок_ТулсетВертикалиАтрибутируетсяПоИмени_ЯдроНет()
    {
        var attribution = _factory.Services.GetRequiredService<SubsystemUsageAttribution>();
        var toolsets = _factory.Services.GetServices<IMcpToolset>().ToList();

        attribution.KeyOfMcp("notes").Should().Be("notes");
        foreach (var t in toolsets)
        {
            var key = attribution.KeyOfMcp(t.Name);
            if (key is not null) attribution.ObservableKeys.Should().Contain(key);
        }
        attribution.KeyOfMcp("нет-такого-тулсета").Should().BeNull();
        attribution.KeyOfMcp("personas").Should().BeNull("тулсет без подсистемы — ядро");
        attribution.KeyOfMcp("wsp").Should().BeNull("тулсет без подсистемы — ядро");
        attribution.KeyOf(typeof(SubsystemUsageAttributionTests)).Should().BeNull("сборка тестов не подсистема");
    }

    // ─── Классовая атрибуция и наблюдаемость ключей из сборок вертикалей ───

    // Контроллер смешанной ручки: классовой и методовый ключ названы одновременно.
    // Таких контроллеров в проде пока нет — сценарий латентный, проверяется синтетически.
    [SubsystemUsage("personas")]
    private sealed class MixedKeyController
    {
        [SubsystemUsage("images")]
        public Task VerticalEndpoint() => Task.CompletedTask;
    }

    // Тип в «сборке вертикали» теста: тест-сборка зарегистрирована домашней для «stub»,
    // а ключ методового атрибута — строка ядра: активна, но не наблюдаема ни по своей
    // сборке (CoreAssemblies пропущены), ни по Main. Без скана сборок вертикалей её
    // обращения молча терялись бы в сторе.
    private sealed class VerticalHomeController
    {
        [SubsystemUsage("core:stub")]
        public Task CountedEndpoint() => Task.CompletedTask;
    }

    private sealed class PersonasStubSubsystem : IAppSubsystem
    {
        public string Key => "personas";
        public string Title => "Заглушка";
        public void Register(IServiceCollection services, IConfiguration config) { }
    }

    private sealed class ImagesStubSubsystem : IAppSubsystem
    {
        public string Key => "images";
        public string Title => "Заглушка";
        public void Register(IServiceCollection services, IConfiguration config) { }
    }

    private static ControllerActionDescriptor DescriptorOf(Type controller, string methodName) =>
        new() { ControllerTypeInfo = (System.Reflection.TypeInfo)controller, MethodInfo = controller.GetMethod(methodName)! };

    // Неактивный ключ метода не отменяет классовый — атрибуция откатывается к классовой
    [Fact]
    public void МетодовыйКлючНеАктивен_АтрибутацияОткатываетсяККлассовой()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new PersonasStubSubsystem()); // «personas» активна; «images» — нет
        var attribution = new SubsystemUsageAttribution(state, []);

        var descriptor = DescriptorOf(typeof(MixedKeyController), nameof(MixedKeyController.VerticalEndpoint));
        attribution.KeyOfAction(descriptor).Should().Be("personas",
            "методовой атрибут уточняет классовой, а не отменяет его");
    }

    // Обратная сторона: когда оба ключа активны, методовой по-прежнему приоритнее
    [Fact]
    public void МетодовыйКлючАктивен_ОнПриоритнееКлассового()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new PersonasStubSubsystem());
        state.RecordActive(new ImagesStubSubsystem());
        var attribution = new SubsystemUsageAttribution(state, []);

        var descriptor = DescriptorOf(typeof(MixedKeyController), nameof(MixedKeyController.VerticalEndpoint));
        attribution.KeyOfAction(descriptor).Should().Be("images", "оба ключа активны — методовой выигрывает");
    }

    // Ключ, который вернула атрибуция, обязан быть наблюдаемым — счётчик растёт
    [Fact]
    public void МетодовыйАтрибутВСборкеВертикали_КлючНаблюдаем_СчётчикРастёт()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new StubSubsystem()); // тест-сборка — домашняя сборка вертикали «stub»
        state.RecordCore("core:stub", "Строка ядра", "");
        var attribution = new SubsystemUsageAttribution(state, []);

        var descriptor = DescriptorOf(typeof(VerticalHomeController), nameof(VerticalHomeController.CountedEndpoint));
        attribution.KeyOfAction(descriptor).Should().Be("core:stub");
        attribution.ObservableKeys.Should().Contain("core:stub",
            "ключ, который вернула атрибуция, обязан быть в наборе стора");

        var store = new SubsystemUsageStore(attribution.ObservableKeys);
        var now = DateTimeOffset.UtcNow;
        store.Hit("core:stub", now);
        store.Snapshot(now).Single(e => e.Key == "core:stub").Last24h.Should().Be(1);
    }

    [Fact]
    public void Snapshot_БезИспользования_ПоляNull()
    {
        var store = new SubsystemStateStore();
        store.RecordActive(new StubSubsystem());
        var info = store.Snapshot(new ConfigurationBuilder().Build()).Single();
        info.LastUsedAt.Should().BeNull();
        info.UsedLast24h.Should().BeNull();
    }

    private sealed class StubSubsystem : IAppSubsystem
    {
        public string Key => "stub";
        public string Title => "Заглушка";
        public void Register(IServiceCollection services, IConfiguration config) { }
    }

    // ─── Гарантированный сброс, лог сбоя атрибуции, MCP через /gw ───

    private static Dictionary<string, string?> DataDirConfig(string dir) =>
        new() { ["DataPath"] = Path.Combine(dir, "projects.json") };

    [Fact]
    public async Task ОтменённыйТокенОстановки_ФинальныйСбросВсёРавноПишетФайл()
    {
        // Остановка с отменённым токеном — файл обязан быть записан. Закреплено этим тестом:
        // именно путь отменённого токена, а НЕ finally. В net10 base.StopAsync отказа не даёт ни на
        // отменённом токене, ни на упавшем ExecuteAsync, ни без предшествующего StartAsync
        // (проверено прогоном на SDK 10.0.114: три случая — без исключения); в 9.0 тот же исход по
        // исходникам рантайма (Task.WhenAny). Поэтому без finally этот тест зелёный — и в этом
        // дереве: проведено снятием finally (1/1 зелёный).
        // Страховка «любой отказ base не снимает последний сброс» тестом не закреплена:
        // достижимого отказа base в текущем рантайме нет.
        var dir = Path.Combine(Path.GetTempPath(), "subsys-flush-" + Guid.NewGuid().ToString("N"));
        var store = new SubsystemUsageStore(["notes"]);
        store.Hit("notes", DateTimeOffset.UtcNow);
        var service = new SubsystemUsageFlushService(
            store, new ConfigurationBuilder().AddInMemoryCollection(DataDirConfig(dir)).Build(),
            NullLogger<SubsystemUsageFlushService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await service.StopAsync(cts.Token);

            var file = Path.Combine(dir, SubsystemUsageFlushService.FileName);
            File.Exists(file).Should().BeTrue("последний сброс пишется при остановке с отменённым токеном");
            JsonNode.Parse(File.ReadAllText(file))!["entries"]!.AsArray()
                .Single(e => e!["key"]!.GetValue<string>() == "notes")!["counts"]!.AsArray()
                .Sum(c => c!.GetValue<int>()).Should().Be(1);
        }
        finally
        {
            service.Dispose();
            // каталога может не быть: его создаёт Save() — без финального сброса он не создан,
            // и падать должен именно тест, а не его уборка
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // Сток записей лога для проверки самого факта записи (у TestServer нет консольного вывода)
    private sealed class CapturingLog<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    // Эндпоинт мидлвар читает через IEndpointFeature (GetEndpoint в ASP.NET Core 3+
    // не смотрит на legacy IRoutingFeature) — его и выставляем
    private sealed class TestEndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    // Сбой атрибуции не ломает ответ (200 остаётся) и пишется в лог — раньше молча терялся.
    // Сбой синтетический: дескриптор с null MethodInfo — KeyOfAction падает в NRE; предмет
    // проверки — поведение мидлвара, а не форма отказа.
    [Fact]
    public async Task СбойАтрибуцииПишетсяВЛог_ОтветНеЛомается()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new NotesStubSubsystem());
        var attribution = new SubsystemUsageAttribution(state, []);
        var store = new SubsystemUsageStore(attribution.ObservableKeys);
        var log = new CapturingLog<SubsystemUsageMiddleware>();
        var middleware = new SubsystemUsageMiddleware(
            static ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            attribution, store, log);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/notes";
        ctx.Features.Set<IEndpointFeature>(new TestEndpointFeature
        {
            Endpoint = new Endpoint(null,
                new EndpointMetadataCollection(new ControllerActionDescriptor { MethodInfo = null! }), "test"),
        });

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        log.Lines.Should().Contain(l => l.Contains("Сбой атрибуции"));
    }

    // Одна нога вызова через шлюз = один счёт. Внешняя /gw/t/{turnId}/mcp/{name} — не обращение
    // к тулсету: до него доходит внутренний запрос шлюза /mcp/{name}/{хвост}, он и есть +1.
    // Эндпоинт шлюза — минимальный (app.Map), без ControllerActionDescriptor, поэтому внешняя нога
    // не даёт счёта ни по сегменту, ни по действию. Прямая ручка — прежний ключ, в том числе
    // в другом регистре: StartsWithSegments был регистронезависим — сегментный поиск обязан быть таким же.
    [Fact]
    public async Task ШлюзХодовMcp_СчитаетсяВнутреннийЗапрос_ВнешнийНет()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new NotesStubSubsystem());
        var attribution = new SubsystemUsageAttribution(state, [new NotesToolsetStub()]);
        var store = new SubsystemUsageStore(attribution.ObservableKeys);
        var middleware = new SubsystemUsageMiddleware(
            static ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            attribution, store, NullLogger<SubsystemUsageMiddleware>.Instance);

        static DefaultHttpContext Ctx(string path)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            return ctx;
        }

        // нога 1: клиент → шлюз. До тулсета ещё не дошло — обращения не было
        await middleware.InvokeAsync(Ctx("/gw/t/" + Guid.NewGuid().ToString("N") + "/mcp/notes/" + Guid.NewGuid().ToString("N")));
        HitsOf(store, "notes").Should().Be(0, "внешний /gw-запрос — не обращение к тулсету");

        // нога 2: шлюз → свой же бэкенд, путь /mcp/{name}/{хвост} — единственный, кто дошёл до тулсета
        await middleware.InvokeAsync(Ctx("/mcp/notes/" + Guid.NewGuid().ToString("N")));
        HitsOf(store, "notes").Should().Be(1, "один вызов тулсета через шлюз = ровно +1");

        // прямая ручка — тот же ключ (прежнее поведение сохранено)
        await middleware.InvokeAsync(Ctx("/MCP/notes/" + Guid.NewGuid().ToString("N")));
        HitsOf(store, "notes").Should().Be(2);
    }

    // На боевом маршруте: внешний /gw/t/{turnId}/mcp/notes/{chat} → шлюз → внутренний
    // /mcp/notes/{chat} в свой же TestServer (тот же мидлвар, Program.cs:1116) = ровно +1.
    // До правки здесь было 2: внешний приписывался по сегменту mcp, внутренний — по своему пути.
    // Мутация «считать обе ноги» = прежний код, краснит ровно на этом тесте.
    [Fact]
    public async Task ВызовТулсетаЧерезШлюзХодов_РовноПлюсОдин()
    {
        using var host = Hosts.GatewayLoopback();
        var ownerId = host.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        using var dev = new GatewayTestDevice(ownerId, host.Services.GetRequiredService<DeviceRegistry>());
        var chat = Guid.NewGuid().ToString("N");
        var turn = host.Services.GetRequiredService<TurnTokenService>().Issue(ownerId, chat, dev.Id);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{turn.Grant.TurnId}/mcp/notes/{chat}")
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        req.Headers.Add(TurnTokenEndpointFilter.HeaderName, turn.Token);
        var resp = await host.CreateClient().SendAsync(dev.Sign(req));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        HitsOf(StoreOf(host), "notes").Should().Be(1,
            "один вызов тулсета через шлюз = ровно +1: счёт даёт внутренний запрос шлюза, не внешний");
    }

    private sealed class NotesStubSubsystem : IAppSubsystem
    {
        public string Key => "notes";
        public string Title => "Заглушка";
        public void Register(IServiceCollection services, IConfiguration config) { }
    }

    [SubsystemUsage("notes")]
    private sealed class NotesToolsetStub : IMcpToolset
    {
        public string Name => "notes";
        public string Version => "test";
        public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments, McpToolCallContext context,
            CancellationToken ct) => Task.FromResult(new McpToolCallResult(""));
    }

    // ─── 403/404 от живого контроллера — состоявшееся обращение ───

    // Контроллер, который отвечает 404/403 по своему делу («заметка не найдена»). Живёт в
    // тест-сборке, а она для этой атрибуции — домашняя сборка вертикали «notes»
    // (NotesStubSubsystem выше), поэтому эндпоинт с таким действием относится к «notes».
    private sealed class NotesLikeController
    {
        public Task Get() => Task.CompletedTask;
    }

    private static SubsystemUsageMiddleware MiddlewareWithStatus(
        SubsystemUsageAttribution attribution, SubsystemUsageStore store, int status) =>
        new(ctx => { ctx.Response.StatusCode = status; return Task.CompletedTask; },
            attribution, store, NullLogger<SubsystemUsageMiddleware>.Instance);

    // Граница мидлвара по признаку ctx.GetEndpoint(): 404 и 403 смонтированного эндпоинта —
    // ответ самого обработчика вертикали, они считаются; 404 без эндпоинта (маршрут снят
    // гейтом подсистемы) — обращения не было. 401 — отказ аутентификации ДО обработчика:
    // в боевой сборке UseRouting стоит раньше UseAuthentication (Program.cs:1070, 1107),
    // поэтому эндпоинт при 401 смонтирован всегда и он ничего не значит — не считается.
    [Fact]
    public async Task ОтказСЭндпоинтомСчитается_БезЭндпоинтаИ401Никогда()
    {
        var state = new SubsystemStateStore();
        state.RecordActive(new NotesStubSubsystem());
        var attribution = new SubsystemUsageAttribution(state, []);
        var store = new SubsystemUsageStore(attribution.ObservableKeys);
        var now = DateTimeOffset.UtcNow;

        var cases = new (int Status, bool HasEndpoint, int Expect)[]
        {
            (StatusCodes.Status404NotFound, true, 1), // «заметка не найдена» живым контроллером
            (StatusCodes.Status403Forbidden, true, 2), // «доступа нет» живым контроллером
            (StatusCodes.Status404NotFound, false, 2), // маршрут снят гейтом: эндпоинта нет
            (StatusCodes.Status401Unauthorized, true, 2), // отказ аутентификации до обработчика
        };

        foreach (var (status, hasEndpoint, expect) in cases)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = "/api/notes/" + Guid.NewGuid().ToString("N");
            if (hasEndpoint)
                ctx.Features.Set<IEndpointFeature>(new TestEndpointFeature
                {
                    Endpoint = new Endpoint(null, new EndpointMetadataCollection(
                        DescriptorOf(typeof(NotesLikeController), nameof(NotesLikeController.Get))), "test"),
                });

            await MiddlewareWithStatus(attribution, store, status).InvokeAsync(ctx);

            store.Snapshot(now).Sum(e => e.Last24h).Should().Be(expect,
                $"статус {status}, эндпоинт {(hasEndpoint ? "смонтирован" : "снят гейтом")}");
        }
    }

    // Тот же признак на живом хосте: 404 от самого контроллера вертикали («нет такой заметки»)
    // — состоявшееся обращение; раньше оно не попадало в счёт нигде, т.е. счёт занижался ровно
    // на самых частых запросах.
    [Fact]
    public async Task КонтроллерВертикалиОтветил404_СчитаетсяОбращением()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(StoreOf(_factory), "notes");

        var resp = await client.GetAsync("/api/notes/resolve?name=" + Guid.NewGuid().ToString("N"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        HitsOf(StoreOf(_factory), "notes").Should().Be(before + 1);
    }

    // ─── Порядок «Load до первого Hit по ключу» ───

    // Первый после рестарта запрос обязан идти к самой вертикале, а не к админской ручке ядра:
    // тогда первый Hit по ключу случается ПОСЛЕ загрузки её состояния с диска и накопляется
    // на него. Мутация «убрать store.Load из SubsystemUsageFlushService.StartAsync» краснит
    // ровно этот тест: без Load счёт начался бы с 1 вместо 3.
    [Fact]
    public async Task Рестарт_ПервыйHitПоКлючуИдётПоверхПрочитанногоСДиска()
    {
        using var a = Hosts.Fresh();
        using var clientA = a.CreateAuthenticatedClient();

        for (var i = 0; i < 2; i++)
            (await clientA.GetAsync("/api/notes")).StatusCode.Should().Be(HttpStatusCode.OK);
        HitsOf(StoreOf(a), "notes").Should().Be(2);

        var flush = a.Services.GetServices<IHostedService>().OfType<SubsystemUsageFlushService>().Single();
        await flush.StopAsync(CancellationToken.None);
        File.Exists(Path.Combine(a.TempDir, SubsystemUsageFlushService.FileName)).Should().BeTrue();

        using var b = a.RestartOnSameData();
        using var clientB = b.CreateAuthenticatedClient();

        (await clientB.GetAsync("/api/notes")).StatusCode.Should().Be(HttpStatusCode.OK);
        HitsOf(StoreOf(b), "notes").Should().Be(3,
            "первый Hit по ключу обязан идти поверх состояния, прочитанного с диска на старте");
    }
}
