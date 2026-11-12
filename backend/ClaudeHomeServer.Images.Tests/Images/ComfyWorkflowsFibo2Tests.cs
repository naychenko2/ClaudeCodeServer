using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Графы Fibo 2.0 против шаблонов ComfyUI 0.45.2 image_fibo2_edit_{dev,turbo}_pe.json и карточки
// briaai/Fibo-2.0-dev: файлы моделей, шаги и CFG, выходы узла условий, проводка картинок
public class ComfyWorkflowsFibo2Tests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(string node, int slot = 0) => new(node, slot);

    [Fact]
    public void ГенерацияDev_ФайлыИСэмплер_КакВКарточке()
    {
        var wf = ComfyWorkflows.Fibo2Generate("ёжик в тумане", 1424, 800, 77, 1, "ccs/p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_dev_int8_convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_4b_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("fibo2");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("flux2-vae.safetensors");

        Class(wf, "cond").Should().Be("TextEncodeFibo2");
        var cond = Inputs(wf, "cond");
        cond["prompt"]!.GetValue<string>().Should().Be("ёжик в тумане");
        cond["negative_prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.Fibo2NegativeT2I);
        cond["width"]!.GetValue<int>().Should().Be(1424);
        cond["height"]!.GetValue<int>().Should().Be(800);
        cond["batch_size"]!.GetValue<int>().Should().Be(1);
        cond.Should().NotContainKey("images.image_1");

        // Негатив и латент — второй и третий выходы узла условий
        var ks = Inputs(wf, "ks");
        ks["model"]!.ToJsonString().Should().Be(Link("unet").ToJsonString());
        ks["positive"]!.ToJsonString().Should().Be(Link("cond").ToJsonString());
        ks["negative"]!.ToJsonString().Should().Be(Link("cond", 1).ToJsonString());
        ks["latent_image"]!.ToJsonString().Should().Be(Link("cond", 2).ToJsonString());
        ks["steps"]!.GetValue<int>().Should().Be(50);
        ks["cfg"]!.GetValue<double>().Should().Be(5.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
        ks["seed"]!.GetValue<long>().Should().Be(77);
        Inputs(wf, "save")["images"]!.ToJsonString().Should().Be(Link("dec").ToJsonString());
    }

    [Fact]
    public void ГенерацияTurbo_4ШагаБезCfg_ПустойНегатив_Пачка()
    {
        var wf = ComfyWorkflows.Fibo2Generate("ёжик", 1024, 1024, 1, 3, "p", turbo: true);

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_edit_turbo_int8_convrot.safetensors");
        Inputs(wf, "cond")["negative_prompt"]!.GetValue<string>().Should().BeEmpty();
        Inputs(wf, "cond")["batch_size"]!.GetValue<int>().Should().Be(3);
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(4);
        Inputs(wf, "ks")["cfg"]!.GetValue<double>().Should().Be(1.0);
    }

    [Fact]
    public void Правка_КартинкиПоПорядку_ПустойНегатив_РазмерВыбираетУзел()
    {
        var wf = ComfyWorkflows.Fibo2Edit("сложи", ["a.png", "b.png", "c.png"], null, 5, "p", turbo: true);

        var cond = Inputs(wf, "cond");
        for (var k = 1; k <= 3; k++)
        {
            Class(wf, $"img{k}").Should().Be("LoadImage");
            cond[$"images.image_{k}"]!.ToJsonString().Should().Be(Link($"img{k}").ToJsonString());
        }
        Inputs(wf, "img1")["image"]!.GetValue<string>().Should().Be("a.png");
        cond["negative_prompt"]!.GetValue<string>().Should().BeEmpty("у правки негатив — null-подпись обучения");
        cond["width"]!.GetValue<int>().Should().Be(0);
        cond["height"]!.GetValue<int>().Should().Be(0);
        cond["vae"]!.ToJsonString().Should().Be(Link("vae").ToJsonString());
        cond["prompt"]!.GetValue<string>().Should().Contain("<image_3>").And.EndWith("сложи");
        wf.Should().NotContainKey("sim");
    }

    [Fact]
    public void Правка_ОднаКартинка_ПромптБезМеток_ХолстИзАргумента()
    {
        var wf = ComfyWorkflows.Fibo2Edit("добавь лодку", ["x.png"], (1152, 864), 1, "p", turbo: false);

        Inputs(wf, "cond")["prompt"]!.GetValue<string>().Should().Be("добавь лодку");
        Inputs(wf, "cond")["width"]!.GetValue<int>().Should().Be(1152);
        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("fibo2_dev_int8_convrot.safetensors");
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(50);
    }

    [Fact]
    public void Правка_СЭталономПерсонажа_ДобавляетУзелСходства()
    {
        var wf = ComfyWorkflows.Fibo2Edit("он в кафе", ["x.png"], null, 1, "p", turbo: true, faceReferences: ["f1.png", "f2.png"]);

        Class(wf, "sim").Should().Be("CcsFaceSimilarity");
        Inputs(wf, "sim")["references"]!.GetValue<string>().Should().Be("f1.png\nf2.png");
    }

    [Fact]
    public void Правка_БольшеЧетырёхКартинок_Отказ()
    {
        var act = () => ComfyWorkflows.Fibo2Edit("сложи", Enumerable.Range(1, 5).Select(i => $"r{i}.png").ToList(), null, 1, "p", true);

        act.Should().Throw<ArgumentOutOfRangeException>();
        LocalImageModels.MaxImagesOf(LocalImageModels.Fibo2Turbo).Should().Be(4);
    }

    [Fact]
    public void Размеры_ОколоМегапикселя_КратныШестнадцати()
    {
        foreach (var (_, (w, h)) in ComfyWorkflows.OneMegapixelSizes)
        {
            (w % 16).Should().Be(0);
            (h % 16).Should().Be(0);
            ((double)w * h).Should().BeInRange(0.9 * 1024 * 1024, 1.1 * 1024 * 1024);
        }
    }
}
