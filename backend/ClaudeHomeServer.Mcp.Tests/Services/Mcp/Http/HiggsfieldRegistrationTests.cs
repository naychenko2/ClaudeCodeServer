using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

// Сторож DI-контейнера для HiggsfieldToolset/HiggsfieldSnapshotWarmer. Задача eefcb96a:
// раньше `AddSingleton<IMcpToolset, HiggsfieldToolset>()` + подсистема, регистрирующая свой
// IMcpToolset позже (NotesToolset в AddSubsystems) — последняя регистрация выигрывает,
// фабрика AddGatedHostedFrom с кастом `(HiggsfieldToolset)sp.GetRequiredService<IMcpToolset>()`
// бросает InvalidCastException на старте процесса. Тесты на тулсет не ловят — hosted в
// Testing выключен гейтом AddGatedHostedFrom.
//
// Что проверяет тест — именно сборку контейнера, а не поведение тулсета:
//   1. Старый паттерн (AddSingleton<IMcpToolset, H1> + AddSingleton<IMcpToolset, H2>)
//      даёт каст в первую реализацию — InvalidCastException. Это и есть блокер.
//   2. Новый паттерн (AddSingleton<H1> + AddSingleton<IMcpToolset>(forwarder) +
//      AddSingleton<IMcpToolset, H2>): каст не нужен, фабрика AddGatedHostedFrom
//      резолвит H1 по собственному типу и успешно создаёт warmer.
//   3. McpToolsetRegistry (IEnumerable<IMcpToolset>) собирает ВСЕ тулсеты —
//      форвардер не теряется, реестр.Find("higgs-like") возвращает H1.
//   4. Полный контейнер через TestWebApplicationFactory: HiggsfieldToolset резолвится
//      напрямую, попадает в реестр, hosted в Testing не зарегистрирован (это объясняет,
//      почему существующие тесты блокер не ловили).
public class HiggsfieldRegistrationTests
{
    // Полный контейнер: HiggsfieldToolset резолвится по собственному типу и попадает в
    // реестр McpToolsetRegistry (IEnumerable<IMcpToolset>). В Testing hosted не
    // регистрируется гейтом AddGatedHostedFrom — поэтому блокер проходил мимо
    // существующих тестов на тулсет.
    [Fact]
    public void Контейнер_HiggsfieldToolset_РезолвитсяИРегистрируетсяВРеестре()
    {
        using var factory = new TestWebApplicationFactory();
        // CreateClient заставляет WebApplicationFactory собрать IServiceProvider — иначе
        // Services пустые, и резолвы падают с InvalidOperationException.
        _ = factory.CreateClient();

        var sp = factory.Services;
        sp.Should().NotBeNull();

        // Прямой резолв по собственному типу — главная проверка: после правки Program.cs
        // AddSingleton<HiggsfieldToolset>() обеспечивает резолв независимо от того, что
        // зарегистрировано под IMcpToolset в подсистемах ниже.
        var higgsfield = sp.GetRequiredService<HiggsfieldToolset>();
        higgsfield.Should().NotBeNull(
            "AddSingleton<HiggsfieldToolset>() обязан давать экземпляр напрямую — это и " +
            "есть форма, по которой фабрика AddGatedHostedFrom достаёт тулсет");

        // Реестр собирает тулсеты через IEnumerable<IMcpToolset>: форвардер Higgsfield и
        // прямые регистрации NotesToolset/прочих идут вместе. До правки реестр тоже бы
        // работал (Higgsfield был зарегистрирован под IMcpToolset), но фабрика AddGatedHostedFrom
        // всё равно падала на касте — здесь мы проверяем, что и реестр, и прямой резолв
        // согласованы после правки.
        var registry = sp.GetRequiredService<McpToolsetRegistry>();
        registry.Should().NotBeNull();
        registry.Find(HiggsfieldToolset.ServerName).Should().BeSameAs(higgsfield,
            "McpToolsetRegistry собирает IEnumerable<IMcpToolset> — форвардер AddSingleton<IMcpToolset>(...) " +
            "попадает в реестр наравне с прямыми регистрациями, иначе Higgsfield пропадёт из tools/list хода");
    }

    // Документирует, почему блокер проходил мимо существующих тестов на Higgsfield:
    // AddGatedHostedFrom в Testing-среде НЕ регистрирует IHostedService. Тест, который
    // бы реально дёрнул фабрику AddGatedHostedFrom, стоит отдельно (см. изолированные
    // ServiceProvider-тесты ниже).
    [Fact]
    public void Контейнер_HiggsfieldSnapshotWarmer_НеЗарегистрированКакHostedВTesting()
    {
        using var factory = new TestWebApplicationFactory();
        _ = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().ToArray();
        hosted.OfType<HiggsfieldSnapshotWarmer>().Should().BeEmpty(
            "AddGatedHostedFrom в Testing-среде не регистрирует hosted — иначе тестовый " +
            "хост тащил бы лишний фоновый цикл. Это и есть причина, по которой блокер " +
            "(InvalidCastException при старте фабрики) проходил мимо существующих тестов.");
    }

    // Старый паттерн регистрации HiggsfieldToolset + каст в фабрике AddGatedHostedFrom:
    // IMcpToolset — это последняя регистрация (NotesLike), каст в HiggsLike бросает
    // InvalidCastException. Это и есть блокер, который выехал на прод.
    [Fact]
    public void СтарыйПаттерн_КастВПервуюРеализациюБросаетИсключение()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMcpToolset, HiggsLike>();
        // Имитируем подсистему, которая регистрирует свой IMcpToolset ПОСЛЕ Higgsfield.
        // Это и есть корневая причина блокера: последний AddSingleton<IMcpToolset, T>()
        // перетирает предыдущий.
        services.AddSingleton<IMcpToolset, NotesLike>();

