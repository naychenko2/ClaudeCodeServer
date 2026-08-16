using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

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
        images.Should().BeEmpty();
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
