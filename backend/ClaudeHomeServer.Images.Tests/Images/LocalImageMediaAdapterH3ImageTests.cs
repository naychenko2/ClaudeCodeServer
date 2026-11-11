using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов редактора с MiniMax-H3-Image: какой граф уходит в ComfyUI, холст и запасной путь на Qwen
public class LocalImageMediaAdapterH3ImageTests
{
    private const string H3Unet = "minimax_h3_image_pruned_int8_convrot.safetensors";

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

    private System.Text.Json.Nodes.JsonObject Graph() =>
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();

    private static string Unet(System.Text.Json.Nodes.JsonObject graph) =>
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>();

    private static int Cond(System.Text.Json.Nodes.JsonObject graph, string input) =>
        graph["cond"]!["inputs"]![input]!.GetValue<int>();

    [Fact]
    public async Task Генерация_РазмерИзТаблицыH3_ПачкаВариантов()
    {
        var submitted = await Build().SubmitAsync(
            new LocalImageRequest(LocalImageOp.Generate, "ёжик", [], "16:9", 2, Model: LocalImageModels.H3Image), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be(H3Unet);
        Cond(graph, "width").Should().Be(2752);
        Cond(graph, "height").Should().Be(1536);
        graph["lat"]!["inputs"]!["amount"]!.GetValue<int>().Should().Be(2);
        graph["cond"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Be("ёжик");
        submitted.EtaSeconds.Should().Be(LocalMediaService.H3ImageGenerateEta(2));
    }

    [Fact]
    public async Task Генерация_ТочныйРазмерФормата_БерётсяКакЕсть()
    {
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "ёжик", [], "1:1", 1,
            Width: 4096, Height: 4096, Model: LocalImageModels.H3Image), default);

        var graph = Graph();
        Cond(graph, "width").Should().Be(4096);
        Cond(graph, "height").Should().Be(4096);
    }

    [Fact]
    public async Task Правка_ХолстПоИсходнику_ПриведёнКРоднойПлощади()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "добавь лодку",
            [LocalMediaTestImages.Png(1000, 750), LocalMediaTestImages.Png(8, 8)], null, 1, Model: LocalImageModels.H3Image), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be(H3Unet);
        Cond(graph, "width").Should().Be(2368);
        Cond(graph, "height").Should().Be(1760);
        graph["cond"]!["inputs"]!.AsObject().ContainsKey("images.image_2").Should().BeTrue("образец тоже идёт в узел условий");
        graph["cond"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Contain("<Picture 2>").And.EndWith("добавь лодку");
        submitted.EtaSeconds.Should().Be(LocalMediaService.H3ImageEditEta(2));
    }

    [Fact]
    public async Task Персонаж_ОстаётсяH3_БезЛоры_СоСходством()
    {
        var photos = Enumerable.Range(0, 3).Select(i => LocalMediaTestImages.Png(8 + i, 8)).ToList();
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "сидит в кафе",
            photos, "1:1", 1, Lora: new LocalImageLora("timur.safetensors", 0.8),
            FaceReferences: [LocalMediaTestImages.Png(8, 8), LocalMediaTestImages.Png(9, 9)], Model: LocalImageModels.H3Image), default);

        submitted.Error.Should().BeNull("LoRA для H3 не проверяется — её нет в списке ComfyUI");
        var graph = Graph();
        Unet(graph).Should().Be(H3Unet);
        graph.ContainsKey("lora").Should().BeFalse("LoRA персонажа обучены под Qwen-Image");
        graph["sim"]!["class_type"]!.GetValue<string>().Should().Be("CcsFaceSimilarity");
        Cond(graph, "width").Should().Be(2048);
        for (var k = 1; k <= 3; k++)
            graph["cond"]!["inputs"]![$"images.image_{k}"]!.ToJsonString().Should().Be($"[\"img{k}\",0]");
    }

    [Fact]
    public async Task ПодстановкаНаЛицо_УходитНаQwenКачественно()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "лицо как на фото",
            [LocalMediaTestImages.Png(64, 64), LocalMediaTestImages.Png(8, 8)], null, 1,
            KeepMask: LocalMediaTestImages.Png(64, 64), Crop: new LocalImageCrop(0, 0, 32, 32, 64, 64),
            Model: LocalImageModels.H3Image), default);

        submitted.Error.Should().BeNull();
        Unet(Graph()).Should().Be("qwen_image_2.1_int8_convrot.safetensors");
    }

    [Fact]
    public void РазмерПоПропорцииВнеТаблицы_ПропорцияQwenНаПлощадиH3()
    {
        LocalImageMediaAdapter.H3ImageSizeFor("1:1").Should().Be((2048, 2048));
        LocalImageMediaAdapter.H3ImageSizeFor("21:9").Should().Be(ComfyWorkflows.H3ImageSizes["16:9"]);
        LocalImageMediaAdapter.H3ImageSizeFor(null).Should().Be((2048, 2048));
    }
}
