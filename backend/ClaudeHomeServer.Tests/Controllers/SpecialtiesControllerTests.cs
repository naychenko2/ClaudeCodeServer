using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// API специальностей: каталог с подписями, глобальные и личные настройки,
// сквозное применение шаблона при создании/смене специальности и ручная правка после.
// Фич-флаг model-routing-rules снят — эндпоинты доступны безусловно, контроль доступа
// сводится к роли admin (глобальный слой) и per-owner изоляции JWT.
public class SpecialtiesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public SpecialtiesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAuthenticatedClient(); // testuser — admin
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // --- Каталог ---

    [Fact]
    public async Task List_ТриИсполнительскиеСПодписями()
    {
        var response = await _admin.GetAsync("/api/specialties");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(i => i.GetProperty("key").GetString()!);

        items.Should().ContainKey("executor").WhoseValue.GetProperty("label").GetString()
            .Should().Be("Исполнитель (универсальный)");
        items.Should().ContainKey("backendExecutor").WhoseValue.GetProperty("label").GetString()
            .Should().Be("Исполнитель (бэкенд)");
        items.Should().ContainKey("frontendExecutor").WhoseValue.GetProperty("label").GetString()
            .Should().Be("Исполнитель (фронтенд)");

        foreach (var key in new[] { "executor", "backendExecutor", "frontendExecutor" })
        {
            items[key].GetProperty("executorFamily").GetBoolean().Should().BeTrue();
            items[key].GetProperty("template").GetProperty("access").GetString().Should().Be("full");
        }
        items["analyst"].GetProperty("executorFamily").GetBoolean().Should().BeFalse();
        items["analyst"].GetProperty("template").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // Значок и цвет роли фронт берёт с сервера (свой каталог иконок он больше не держит):
    // поля есть у ВСЕХ записей каталога, непусты у всех ролей, кроме «Не задана».
    [Fact]
    public async Task List_ЗначокИЦвет_ЕстьУВсехЗаписей()
    {
        var items = (await (await _user.GetAsync("/api/specialties")).Content
            .ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        items.Should().HaveCount(15);
        foreach (var item in items)
        {
            item.TryGetProperty("icon", out var icon).Should().BeTrue();
            item.TryGetProperty("color", out var color).Should().BeTrue();

            var key = item.GetProperty("key").GetString();
            if (key == "none") continue; // «Не задана» — не роль, значка у неё нет
            icon.GetString().Should().NotBeNullOrWhiteSpace($"у роли {key} задан значок");
            color.GetString().Should().NotBeNullOrWhiteSpace($"у роли {key} задан цвет");
        }

        var byKey = items.ToDictionary(i => i.GetProperty("key").GetString()!);
        byKey["backendExecutor"].GetProperty("icon").GetString().Should().Be("server");
        byKey["backendExecutor"].GetProperty("color").GetString().Should().Be("blue");
    }

    // --- Настройки: глобальные и личные ---

    [Fact]
    public async Task Settings_ГлобальныеПишетТолькоАдмин()
    {
        var layer = new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { access = "readOnly", tools = new[] { "web" } },
            },
            presets = new[]
            {
                new { name = "Правила", steps = new[] { "tier:strong" } },
            },
        };

        (await _user.PutAsJsonAsync("/api/specialties/settings/global", layer)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "не-админ не правит глобальный слой");

        var adminResponse = await _admin.PutAsJsonAsync("/api/specialties/settings/global", layer);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await ResetGlobalLayer();
    }

    // Настройка инстанса видна в каталоге как эффективный шаблон — одинаково всем.
    [Fact]
    public async Task Settings_НастройкаИнстанса_ВиднаВКаталогеВсем()
    {
        var put = await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["frontendExecutor"] = new { access = "custom", tools = new[] { "tasks" }, disallowedTools = new[] { "Bash" } },
            },
            presets = Array.Empty<object>(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("global").GetProperty("specialties")
            .TryGetProperty("frontendExecutor", out var entry).Should().BeTrue();
        entry.GetProperty("access").GetString().Should().Be("custom");

        // Не-админ видит тот же эффективный шаблон в каталоге
        var items = (await (await _user.GetAsync("/api/specialties")).Content
                .ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(i => i.GetProperty("key").GetString()!);
        var template = items["frontendExecutor"].GetProperty("template");
        template.GetProperty("access").GetString().Should().Be("custom");
        template.GetProperty("disallowedTools").EnumerateArray().Single().GetString().Should().Be("Bash");

        await ResetGlobalLayer();
    }

    [Fact]
    public async Task Settings_НеизвестнаяСпециальность_400()
    {
        var response = await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object> { ["no-such"] = new { access = "full" } },
            presets = Array.Empty<object>(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // GET /settings отдаёт ОДИН слой: полей user и owner больше нет, у пресетов
    // единственный scope — global (ADR-012).
    [Fact]
    public async Task Settings_ОдинСлой_БезOwnerИUser()
    {
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { name = "Дешёвый фон", steps = new[] { "tier:weak" } } },
        });

        foreach (var client in new[] { _admin, _user })
        {
            var settings = await (await client.GetAsync("/api/specialties/settings"))
                .Content.ReadFromJsonAsync<JsonElement>();

            settings.TryGetProperty("owner", out _).Should().BeFalse("личного слоя больше нет");
            settings.TryGetProperty("user", out _).Should().BeFalse("слоя «пользователь» больше нет");
            settings.GetProperty("version").GetInt32().Should().Be(5);

            var presets = settings.GetProperty("presets").EnumerateArray().ToList();
            presets.Should().ContainSingle();
            presets[0].GetProperty("scope").GetString().Should().Be("global");
            presets[0].GetProperty("steps")[0].GetString().Should().Be("tier:weak");
        }

        await ResetGlobalLayer();
    }

    // Запись настроек — только админ, чтение — любой аутентифицированный.
    [Fact]
    public async Task Settings_ЗаписьТолькоАдмин_ЧтениеВсем()
    {
        var layer = new
        {
            specialties = new Dictionary<string, object> { ["executor"] = new { access = "readOnly" } },
            presets = Array.Empty<object>(),
        };

        (await _user.PutAsJsonAsync("/api/specialties/settings/global", layer)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "настройки специальностей общие — правит их админ");
        (await _user.PutAsJsonAsync("/api/specialties/settings/fallback/global", new { maxSubstitutions = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _user.PostAsJsonAsync("/api/specialties/settings/reset/global", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await _user.GetAsync("/api/specialties/settings")).StatusCode
            .Should().Be(HttpStatusCode.OK, "чтение общего слоя доступно всем");
        (await _admin.PutAsJsonAsync("/api/specialties/settings/global", layer)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        await ResetGlobalLayer();
    }

    // Маршруты записи per-owner и назначения пользователю сняты вместе со слоями.
    [Theory]
    [InlineData("PUT", "/api/specialties/settings")]
    [InlineData("PUT", "/api/specialties/settings/owner")]
    [InlineData("PUT", "/api/specialties/settings/fallback/owner")]
    [InlineData("GET", "/api/specialties/settings/user/whoever")]
    [InlineData("PUT", "/api/specialties/settings/user/whoever")]
    public async Task Settings_СнятыеМаршруты_НеСуществуют(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "PUT")
            request.Content = JsonContent.Create(new
            {
                specialties = new Dictionary<string, object>(),
                presets = Array.Empty<object>(),
            });

        // Админский клиент: 404/405 — это отсутствие маршрута, а не отказ по роли
        var response = await _admin.SendAsync(request);
        response.StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
            $"{method} {route} снят вместе со слоями настроек");
    }

    private async Task ResetGlobalLayer() =>
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = Array.Empty<object>(),
        });

    // --- Сквозное применение шаблона к персоне ---

    [Fact]
    public async Task Persona_СозданиеСоСпециальностью_ПодставляетШаблон()
    {
        // Шаблон инстанса: бэкенд-исполнитель — readOnly с только web
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { access = "readOnly", tools = new[] { "web" } },
            },
            presets = Array.Empty<object>(),
        });

        var response = await _user.PostAsJsonAsync("/api/personas", new
        {
            name = "Тестовый бэкендер",
            specialty = "backendExecutor",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();

        persona.GetProperty("access").GetString().Should().Be("readOnly", "подставлено из шаблона");
        persona.GetProperty("tools").EnumerateArray().Single().GetString().Should().Be("web");

        await ResetGlobalLayer();
    }

    [Fact]
    public async Task Persona_СменаСпециальности_ПодставляетИПравитсяРуками()
    {
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { access = "readOnly", tools = new[] { "web" } },
            },
            presets = Array.Empty<object>(),
        });

        var created = await (await _user.PostAsJsonAsync("/api/personas", new
        {
            name = "Тестовый универсал",
            specialty = "executor",
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("access").GetString().Should().Be("full", "дефолтный шаблон исполнителя");

        // Смена специальности → подстановка личного шаблона backendExecutor
        var updated = await (await _user.PutAsJsonAsync($"/api/personas/{id}", new
        {
            specialty = "backendExecutor",
        })).Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("specialty").GetString().Should().Be("backendExecutor");
        updated.GetProperty("access").GetString().Should().Be("readOnly");
        updated.GetProperty("tools").EnumerateArray().Single().GetString().Should().Be("web");

        // Ручная правка после подстановки сохраняется: источник правды — персона,
        // та же специальность в запросе шаблон повторно не применяет
        var manual = await (await _user.PutAsJsonAsync($"/api/personas/{id}", new
        {
            access = "custom",
            disallowedTools = new[] { "Bash" },
        })).Content.ReadFromJsonAsync<JsonElement>();
        manual.GetProperty("access").GetString().Should().Be("custom");
        manual.GetProperty("disallowedTools").EnumerateArray().Single().GetString().Should().Be("Bash");
        manual.GetProperty("specialty").GetString().Should().Be("backendExecutor");

        await ResetGlobalLayer();
    }
}
