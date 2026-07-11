using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Загрузка аватара персоны с кропом: multipart-валидация (ContentType + magic bytes),
// сохранение файлов, перекроп, изоляция по владельцу.
public class PersonasControllerAvatarTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _stranger;

    public PersonasControllerAvatarTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // Минимальный «PNG»: валидная сигнатура + произвольный хвост (валидация — по magic bytes)
    private static byte[] FakePng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private async Task<string> CreatePersonaAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/personas", new { name = "Аватарная" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static MultipartFormDataContent UploadForm(byte[] original, byte[] cropped,
        string contentType = "image/png", string crop = """{"scale":2,"offsetX":10,"offsetY":-5}""")
    {
        var form = new MultipartFormDataContent();
        var originalContent = new ByteArrayContent(original);
        originalContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(originalContent, "original", "original.png");
        var croppedContent = new ByteArrayContent(cropped);
        croppedContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(croppedContent, "cropped", "avatar.png");
        form.Add(new StringContent(crop), "crop");
        return form;
    }

    [Fact]
    public async Task Upload_ВалидныйPng_УстанавливаетАватарИПишетФайлы()
    {
        var id = await CreatePersonaAsync();

        var response = await _client.PostAsync($"/api/personas/{id}/avatar/upload",
            UploadForm(FakePng(), FakePng()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();
        var avatar = persona.GetProperty("avatar");
        avatar.GetProperty("kind").GetString().Should().Be("image");
        var imageFile = avatar.GetProperty("imageFile").GetString();
        var originalFile = avatar.GetProperty("originalFile").GetString();
        imageFile.Should().StartWith("avatar-").And.EndWith(".png");
        originalFile.Should().StartWith("original-").And.EndWith(".png");
        avatar.GetProperty("crop").GetProperty("scale").GetDouble().Should().Be(2);

        // Файлы реально лежат в data/personas/{id}/
        var dir = Path.Combine(_factory.TempDir, "personas", id);
        File.Exists(Path.Combine(dir, imageFile!)).Should().BeTrue();
        File.Exists(Path.Combine(dir, originalFile!)).Should().BeTrue();
    }

    [Fact]
    public async Task Upload_ФейковыйContentTypeБезMagicBytes_400()
    {
        var id = await CreatePersonaAsync();
        var textBytes = Encoding.UTF8.GetBytes("это совсем не картинка, а текст подлиннее");

        var response = await _client.PostAsync($"/api/personas/{id}/avatar/upload",
            UploadForm(textBytes, textBytes));   // ContentType заявлен image/png

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_НедопустимыйContentType_400()
    {
        var id = await CreatePersonaAsync();

        var response = await _client.PostAsync($"/api/personas/{id}/avatar/upload",
            UploadForm(FakePng(), FakePng(), contentType: "image/gif"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recrop_БезОригинала_400()
    {
        var id = await CreatePersonaAsync();
        var form = new MultipartFormDataContent();
        var cropped = new ByteArrayContent(FakePng());
        cropped.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(cropped, "cropped", "avatar.png");

        var response = await _client.PostAsync($"/api/personas/{id}/avatar/recrop", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recrop_ПослеЗагрузки_МеняетКартинкуИОставляетОригинал()
    {
        var id = await CreatePersonaAsync();
        var uploaded = await _client.PostAsync($"/api/personas/{id}/avatar/upload",
            UploadForm(FakePng(), FakePng()));
        var before = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var originalBefore = before.GetProperty("avatar").GetProperty("originalFile").GetString();

        var form = new MultipartFormDataContent();
        var cropped = new ByteArrayContent(FakePng());
        cropped.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(cropped, "cropped", "avatar.png");
        form.Add(new StringContent("""{"scale":3,"offsetX":0,"offsetY":0}"""), "crop");

        var response = await _client.PostAsync($"/api/personas/{id}/avatar/recrop", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await response.Content.ReadFromJsonAsync<JsonElement>();
        var avatar = after.GetProperty("avatar");
        avatar.GetProperty("originalFile").GetString().Should().Be(originalBefore);
        avatar.GetProperty("crop").GetProperty("scale").GetDouble().Should().Be(3);
    }

    [Fact]
    public async Task GetOriginal_ПослеЗагрузки_200()
    {
        var id = await CreatePersonaAsync();
        await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(FakePng(), FakePng()));

        var response = await _client.GetAsync($"/api/personas/{id}/avatar/original");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upload_ЧужаяПерсона_404()
    {
        var id = await CreatePersonaAsync();   // персона основного юзера

        var response = await _stranger.PostAsync($"/api/personas/{id}/avatar/upload",
            UploadForm(FakePng(), FakePng()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
