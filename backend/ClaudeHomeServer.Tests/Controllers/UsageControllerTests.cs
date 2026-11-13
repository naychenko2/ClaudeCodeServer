using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// /api/usage: LoginCommand в per-аккаунт блоке подписок — готовая команда входа для
// плашки «нужен claude login» на экране использования. Логика вычисления пути покрыта
// юнит-тестами SubscriptionOAuthUsageServiceTests.LoginCommandFor_*; здесь проверяем,
// что контроллер реально прокидывает поле в JSON-ответ для аккаунта пула.
[Trait("Category", "Host")]
public class UsageControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public UsageControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Приёмка-2 (задача 977c3ee5): «вторая подписка не отображается». Пул из двух
    // подписок обязан отдать обе в блоке subscriptions — экран рисует ровно то,
    // что пришло, и терять ключи не должен ни контроллер, ни пул.
    [Fact]
    public async Task GetUsage_ПулИзДвухПодписок_ОтдаётОбеВБлокеSubscriptions()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude:DisplayName"] = "Claude 1 (Max)",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-two",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:DisplayName"] = "Claude 2 (Max)",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var subs = usage.GetProperty("subscriptions");
        subs.GetProperty("claude").GetProperty("name").GetString().Should().Be("Claude 1 (Max)");
        subs.GetProperty("claude-2").GetProperty("name").GetString().Should().Be("Claude 2 (Max)");
    }

    // Карточка подписки должна показывать ограничения тарифа (Opus / 1M), иначе UI
    // честно объявляет аккаунт «В ротации», а по факту Pick его не отдаст ни одному
    // opus- или 1m-ходу. Реальный случай: claude-3 стоял с обоими флагами false.
    [Fact]
    public async Task GetUsage_АккаунтПула_ОтдаётВозможностиАккаунтаИзКонфига()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude:SupportsOpus"] = "true",
                    [$"{ClaudeSubscriptionPool.Section}:claude:Supports1M"] = "true",
                    [$"{ClaudeSubscriptionPool.Section}:claude-3:OAuthToken"] = "token-three",
                    [$"{ClaudeSubscriptionPool.Section}:claude-3:DisplayName"] = "Claude 3 (Pro)",
                    [$"{ClaudeSubscriptionPool.Section}:claude-3:SupportsOpus"] = "false",
                    [$"{ClaudeSubscriptionPool.Section}:claude-3:Supports1M"] = "false",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var claude = usage.GetProperty("subscriptions").GetProperty("claude");
        claude.GetProperty("supportsOpus").GetBoolean().Should().BeTrue();
        claude.GetProperty("supports1M").GetBoolean().Should().BeTrue();

        var claude3 = usage.GetProperty("subscriptions").GetProperty("claude-3");
        claude3.GetProperty("supportsOpus").GetBoolean().Should().BeFalse();
        claude3.GetProperty("supports1M").GetBoolean().Should().BeFalse();
    }

    // Дефолтный сценарий: флаги в конфиге не заданы, бэкенд подставляет true (Opus/1M
    // доступны). Карточка тогда пилюлю не рисует — она срабатывает только на false.
    // Важно зафиксировать дефолт тестом, иначе можно случайно сменить его на «выкл по
    // умолчанию» и получить ложные «Без Opus» у всех подписок.
    [Fact]
    public async Task GetUsage_ФлагиВКонфигеНеЗаданы_ДефолтTrueПилюлиНет()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    // SupportsOpus/Supports1M намеренно не заданы
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var claude = usage.GetProperty("subscriptions").GetProperty("claude");
        claude.GetProperty("supportsOpus").GetBoolean().Should().BeTrue();
        claude.GetProperty("supports1M").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetUsage_АккаунтПула_ОтдаётLoginCommandСПутёмПрофиля()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var login = usage.GetProperty("subscriptions").GetProperty("second")
            .GetProperty("loginCommand").GetString();
        login.Should().NotBeNull();
        login.Should().Contain("CLAUDE_CONFIG_DIR")
            .And.Contain($"sub-second")
            .And.Contain("claude login");
    }

    // Пометка «модель недоступна на этой подписке» обязана быть видна наружу и сниматься
    // досрочно. Без выдачи человек не понимает, почему модель не выбирается: подписка
    // «В ротации», лимит не исчерпан, а ходы на неё не идут. Без сброса он ждёт истечения
    // TTL (сутки у no_access), хотя доступ могли включить минуту назад.
    [Fact]
    public async Task ModelAvailability_ПометкаВидна_ИСнимаетсяЭндпоинтомСброса()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-two",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);
        var pool = withPool.Services.GetRequiredService<ClaudeSubscriptionPool>();
        pool.MarkModelUnavailable("claude-2", "fable", FallbackErrorClass.ModelOutOfCredits);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var mark = usage.GetProperty("subscriptions").GetProperty("claude-2")
            .GetProperty("unavailableModels").EnumerateArray().Should().ContainSingle().Subject;
        mark.GetProperty("model").GetString().Should().Be("fable");
        mark.GetProperty("reason").GetString().Should().Be("model_out_of_credits");
        mark.GetProperty("until").GetDateTime().ToUniversalTime()
            .Should().BeAfter(DateTime.UtcNow, "пометка живая — по этому сроку UI пишет, когда проверит снова");
        // Пометка адресная: соседняя подписка её не наследует
        usage.GetProperty("subscriptions").GetProperty("claude")
            .GetProperty("unavailableModels").EnumerateArray().Should().BeEmpty();

        var clear = await client.PostAsJsonAsync(
            "/api/usage/subscriptions/claude-2/model-availability/clear", new { model = "fable" });
        clear.EnsureSuccessStatusCode();

        pool.IsModelUnavailable("claude-2", "fable").Should().BeFalse("сброс вернул пару в ротацию");
        var after = await client.GetFromJsonAsync<JsonElement>("/api/usage");
        after.GetProperty("subscriptions").GetProperty("claude-2")
            .GetProperty("unavailableModels").EnumerateArray().Should().BeEmpty();
    }

    // Пометки — админское состояние ротации: снимает их только админ, значит и видеть их
    // должен он же. Не-админу отдаём пустой список (уровень доступа тот же, что у сброса).
    [Fact]
    public async Task ModelAvailability_НеАдмин_ПометокНеВидит()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-two",
                });
            });
        });
        var pool = withPool.Services.GetRequiredService<ClaudeSubscriptionPool>();
        pool.MarkModelUnavailable("claude-2", "fable", FallbackErrorClass.ModelOutOfCredits);
        var user = await AuthenticateAsync(withPool,
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var usage = await user.GetFromJsonAsync<JsonElement>("/api/usage");

        usage.GetProperty("subscriptions").GetProperty("claude-2")
            .GetProperty("unavailableModels").EnumerateArray().Should().BeEmpty();
        // Сброс не-админу тоже закрыт — уровни доступа сходятся
        var clear = await user.PostAsJsonAsync(
            "/api/usage/subscriptions/claude-2/model-availability/clear", new { model = "fable" });
        clear.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    // Опечатка в ключе подписки не должна выглядеть как успешный сброс: человек ждал бы от
    // пары работы, которой не будет (пометка на самом деле осталась висеть на другом ключе).
    [Fact]
    public async Task ModelAvailability_НеизвестнаяПодписка_404()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-two",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var response = await client.PostAsJsonAsync(
            "/api/usage/subscriptions/claude-42/model-availability/clear", new { model = "fable" });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    // === scope=limits (перф-3 B6): лёгкий ответ шапки чата ===

    // Лёгкий ответ — только свежий снимок на окно (не 8-дневная история) и без блока Ollama;
    // полный ответ — вся история. Одна служба (UsageService) считает, проекции разные.
    [Fact]
    public async Task GetUsage_ScopeLimits_ОтдаётТолькоСвежийСнимок_АПолный_Историю()
    {
        var usage = _factory.Services.GetRequiredService<UsageService>();
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        usage.Record("five_hour", 0.1, "allowed", isUsingOverage: false, resetsAt: future, subscriptionKey: "claude");
        usage.Record("five_hour", 0.2, "allowed", isUsingOverage: false, resetsAt: future, subscriptionKey: "claude");
        usage.Record("five_hour", 0.3, "allowed", isUsingOverage: false, resetsAt: future, subscriptionKey: "claude");

        var client = await AuthenticateAsync(_factory);
        var limits = await client.GetFromJsonAsync<JsonElement>("/api/usage?scope=limits");
        var full = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        // Лёгкий: ровно один снимок (самый свежий, 0.3) и без Ollama
        limits.GetProperty("snapshots").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("utilization").GetDouble().Should().Be(0.3);
        limits.GetProperty("ollama").ValueKind.Should().Be(JsonValueKind.Null);

        // Полный: вся история (3 снимка) — экран «Квоты» строит по ней спарклайн тренда
        full.GetProperty("snapshots").EnumerateArray().Should().HaveCount(3);
    }

    // Кэш лёгкого ответа инвалидируется по НОВОЙ ЗАПИСИ (версия UsageService), а не по таймеру:
    // записали свежий процент — следующий запрос уже его видит, а не закешированный старый.
    [Fact]
    public async Task GetUsage_ScopeLimits_КэшИнвалидируетсяПоНовойЗаписи()
    {
        var usage = _factory.Services.GetRequiredService<UsageService>();
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        usage.Record("five_hour", 0.1, "allowed", isUsingOverage: false, resetsAt: future, subscriptionKey: "claude");

        var client = await AuthenticateAsync(_factory);
        var first = await client.GetFromJsonAsync<JsonElement>("/api/usage?scope=limits");
        first.GetProperty("snapshots").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("utilization").GetDouble().Should().Be(0.1);

        // Кэш заполнен первым запросом — проверим, что запись в нём лежит (а не «всегда пересбор»).
        // Отпечаток собираем теми же версиями, что контроллер: в тестовом хосте пул/опрос не
        // меняют состояния, поэтому их версии стабильны между GET и этой проверкой.
        var pool = _factory.Services.GetRequiredService<ClaudeSubscriptionPool>();
        var oauth = _factory.Services.GetRequiredService<SubscriptionOAuthUsageService>();
        var providers = _factory.Services.GetRequiredService<LlmProviderRegistry>();
        var cache = _factory.Services.GetRequiredService<UsageResponseCache>();
        var fp = new UsageResponseCache.Fingerprint(
            Usage: usage.Version, Pool: pool.Version, Poll: oauth.Version, Plan: usage.PlanVersion,
            Providers: UsageResponseCache.ProviderFingerprint(providers.All));
        cache.TryGet("limits", admin: true, fp, out _).Should().BeTrue("после GET кэш лёгкого ответа заполнен");

        // Новая запись (версия выросла) — кэш пересобран, ответ отражает свежий процент
        usage.Record("five_hour", 0.9, "allowed", isUsingOverage: false, resetsAt: future, subscriptionKey: "claude");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/usage?scope=limits");
        second.GetProperty("snapshots").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("utilization").GetDouble().Should().Be(0.9);
    }

    // WithWebHostBuilder возвращает базовый WebApplicationFactory<Program> — расширение
    // CreateAuthenticatedClient из TestWebApplicationFactory ему недоступно, логинимся вручную.
    private static async Task<HttpClient> AuthenticateAsync(WebApplicationFactory<Program> factory,
        string? username = null, string? password = null)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = username ?? TestWebApplicationFactory.TestUsername,
            password = password ?? TestWebApplicationFactory.TestPassword,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    // === W2c · блок «Исполнители ночи» (разрез «ночь вики на нескольких локальных
    // исполнителях» §2) ===

    // Стор с двумя исполнителями места — ответ обязан отдать тот же список в том же порядке
    // (порядок списка = приоритет: свободный слот первого получает чат раньше) и флаг каталога:
    // UI рисует блок только при нём. Места без флага — false и null: они идут по своему маршруту.
    [Fact]
    public async Task GetUsage_ИсполнителиМеста_СписокПорядкомСтора_ИФлагКаталога()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalEngines:gemma:BaseUrl"] = "http://127.0.0.1:1",
                    ["LocalEngines:gemma:Model"] = "gemma-4-26b-a4b",
                    ["LocalEngines:gemma:Title"] = "Gemma 4",
                });
            });
        });
        // Заполняем стор тем же синглтоном, который читает контроллер: Save калирует через
        // Validate (gemma — живой движок из LocalEngines, «local» — основная локаль) и пишет на диск.
        var store = host.Services.GetRequiredService<LocalActionExecutorsStore>();
        store.Save(LocalActionCatalog.WikiNightly, new LocalActionExecutorsConfig
        {
            MaxTotal = 2,
            // Порядок стора — «local» первым: он отличается и от алфавитного, и от порядка
            // реестра движков, поэтому любая сортировка в контроллере попадается тестом.
            Executors =
            [
                new() { Engine = LocalActionOverridesStore.LocalRoute, Parallel = 2 },
                new() { Engine = "gemma", Parallel = 1 },
            ],
        }).Should().BeTrue("место с ParallelExecutors принимает список локальных движков");

        var client = await AuthenticateAsync(host);
        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");
        var actions = usage.GetProperty("ollama").GetProperty("actions").EnumerateArray().ToList();

        var nightly = actions.First(a => a.GetProperty("key").GetString() == LocalActionCatalog.WikiNightly);
        nightly.GetProperty("parallelExecutors").GetBoolean().Should().BeTrue();
        var exec = nightly.GetProperty("executors");
        exec.GetProperty("maxTotal").GetInt32().Should().Be(2);
        var items = exec.GetProperty("items").EnumerateArray().ToList();
        // Порядок items — порядок стора (здесь — не алфавитный), а не сортировка контроллера.
        items.Select(i => i.GetProperty("engine").GetString())
            .Should().Equal(LocalActionOverridesStore.LocalRoute, "gemma");
        items.Select(i => i.GetProperty("parallel").GetInt32()).Should().Equal(2, 1);

        var triage = actions.First(a => a.GetProperty("key").GetString() == LocalActionCatalog.WikiTriage);
        triage.GetProperty("parallelExecutors").GetBoolean().Should().BeFalse();
        triage.GetProperty("executors").ValueKind.Should().Be(JsonValueKind.Null,
            "без списка место идёт по своему маршруту, как до фичи");
    }

    // MAJOR по сверке W2a: файл стора правят руками, и в нём бывает «[null]» или строка без
    // имени движка. Путь GET /api/usage (TryGet → DescribeExecutors) обязан на таком файле не
    // падать: его читает экран «Использование» при каждой загрузке. Стор подменяю своим:
    // он Load'ит файл при создании, а синглтон тестового хоста был бы прочитан раньше,
    // чем я успел бы его записать.
    [Fact]
    public async Task GetUsage_ИсполнителиМеста_БитыйФайлНеРоняетОтвет_ЧислаУрезаны()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "local-action-executors.json"),
            """{"wiki-nightly":{"maxTotal":9,"executors":[{"engine":"local","parallel":9},null,{"engine":""}]}}""");
        var storeConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
            })
            .Build();
        var store = new LocalActionExecutorsStore(storeConfig);

        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(LocalActionExecutorsStore)).ToList())
                    services.Remove(d);
                services.AddSingleton(store);
            });
        });

        var client = await AuthenticateAsync(host);
        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");
        var actions = usage.GetProperty("ollama").GetProperty("actions").EnumerateArray().ToList();

        var nightly = actions.First(a => a.GetProperty("key").GetString() == LocalActionCatalog.WikiNightly);
        var exec = nightly.GetProperty("executors");
        // Ответ живой, а не 500, и числа в нём уже внутри границ стора: 9 → 4; parallel 9 у
        // «local» → потолок той же основной локали (без реестра провайдеров — DefaultLimit).
        exec.GetProperty("maxTotal").GetInt32().Should().Be(LocalActionExecutorsStore.MaxTotalLimit);
        var items = exec.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle("null-элемент и строка без имени не доходят до ответа");
        items[0].GetProperty("engine").GetString().Should().Be(LocalActionOverridesStore.LocalRoute);
        items[0].GetProperty("parallel").GetInt32().Should().Be(TrackedLocalLlmClient.DefaultLimit);
    }

    // Список движков для выбора (И3): только «local» и имена включённых LocalEngines:*.
    // В тестовом хосте облачные провайдеры настроены базовым appsettings.json (deepseek, glm, …) —
    // ни одного их ключа в списке быть не может. Имя = значение engine в PUT (контракт W2b);
    // подпись — цепочка EngineLabel (Title, иначе модель, иначе имя); потолок — свой у движка.
    [Fact]
    public async Task GetUsage_СписокЛокальныхДвижков_ТолькоЛокальные_БезОблачныхПровайдеров()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalEngines:gemma:BaseUrl"] = "http://127.0.0.1:1",
                    ["LocalEngines:gemma:Model"] = "gemma-4-26b-a4b",
                    ["LocalEngines:gemma:Title"] = "Gemma 4",
                    ["LocalEngines:gemma:MaxConcurrency"] = "2",
                });
            });
        });
        // Облачные провайдеры тестового хоста — ровно то, чего в списке быть не может.
        var providerKeys = host.Services.GetRequiredService<LlmProviderRegistry>().All
            .Select(p => p.Key).ToList();
        providerKeys.Should().NotBeEmpty("тест проверяет исключение именно при настроенных облачных провайдерах");

        var client = await AuthenticateAsync(host);
        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");
        var engines = usage.GetProperty("ollama").GetProperty("localEngines").EnumerateArray().ToList();
        var names = engines.Select(e => e.GetProperty("name").GetString()).ToList();

        names.Should().Equal(new[] { LocalActionOverridesStore.LocalRoute, "gemma" },
            "только основная локаль и именованные движки, в порядке: local первым");
        foreach (var key in providerKeys)
            names.Should().NotContain(key, "облачный провайдер не может быть исполнителем ночи (И3)");
        // Подпись именованного — Title (та же цепочка, что Engine в actions); потолок — его MaxConcurrency.
        engines[1].GetProperty("title").GetString().Should().Be("Gemma 4");
        engines[1].GetProperty("maxConcurrency").GetInt32().Should().Be(2);
        // Основная локаль тестового хоста не настроена (Ollama:Model="") — запасная подпись и
        // дефолтный потолок: то же число, по которому валидатор стора мерил бы parallel.
        engines[0].GetProperty("maxConcurrency").GetInt32().Should().Be(TrackedLocalLlmClient.DefaultLimit);
    }

    // Подпись основной локали в списке «Исполнителей ночи» — то же значение, чем зовёт её
    // панель «Локальные модели» (LlmProviders:<ключ>:Stats:Title): под алиасом qwen3.8-27b с
    // октября работает Flash-Next, и экран не имеет права показывать старое имя. Мутация:
    // убрать ветку MainLocalTitle в UsageController.BuildLocalEngineOptions — краснеет первый
    // assert; убрать её в CheapTextRunner.EngineTitle — краснеет
    // LocalExecutorsFor_MainLocalTitleFromStats… в Llm.Tests.
    [Fact]
    public async Task GetUsage_ОсновнаяЛокаль_ПодписьИзStatsTitle_БезНего_ПрежнийЗапасныйВариант()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LlmProviders:local-qwen:IsLocal"] = "true",
                    ["LlmProviders:local-qwen:Stats:Title"] = "Flash-Next",
                });
            });
        });

        var client = await AuthenticateAsync(host);
        var engines = (await client.GetFromJsonAsync<JsonElement>("/api/usage"))
            .GetProperty("ollama").GetProperty("localEngines").EnumerateArray().ToList();
        engines[0].GetProperty("name").GetString().Should().Be(LocalActionOverridesStore.LocalRoute,
            "ключ исполнителя не меняется: подпись — только то, что видит человек");
        engines[0].GetProperty("title").GetString().Should().Be("Flash-Next");

        // Без явной подписи — прежнее поведение. В базовом тестовом хосте основная локаль
        // не настроена (Ollama:Model="") → запасный «Основная», как и раньше.
        var plain = _factory.CreateAuthenticatedClient();
        var plainEngines = (await plain.GetFromJsonAsync<JsonElement>("/api/usage"))
            .GetProperty("ollama").GetProperty("localEngines").EnumerateArray().ToList();
        plainEngines[0].GetProperty("title").GetString().Should().Be("Основная");
    }

    [Fact]
    public async Task GetUsage_МестоСПресетом_ОтдаётPresetИдентичноОписателю()
    {
        // Вкладка «Применение» читает /api/usage (OllamaActionInfo), а не Describe — preset тут
        // отсутствовал и триггер маскировал пресет слотом. Теперь поле заполнено той же логикой.
        var admin = _factory.CreateAuthenticatedClient();
        var presetId = "us-" + Guid.NewGuid().ToString("N");
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[]
            {
                new { id = presetId, name = "Цепочка", steps = new[] { "tier:strong", "glm-5.2" } },
            },
        })).EnsureSuccessStatusCode();

        (await admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.ChatNew}", new { route = $"preset:{presetId}" }))
            .EnsureSuccessStatusCode();

        var usage = await admin.GetFromJsonAsync<JsonElement>("/api/usage");
        var action = usage.GetProperty("ollama").GetProperty("actions").EnumerateArray()
            .First(a => a.GetProperty("key").GetString() == LocalActionCatalog.ChatNew);
        var preset = action.GetProperty("preset");
        preset.GetProperty("id").GetString().Should().Be(presetId);
        preset.GetProperty("name").GetString().Should().Be("Цепочка");
        preset.GetProperty("steps").EnumerateArray().Select(s => s.GetString())
            .Should().Equal(new[] { "tier:strong", "glm-5.2" });
    }
}
