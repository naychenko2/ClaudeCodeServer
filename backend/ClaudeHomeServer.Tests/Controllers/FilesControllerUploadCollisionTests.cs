using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Х-10 (хвост ревью Р-Х-7): коллизия имени с каталогом на проектных загрузочных дверях
// даёт 409 «exists», а не 500 — тот же класс, что починен в личном маршруте (Х-7,
// коммит 2a7899b74). Upload: File.Move в атомарной записи бросает IOException —
// ловится ровно вокруг записи. SaveFromUrl пишет прямым FileStream: на каталоге он
// бросает UnauthorizedAccessException (было 403) — поэтому здесь явная проверка цели,
// как в Rename; настоящий отказ права записи остаётся 403.
// Форма тела — своя для FilesController: {error:"exists"} (CreateFile/Rename), а не
// личная {error,code}.
// Стаб IHttpClientFactory — как в PersonalFilesEndpointTests: настоящей сети нет,
// поведение по пути URL; адрес — публичный литерал 8.8.8.8 (SSRF-guard решает без DNS).
[Trait("Category", "Host")]
public class FilesControllerUploadCollisionTests : IClassFixture<FilesControllerUploadCollisionTests.Host>
{
    public sealed class Host : TestWebApplicationFactory
    {
        public Host() => ExtraServices = services => services.AddSingleton<IHttpClientFactory>(new StubHttpFactory());
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            private static readonly byte[] Small = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                HttpResponseMessage resp;
                if (path.EndsWith("/small.png"))
                    resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Small) };
                else
                    resp = new HttpResponseMessage(HttpStatusCode.NotFound);
                return Task.FromResult(resp);
            }
        }
    }

    private readonly Host _host;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public FilesControllerUploadCollisionTests(Host host)
    {
        _host = host;
        _client = host.CreateAuthenticatedClient();
        _tempDir = Path.Combine(host.TempDir, "upload_collision");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Проект с готовой папкой dirName на диске: (id, корень).</summary>
    private async Task<(string ProjectId, string Dir)> ProjectWithDirAsync(string dirName)
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, dirName));
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "CollisionProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return (json.GetProperty("id").GetString()!, dir);
    }

    private static MultipartFormDataContent Form(string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", name);
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Загрузка_файла_с_именем_существующей_папки_даёт_409_exists_а_не_500()
    {
        var (projectId, dir) = await ProjectWithDirAsync("docs");

        var resp = await _client.PostAsync($"/api/projects/{projectId}/files/upload", Form("docs"));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("error").GetString().Should().Be("exists",
            "та же форма коллизии имени, что у create/rename");

        // каталог цел, файла под его именем нет, временный файл вычищен
        Directory.Exists(Path.Combine(dir, "docs")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "docs")).Should().BeFalse();
        Directory.GetFiles(dir).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Сохранение_по_URL_с_именем_существующей_папки_даёт_409_exists_а_не_500()
    {
        var (projectId, dir) = await ProjectWithDirAsync("sub");
        File.WriteAllText(Path.Combine(dir, "sub", "keep.txt"), "внутри");

        var save = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/save-from-url",
            new { url = "http://8.8.8.8/small.png", path = "sub" });
        save.StatusCode.Should().Be(HttpStatusCode.Conflict, await save.Content.ReadAsStringAsync());
        (await Json(save)).GetProperty("error").GetString().Should().Be("exists",
            "та же форма коллизии имени, что у create/rename");

        // каталог и его содержимое целы, файла под его именем нет
        Directory.Exists(Path.Combine(dir, "sub")).Should().BeTrue();
        File.ReadAllText(Path.Combine(dir, "sub", "keep.txt")).Should().Be("внутри");
        File.Exists(Path.Combine(dir, "sub")).Should().BeFalse();
    }

    [Fact]
    public async Task Свободная_цель_записывается_как_раньше_а_перезапись_файла_разрешена()
    {
        var (projectId, dir) = await ProjectWithDirAsync("docs");

        // Upload на свободное имя — 200, файл на месте
        var up = await _client.PostAsync($"/api/projects/{projectId}/files/upload?path=docs", Form("a.bin"));
        up.StatusCode.Should().Be(HttpStatusCode.OK, await up.Content.ReadAsStringAsync());
        File.ReadAllBytes(Path.Combine(dir, "docs", "a.bin")).Should().Equal([1, 2, 3]);

        // Upload поверх существующего файла — перезапись разрешена, как раньше
        var again = await _client.PostAsync($"/api/projects/{projectId}/files/upload?path=docs", Form("a.bin"));
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        File.ReadAllBytes(Path.Combine(dir, "docs", "a.bin")).Should().Equal([1, 2, 3]);

        // SaveFromUrl на свободное имя — 200 (для этого и живёт стаб сети)
        var save = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/save-from-url",
            new { url = "http://8.8.8.8/small.png", path = "docs/b.png" });
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        File.ReadAllBytes(Path.Combine(dir, "docs", "b.png")).Should().Equal([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
    }

    // Узкость отказа: сеть за пределами коллизии остаётся 502 (catch не шире записи),
    // и обычный путь не изменился
    [Fact]
    public async Task Сетевой_сбой_save_from_url_остаётся_502_а_не_409()
    {
        var (projectId, _) = await ProjectWithDirAsync("docs");

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/save-from-url",
            new { url = "http://8.8.8.8/gone.png", path = "docs/x.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway, "404 от источника — прежнее 502, не 409");
    }
}
