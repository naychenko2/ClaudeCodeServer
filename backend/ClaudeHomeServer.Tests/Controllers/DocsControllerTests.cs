using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-слой панели «Доки»: авторизация, чужой проект, гейт области.
// Разбор корпуса покрыт юнит-тестами DocsIndexTests — здесь только контракт эндпоинтов.
public class DocsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public DocsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "docs_tests");
        Directory.CreateDirectory(_tempDir);
    }

    // Проект с README, документом в docs/ и файлом вне области
    private async Task<string> SetupProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        Directory.CreateDirectory(Path.Combine(dir, "backend"));
        File.WriteAllText(Path.Combine(dir, "README.md"), "# Проект\n\nСмотри [архитектуру](./docs/architecture.md).");
        File.WriteAllText(Path.Combine(dir, "docs", "architecture.md"), "# Архитектура\n\n## Слои\n\nтекст про слои");
        File.WriteAllText(Path.Combine(dir, "backend", "SECRET.md"), "# Секрет");

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DocsProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Индекс_ОтдаётТолькоДокументыОбласти()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/docs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var paths = body.EnumerateArray().Select(d => d.GetProperty("path").GetString()).ToList();
        paths.Should().BeEquivalentTo(["README.md", "docs/architecture.md"]);
    }

    [Fact]
    public async Task Документ_ОтдаётСодержимоеИСвязи()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/docs/doc?path=docs/architecture.md");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("title").GetString().Should().Be("Архитектура");
        body.GetProperty("content").GetString().Should().Contain("текст про слои");
        // README ссылается сюда — обратная ссылка на месте
        body.GetProperty("backlinks").EnumerateArray().Single()
            .GetProperty("path").GetString().Should().Be("README.md");
    }

    [Fact]
    public async Task Документ_ВнеОбласти_Возвращает404()
    {
        var id = await SetupProjectAsync();

        // Файл существует и лежит внутри проекта, но документацией не считается
        var response = await _client.GetAsync($"/api/projects/{id}/docs/doc?path=backend/SECRET.md");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Документ_ВыходЗаКорень_Возвращает404()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/docs/doc?path=../../secret.md");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Поиск_НаходитПоТелуДокумента()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/docs/search?q=слои");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.EnumerateArray().Should().NotBeEmpty();
        body.EnumerateArray().First().GetProperty("path").GetString().Should().Be("docs/architecture.md");
    }

    [Fact]
    public async Task Область_ОтдаётВыбранноеКандидатовИДефолты()
    {
        var id = await SetupProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/docs/scope");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var selected = body.GetProperty("selected");
        selected.GetProperty("folders").EnumerateArray().Select(x => x.GetString()).Should().Equal("docs");
        selected.GetProperty("rootFiles").EnumerateArray().Select(x => x.GetString()).Should().Equal("README.md");
        selected.GetProperty("types").EnumerateArray().Select(x => x.GetString()).Should().Equal("markdown");
        // backend/SECRET.md делает backend кандидатом, хотя в область он не входит
        body.GetProperty("folderCandidates").EnumerateArray().Select(c => c.GetProperty("path").GetString())
            .Should().Contain(["docs", "backend"]);
        body.GetProperty("rootFileCandidates").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Contain("README.md");
        // Группы типов — из них и выбирают; расширения внутри группы клиенту не нужны
        body.GetProperty("typeGroups").EnumerateArray().Select(g => g.GetProperty("key").GetString())
            .Should().Contain(["markdown", "pdf", "visio", "audio"]);
    }

    [Fact]
    public async Task Область_СменаПапок_МеняетИндексИГейт()
    {
        var id = await SetupProjectAsync();

        var put = await _client.PutAsJsonAsync($"/api/projects/{id}/docs/scope",
            new { folders = new[] { "backend" }, rootFiles = new[] { "README.md" }, types = new[] { "markdown" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var index = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{id}/docs")).Content.ReadAsStringAsync());
        index.EnumerateArray().Select(d => d.GetProperty("path").GetString())
            .Should().BeEquivalentTo(["README.md", "backend/SECRET.md"]);

        // Гейт следует за настройкой: вчерашний документ области больше не отдаётся
        (await _client.GetAsync($"/api/projects/{id}/docs/doc?path=docs/architecture.md"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Область_СнятыйФайлКорня_УходитИзИндекса()
    {
        var id = await SetupProjectAsync();

        await _client.PutAsJsonAsync($"/api/projects/{id}/docs/scope",
            new { folders = new[] { "docs" }, rootFiles = Array.Empty<string>(), types = new[] { "markdown" } });

        var index = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{id}/docs")).Content.ReadAsStringAsync());
        index.EnumerateArray().Select(d => d.GetProperty("path").GetString())
            .Should().BeEquivalentTo(["docs/architecture.md"]);
    }

    [Fact]
    public async Task Область_МусорныеЗначения_НеСохраняются()
    {
        var id = await SetupProjectAsync();

        var put = await _client.PutAsJsonAsync($"/api/projects/{id}/docs/scope", new
        {
            folders = new[] { "../../etc", "docs/" },
            rootFiles = new[] { "docs/architecture.md", "README.md" },
            types = new[] { "выдумка", "MARKDOWN" },
        });

        var selected = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync())
            .GetProperty("selected");
        selected.GetProperty("folders").EnumerateArray().Select(x => x.GetString()).Should().Equal("docs");
        selected.GetProperty("rootFiles").EnumerateArray().Select(x => x.GetString()).Should().Equal("README.md");
        selected.GetProperty("types").EnumerateArray().Select(x => x.GetString()).Should().Equal("markdown");
    }

    [Fact]
    public async Task НесуществующийПроект_Возвращает404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent/docs");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
