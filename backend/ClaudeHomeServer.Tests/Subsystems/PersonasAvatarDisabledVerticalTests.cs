using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace ClaudeHomeServer.Tests.Subsystems;

// Вертикаль images выключена гейтом Subsystems:images:Enabled=false
// (тот же DisabledFactory-приём, что в ImageEditorDisabledTests; ключ совпадает с ключом
// строки матрицы). Ручка генерации аватара отвечает 503 с причиной (ADR-014 форма 3),
// и гейт стоит ДО разбора персоны: на выключенной вертикали «персоны нет» — тоже 503,
// потому что причина отказа — вертикаль, а не отсутствие пробы. На контрольном включённом
// хосте «персоны нет» остаёт 404 — различение двух отказов сохранено.
// Готовая миниатюра на диске отдаётся и без растра — вызывающий не отдаёт оригинал.
[Trait("Category", "Host")]
public class PersonasAvatarDisabledVerticalTests : IClassFixture<PersonasAvatarDisabledVerticalTests.Hosts>
{
    private readonly Hosts _hosts;

    public PersonasAvatarDisabledVerticalTests(Hosts hosts) => _hosts = hosts;

    // Два хоста на КЛАСС: выключенный меряет отказ вертикали, включённый — контрольный 404
    public sealed class Hosts : IDisposable
    {
        public DisabledFactory Disabled { get; } = new();

        // Контрольный хост: без гейта persona-пути живые — значит 503 ниже от гейта,
        // а не от сломанного маршрута
        public EnabledFactory Enabled { get; } = new();

        public void Dispose()
        {
            Disabled.Dispose();
            Enabled.Dispose();
        }
    }

    public sealed class EnabledFactory : TestWebApplicationFactory;

