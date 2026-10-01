using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Своя картинка иконки проекта (ревизия ADR-009 от 01.10.2026): загрузка, отдача,
// переключение режима, права владельца и проверка формата по байтам.
public class ProjectIconImageControllerTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"10\"/></svg>";

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var dir = Path.Combine(factory.TempDir, "icon_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Иконочный", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Имя файла от клиента нарочно врёт: формат обязан определяться по байтам
    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string projectId, byte[] bytes) =>
        client.PostAsync($"/api/projects/{projectId}/icon/upload", new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "logo.png" },
        });

    private static async Task<JsonElement> IconOf(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("icon");

    [Fact]
    public async Task Загруженный_svg_становится_иконкой_и_отдаётся_с_CSP()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        var upload = await UploadAsync(owner, projectId, Encoding.UTF8.GetBytes(Svg));

        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        var icon = await IconOf(upload);
        icon.GetProperty("kind").GetString().Should().Be("image");
        icon.GetProperty("imageFile").GetString().Should().EndWith(".svg");

        var image = await owner.GetAsync($"/api/projects/{projectId}/icon/image");
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        image.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("default-src 'none'");
    }

    [Fact]
    public async Task Повторная_загрузка_меняет_имя_файла_и_удаляет_прежний()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        var first = (await IconOf(await UploadAsync(owner, projectId, Png))).GetProperty("imageFile").GetString()!;
        var second = (await IconOf(await UploadAsync(owner, projectId, Encoding.UTF8.GetBytes(Svg))))
            .GetProperty("imageFile").GetString()!;

        second.Should().NotBe(first);
        var dir = Path.Combine(factory.TempDir, "project-icon-images", projectId);
        File.Exists(Path.Combine(dir, first)).Should().BeFalse();
        File.Exists(Path.Combine(dir, second)).Should().BeTrue();
    }

    [Fact]
    public async Task Мусор_и_svg_с_DTD_отвергаются()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);
        const string dtd =
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaa\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&a;</svg>";

        (await UploadAsync(owner, projectId, Encoding.UTF8.GetBytes("not an image")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(owner, projectId, Encoding.UTF8.GetBytes(dtd)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(owner, projectId, Encoding.UTF8.GetBytes("<html><body/></html>")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Картинка_больше_лимита_отвергается()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);
        var big = new byte[ProjectIconImage.MaxBytes + 1];
        Png.CopyTo(big, 0);

        (await UploadAsync(owner, projectId, big)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Не_владелец_получает_404()
    {
        var owner = factory.CreateAuthenticatedClient();
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectId = await CreateProjectAsync(owner);
        (await UploadAsync(owner, projectId, Png)).EnsureSuccessStatusCode();

        (await UploadAsync(stranger, projectId, Png)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/projects/{projectId}/icon/image"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Режим_image_без_картинки_отвергается_а_с_картинкой_возвращается()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/icon/mode", new { kind = "image" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await UploadAsync(owner, projectId, Png)).EnsureSuccessStatusCode();
        var toInitials = await owner.PostAsJsonAsync($"/api/projects/{projectId}/icon/mode", new { kind = "initials" });
        (await IconOf(toInitials)).GetProperty("kind").GetString().Should().Be("initials");

        // Путь назад на инициалы не стирает картинку — обратно без повторной загрузки
        var back = await owner.PostAsJsonAsync($"/api/projects/{projectId}/icon/mode", new { kind = "image" });
        back.StatusCode.Should().Be(HttpStatusCode.OK);
        (await IconOf(back)).GetProperty("kind").GetString().Should().Be("image");
    }

    [Fact]
    public async Task Удаление_проекта_уносит_картинку()
    {
        var owner = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(owner);
        (await UploadAsync(owner, projectId, Png)).EnsureSuccessStatusCode();
        var dir = Path.Combine(factory.TempDir, "project-icon-images", projectId);
        Directory.Exists(dir).Should().BeTrue();

        (await owner.DeleteAsync($"/api/projects/{projectId}")).EnsureSuccessStatusCode();

        Directory.Exists(dir).Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, "ico")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    public void Формат_определяется_по_сигнатуре(byte[] bytes, string extension) =>
        ProjectIconImage.Detect(bytes)!.Extension.Should().Be(extension);
}
