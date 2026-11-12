using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Стили Krea 2 Turbo против шаблонов ComfyUI image_krea2_turbo_t2i_int8.json (стилевые LoRA) и
// image_krea2_turbo_int8_image_style_reference.json («стиль по образцу» ostris)
public class ComfyWorkflowsKrea2StylesTests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static string Link(JsonObject wf, string node, string input) => Inputs(wf, node)[input]!.ToJsonString();

    private static string To(string node, int slot = 0) => new JsonArray(node, slot).ToJsonString();

    [Fact]
    public void Девять_Стилей_ТриггерыИФайлыКакВЗаметкеШаблона()
    {
        Krea2Styles.All.Select(s => (s.Id, s.LoraFile, s.Trigger)).Should().Equal(
            ("darkbrush", "krea2_darkbrush.safetensors", "monochrome ink wash style"),
            ("dotmatrix", "krea2_dotmatrix.safetensors", "monochrome stippling style"),
            ("kidsdrawing", "krea2_kidsdrawing.safetensors", "naive expressive sketch style"),
            ("neondrip", "krea2_neondrip.safetensors", "textured abstract style"),
            ("rainywindow", "krea2_rainywindow.safetensors", "rainy window style"),
            ("retroanime", "krea2_retroanime.safetensors", "purple retro anime style"),
            ("softwatercolor", "krea2_softwatercolor.safetensors", "art deco watercolor style"),
            ("sunsetblur", "krea2_sunsetblur.safetensors", "ethereal motion blur style"),
            ("vintagetarot", "krea2_vintagetarot.safetensors", "vintage tarot style"));
    }

    [Theory]
    [InlineData("darkbrush")]
    [InlineData("vintagetarot")]
    public void Стиль_ЛораМеждуМодельюИСэмплером_ТриггерВПромпте(string id)
    {
        var style = Krea2Styles.Find(id)!;
        var wf = ComfyWorkflows.Krea2StyleGenerate("ёжик в тумане", style, 1424, 800, 7, 2, "p");

        Class(wf, "lora").Should().Be("LoraLoaderModelOnly");
        Link(wf, "lora", "model").Should().Be(To("unet"));
        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be(style.LoraFile);
        Inputs(wf, "lora")["strength_model"]!.GetValue<double>().Should().Be(0.8, "сила узла шаблона");
        Link(wf, "ks", "model").Should().Be(To("lora"));
        Inputs(wf, "pos")["text"]!.GetValue<string>().Should().Be($"ёжик в тумане, {style.Trigger}");
        Inputs(wf, "ks")["steps"]!.GetValue<int>().Should().Be(8);
        Inputs(wf, "ks")["cfg"]!.GetValue<double>().Should().Be(1.0);
        Class(wf, "neg").Should().Be("ConditioningZeroOut");
    }

    [Fact]
    public void БезСтиля_ГрафСовпадаетСОбычнымKrea()
    {
        var plain = ComfyWorkflows.Krea2Generate("ёжик", 1024, 1024, 3, 1, "p");
        var styled = ComfyWorkflows.Krea2StyleGenerate("ёжик", null, 1024, 1024, 3, 1, "p");

        styled.ToJsonString().Should().Be(plain.ToJsonString());
        styled.Should().NotContainKey("lora");
    }

    [Theory]
    [InlineData("", "monochrome ink wash style")]
    [InlineData("  ", "monochrome ink wash style")]
    [InlineData("кот ", "кот, monochrome ink wash style")]
    public void ТриггерПустомуПромптуИПробелам(string prompt, string expected) =>
        ComfyWorkflows.Krea2StylePrompt(prompt, Krea2Styles.Find("darkbrush")).Should().Be(expected);

    [Fact]
    public void НеизвестныйСтиль_Find_Null()
    {
        Krea2Styles.Find("нет-такого").Should().BeNull();
        Krea2Styles.Find(null).Should().BeNull();
        Krea2Styles.Find("").Should().BeNull();
    }

    [Fact]
    public void СтильПоОбразцу_УзлыИЗначенияШаблона()
    {
        var wf = ComfyWorkflows.Krea2StyleReference("йети читает книгу", "ref.png", 1024, 1024, 11, 1, "p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("krea2_turbo_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("krea2");
        Inputs(wf, "img")["image"]!.GetValue<string>().Should().Be("ref.png");

        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be("krea2_style_reference.safetensors");
        Inputs(wf, "lora")["strength_model"]!.GetValue<double>().Should().Be(1.0);
        Link(wf, "lora", "model").Should().Be(To("unet"));

        Class(wf, "sampling").Should().Be("ModelSamplingFlux");
        Inputs(wf, "sampling")["max_shift"]!.GetValue<double>().Should().Be(1.15);
        Inputs(wf, "sampling")["base_shift"]!.GetValue<double>().Should().Be(0.5);
        Inputs(wf, "sampling")["width"]!.GetValue<int>().Should().Be(1024);
        Link(wf, "sampling", "model").Should().Be(To("lora"));

        Class(wf, "enc").Should().Be("TextEncodeQwenImageEditPlus");
        Inputs(wf, "enc")["prompt"]!.GetValue<string>().Should().Be("йети читает книгу");
        Link(wf, "enc", "image1").Should().Be(To("img"));
        Link(wf, "enc", "vae").Should().Be(To("vae"));
        Inputs(wf, "pos")["reference_latents_method"]!.GetValue<string>().Should().Be("index_timestep_zero");
        Link(wf, "pos", "conditioning").Should().Be(To("enc"));
        Class(wf, "neg").Should().Be("ConditioningZeroOut");
        Link(wf, "neg", "conditioning").Should().Be(To("pos"));

        Class(wf, "guider").Should().Be("CFGGuider");
        Inputs(wf, "guider")["cfg"]!.GetValue<double>().Should().Be(1.0);
        Link(wf, "guider", "model").Should().Be(To("sampling"));
        Inputs(wf, "sigmas")["steps"]!.GetValue<int>().Should().Be(8);
        Link(wf, "sigmas", "model").Should().Be(To("sampling"));
        Inputs(wf, "sampler")["sampler_name"]!.GetValue<string>().Should().Be("euler");
        Inputs(wf, "noise")["noise_seed"]!.GetValue<long>().Should().Be(11);
        Class(wf, "ks").Should().Be("SamplerCustomAdvanced");
        Link(wf, "dec", "samples").Should().Be(To("ks"));
    }

    [Fact]
    public void СтильПоОбразцу_БатчВариантов()
    {
        var wf = ComfyWorkflows.Krea2StyleReference("a", "r.png", 1424, 800, 1, 3, "p");

        Inputs(wf, "lat")["batch_size"]!.GetValue<int>().Should().Be(3);
        Inputs(wf, "lat")["height"]!.GetValue<int>().Should().Be(800);
    }
}
