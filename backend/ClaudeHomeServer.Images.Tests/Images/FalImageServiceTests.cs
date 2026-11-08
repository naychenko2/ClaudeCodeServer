using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Images;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Драйвер fal: сборка тела запроса по модели (схемы входа у семейств fal разные),
// каталог моделей для пикера и маппинг content-type в расширение файла. Сеть не трогаем.
public class FalImageServiceTests
{
    private static FalImageService Service(string? apiKey, string? configuredModel = null)
    {
        var values = new Dictionary<string, string?>();
        if (apiKey is not null) values["Fal:ApiKey"] = apiKey;
        if (configuredModel is not null) values["Fal:ImageModel"] = configuredModel;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new FalImageService(
            Mock.Of<IHttpClientFactory>(), config, NullLogger<FalImageService>.Instance);
    }

    [Fact]
    public void БезКлюча_ДрайверВыключен()
    {
        // Ключа нет и в окружении теста FAL_KEY не ждём — иначе проверка бессмысленна
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_KEY")))
            Service(null).Enabled.Should().BeFalse();
        Service("fal-key").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task БезКлюча_ГенерацияВозвращаетПусто_БезСети()
    {
        // Фабрика клиентов — заглушка: полезет в сеть — упадёт по NRE
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAL_KEY"))) return;
        var images = await Service(null).GenerateManyAsync("иконка проекта", 2, null);
        images.Images.Should().BeEmpty();
        images.RequestId.Should().BeNull();
    }

    // Фабрика клиентов с подставным обработчиком: сеть не нужна, ответ задаёт тест
    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(respond));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static FalImageService ServiceWith(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "k" }).Build();
        return new FalImageService(new StubFactory(respond), config, NullLogger<FalImageService>.Instance);
    }

    [Fact]
    public async Task RequestId_ВсеСкачиванияУпали_ВсёРавноВозвращён()
    {
        // 200 и images[], но data:-ссылка с битым base64 не скачивается: картинок нет, а запрос принят
        var svc = ServiceWith(_ =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"images\":[{\"url\":\"data:image/png;base64,!!!\"}]}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("x-fal-request-id", "req-dl-fail");
            return resp;
        });

        var result = await svc.GenerateManyAsync("кот", 1, null);

        result.Images.Should().BeEmpty();
        result.RequestId.Should().Be("req-dl-fail");
    }

    [Fact]
    public async Task RequestId_Ответ422СЗаголовком_ВозвращённПриПустыхКартинках()
    {
        var svc = ServiceWith(_ =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("{\"detail\":\"bad\"}"),
            };
            resp.Headers.Add("x-fal-request-id", "req-422");
            return resp;
        });

        var result = await svc.GenerateManyAsync("кот", 1, null);

        result.Images.Should().BeEmpty();
        result.RequestId.Should().Be("req-422");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task RequestId_ТелоНеРазбирается_ВсёРавноВозвращён(string body)
    {
        // 200 с заголовком, но тело не JSON (прокси-страница) или не объект: ответ уже получен,
        // запрос принят и оплачен — id терять нельзя
        var svc = ServiceWith(_ =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("x-fal-request-id", "req-x");
            return resp;
        });

        var result = await svc.GenerateManyAsync("кот", 1, null);

        result.Images.Should().BeEmpty();
        result.RequestId.Should().Be("req-x");
    }

    [Fact]
    public async Task RequestId_200БезImages_ВозвращённПриПустыхКартинках()
    {
        var svc = ServiceWith(_ =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"detail\":\"x\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("x-fal-request-id", "req-noimg");
            return resp;
        });

        var result = await svc.GenerateManyAsync("кот", 1, null);

        result.Images.Should().BeEmpty();
        result.RequestId.Should().Be("req-noimg");
    }

    [Fact]
    public async Task RequestId_ИсключениеДоОтвета_Null()
    {
        var svc = ServiceWith(_ => throw new HttpRequestException("нет сети"));

        var result = await svc.GenerateManyAsync("кот", 1, null);

        result.Images.Should().BeEmpty();
        result.RequestId.Should().BeNull();
    }

    [Fact]
    public void Каталог_СодержитАктуальныеМодели_ДефолтПервый()
    {
        var ids = Service("k").Models.Select(m => m.Id).ToList();

        ids[0].Should().Be(FalImageService.DefaultModel, "дефолт стоит первым в пикере");
        ids.Should().Contain([
            "fal-ai/nano-banana-2",
            "fal-ai/flux-2/klein/9b",
            "fal-ai/recraft/v4.1/text-to-vector",
        ]);
        FalImageService.DefaultModel.Should().NotContain("text-to-vector",
            "векторная модель отдаёт SVG и дефолтом не ставится");
    }

    [Fact]
    public void Каталог_МодельИзКонфига_ДобавляетсяПервой()
    {
        var models = Service("k", "custom-vendor/my-model").Models;

        models[0].Id.Should().Be("custom-vendor/my-model");
        models[0].Description.Should().Contain("Fal:ImageModel");
        // Уже известная модель в конфиге не дублируется
        Service("k", "fal-ai/flux/dev").Models
            .Count(m => m.Id == "fal-ai/flux/dev").Should().Be(1);
    }

    [Fact]
    public void ТелоЗапроса_FluxПодобные_ЭтоImageSizeSquareHdИNumImages()
    {
        var body = FalImageService.BuildRequestBody("fal-ai/flux-2/klein/9b", "иконка", 3);

        body["prompt"].Should().Be("иконка");
        body["image_size"].Should().Be("square_hd", "у \"square\" сторона 512 — иконка мылит");
        body["num_images"].Should().Be(3);
    }

    [Fact]
    public void ТелоЗапроса_НезнакомаяМодель_ИдётПоFluxСхеме()
    {
        var body = FalImageService.BuildRequestBody("custom-vendor/my-model", "иконка", 1);

        body.Keys.Should().BeEquivalentTo(["prompt", "image_size", "num_images"]);
    }

    [Fact]
    public void ТелоЗапроса_NanoBanana_БезImageSize_СAspectRatioИResolution()
    {
        // У nano-banana-2 в схеме нет image_size — лишний параметр вернул бы 422
        var body = FalImageService.BuildRequestBody("fal-ai/nano-banana-2", "иконка", 2);

        body.Should().NotContainKey("image_size");
        body["aspect_ratio"].Should().Be("1:1");
        body["resolution"].Should().Be("1K");
        body["num_images"].Should().Be(2);
    }

    [Fact]
    public void ТелоЗапроса_ВекторныйRecraft_БезNumImages()
    {
        // У recraft/v4.1/text-to-vector в схеме нет num_images
        var body = FalImageService.BuildRequestBody("fal-ai/recraft/v4.1/text-to-vector", "лого", 4);

        body.Keys.Should().BeEquivalentTo(["prompt", "image_size"]);
        body["image_size"].Should().Be("square_hd");
    }

    [Fact]
    public void ТелоЗапроса_ЛишниеСлешиВИдентификатореНеМешают()
    {
        FalImageService.BuildRequestBody("/fal-ai/nano-banana-2/", "x", 1)
            .Should().ContainKey("aspect_ratio");
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/svg+xml; charset=utf-8", ".svg")]
    [InlineData("application/octet-stream", ".png")]
    public void РасширениеПоContentType(string contentType, string expected)
    {
        ImageAssetHelper.ExtFor(contentType).Should().Be(expected);
    }
}