        using var sp = services.BuildServiceProvider();

        var toolset = sp.GetRequiredService<IMcpToolset>();
        toolset.Should().BeOfType<NotesLike>(
            "AddSingleton<IMcpToolset, T>() под вторым T перетирает первый — это и есть " +
            "источник InvalidCastException в HiggsfieldSnapshotWarmer до правки");

        // Имитация фабрики AddGatedHostedFrom из Program.cs (до правки) — падает именно так,
        // как упал бы бэкенд на проде.
        var act = () => (HiggsLike)sp.GetRequiredService<IMcpToolset>();
        act.Should().Throw<InvalidCastException>(
            "старый паттерн регистрации — IMcpToolset резолвится в NotesLike, каст в HiggsLike падает");
    }

    // Новый паттерн: собственный тип + форвардер + вторая IMcpToolset-регистрация подсистемы.
    // Проверяет, что:
    //   - фабрика AddGatedHostedFrom с `sp.GetRequiredService<HiggsLike>()` работает без каста;
    //   - McpToolsetRegistry собирает ОБА тулсета через IEnumerable<IMcpToolset>
    //     (форвардер не теряется — именно так работает McpToolsetRegistry на проде);
    //   - GetServices<IMcpToolset>() видит и HiggsLike, и NotesLike, итого два.
    [Fact]
    public void НовыйПаттерн_ФабрикаWarmerИРеестрРаботают()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // ILogger<HiggsLikeWarmer>
        services.AddSingleton<HiggsLike>();
        services.AddSingleton<IMcpToolset>(sp => sp.GetRequiredService<HiggsLike>());
        services.AddSingleton<IMcpToolset, NotesLike>();
        services.AddSingleton<McpToolsetRegistry>();

        using var sp = services.BuildServiceProvider();

        var higgs = sp.GetRequiredService<HiggsLike>();

        // Реестр собирает IEnumerable<IMcpToolset>: форвардер Higgsfield и прямой Notes.
        // Find("higgs-like") возвращает именно HiggsLike, а не последнюю регистрацию.
        var registry = sp.GetRequiredService<McpToolsetRegistry>();
        registry.Find("higgs-like").Should().BeSameAs(higgs,
            "McpToolsetRegistry собирает IEnumerable<IMcpToolset> — форвардер попадает " +
            "в реестр наравне с прямыми регистрациями");
        registry.Find("notes-like").Should().NotBeNull(
            "NotesLike тоже в реестре — иначе он пропал бы из tools/list хода");

        // GetServices<IMcpToolset>() — обе регистрации видны DI (forwarder + прямой).
        // Именно это поведение использует McpToolsetRegistry внутри.
        sp.GetServices<IMcpToolset>().Should().HaveCount(2,
            "HiggsLike под форвардером + NotesLike под прямой регистрацией — обе в контейнере");

        // Фабрика AddGatedHostedFrom: создаём warmer без каста — ровно как в Program.cs
        // после правки. До правки здесь стояло `(HiggsfieldToolset)sp.GetRequiredService<IMcpToolset>()`,
        // и фабрика бросала бы InvalidCastException в Production.
        var factory = (IServiceProvider provider) => new HiggsLikeWarmer(
            provider.GetRequiredService<HiggsLike>(),
            provider.GetRequiredService<ILogger<HiggsLikeWarmer>>());

        var warmer = factory(sp);
        warmer.Should().NotBeNull();
        warmer.Toolset.Should().BeSameAs(higgs,
            "warmer обязан получить тот же экземпляр HiggsLike, что и прямой резолв");
    }

    // Заглушки — сведены к минимуму, чтобы тест DI-резолва не зависел от конкретики
    // HiggsfieldToolset (OAuth, IHttpClientFactory, SessionManager). Поведение общее для
    // любых двух IMcpToolset-регистраций, поэтому заглушки законны.
    private sealed class HiggsLike : IMcpToolset
    {
        public string Name => "higgs-like";
        public string Version => "1.0";
        public Task<McpToolCallResult> CallAsync(string tool, System.Text.Json.Nodes.JsonObject arguments,
            McpToolCallContext context, CancellationToken ct) => Task.FromResult(new McpToolCallResult(""));
    }

    private sealed class NotesLike : IMcpToolset
    {
        public string Name => "notes-like";
        public string Version => "1.0";
        public Task<McpToolCallResult> CallAsync(string tool, System.Text.Json.Nodes.JsonObject arguments,
            McpToolCallContext context, CancellationToken ct) => Task.FromResult(new McpToolCallResult(""));
    }

    // Заглушка hosted-сервиса с минимальным контрактом: тильзу хватает собственного
    // экземпляра тулсета и логгера. HiggsfieldSnapshotWarmer принимает ровно эти два
    // параметра, поэтому конструктор совместим — а резолв проверяет паттерн DI без
    // зависимостей HiggsfieldToolset.
    private sealed class HiggsLikeWarmer(HiggsLike toolset, ILogger<HiggsLikeWarmer> log)
        : IHostedService
    {
        public HiggsLike Toolset => toolset;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // log держим ровно как HiggsfieldSnapshotWarmer: один LogInformation на старте,
            // чтобы резолв DI прошёл по тому же контракту, что в боевой фабрике.
            log.LogInformation("higgs-like warmer started");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}