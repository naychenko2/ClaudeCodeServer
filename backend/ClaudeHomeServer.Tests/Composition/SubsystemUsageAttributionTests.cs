using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Composition;

// Учёт обращений по ключу подсистемы (S1b): карта «сборка → ключ», мидлвар на живом хосте,
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

    // Ручки llm (Usage/Providers/Models) привязаны атрибутом в S2b-1: обращение к расходу —
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

    // ─── Хвост R-S2b-1: откат к классовой атрибуции и наблюдаемость ключей из сборок вертикалей ───

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

    // П.1: неактивный ключ метода не отменяет классовый — атрибуция откатывается к классовой
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

    // П.1, обратная сторона: когда оба ключа активны, методовой по-прежнему приоритнее
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

    // П.2: ключ, который вернула атрибуция, обязан быть наблюдаемым — счётчик растёт
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
}
