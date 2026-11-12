using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Графы MiniMax-H3-Image против шаблонов ComfyUI 0.45.2 image_minimax_h3_image_{t2i,edit}.json:
// файлы моделей, 6 шагов без CFG, проводка картинок в узел условий и холст правки
public class ComfyWorkflowsH3ImageTests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(string node, int slot = 0) => new(node, slot);

    [Fact]
    public void Генерация_ФайлыИСэмплер_КакВШаблоне()
    {
        var wf = ComfyWorkflows.H3ImageGenerate("ёжик в тумане", 2752, 1536, 77, 1, "ccs/p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("minimax_h3_image_pruned_int8_convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("minimax");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("minimax_h3_image_vae_fp16.safetensors");

        Class(wf, "cond").Should().Be("MiniMaxH3ImageConditioning");
        var cond = Inputs(wf, "cond");
        cond["prompt"]!.GetValue<string>().Should().Be("ёжик в тумане");
        cond["width"]!.GetValue<int>().Should().Be(2752);
        cond["height"]!.GetValue<int>().Should().Be(1536);
        cond.Should().NotContainKey("images.image_1");

        // Без CFG: негатив — обнулённый положительный, латент холста — второй выход узла условий
        Class(wf, "neg").Should().Be("ConditioningZeroOut");
        var ks = Inputs(wf, "ks");
        ks["model"]!.ToJsonString().Should().Be(Link("unet").ToJsonString());
        ks["positive"]!.ToJsonString().Should().Be(Link("cond").ToJsonString());
        ks["negative"]!.ToJsonString().Should().Be(Link("neg").ToJsonString());
        ks["latent_image"]!.ToJsonString().Should().Be(Link("cond", 1).ToJsonString());
        ks["steps"]!.GetValue<int>().Should().Be(6);
        ks["cfg"]!.GetValue<double>().Should().Be(1.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
        ks["seed"]!.GetValue<long>().Should().Be(77);
        Inputs(wf, "save")["images"]!.ToJsonString().Should().Be(Link("dec").ToJsonString());
        wf.Should().NotContainKey("lat");
    }

    [Fact]
    public void Генерация_НесколькоВариантов_ПачкойИзЛатентаХолста()
    {
        var wf = ComfyWorkflows.H3ImageGenerate("ёжик", 2048, 2048, 1, 3, "p");

        Class(wf, "lat").Should().Be("RepeatLatentBatch");
        Inputs(wf, "lat")["samples"]!.ToJsonString().Should().Be(Link("cond", 1).ToJsonString());
        Inputs(wf, "lat")["amount"]!.GetValue<int>().Should().Be(3);
        Inputs(wf, "ks")["latent_image"]!.ToJsonString().Should().Be(Link("lat").ToJsonString());
    }

    [Fact]
    public void Правка_КартинкиПоПорядку_ВоВходыУзлаУсловий()
    {
        var wf = ComfyWorkflows.H3ImageEdit("сложи", ["a.png", "b.png", "c.png"], (2368, 1760), 5, "p");

        var cond = Inputs(wf, "cond");
        for (var k = 1; k <= 3; k++)
        {
            Class(wf, $"img{k}").Should().Be("LoadImage");
            cond[$"images.image_{k}"]!.ToJsonString().Should().Be(Link($"img{k}").ToJsonString());
        }
        Inputs(wf, "img1")["image"]!.GetValue<string>().Should().Be("a.png");
        Inputs(wf, "img3")["image"]!.GetValue<string>().Should().Be("c.png");
        cond["width"]!.GetValue<int>().Should().Be(2368);
        cond["height"]!.GetValue<int>().Should().Be(1760);
        cond["vae"]!.ToJsonString().Should().Be(Link("vae").ToJsonString());
        wf.Should().NotContainKey("sim");
    }

    [Fact]
    public void Правка_ПромптСвязываетНомераСМетками()
    {
        var wf = ComfyWorkflows.H3ImageEdit("1) исходная картинка; 2) образец", ["a.png", "b.png"], (2048, 2048), 1, "p");

        var prompt = Inputs(wf, "cond")["prompt"]!.GetValue<string>();
        prompt.Should().StartWith("Входные картинки по порядку: картинка 1 = <Picture 1>, картинка 2 = <Picture 2>.");
        prompt.Should().EndWith("1) исходная картинка; 2) образец");
    }

    [Fact]
    public void Генерация_БезКартинок_ПромптКакЕсть()
    {
        ComfyWorkflows.H3ImagePrompt("кот", 0).Should().Be("кот");
    }

    [Fact]
    public void Персонаж_ЭталонИдётТолькоВУзелСходства()
    {
        var wf = ComfyWorkflows.H3ImageEdit("сюда его", ["x.png", "f1.png"], (2048, 2048), 1, "p", ["f1.png", "f2.png"]);

        Class(wf, "sim").Should().Be("CcsFaceSimilarity");
        Inputs(wf, "sim")["image"]!.ToJsonString().Should().Be(Link("dec").ToJsonString());
        Inputs(wf, "sim")["references"]!.GetValue<string>().Should().Be("f1.png\nf2.png");
        Inputs(wf, "cond").Should().NotContainKey("images.image_3");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Правка_ЧислоКартинокВнеДиапазона_Отказ(int images)
    {
        var act = () => ComfyWorkflows.H3ImageEdit("x", Enumerable.Range(1, images).Select(i => $"{i}.png").ToList(), (2048, 2048), 1, "p");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1328, 1328, 2048, 2048)]
    [InlineData(1024, 1024, 2048, 2048)]
    [InlineData(4096, 4096, 2048, 2048)]
    [InlineData(1664, 928, 2752, 1536)]
    [InlineData(1000, 1500, 1664, 2496)]
    public void Холст_ПриведёнКРоднойПлощади_КратноЗ2(int width, int height, int expectedWidth, int expectedHeight)
    {
        var (w, h) = ComfyWorkflows.H3ImageCanvas(width, height);

        (w, h).Should().Be((expectedWidth, expectedHeight));
        (w % 32).Should().Be(0);
        (h % 32).Should().Be(0);
    }

    [Fact]
    public void ТаблицаРазмеров_ТеЖеПропорции_ОколоЧетырёхМегапикселей()
    {
        ComfyWorkflows.H3ImageSizes.Keys.Should().BeEquivalentTo(ComfyWorkflows.ImageSizes.Keys);
        foreach (var (aspect, (w, h)) in ComfyWorkflows.H3ImageSizes)
        {
            (w % 32).Should().Be(0, aspect);
            (h % 32).Should().Be(0, aspect);
            ((double)w * h / 1_000_000).Should().BeInRange(3.9, 4.5, aspect);
            var qwen = ComfyWorkflows.ImageSizes[aspect];
            ((double)w / h).Should().BeApproximately((double)qwen.Width / qwen.Height, 0.03, aspect);
        }
    }

    [Fact]
    public void Модель_ИзвестнаИБерётПерсонажаПоФото()
    {
        LocalImageModels.IsKnown(LocalImageModels.H3Image).Should().BeTrue();
        LocalImageModels.TakesCharacterByPhotos(LocalImageModels.H3Image).Should().BeTrue();
        LocalImageModels.MaxImagesOf(LocalImageModels.H3Image).Should().Be(16);
        ComfyWorkflows.IsH3Image("minimax-h3-image").Should().BeTrue();
    }

    // Маски, дорисовка, кроп лица и больше 16 картинок — на Qwen «качественно», как у Klein
    [Fact]
    public void НепроверенныеСлучаи_УходятНаQwen()
    {
        var plain = new LocalImageRequest(LocalImageOp.Edit, "x", [[1]], null, 1, Model: LocalImageModels.H3Image);
        LocalImageModels.Effective(plain).Should().Be(LocalImageModels.H3Image);
        LocalImageModels.Effective(plain with { KeepMask = [1] }).Should().Be(LocalImageModels.Quality);
        LocalImageModels.Effective(plain with { Pad = new LocalImagePad(10, 0, 10, 0) }).Should().Be(LocalImageModels.Quality);
        LocalImageModels.Effective(plain with { Images = Enumerable.Repeat(new byte[] { 1 }, 16).ToList() })
            .Should().Be(LocalImageModels.H3Image);
        LocalImageModels.Effective(plain with { Images = Enumerable.Repeat(new byte[] { 1 }, 17).ToList() })
            .Should().Be(LocalImageModels.Quality);
    }
}
