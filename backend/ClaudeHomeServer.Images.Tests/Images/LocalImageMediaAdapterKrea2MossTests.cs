using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов редактора с Krea 2 Turbo и MOSS-Image: генерация по тексту — своим графом, всё с картинками — на Qwen
public class LocalImageMediaAdapterKrea2MossTests
{
    private readonly FakeComfy _comfy = new();

    private LocalImageMediaAdapter Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
        }).Build();
        return new LocalImageMediaAdapter(new ComfyClient(new FakeComfyFactory(_comfy), config), config,
            NullLogger<LocalImageMediaAdapter>.Instance);
    }

    private string Unet() =>
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!["unet"]!["inputs"]!["unet_name"]!.GetValue<string>();

    [Theory]
    [InlineData(LocalImageModels.Krea2Turbo, "krea2_turbo_int8_convrot.safetensors")]
    [InlineData(LocalImageModels.Moss, "moss_image_sft_int8_convrot.safetensors")]
    public async Task Генерация_СвойГраф_РазмерИзТаблицы1Мп(string model, string unet)
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "ёжик", [], "16:9", 2, Model: model), default);

        submitted.Error.Should().BeNull();
        Unet().Should().Be(unet);
        var lat = _comfy.Prompts[0]["prompt"]!["lat"]!["inputs"]!;
        lat["width"]!.GetValue<int>().Should().Be(1424);
        lat["height"]!.GetValue<int>().Should().Be(800);
        lat["batch_size"]!.GetValue<int>().Should().Be(2);
        submitted.EtaSeconds.Should().Be(model == LocalImageModels.Moss
            ? LocalMediaService.MossGenerateEta(2)
            : LocalMediaService.Krea2GenerateEta(2));
    }

    [Theory]
    [InlineData(LocalImageModels.Krea2Turbo)]
    [InlineData(LocalImageModels.Moss)]
    public async Task ГенерацияПоОбразцам_УходитНаQwen(string model)
    {
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "как на образце",
            [LocalMediaTestImages.Png(8, 8)], "1:1", 1, Model: model), default);

        Unet().Should().Be("qwen_image_2.1_int8_convrot.safetensors");
    }

    [Fact]
    public async Task Правка_УходитНаQwen()
    {
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "добавь лодку",
            [LocalMediaTestImages.Png(64, 64)], null, 1, Model: LocalImageModels.Krea2Turbo), default);

        Unet().Should().Be("qwen_image_2.1_int8_convrot.safetensors");
    }
}
