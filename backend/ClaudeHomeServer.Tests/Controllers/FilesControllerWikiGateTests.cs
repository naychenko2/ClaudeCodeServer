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
// Хост — на КЛАСС (храповик, тест. «Г3»): data-каталог один на класс, а предмет теста —
// именно проект, корень которого и есть этот каталог: гейт работает по абсолютному пути
// {data}/wiki/**, боковая папка под него не подпадает. Общего корня несколько тесту
// не даёт EnsureRootFree (одна папка — один проект на владельца), поэтому каждый тест
// заводит СВОЙ проект на TempDir и после себя удаляет: удаление освобождает корень,
// следующий тест создаёт свой. Внутри класса xUnit гоняет тесты последовательно.
[Trait("Category", "Host")]
public class FilesControllerWikiGateTests : IClassFixture<FilesControllerWikiGateTests.Hosts>
{
    // Хост на класс: один экземпляр хоста и его TempDir на все тесты класса.
    public sealed class Hosts : TestWebApplicationFactory { }

    private readonly Hosts _hosts;
    private readonly HttpClient _client;

    public FilesControllerWikiGateTests(Hosts hosts)
    {
        _hosts = hosts;
        _client = hosts.CreateAuthenticatedClient();
    }

    private async Task<string> CreateProjectOverDataDirAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Проект-над-data", rootPath = _hosts.TempDir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Уборка — дело теста, а не предмет проверки: код ответа не проверяем; не удалили бы —
    // следующий тест упал бы на создании проекта с явной причиной «папка уже подключена».
    private async Task DeleteProjectAsync(string id) => await _client.DeleteAsync($"/api/projects/{id}");

    [Fact]
    public async Task Upload_ПодDataWiki_Отказывает403_ИФайлаНаДискеНет()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        try
        {
            var target = Path.Combine(_hosts.TempDir, "wiki", "u1", "pages", "Страница.md");

            var form = new MultipartFormDataContent();
            var part = new StringContent("тело");
            part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
            form.Add(part, "file", "Страница.md");

            var response = await _client.PostAsync($"/api/projects/{projectId}/files/upload?path=wiki/u1/pages", form);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            File.Exists(target).Should().BeFalse();
            // и папки не появилось: отказ стоит до CreateDirectory
            Directory.Exists(Path.Combine(_hosts.TempDir, "wiki")).Should().BeFalse();
        }
        finally { await DeleteProjectAsync(projectId); }
    }

    [Fact]
    public async Task SaveFromUrl_ПодDataWiki_Отказывает403_ИФайлаНаДискеНет()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        try
        {
            var target = Path.Combine(_hosts.TempDir, "wiki", "u1", "pages", "Из-URL.png");

            // Публичный IP-литерал: SsrfGuard пропускает его без DNS. Гейт стоит до запроса —
            // ничего не скачано, на диске пусто.
            var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/save-from-url",
                new { url = "http://8.8.8.8/x.png", path = "wiki/u1/pages/Из-URL.png" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            File.Exists(target).Should().BeFalse();
            Directory.Exists(Path.Combine(_hosts.TempDir, "wiki")).Should().BeFalse();
        }
        finally { await DeleteProjectAsync(projectId); }
    }

    [Fact]
    public async Task Revert_ПодDataWiki_Отказывает403_АНе500()
    {
        // RevertFile бросает до проверки git-репозитория: без catch в маршруте это был бы 500.
        var projectId = await CreateProjectOverDataDirAsync();
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/revert",
                new { path = "wiki/u1/pages/Страница.md" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await DeleteProjectAsync(projectId); }
    }

    [Fact]
    public async Task Upload_ОбычныйПутьПроекта_РаботаетКакПрежде()
    {
        var projectId = await CreateProjectOverDataDirAsync();
        try
        {
            var target = Path.Combine(_hosts.TempDir, "загружено.txt");

            var form = new MultipartFormDataContent();
            var part = new StringContent("тело");
            part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(part, "file", "загружено.txt");

            var response = await _client.PostAsync($"/api/projects/{projectId}/files/upload", form);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            File.ReadAllText(target).Should().Be("тело");
        }
        finally { await DeleteProjectAsync(projectId); }
    }
}
