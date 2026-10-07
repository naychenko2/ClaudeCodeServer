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

    // Главный инвариант записи: пустой список отмеченных правок означает «применять
    // нечего» — ответ штатный, файл не тронут ни на байт
    [Fact]
    public async Task Применение_ПустойСписокIds_НичегоНеПишетВФайл()
    {
        var id = await SetupProjectAsync();
        var root = await RootPathAsync(id);
        var scan = await ScanAsync(id);
        var before = File.ReadAllBytes(Path.Combine(root, "CLAUDE.md"));

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/apply",
            new { baseSha = scan.GetProperty("baseSha").GetString(), ids = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("applied").EnumerateArray().Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(root, "CLAUDE.md")).Should().Equal(before);
    }

    // Чужой писал бы в чужой CLAUDE.md, если бы проверка владения отсутствовала
    [Fact]
    public async Task Применение_ПроектЧужогоВладельца_Возвращает404()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var response = await stranger.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/apply",
            new { baseSha = scan.GetProperty("baseSha").GetString(), ids = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Живой путь целиком: отмеченная человеком правка чинит ссылку в файле, а ответ несёт
    // отчёт по итоговому тексту — фронту не нужен второй запрос ради перерисовки шапки
    [Fact]
    public async Task Применение_ЧинитМёртвуюСсылкуВФайле()
    {
        var id = await SetupProjectAsync();
        var root = await RootPathAsync(id);
        // Одноимённый файл в проекте ровно один — условие механической починки
        var candidate = Path.Combine(root, "backend", "Core", "Models");
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "FeatureFlag.cs"), "// код");

        var scan = await ScanAsync(id);
        var suggestion = scan.GetProperty("suggestions").EnumerateArray()
            .First(s => s.GetProperty("kind").GetString() == "dead-link");
        suggestion.GetProperty("apply").GetProperty("after").GetString()
            .Should().Be("[нет такого](backend/Core/Models/FeatureFlag.cs)");

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/apply",
            new { baseSha = scan.GetProperty("baseSha").GetString(), ids = new[] { suggestion.GetProperty("id").GetString() } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("applied").EnumerateArray().Should().HaveCount(1);
        body.GetProperty("failed").EnumerateArray().Should().BeEmpty();
        body.GetProperty("scan").GetProperty("deadLinkCount").GetInt32().Should().Be(0);
        File.ReadAllText(Path.Combine(root, "CLAUDE.md"))
            .Should().Contain("backend/Core/Models/FeatureFlag.cs");
    }

    // Карту дописали, пока отчёт был открыт: файл не тронут, человек видит плашку
    // «файл изменился после проверки · Проверить заново»
    [Fact]
    public async Task Применение_УстаревшийBaseSha_Возвращает409_ФайлНеТронут()
    {
        var id = await SetupProjectAsync();
        var root = await RootPathAsync(id);
        var before = File.ReadAllBytes(Path.Combine(root, "CLAUDE.md"));

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/apply",
            new { baseSha = "устарел", ids = new[] { "deadbeefdeadbeef" } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Be("staleBaseSha");
        body.GetProperty("baseSha").GetString().Should().NotBeNullOrEmpty();
        File.ReadAllBytes(Path.Combine(root, "CLAUDE.md")).Should().Equal(before);
    }

    private async Task<string> RootPathAsync(string id)
    {
        var response = await _client.GetAsync($"/api/projects/{id}");
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("rootPath").GetString()!;
    }

    // Заглушка раннера в тестовой фабрике отдаёт «[]» — не разбираемый как суждения
    // ответ. Это и есть штатный тихий фолбэк: факты сканера доезжают целиком, а причину
    // неполноты пишет СЕРВЕР. Пустой экран или 500 здесь были бы дефектом
    [Fact]
    public async Task Ревью_ОтветМоделиНеРазобрался_ОтдаётФактыИПричинуОтСервера()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);

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

    // Сторож шва с фронтом: review отвечает ПОЛНЫМ отчётом, той же формой, что scan.
    // Раньше он отдавал тройку { baseSha, modelNote, suggestions }, фронт клал её в то же
    // состояние, что и скан, и первый же живой вызов падал в рендере на report.lines —
    // при зелёных тестах обеих сторон и зелёном tsc. Проверяем ИМЕНА полей в JSON:
    // разъезжается контракт именно здесь, а не в типах C#
    [Fact]
    public async Task Ревью_ОтдаётПолныйОтчёт_ТойЖеФормыЧтоСкан()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review",
            new { baseSha = scan.GetProperty("baseSha").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        // Каждое поле скана есть и в ответе review — иначе шапка отчёта осталась бы
        // без чисел, а родительская сводка получила бы битый объект
        foreach (var field in scan.EnumerateObject())
            body.TryGetProperty(field.Name, out _).Should().BeTrue($"поле «{field.Name}» из скана обязано доехать");
        body.GetProperty("path").GetString().Should().Be(scan.GetProperty("path").GetString());
        body.GetProperty("lines").GetInt32().Should().Be(scan.GetProperty("lines").GetInt32());
        body.GetProperty("bytes").GetInt64().Should().Be(scan.GetProperty("bytes").GetInt64());
        body.GetProperty("approxTokens").GetInt32().Should().Be(scan.GetProperty("approxTokens").GetInt32());
        body.GetProperty("budget").GetProperty("recommendedLines").GetInt32()
            .Should().Be(scan.GetProperty("budget").GetProperty("recommendedLines").GetInt32());
        body.GetProperty("baseSha").GetString().Should().Be(scan.GetProperty("baseSha").GetString());
    }

    // Ход модели идёт до минут, и за это время карту в общем дереве могут дописать:
    // суждения приехали бы по новому составу поверх старых фактов на экране — с другими
    // id, то есть с чекбоксами, тихо переставшими совпадать с находками
    [Fact]
    public async Task Ревью_УстаревшийBaseSha_Возвращает409()
    {
        var id = await SetupProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/map-hygiene/review",
            new { baseSha = "устарел" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Be("staleBaseSha");
        // Актуальный отпечаток отдаётся сразу: фронту не нужен второй запрос ради «Проверить заново»
        body.GetProperty("baseSha").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ревью_ПроектЧужогоВладельца_Возвращает404()
    {
        var id = await SetupProjectAsync();
        var scan = await ScanAsync(id);
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
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

    [Fact]
    public async Task Скан_БезАвторизации_Возвращает401()
    {
        var id = await SetupProjectAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/projects/{id}/map-hygiene/scan");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
