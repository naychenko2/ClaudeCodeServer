using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-слой гигиены карты проекта: авторизация, чужой проект, форма ответа.
// Сам разбор покрыт юнит-тестами ProjectMapScannerTests — здесь только контракт эндпоинта.
public class ProjectMapControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public ProjectMapControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "map_hygiene_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> SetupProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"),
            "# Карта\n\n## Раздел\n\n[нет такого](backend/Models/FeatureFlag.cs)\n");

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "MapProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Скан_ОтдаётФактыКарты()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("exists").GetBoolean().Should().BeTrue();
        body.GetProperty("path").GetString().Should().Be("CLAUDE.md");
        body.GetProperty("deadLinks").EnumerateArray().Single()
            .GetProperty("target").GetString().Should().Be("backend/Models/FeatureFlag.cs");
    }

    // Кейс 9 плана: несуществующий проект — 404
    [Fact]
    public async Task Скан_НесуществующийПроект_Возвращает404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Главный инвариант эндпоинта — изоляция по владельцу, и проверять его надо на РЕАЛЬНО
    // существующем чужом проекте: по выдуманному id срабатывает ветка «проекта нет»,
    // а проверка владельца при этом может отсутствовать вовсе. Чужой владелец иначе получил
    // бы карту чужого репозитория — имена секций, пути файлов, вложенные карты
    [Fact]
    public async Task Скан_ПроектЧужогоВладельца_Возвращает404()
    {
        var id = await SetupProjectAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── review: формулировки модели поверх тех же фактов ───────────────────────

    // Ручки review и apply закрыты фич-флагом (план Р11): пока он выключен, фичи для
    // пользователя не существует — 404, а не 403 и не 501. Держим оба конца в одном
    // тесте: гейт теряется именно при сведении веток, и «включённый флаг работает»
    // без «выключенный отвечает 404» ловит только половину пропажи
    [Fact]
    public async Task Ревью_ГейтФичФлага_ВыключенныйФлагДаёт404_ВключённыйПускает()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);
        var body = new { baseSha = scan.GetProperty("baseSha").GetString() };

        await SetFeatureAsync(_client, false);
        var closed = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review", body);
        closed.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await SetFeatureAsync(_client, true);
        var opened = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review", body);
        opened.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // apply — заглушка волны 4, но гейт на ней уже настоящий: ручка ПИШЕТ в CLAUDE.md,
    // и открытой при выключенной фиче быть не должна ни на одном этапе
    [Fact]
    public async Task Применение_ВыключенныйФлаг_Возвращает404()
    {
        var id = await SetupProjectAsync();
        await SetFeatureAsync(_client, false);

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/apply",
            new { baseSha = "любой", ids = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Скан флагом не закрыт (план §11): он ничего не меняет, и тест волны 1 флага не знает
    [Fact]
    public async Task Скан_ВыключенныйФлаг_ВсёРавноОтдаётФакты()
    {
        var id = await SetupProjectAsync();
        await SetFeatureAsync(_client, false);

        var response = await _client.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Заглушка раннера в тестовой фабрике отдаёт «[]» — не разбираемый как суждения
    // ответ. Это и есть штатный тихий фолбэк: факты сканера доезжают целиком, а причину
    // неполноты пишет СЕРВЕР. Пустой экран или 500 здесь были бы дефектом
    [Fact]
    public async Task Ревью_ОтветМоделиНеРазобрался_ОтдаётФактыИПричинуОтСервера()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);
        await SetFeatureAsync(_client, true);

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review",
            new { baseSha = scan.GetProperty("baseSha").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("modelNote").GetString().Should().NotBeNullOrEmpty();
        var suggestions = body.GetProperty("suggestions").EnumerateArray().ToList();
        suggestions.Should().NotBeEmpty();
        suggestions[0].GetProperty("kind").GetString().Should().Be("dead-link");
        suggestions[0].GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        // Формулировки нет, а факт есть — принцип «факт важнее формулировки»
        suggestions[0].GetProperty("modelSays").ValueKind.Should().Be(JsonValueKind.Null);
        suggestions[0].GetProperty("fact").GetString().Should().Contain("FeatureFlag.cs");
    }

    // Ход модели идёт до минут, и за это время карту в общем дереве могут дописать:
    // суждения приехали бы по новому составу поверх старых фактов на экране — с другими
    // id, то есть с чекбоксами, тихо переставшими совпадать с находками
    [Fact]
    public async Task Ревью_УстаревшийBaseSha_Возвращает409()
    {
        var id = await SetupProjectAsync();
        await SetFeatureAsync(_client, true);

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review",
            new { baseSha = "устарел" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Be("staleBaseSha");
        // Актуальный отпечаток отдаётся сразу: фронту не нужен второй запрос ради «Проверить заново»
        body.GetProperty("baseSha").GetString().Should().NotBeNullOrEmpty();
    }

    // Флаг чужому владельцу включаем НАМЕРЕННО: иначе 404 пришёл бы от гейта фичи,
    // проверка владения не выполнилась бы вовсе, а тест остался бы зелёным при любой
    // дыре в изоляции
    [Fact]
    public async Task Ревью_ПроектЧужогоВладельца_Возвращает404()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await SetFeatureAsync(stranger, true);

        var response = await stranger.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review",
            new { baseSha = scan.GetProperty("baseSha").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<JsonElement> ScanAsync(string id)
    {
        var response = await _client.GetAsync($"/api/projects/{id}/map-hygiene/scan");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    // Флаг per-user, значение живёт в users.json — выставляем тем же клиентом, чьи ручки
    // потом дёргаем
    private static async Task SetFeatureAsync(HttpClient client, bool enabled)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.ProjectMapHygiene}", new { enabled });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Скан_БезАвторизации_Возвращает401()
    {
        var id = await SetupProjectAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
