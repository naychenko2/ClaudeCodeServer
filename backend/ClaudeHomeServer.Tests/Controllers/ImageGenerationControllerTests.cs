using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

// Фабрика с предсказуемой доступностью генераторов: fal настроен, glif — нет.
// Без этого «выключенный провайдер» зависел бы от переменных окружения машины
// (FalImageService читает FAL_KEY, GlifImageGenerator — Glif:McpToken).
public class ImageProvidersFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fal:ApiKey"] = "test-fal-key",
                ["Glif:McpToken"] = "",
            });
        });
    }
}

// Настройка генератора картинок для вкладки «Применение»: по месту (иконка проекта,
// аватар персоны), чтение — всем, запись — админам.
public class ImageGenerationControllerTests : IClassFixture<ImageProvidersFactory>
{
    private const string Url = "/api/image-generation";
    private const string Icon = "project-icon";
    private const string Avatar = "persona-avatar";

    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public ImageGenerationControllerTests(ImageProvidersFactory factory)
    {
        _admin = factory.CreateAuthenticatedClient();
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // Сброс к дефолтам: тесты класса делят один стор, порядок методов xUnit не гарантирует
    private async Task ResetAsync()
    {
        var empty = new
        {
            provider = "",
            models = new Dictionary<string, string?> { ["fal"] = "", ["glif"] = "" },
        };
        var response = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object> { [Icon] = empty, [Avatar] = empty },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static JsonElement Place(JsonElement body, string key) =>
        body.GetProperty("places").EnumerateArray().First(p => p.GetProperty("key").GetString() == key);

    private static string? ModelOf(JsonElement body, string place, string provider) =>
        Place(body, place).GetProperty("models").GetProperty(provider).GetString();

    [Fact]
    public async Task ФормаНастройкиЧитаетсяЛюбымПользователем()
    {
        await ResetAsync();

        var response = await _user.GetAsync(Url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Каталог провайдеров — в порядке auto-выбора: сначала glif, потом fal
        var providers = body.GetProperty("providers").EnumerateArray().ToList();
        providers.Select(p => p.GetProperty("key").GetString()).Should().Equal("glif", "fal");
        providers[0].GetProperty("enabled").GetBoolean().Should().BeFalse();

        var fal = providers[1];
        fal.GetProperty("enabled").GetBoolean().Should().BeTrue();
        fal.GetProperty("displayName").GetString().Should().NotBeNullOrWhiteSpace();
        var models = fal.GetProperty("models").EnumerateArray().ToList();
        models.Should().NotBeEmpty();
        models[0].GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        models[0].GetProperty("displayName").GetString().Should().NotBeNullOrWhiteSpace();

        // Оба места — с названием, режимом и вычисленным активным провайдером
        body.GetProperty("places").EnumerateArray()
            .Select(p => p.GetProperty("key").GetString()).Should().Equal(Icon, Avatar);
        foreach (var key in new[] { Icon, Avatar })
        {
            var place = Place(body, key);
            place.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            place.GetProperty("provider").GetString().Should().Be("auto");
            // auto = первый доступный по порядку glif → fal; glif в этой фабрике выключен
            place.GetProperty("activeProvider").GetString().Should().Be("fal");
            place.GetProperty("enabled").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task ОбычныйПользовательНеМожетМенятьНастройку()
    {
        var put = await _user.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object> { [Icon] = new { provider = "fal" } },
        });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ВыключенныйПровайдер400()
    {
        await ResetAsync();

        // Явный выбор фолбэка не даёт — ненастроенный glif означал бы неработающую генерацию
        var put = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object> { [Icon] = new { provider = "glif" } },
        });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("не настроен");

        // Настройка не изменилась
        var after = await _admin.GetFromJsonAsync<JsonElement>(Url);
        Place(after, Icon).GetProperty("provider").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task НеизвестныйПровайдер400()
    {
        var put = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object> { [Icon] = new { provider = "midjourney" } },
        });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("midjourney");
    }

    [Fact]
    public async Task НеизвестноеМесто400_ИСоседнееНеСохраняется()
    {
        await ResetAsync();

        var put = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object>
            {
                [Icon] = new { provider = "fal" },
                ["chat-background"] = new { provider = "fal" },
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("chat-background");

        // Валидные места из того же запроса тоже не применились
        var after = await _admin.GetFromJsonAsync<JsonElement>(Url);
        Place(after, Icon).GetProperty("provider").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task НеизвестнаяМодель400()
    {
        var put = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object>
            {
                [Icon] = new { models = new Dictionary<string, string?> { ["fal"] = "fal-ai/нет-такой-модели" } },
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("нет-такой-модели");
    }

    [Fact]
    public async Task ПатчНеЗатираетСоседнееПоле()
    {
        await ResetAsync();

        // Только модель: провайдер прислан не был — остаётся auto
        var models = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object>
            {
                [Icon] = new { models = new Dictionary<string, string?> { ["fal"] = "fal-ai/flux/dev" } },
            },
        });
        models.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await models.Content.ReadFromJsonAsync<JsonElement>();
        Place(body, Icon).GetProperty("provider").GetString().Should().Be("auto");
        ModelOf(body, Icon, "fal").Should().Be("fal-ai/flux/dev");

        // Только провайдер: модель не прислана — прежняя остаётся на месте
        var provider = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object> { [Icon] = new { provider = "fal" } },
        });
        provider.StatusCode.Should().Be(HttpStatusCode.OK);
        body = await provider.Content.ReadFromJsonAsync<JsonElement>();
        Place(body, Icon).GetProperty("provider").GetString().Should().Be("fal");
        ModelOf(body, Icon, "fal").Should().Be("fal-ai/flux/dev");

        // И то же самое после перечитывания
        body = await _admin.GetFromJsonAsync<JsonElement>(Url);
        Place(body, Icon).GetProperty("provider").GetString().Should().Be("fal");
        ModelOf(body, Icon, "fal").Should().Be("fal-ai/flux/dev");

        await ResetAsync();
    }

    [Fact]
    public async Task МестаНастраиваютсяОтдельно()
    {
        await ResetAsync();

        var put = await _admin.PutAsJsonAsync(Url, new
        {
            places = new Dictionary<string, object>
            {
                [Icon] = new
                {
                    provider = "fal",
                    models = new Dictionary<string, string?> { ["fal"] = "fal-ai/flux/dev" },
                },
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await _admin.GetFromJsonAsync<JsonElement>(Url);
        Place(body, Icon).GetProperty("provider").GetString().Should().Be("fal");
        ModelOf(body, Icon, "fal").Should().Be("fal-ai/flux/dev");

        // Аватар остался на своей настройке — правка иконки его не касается
        Place(body, Avatar).GetProperty("provider").GetString().Should().Be("auto");
        ModelOf(body, Avatar, "fal").Should().BeNull();

        await ResetAsync();
    }

    [Fact]
    public async Task ПустойЗапрос400()
    {
        var put = await _admin.PutAsJsonAsync(Url, new { });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