    public sealed class DisabledFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:images:Enabled", "false");
        }
    }

    // Настоящий PNG 100×100 (градиент) — тот же, что в PersonasControllerAvatarTests
    private static byte[] RealPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAIAAAD/gAIDAAACmklEQVR4nO2cwU7DMBBEx07//5s5VKUlid1uY3vfopU4RINE5w0kegfiIqmqbCpVZVO9X/9eDAl3350Rji3coripFun+VQ8XUcJFn3jT5o4aYymp3v+yaPzEpaRy00bjhy6ll2cWhJ+7lB7PLAg/eimpvt6G/m3ISymgOnjWiKUOzjUCqYN/jSjqgKgRQh0gNQKoA6RGEV4dIDXK45nFVQdIjec1Vh0gNf6ETHWA1NiHQHWA1DgJaeoAqXEeotQBUqMZctQBUqMXQtSBNUorJKgDbpRW6K4OxFFaoa86+PObQkd1QPCbQi91oPCbQhd1APGbwvXqwOI3hYvVAcdvCleqA5HfFC5TB3/U6+HxNsylmhS72zCX6lHMVgcI6hiKqeoAQR1GMU8dIKgjKSapAwR1MMUMdYCgjqcYrg4Q1CkUY9UBgjqLYqA6QFAnUoxSBwjqXIoh6gBBnU5xXR0gqCsoLqoDBHXR7/uKOkBQFy11vA1zqV74nTpAUJcupa/UAYK6einZ1QGC6rDU8ZmVS/XCz9UBguq2lD5WBwiq51LH2zCX6oVv1QGC6r+U3qkDBBWxlLrqAEGlLHV8ZtFQQUupoQ4QVNZSYvy3coylOrdh6HDWDwe+6ARdSrwXnbhLCfaiE3opkV50oi8lzItOAZbSv1CHdZ8YXR2WfmJodVj9iXHVwaFGUHXwqRFRHdxqhFMHzxqx1MG5RiB18K8RRR0QNUKoA6RGAHWA1CjCqwOkRnk8s7jqAKnxvMaqA6TGn5CpDpAa+xCoDpAaJyFNHSA1zkOUOkBqNEOOOkBq9EKIOrBGaYUEdcCN0grd1YE4SivMk9kMYZ7MZgjzZDZDmCezGcI8mc0Q5slshjBPZjOEeTKbIcyT2QwUP2GvNdcQjiWyAAAAAElFTkSuQmCC");

    // Миниатюра «из прошлого»: произвольные байты с PNG-сигнатурой — сервис существующий
    // файл не переписывает и не проверяет его содержимое, вызывающий отдаёт его как есть
    private static byte[] CachedThumbBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF];

    private static MultipartFormDataContent UploadForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(part, "original", "original.png");
        var cropped = new ByteArrayContent(bytes);
        cropped.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(cropped, "cropped", "avatar.png");
        form.Add(new StringContent("""{"scale":2,"offsetX":0,"offsetY":0}"""), "crop");
        return form;
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/personas", new { name });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Существующая персона на выключенной вертикали — 503 с причиной в теле
    [Fact]
    public async Task Generate_ВертикальВыключена_503СПричинойАНе400()
    {
        var client = _hosts.Disabled.CreateAuthenticatedClient();
        var id = await CreatePersonaAsync(client, "Без генерации");

        var response = await client.PostAsJsonAsync($"/api/personas/{id}/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Подсистема генерации изображений выключена");
        // Та же метка причины, что у прочих починенных вертикалей (сторож матрицы
        // «Тело_503_несёт_причину…» меряет её по всем привязанным маршрутам images)
        body.Should().Contain("Subsystems:images:Enabled=false");
    }

    // «Персоны нет» на выключенной вертикали — тоже 503, не 404: гейт раньше
    // разбора персоны, и проба матрицы (несуществующий id) меряет отказ вертикали,
    // а не отсутствие пробы. Мутация «проверять персону раньше гейта» красит этот тест.
    [Fact]
    public async Task Generate_ВертикальВыключенаПерсоныНет_Тоже503ГейтРаньшеПерсоны()
    {
        var client = _hosts.Disabled.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/personas/нет-такой-персоны/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "на выключенной вертикали причина отказа — вертикаль; 404 здесь мерял бы пробу, а не гейт");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Subsystems:images:Enabled=false");
    }

    // Обратная сторона: на включённой вертикали «персоны нет» остаёт 404 —
    // перенос гейта не превратил все 404 в 503, различение двух отказов сохранено
    [Fact]
    public async Task Generate_ВертикальВключенаПерсоныНет_Остаётся404()
    {
        var client = _hosts.Enabled.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/personas/нет-такой-персоны/avatar/generate",
            new { prompt = "рыжая кошка", count = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Миниатюра записана на диске («пока вертикаль была включена»), растра нет —
    // GET avatar?size отдаёт миниатюру, а не оригинал: кэшированный файл не требует генерации
    [Fact]
    public async Task Avatar_ВертикальВыключенаГотоваяМиниатюра_ОтдаётМиниатюруНеОригинал()
    {
        var client = _hosts.Disabled.CreateAuthenticatedClient();
        var id = await CreatePersonaAsync(client, "Готовая миниатюра");

        // Загрузка аватара — ядро персон, работает и на выключенной вертикали
        var uploaded = await client.PostAsync($"/api/personas/{id}/avatar/upload", UploadForm(RealPng()));
        uploaded.StatusCode.Should().Be(HttpStatusCode.OK);
        var imageFile = (await uploaded.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("avatar").GetProperty("imageFile").GetString()!;

        // Миниатюра ступени 64 лежит рядом с оригиналом — записана до выключения вертикали
        var dir = Path.Combine(_hosts.Disabled.TempDir, "personas", id);
        var thumbPath = Path.Combine(dir, imageFile[..^4] + "-64.png");
        var thumbBytes = CachedThumbBytes();
        File.WriteAllBytes(thumbPath, thumbBytes);

        var response = await client.GetAsync($"/api/personas/{id}/avatar?size=40");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().BeEquivalentTo(thumbBytes);          // это миниатюра с диска
        body.Should().NotBeEquivalentTo(RealPng());        // не фолбэк на оригинал
        // Путь миниатюры — долгий immutable-кэш (фолбэк на оригинал дал бы no-cache):
        // заголовок отличает «миниатюру отдали» от «миниатюры нет»
        response.Headers.GetValues("Cache-Control").Should().ContainSingle()
            .Which.Should().Be("max-age=604800, private, immutable");
    }
}
