using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Графы Krea 2 Turbo и MOSS-Image против шаблонов ComfyUI image_krea2_turbo_t2i_int8.json и
// image_moss_image_t2i.json: файлы моделей, сэмплер, негатив, расширитель промпта MOSS
public class ComfyWorkflowsKrea2MossTests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(string node, int slot = 0) => new(node, slot);

    [Fact]
    public void Krea2_ФайлыИСэмплер_КакВШаблоне()
    {
        var wf = ComfyWorkflows.Krea2Generate("ёжик в тумане", 1424, 800, 77, 2, "ccs/p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("krea2_turbo_int8_convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_4b_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("krea2");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("qwen_image_vae.safetensors");
        Inputs(wf, "pos")["text"]!.GetValue<string>().Should().Be("ёжик в тумане");
        Class(wf, "neg").Should().Be("ConditioningZeroOut");
        Class(wf, "lat").Should().Be("EmptyLatentImage");
        Inputs(wf, "lat")["width"]!.GetValue<int>().Should().Be(1424);
        Inputs(wf, "lat")["batch_size"]!.GetValue<int>().Should().Be(2);
        var ks = Inputs(wf, "ks");
        ks["model"]!.ToJsonString().Should().Be(Link("unet").ToJsonString());
        ks["steps"]!.GetValue<int>().Should().Be(8);
        ks["cfg"]!.GetValue<double>().Should().Be(1.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["seed"]!.GetValue<long>().Should().Be(77);
        wf.Should().NotContainKey("ups", "расширитель промпта Krea выключен");
    }

    [Fact]
    public void Moss_РасширительПромпта_ИСэмплерШаблона()
    {
        var wf = ComfyWorkflows.MossGenerate("афиша «ДЖАЗ»", 832, 1248, 5, 1, "p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("moss_image_sft_int8_convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3.5_9b_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("moss_image");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("flux2-vae_small.safetensors");

        Class(wf, "ups").Should().Be("TextGenerate");
        var ups = Inputs(wf, "ups");
        ups["prompt"]!.GetValue<string>().Should().Be("афиша «ДЖАЗ»");
        ups["clip"]!.ToJsonString().Should().Be(Link("clip").ToJsonString());
        ups["system_prompt"]!.GetValue<string>().Should().StartWith("You are a prompt engineer for an image generation model.");
        ups["sampling_mode.seed"]!.GetValue<long>().Should().Be(5);
        Inputs(wf, "pos")["text"]!.ToJsonString().Should().Be(Link("ups").ToJsonString());
        Inputs(wf, "neg")["text"]!.GetValue<string>().Should().Be(ComfyWorkflows.MossNegative);
        Class(wf, "lat").Should().Be("EmptyFlux2LatentImage");
        Inputs(wf, "lat")["height"]!.GetValue<int>().Should().Be(1248);

        var ks = Inputs(wf, "ks");
        ks["steps"]!.GetValue<int>().Should().Be(25);
        ks["cfg"]!.GetValue<double>().Should().Be(2.5);
        ks["sampler_name"]!.GetValue<string>().Should().Be("er_sde");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
    }
}
