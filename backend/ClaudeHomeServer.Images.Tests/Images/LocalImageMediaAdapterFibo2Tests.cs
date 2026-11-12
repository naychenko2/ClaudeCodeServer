using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов редактора с Fibo 2.0: какой граф уходит в ComfyUI, холст и запасной путь на Qwen
public class LocalImageMediaAdapterFibo2Tests
{
    private const string DevUnet = "fibo2_dev_int8_convrot.safetensors";
    private const string TurboUnet = "fibo2_edit_turbo_int8_convrot.safetensors";
    private const string QwenUnet = "qwen_image_2.1_int8_convrot.safetensors";

    private readonly FakeComfy _comfy = new();

    private LocalImageMediaAdapter Build(HttpMessageHandler? handler = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
        }).Build();
        return new LocalImageMediaAdapter(new ComfyClient(new FakeComfyFactory(handler ?? _comfy), config), config,
            NullLogger<LocalImageMediaAdapter>.Instance);
    }

    private System.Text.Json.Nodes.JsonObject Graph() =>
        _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();

    private static string Unet(System.Text.Json.Nodes.JsonObject graph) =>
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>();

    private static int Cond(System.Text.Json.Nodes.JsonObject graph, string input) =>
        graph["cond"]!["inputs"]![input]!.GetValue<int>();

    [Fact]
    public async Task ГенерацияDev_РазмерИзТаблицыFibo_ПачкаВариантов()
    {
        var submitted = await Build().SubmitAsync(
            new LocalImageRequest(LocalImageOp.Generate, "ёжик", [], "16:9", 2, Model: LocalImageModels.Fibo2), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be(DevUnet);
        Cond(graph, "width").Should().Be(1424);
        Cond(graph, "height").Should().Be(800);
        Cond(graph, "batch_size").Should().Be(2);
        submitted.EtaSeconds.Should().Be(LocalMediaService.Fibo2GenerateEta(false, 2));
    }

    [Fact]
    public async Task ПравкаTurbo_РазмерВыбираетУзел_ОбразецВУзлеУсловий()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "добавь лодку",
            [LocalMediaTestImages.Png(1000, 750), LocalMediaTestImages.Png(8, 8)], null, 1, Model: LocalImageModels.Fibo2Turbo), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be(TurboUnet);
        Cond(graph, "width").Should().Be(0);
        Cond(graph, "height").Should().Be(0);
        graph["cond"]!["inputs"]!.AsObject().ContainsKey("images.image_2").Should().BeTrue();
        graph["cond"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Contain("<image_2>").And.EndWith("добавь лодку");
        submitted.EtaSeconds.Should().Be(LocalMediaService.Fibo2EditEta(true, 2, 1));
    }

    [Fact]
    public async Task ГенерацияПоОбразцам_ХолстИзТаблицы()
    {
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "как на образце",
            [LocalMediaTestImages.Png(8, 8)], "3:4", 1, Model: LocalImageModels.Fibo2), default);

        var graph = Graph();
        Cond(graph, "width").Should().Be(864);
        Cond(graph, "height").Should().Be(1152);
    }

    [Fact]
    public async Task ПерсонажСLoRA_УходитНаQwenКачественно()
    {
        var adapter = Build(new LorasComfy(_comfy, ["hero.safetensors"]));
        var submitted = await adapter.SubmitAsync(new LocalImageRequest(LocalImageOp.Generate, "сидит в кафе",
            [LocalMediaTestImages.Png(8, 8)], "1:1", 1, Lora: new LocalImageLora("hero.safetensors", 0.8),
            Model: LocalImageModels.Fibo2Turbo), default);

        submitted.Error.Should().BeNull();
        var graph = Graph();
        Unet(graph).Should().Be(QwenUnet);
        graph.ContainsKey("lora").Should().BeTrue("на Qwen LoRA персонажа применяется");
    }

    [Fact]
    public async Task ПодстановкаНаЛицо_УходитНаQwenКачественно()
    {
        var submitted = await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "лицо как на фото",
            [LocalMediaTestImages.Png(64, 64), LocalMediaTestImages.Png(8, 8)], null, 1,
            KeepMask: LocalMediaTestImages.Png(64, 64), Crop: new LocalImageCrop(0, 0, 32, 32, 64, 64),
            Model: LocalImageModels.Fibo2), default);

        submitted.Error.Should().BeNull();
        Unet(Graph()).Should().Be(QwenUnet);
    }

    [Fact]
    public async Task ПятьКартинок_УходятНаQwenКачественно()
    {
        var images = Enumerable.Range(0, 5).Select(i => LocalMediaTestImages.Png(8 + i, 8)).ToList();
        await Build().SubmitAsync(new LocalImageRequest(LocalImageOp.Edit, "сложи", images, null, 1,
            Model: LocalImageModels.Fibo2Turbo), default);

        Unet(Graph()).Should().Be(QwenUnet);
    }

    [Fact]
    public void РазмерПоПропорцииВнеТаблицы_Квадрат()
    {
        LocalImageMediaAdapter.OneMegapixelSizeFor("16:9").Should().Be((1424, 800));
        LocalImageMediaAdapter.OneMegapixelSizeFor("21:9").Should().Be((1024, 1024));
        LocalImageMediaAdapter.OneMegapixelSizeFor(null).Should().Be((1024, 1024));
    }

    // Фейк ComfyUI не знает /models/loras — отдаём список поверх него
    private sealed class LorasComfy(FakeComfy inner, string[] loras) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.PathAndQuery != "/models/loras") return base.SendAsync(request, ct);
            var body = new System.Text.Json.Nodes.JsonArray([.. loras.Select(n => (System.Text.Json.Nodes.JsonNode)n)]);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
