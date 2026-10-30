using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Гейт «в вики пишет только Хранитель» на загрузке файлов (ADR-035, решение 2).
//
// Upload и SaveFromUrl пишут FileStream мимо FileService — их часть проверки стоит в
// контроллере. Тестовый хост держит data/ в своей TempDir (TestWebApplicationFactory:
// DataPath = {TempDir}/projects.json), поэтому «проект на весь домашний каталог» — это
// проект с rootPath = TempDir: относительный путь «wiki/{owner}/…» под ним лежит под
// data/wiki/. В бою data/ вне корней проектов — страховка ровно на этот случай.
//
// Фабрика — на каждый тест: ProjectManager.EnsureRootFree запрещает второй проект на той
// же папке, а общий IClassFixture-хост дал бы всем тестам один и тот же корень.
public class FilesControllerWikiGateTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public FilesControllerWikiGateTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> CreateProjectOverDataDirAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Проект-над-data", rootPath = _factory.TempDir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Upload_ПодDataWiki_Отказывает403_ИФайлаНаДискеНет()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        var target = Path.Combine(_factory.TempDir, "wiki", "u1", "pages", "Страница.md");

        var form = new MultipartFormDataContent();
        var part = new StringContent("тело");
        part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(part, "file", "Страница.md");

        var response = await _client.PostAsync($"/api/projects/{projectId}/files/upload?path=wiki/u1/pages", form);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        File.Exists(target).Should().BeFalse();
        // и папки не появилось: отказ стоит до CreateDirectory
        Directory.Exists(Path.Combine(_factory.TempDir, "wiki")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveFromUrl_ПодDataWiki_Отказывает403_ИФайлаНаДискеНет()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        var target = Path.Combine(_factory.TempDir, "wiki", "u1", "pages", "Из-URL.png");

        // Публичный IP-литерал: SsrfGuard пропускает его без DNS. Гейт стоит до запроса —
        // ничего не скачано, на диске пусто.
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/save-from-url",
            new { url = "http://8.8.8.8/x.png", path = "wiki/u1/pages/Из-URL.png" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        File.Exists(target).Should().BeFalse();
        Directory.Exists(Path.Combine(_factory.TempDir, "wiki")).Should().BeFalse();
    }

    [Fact]
    public async Task Revert_ПодDataWiki_Отказывает403_АНе500()
    {
        // RevertFile бросает до проверки git-репозитория: без catch в маршруте это был бы 500.
        var projectId = await CreateProjectOverDataDirAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/revert",
            new { path = "wiki/u1/pages/Страница.md" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Upload_ОбычныйПутьПроекта_РаботаетКакПрежде()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        var target = Path.Combine(_factory.TempDir, "загружено.txt");

        var form = new MultipartFormDataContent();
        var part = new StringContent("тело");
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "загружено.txt");

        var response = await _client.PostAsync($"/api/projects/{projectId}/files/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(target).Should().Be("тело");
    }
}
