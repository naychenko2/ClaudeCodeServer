using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов редактора с FLUX 3 Klein: какой граф уходит в ComfyUI, холст правки и запасной путь на Qwen
public class LocalImageMediaAdapterKleinTests
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

    private System.Text.Json.Nodes.JsonObject Graph() =>
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();

    private static string Unet(System.Text.Json.Nodes.JsonObject graph) =>
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>();

    [Fact]
    public async Task Генерация_Klein_ГрафKlein_РазмерИзТаблицы()
    {
        var submitted = await Build().SubmitAsync(
            new LocalImageRequest(LocalImageOp.Generate, "ёжик", [], "16:9", 2, Model: LocalImageModels.Klein), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be("flux3-image-klein-int8-convrot.safetensors");
        graph["lat"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(1664);
        graph["lat"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(928);
        graph["lat"]!["inputs"]!["batch_size"]!.GetValue<int>().Should().Be(2);
        graph["ups"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Be("ёжик");
        submitted.EtaSeconds.Should().Be(LocalMediaService.KleinGenerateEta(2));
    }

    [Fact]
    public async Task Правка_Klein_ХолстПоИсходнику()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "добавь лодку",
            [LocalMediaTestImages.Png(1000, 750), LocalMediaTestImages.Png(8, 8)], null, 1, Model: LocalImageModels.Klein), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be("flux3-image-klein-int8-convrot.safetensors");
        graph["lat"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(992);
        graph["lat"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(736);
        graph["ref"]!["inputs"]!.AsObject().ContainsKey("images.image1").Should().BeTrue("образец тоже идёт референсом");
        submitted.EtaSeconds.Should().Be(LocalMediaService.KleinEditEta(2));
    }

    [Fact]
    public async Task Генерация_ПоОбразцам_Klein_ХолстИзФормата()
    {
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "кот как на фото",
            [LocalMediaTestImages.Png(8, 8)], "3:4", 1, Model: LocalImageModels.Klein), default);

        var graph = Graph();
        graph.ContainsKey("ref").Should().BeTrue();
        graph["lat"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(1104);
        graph["lat"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(1472);
    }

    [Fact]
    public async Task Klein_СФотоЭталона_УходитНаQwenКачественно()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "сидит в кафе",
            [LocalMediaTestImages.Png(8, 8)], null, 1, FaceReferences: [LocalMediaTestImages.Png(8, 8)],
            Model: LocalImageModels.Klein), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be("qwen_image_2.1_int8_convrot.safetensors");
        graph["ks"]!["inputs"]!["steps"]!.GetValue<int>().Should().Be(25);
        graph.ContainsKey("ups").Should().BeFalse();
        submitted.EtaSeconds.Should().Be(LocalMediaService.EditImageEta(1));
    }

    [Fact]
    public async Task Klein_БольшеДесятиКартинок_УходитНаQwen()
    {
        var images = Enumerable.Range(0, 11).Select(_ => LocalMediaTestImages.Png(8, 8)).ToList();
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "сложи", images, null, 1, Model: LocalImageModels.Klein), default);

        Unet(Graph()).Should().Be("qwen_image_2.1_int8_convrot.safetensors");
    }

    [Fact]
    public void Eta_Klein_БыстрееQwen()
    {
        var adapter = Build();
        adapter.EtaSeconds(LocalImageOp.Generate, 1, 0, LocalImageModels.Klein).Should().Be(15);
        adapter.EtaSeconds(LocalImageOp.Edit, 1, 1, LocalImageModels.Klein).Should().Be(20);
    }
}
