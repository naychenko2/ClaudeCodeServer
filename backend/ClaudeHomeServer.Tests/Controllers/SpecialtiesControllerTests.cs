using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

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
    }

    [Fact]
    public async Task Settings_ЛичноеПереопределениеВидноВКаталоге()
    {
        var put = await _user.PutAsJsonAsync("/api/specialties/settings", new
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
        settings.GetProperty("owner").GetProperty("specialties")
            .TryGetProperty("frontendExecutor", out var ownerEntry).Should().BeTrue();
        ownerEntry.GetProperty("access").GetString().Should().Be("custom");

        // Эффективный шаблон в каталоге reflects личное переопределение
        var items = (await (await _user.GetAsync("/api/specialties")).Content
                .ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(i => i.GetProperty("key").GetString()!);
        var template = items["frontendExecutor"].GetProperty("template");
        template.GetProperty("access").GetString().Should().Be("custom");
        template.GetProperty("disallowedTools").EnumerateArray().Single().GetString().Should().Be("Bash");
    }

    [Fact]
    public async Task Settings_НеизвестнаяСпециальность_400()
    {
        var response = await _admin.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object> { ["no-such"] = new { access = "full" } },
            presets = Array.Empty<object>(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_ОбъединённыйСписокПресетовСПризнакомОбщийМой()
    {
        // Админ задаёт общий пресет, пользователь — личный с тем же именем:
        // личный не затирает общий, оба видны в одном списке
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[]
            {
                new { name = "Дешёвый фон", steps = new[] { "tier:weak" } },
            },
        });
        await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[]
            {
                new { name = "Дешёвый фон", steps = new[] { "local" } },
            },
        });

        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var presets = settings.GetProperty("presets").EnumerateArray().ToList();

        presets.Should().HaveCount(2, "оба набора в одном списке");
        presets.Select(p => p.GetProperty("name").GetString()).Should().AllBeEquivalentTo("Дешёвый фон");
        presets[0].GetProperty("scope").GetString().Should().Be("owner");
        presets[0].GetProperty("steps")[0].GetString().Should().Be("local");
        presets[1].GetProperty("scope").GetString().Should().Be("global");
        presets[1].GetProperty("steps")[0].GetString().Should().Be("tier:weak");

        // Слои на месте — фронт правит пресеты по слоям, признак только в общем списке
        settings.GetProperty("global").GetProperty("presets").EnumerateArray().Should().ContainSingle();
        settings.GetProperty("owner").GetProperty("presets").EnumerateArray().Should().ContainSingle();

        // У админа свой личный слой пуст — в его списке только общий пресет
        var adminSettings = await (await _admin.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var adminPresets = adminSettings.GetProperty("presets").EnumerateArray().ToList();
        adminPresets.Should().ContainSingle();
        adminPresets[0].GetProperty("scope").GetString().Should().Be("global");
    }

    [Fact]
    public async Task Settings_ПустойЛичныйСлой_СбросКГлобальным()
    {
        await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object> { ["executor"] = new { access = "readOnly" } },
            presets = Array.Empty<object>(),
        });
        var reset = await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object>(),
            presets = Array.Empty<object>(),
        });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("owner").GetProperty("specialties").EnumerateObject()
            .Should().BeEmpty("пустой слой снял переопределения");
    }

    // QA-регресс: «бэкенд не отдаёт PUT /api/specialties/settings/owner — 404, только global».
    // Маршрут раньше не существовал (UI писал короткий PUT /settings). Теперь оба
    // маршрута живут, per-owner изоляция идёт от UserId JWT — подменить чужой слой
    // нельзя, сбрасывается пустым слоем.
    [Fact]
    public async Task Settings_ЛичныйСлойМаршрутOwner_Доступен_ИИзолирован()
    {
        var layer = new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { access = "custom", tools = new[] { "web" }, disallowedTools = new[] { "Bash" } },
            },
            presets = new[]
            {
                new { name = "Личный пресет владельца", steps = new[] { "tier:weak" } },
            },
        };

        // 1) Маршрут существует и отвечает 200 (раньше был 404)
        var put = await _user.PutAsJsonAsync("/api/specialties/settings/owner", layer);
        put.StatusCode.Should().Be(HttpStatusCode.OK,
            "явный маршрут per-owner должен жить наравне с коротким /settings");

        // 2) Прочитанный личный слой содержит то, что записали
        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var ownerSpec = settings.GetProperty("owner").GetProperty("specialties")
            .GetProperty("backendExecutor");
        ownerSpec.GetProperty("access").GetString().Should().Be("custom");
        ownerSpec.GetProperty("disallowedTools").EnumerateArray().Single().GetString().Should().Be("Bash");
        settings.GetProperty("owner").GetProperty("presets").EnumerateArray().Should().ContainSingle();

        // 3) Per-owner изоляция: админ не видит личный слой пользователя
        var adminSettings = await (await _admin.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        adminSettings.GetProperty("owner").GetProperty("presets").EnumerateArray().Should().BeEmpty(
            "UserId у админа свой — личный слой пользователя ему не виден");

        // 4) Сброс пустым слоем снимает переопределения
        var reset = await _user.PutAsJsonAsync("/api/specialties/settings/owner", new
        {
            specialties = new Dictionary<string, object>(),
            presets = Array.Empty<object>(),
        });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterReset = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        afterReset.GetProperty("owner").GetProperty("specialties").EnumerateObject().Should().BeEmpty();
        afterReset.GetProperty("owner").GetProperty("presets").EnumerateArray().Should().BeEmpty();

        // 5) Короткий и длинный маршруты пишут ОДНО И ТО ЖЕ: после записи через короткий
        // длинный тоже подхватывает (для UI ничего не меняется)
        await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object> { ["executor"] = new { access = "readOnly" } },
            presets = Array.Empty<object>(),
        });
        var viaLong = await _user.PutAsJsonAsync("/api/specialties/settings/owner", new
        {
            specialties = new Dictionary<string, object> { ["executor"] = new { access = "full" } },
            presets = Array.Empty<object>(),
        });
        viaLong.StatusCode.Should().Be(HttpStatusCode.OK);
        var finalSettings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        finalSettings.GetProperty("owner").GetProperty("specialties")
            .GetProperty("executor").GetProperty("access").GetString().Should().Be("full");
    }

    // Контроль доступа после снятия флага: не-админ не пишет глобальный слой
    // (раньше тест был «без флага → 403», теперь — «без admin-роли → 403»).
    [Fact]
    public async Task Settings_ГлобальныеБезAdmin_403()
    {
        var response = await _user.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = Array.Empty<object>(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "глобальный слой — только admin");
    }

    // --- Сквозное применение шаблона к персоне ---

    [Fact]
    public async Task Persona_СозданиеСоСпециальностью_ПодставляетШаблон()
    {
        // Личный шаблон: бэкенд-исполнитель — readOnly с только web
        await _user.PutAsJsonAsync("/api/specialties/settings", new
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
    }

    [Fact]
    public async Task Persona_СменаСпециальности_ПодставляетИПравитсяРуками()
    {
        await _user.PutAsJsonAsync("/api/specialties/settings", new
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
    }
}
