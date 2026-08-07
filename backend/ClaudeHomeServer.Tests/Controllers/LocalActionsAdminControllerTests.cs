using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Управление маршрутом фоновых действий (локаль/claude) — эндпоинт только для админов.
public class LocalActionsAdminControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public LocalActionsAdminControllerTests(TestWebApplicationFactory factory)
    {
        _admin = factory.CreateAuthenticatedClient();
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    [Fact]
    public async Task ОбычныйПользовательНеМожетМенятьМаршрут()
    {
        var put = await _user.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "claude" });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var del = await _user.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task АдминПереключаетМаршрутИВидитЭтоВUsage()
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "claude" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("source").GetString().Should().Be("admin");
        // Легаси-значение одиночной «модели по умолчанию» нормализуется в средний слот:
        // в ответ уходит слот, а не эхо ввода — селектор в UI знает только слоты и модели
        body.GetProperty("route").GetString().Should().Be("tier:medium");

        // Экран «Использование» отдаёт тот же источник — тумблер в UI будет отмечен как переопределённый
        var usage = await _admin.GetFromJsonAsync<JsonElement>("/api/usage");
        var action = usage.GetProperty("ollama").GetProperty("actions").EnumerateArray()
            .First(a => a.GetProperty("key").GetString() == LocalActionCatalog.NotesTags);
        action.GetProperty("source").GetString().Should().Be("admin");

        // Сброс возвращает действие к дефолту каталога
        var del = await _admin.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await del.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("source").GetString().Should().Be("default");
    }

    [Fact]
    public async Task АдминНазначаетМестуСлотТира()
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "tier:strong" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("route").GetString().Should().Be("tier:strong");
        body.GetProperty("source").GetString().Should().Be("admin");

        await _admin.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
    }

    [Fact]
    public async Task НеизвестноеДействие404()
    {
        var put = await _admin.PutAsJsonAsync("/api/admin/local-actions/bogus-action", new { route = "local" });
        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task АдминПрименяетTiersПресеты()
    {
        var tiers = await _admin.PostAsJsonAsync("/api/admin/local-actions/preset", new { preset = "tiers" });
        tiers.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await tiers.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("preset").GetString().Should().Be("tiers");

        var tiersLocal = await _admin.PostAsJsonAsync("/api/admin/local-actions/preset", new { preset = "tiers-local" });
        tiersLocal.StatusCode.Should().Be(HttpStatusCode.OK);
        body = await tiersLocal.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("preset").GetString().Should().Be("tiers-local");
    }

    [Fact]
    public async Task НесуществующаяМодель400()
    {
        // Опечатка в id модели должна отлавливаться при сохранении, а не всплывать
        // при первом фоновом вызове через полчаса
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "нет-такой-модели" });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ПресетДопустимМестуКаталога_ПриНаличииВСторе()
    {
        // Создаём общий пресет (ADR-007 §3): место «Кто что выполняет» обязано принимать preset:{id}
        var presetId = "ps-" + Guid.NewGuid().ToString("N");
        var layer = new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[]
            {
                new { id = presetId, name = "Тест-цепочка", steps = new[] { "tier:strong", "glm-5.2" } },
            },
        };
        (await _admin.PutAsJsonAsync("/api/specialties/settings/global", layer)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // preset:{id} принимается местом каталога (раньше падал в «Модель отсутствует в каталоге»)
        var ok = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.ChatPersona}", new { route = $"preset:{presetId}" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Несуществующий id пресета — 400 (опечатка ловится при сохранении, как у моделей)
        var bad = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.ChatNew}", new { route = "preset:нет-такого-пресета" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Describe_PresetМаршрут_ОтдаётПресетСИменем()
    {
        var presetId = "ds-" + Guid.NewGuid().ToString("N");
        (await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[]
            {
                new { id = presetId, name = "Цепочка", steps = new[] { "tier:strong", "glm-5.2" } },
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.ChatNew}", new { route = $"preset:{presetId}" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        // preset раскрыт именем и шагами — UI показывает «Цепочка», а не первый шаг
        var preset = body.GetProperty("preset");
        preset.GetProperty("id").GetString().Should().Be(presetId);
        preset.GetProperty("name").GetString().Should().Be("Цепочка");
        preset.GetProperty("steps").EnumerateArray().Select(s => s.GetString())
            .Should().Equal(new[] { "tier:strong", "glm-5.2" });
        // Существующие поля на месте (обратная совместимость — «Сейчас пойдёт» и старый UI)
        body.GetProperty("route").GetString().Should().NotBeNull();
        body.GetProperty("source").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task Describe_НеПresetМаршрут_PresetNull()
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "tier:strong" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("preset").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("route").GetString().Should().Be("tier:strong");
    }

    // --- unit DescribePreset: через HTTP битую ссылку не смоделировать (валидация Set
    //     отсекает несуществующий id), поэтому пробуем логику напрямую. ---

    // Сериализуем descriptor в wire-формат (camelCase, как отдаёт ASP.NET) — чтобы unit-проверка
    // видела те же имена полей, что и фронт (id/name/steps), а не PascalCase дефолта S.T.J.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonElement DescriptorAsJson(object? obj) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj!, WireOptions));

    [Fact]
    public void DescribePreset_ВалиднаяСсылка_ИмяИШаги()
    {
        var presets = new[]
        {
            new ModelRoutePreset { Id = "p1", Name = "Рабочая", Steps = ["opus", "glm-5.2"] },
        };
        var d = DescriptorAsJson(LocalActionsAdminController.DescribePreset("preset:p1", presets));
        d.GetProperty("id").GetString().Should().Be("p1");
        d.GetProperty("name").GetString().Should().Be("Рабочая");
        d.GetProperty("steps").EnumerateArray().Select(s => s.GetString())
            .Should().Equal(new[] { "opus", "glm-5.2" });
    }

    [Fact]
    public void DescribePreset_БитаяСсылка_NameNullStepsПусто()
    {
        // Битая ссылка (пресет удалён, файл правили руками) → { id, name=null, steps=[] }.
        var d = DescriptorAsJson(LocalActionsAdminController.DescribePreset("preset:ghost", Array.Empty<ModelRoutePreset>()));
        d.GetProperty("id").GetString().Should().Be("ghost");
        d.GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null);
        d.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void DescribePreset_НеСсылка_Null()
    {
        LocalActionsAdminController.DescribePreset("tier:strong", Array.Empty<ModelRoutePreset>())
            .Should().BeNull();
        LocalActionsAdminController.DescribePreset(null, Array.Empty<ModelRoutePreset>())
            .Should().BeNull();
    }
}
