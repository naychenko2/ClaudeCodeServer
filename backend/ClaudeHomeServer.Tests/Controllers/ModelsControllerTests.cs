using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class ModelsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ModelsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_AssignmentsRespectUserTierOverrides()
    {
        // Основной тестовый пользователь — admin (TestUsername), второй — user
        var admin = _factory.CreateAuthenticatedClient();
        var userClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        // Гарантируем известное состояние: у admin — личный strong, у user — наследование
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "admin-opus", medium = "", weak = "" });
        await userClient.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });

        // GET /api/models отдаёт resolved assignments с учётом caller-id
        var response = await admin.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var assignments = body.GetProperty("assignments");
        assignments.GetProperty("chat-new").GetString().Should().Be("admin-opus");

        // У второго пользователя личного слота нет — назначение chat-new должно совпадать
        // с глобальным (null, если глобальный тоже пуст)
        var userResponse = await userClient.GetAsync("/api/models");
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var userBody = JsonSerializer.Deserialize<JsonElement>(await userResponse.Content.ReadAsStringAsync());
        var userAssignments = userBody.GetProperty("assignments");
        userAssignments.GetProperty("chat-new").GetString().Should().BeNull();
    }

    [Fact]
    public async Task Preview_ЯчейкаПерсоныБезУровня_ИдётМодельюЯчейки()
    {
        // п.3 (запись tierStrong через API) + п.1 (preview): персона с заполненной ячейкой,
        // без явного уровня, в чате персоны идёт моделью своей ячейки (source=persona-cell).
        var admin = _factory.CreateAuthenticatedClient();

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Тест-превью",
            tierStrong = "persona-opus",
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var personaId = persona.GetProperty("id").GetString()!;
        // Ячейка сохранена и отдаётся в ответе (п.3 — проброс полей)
        persona.GetProperty("tierStrong").GetString().Should().Be("persona-opus");

        var resp = await admin.GetAsync($"/api/models/preview?place=chat-persona&personaId={personaId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var d = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        d.GetProperty("model").GetString().Should().Be("persona-opus");
        d.GetProperty("source").GetString().Should().Be("persona-cell");
        d.GetProperty("tier").GetString().Should().Be("strong");
        d.GetProperty("tierOrigin").GetString().Should().Be("place");
    }

    [Fact]
    public async Task PresetUsage_СчитаетМестаПоСторам()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var presetId = "pu-" + Guid.NewGuid().ToString("N");

        // Общий пресет
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Где я", steps = new[] { "tier:strong" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Ставим пресет в личный слот strong текущего админа
        (await admin.PutAsJsonAsync("/api/me/model-tiers",
            new { strong = $"preset:{presetId}", medium = "", weak = "" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await admin.GetAsync($"/api/models/presets/{presetId}/usage");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("presetId").GetString().Should().Be(presetId);
        body.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        var kinds = body.GetProperty("usages").EnumerateArray()
            .Select(u => u.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain("owner-slot");
    }

    [Fact]
    public async Task Preview_СпециальностьБезПерсоны_ПоАпи()
    {
        var admin = _factory.CreateAuthenticatedClient();
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { tierStrong = "spec-opus", defaultTier = "strong" },
            },
            presets = Array.Empty<object>(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Без personaId/place — превью карточки специальности: модель из ячейки специальности
        var resp = await admin.GetAsync("/api/models/preview?specialty=backendExecutor");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var d = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        d.GetProperty("model").GetString().Should().Be("spec-opus");
        d.GetProperty("source").GetString().Should().Be("specialty-cell");
        d.GetProperty("tier").GetString().Should().Be("strong");
        d.GetProperty("tierOrigin").GetString().Should().Be("specialty");
    }
}
