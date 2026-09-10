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
