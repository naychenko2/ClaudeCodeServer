using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    // Настоящий PNG 100×100 (градиент) — SkiaSharp его декодирует, и миниатюра генерируется.
    // FakePng той же сигнатурой Skia не читается — тест фолбэка на оригинал.
    private static byte[] RealPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAIAAAD/gAIDAAACmklEQVR4nO2cwU7DMBBEx07//5s5VKUlid1uY3vfopU4RINE5w0kegfiIqmqbCpVZVO9X/9eDAl3350Rji3coripFun+VQ8XUcJFn3jT5o4aYymp3v+yaPzEpaRy00bjhy6ll2cWhJ+7lB7PLAg/eimpvt6G/m3ISymgOnjWiKUOzjUCqYN/jSjqgKgRQh0gNQKoA6RGEV4dIDXK45nFVQdIjec1Vh0gNf6ETHWA1NiHQHWA1DgJaeoAqXEeotQBUqMZctQBUqMXQtSBNUorJKgDbpRW6K4OxFFaoa86+PObQkd1QPCbQi91oPCbQhd1APGbwvXqwOI3hYvVAcdvCleqA5HfFC5TB3/U6+HxNsylmhS72zCX6lHMVgcI6hiKqeoAQR1GMU8dIKgjKSapAwR1MMUMdYCgjqcYrg4Q1CkUY9UBgjqLYqA6QFAnUoxSBwjqXIoh6gBBnU5xXR0gqCsoLqoDBHXR7/uKOkBQFy11vA1zqV74nTpAUJcupa/UAYK6einZ1QGC6rDU8ZmVS/XCz9UBguq2lD5WBwiq51LH2zCX6oVv1QGC6r+U3qkDBBWxlLrqAEGlLHV8ZtFQQUupoQ4QVNZSYvy3coylOrdh6HDWDwe+6ARdSrwXnbhLCfaiE3opkV50oi8lzItOAZbSv1CHdZ8YXR2WfmJodVj9iXHVwaFGUHXwqRFRHdxqhFMHzxqx1MG5RiB18K8RRR0QNUKoA6RGAHWA1CjCqwOkRnk8s7jqAKnxvMaqA6TGn5CpDpAa+xCoDpAaJyFNHSA1zkOUOkBqNEOOOkBq9EKIOrBGaYUEdcCN0grd1YE4SivMk9kMYZ7MZgjzZDZDmCezGcI8mc0Q5slshjBPZjOEeTKbIcyT2QwUP2GvNdcQjiWyAAAAAElFTkSuQmCC");

    // PNG: ширина/высота в IHDR — байты 16..19 и 20..23 (big-endian)
    private static (int W, int H) PngSize(byte[] png) =>
        ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));

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
    public async Task GetAvatar_БезРазмера_Оригинал_КэшImmutable()
    {
        var id = await CreatePersonaAsync();
        var uploaded = await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(RealPng(), RealPng()));
        var persona = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var imageFile = persona.GetProperty("avatar").GetProperty("imageFile").GetString()!;

        var response = await _client.GetAsync($"/api/personas/{id}/avatar");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(RealPng());
        // ASP.NET нормализует Cache-Control в канонический порядок (max-age первым)
        response.Headers.GetValues("Cache-Control").Should().ContainSingle()
            .Which.Should().Be("max-age=604800, private, immutable");
        File.Exists(Path.Combine(_factory.TempDir, "personas", id, imageFile)).Should().BeTrue();
    }

    [Fact]
    public async Task GetAvatar_СРазмером_Миниатюра64_ГенерируетсяОдинРаз()
    {
        var id = await CreatePersonaAsync();
        var uploaded = await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(RealPng(), RealPng()));
        var persona = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var imageFile = persona.GetProperty("avatar").GetProperty("imageFile").GetString()!;
        var dir = Path.Combine(_factory.TempDir, "personas", id);

        var response = await _client.GetAsync($"/api/personas/{id}/avatar?size=40");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsByteArrayAsync();
        PngSize(body).Should().Be((64, 64));
        body.Should().NotBeEquivalentTo(RealPng());   // не оригинал, а уменьшенная копия
        // Миниатюра сохранена рядом с оригиналом: avatar-<guid>-64.png
        var thumb = Path.Combine(dir, imageFile[..^4] + "-64.png");
        File.Exists(thumb).Should().BeTrue();
        File.ReadAllBytes(thumb).Should().BeEquivalentTo(body);

        // Второй запрос — из сохранённого файла: байты те же, файл не тронут (нет регенерации)
        var mtimeBefore = File.GetLastWriteTimeUtc(thumb);
        var second = await _client.GetAsync($"/api/personas/{id}/avatar?size=40");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(body);
        File.GetLastWriteTimeUtc(thumb).Should().Be(mtimeBefore);
    }

    [Fact]
    public async Task GetAvatar_РазмерБольшеСетки_Оригинал()
    {
        var id = await CreatePersonaAsync();
        await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(RealPng(), RealPng()));

        var response = await _client.GetAsync($"/api/personas/{id}/avatar?size=512");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(RealPng());
    }

    [Fact]
    public async Task GetAvatar_БитаяКартинка_ФолбэкНаОригинал()
    {
        var id = await CreatePersonaAsync();
        await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(FakePng(), FakePng()));

        // 16-байтовый «PNG» Skia не декодирует — миниатюра не генерируется, отдаётся оригинал
        var response = await _client.GetAsync($"/api/personas/{id}/avatar?size=40");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(FakePng());
    }

    // Дефект (Перф-3 B10, п.0): при сбое генерации фолбэк на оригинал НЕ должен
    // залипать в кэше браузера. До фикса заголовок immutable выставлялся до попытки
    // генерации, и 1,5 МБ PNG с size=64 держался в кэше неделю. Теперь — no-cache:
    // следующий запрос попробует миниатюру снова.
    [Fact]
    public async Task GetAvatar_СбойГенерации_ФолбэкБезImmutable()
    {
        var id = await CreatePersonaAsync();
        await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(FakePng(), FakePng()));

        var response = await _client.GetAsync($"/api/personas/{id}/avatar?size=40");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Cache-Control").Should().ContainSingle()
            .Which.Should().Be("no-cache");
    }

    [Fact]
    public async Task GetAvatar_ЧужаяПерсона_404()
    {
        var id = await CreatePersonaAsync();   // персона основного юзера

        var response = await _stranger.GetAsync($"/api/personas/{id}/avatar?size=40");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Recrop_СтараяМиниатюра_Удаляется()
    {
        var id = await CreatePersonaAsync();
        var uploaded = await _client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(RealPng(), RealPng()));
        var before = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var oldImageFile = before.GetProperty("avatar").GetProperty("imageFile").GetString()!;
        var dir = Path.Combine(_factory.TempDir, "personas", id);
        var oldThumb = Path.Combine(dir, oldImageFile[..^4] + "-64.png");

        await _client.GetAsync($"/api/personas/{id}/avatar?size=40");   // сгенерировать миниатюру
        File.Exists(oldThumb).Should().BeTrue();

        var form = new MultipartFormDataContent();
        var cropped = new ByteArrayContent(RealPng());
        cropped.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(cropped, "cropped", "avatar.png");
        form.Add(new StringContent("""{"scale":3,"offsetX":0,"offsetY":0}"""), "crop");
        var response = await _client.PostAsync($"/api/personas/{id}/avatar/recrop", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await response.Content.ReadFromJsonAsync<JsonElement>();
        var newImageFile = after.GetProperty("avatar").GetProperty("imageFile").GetString()!;

        newImageFile.Should().NotBe(oldImageFile);
        File.Exists(oldThumb).Should().BeFalse();               // миниатюра старого аватара осиротела — удалена
        File.Exists(Path.Combine(dir, oldImageFile)).Should().BeFalse();
        File.Exists(Path.Combine(dir, newImageFile)).Should().BeTrue();
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

// Фабрика без единого настроенного генератора картинок: оба ключа пусты. Пустая строка
// (а не отсутствие ключа) важна — FalImageService иначе подхватил бы FAL_KEY из окружения
// машины, и «выключенная генерация» перестала бы быть выключенной.
public class NoImageProvidersFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fal:ApiKey"] = "",
                ["Glif:McpToken"] = "",
            });
        });
    }
}

// Аватар персоны при выключенной генерации: caps честно говорит «нельзя», а сам аватар
// не теряется — заявка уходит в очередь догоняющей генерации.
public class PersonasAvatarCapsTests : IClassFixture<NoImageProvidersFactory>
{
    private readonly NoImageProvidersFactory _factory;
    private readonly HttpClient _client;

    public PersonasAvatarCapsTests(NoImageProvidersFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task Caps_БезНастроенныхПровайдеров_GenerateFalseБезПровайдераИМодели()
    {
        var response = await _client.GetAsync("/api/personas/avatar/caps");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("generate").GetBoolean().Should().BeFalse();
        body.GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Generate_БезНастроенныхПровайдеров_400ИЗаявкаВОчереди()
    {
        var created = await _client.PostAsJsonAsync("/api/personas", new { name = "Без аватара" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/personas/{id}/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().NotContain("Fal:ApiKey");

        var store = _factory.Services.GetRequiredService<ImageBackfillStore>();
        store.Find(ImageBackfillKinds.PersonaAvatar, id).Should().NotBeNull();
    }
}
