using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Графы FLUX 3 Klein против официальных графов BFL (flux3_klein_int8_{t2i,i2i,mi2i}.json) и драйвера
// A/B klein_ab.py: файлы моделей, апсемплер промпта, сэмплер и проводка правки
public class ComfyWorkflowsKleinTests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    private static JsonArray Link(string node, int slot = 0) => new(node, slot);

    [Fact]
    public void Генерация_ФайлыСэмплерИАпсемплер_КакУBFL()
    {
        var wf = ComfyWorkflows.KleinGenerate("ёжик в тумане", 1664, 928, 77, 3, "ccs/p");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("flux3-image-klein-int8-convrot.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_4b_int8_convrot.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("flux3");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("flux2-vae.safetensors");
        Inputs(wf, "ups_clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3.5_9b_flux3_prompt_upsampler_int8_convrot.safetensors");
        Inputs(wf, "ups_clip")["type"]!.GetValue<string>().Should().Be("flux3");

        var ms = Inputs(wf, "ms");
        ms["max_shift"]!.GetValue<double>().Should().BeApproximately(2.772589, 1e-6);
        ms["base_shift"]!.GetValue<double>().Should().BeApproximately(2.772589, 1e-6);
        ms["width"]!.GetValue<int>().Should().Be(1024);
        ms["height"]!.GetValue<int>().Should().Be(1024);

        var ks = Inputs(wf, "ks");
        ks["model"]!.ToJsonString().Should().Be(Link("ms").ToJsonString());
        ks["steps"]!.GetValue<int>().Should().Be(16);
        ks["cfg"]!.GetValue<double>().Should().Be(1.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
        ks["seed"]!.GetValue<long>().Should().Be(77);

        Class(wf, "lat").Should().Be("EmptyFlux2LatentImage");
        Inputs(wf, "lat")["width"]!.GetValue<int>().Should().Be(1664);
        Inputs(wf, "lat")["height"]!.GetValue<int>().Should().Be(928);
        Inputs(wf, "lat")["batch_size"]!.GetValue<int>().Should().Be(3);
        Inputs(wf, "neg")["text"]!.GetValue<string>().Should().BeEmpty();

        // Подпись апсемплера — в положительный промпт и в историю ComfyUI
        Inputs(wf, "pos")["text"]!.ToJsonString().Should().Be(Link("ups").ToJsonString());
        Inputs(wf, "ups_view")["source"]!.ToJsonString().Should().Be(Link("ups").ToJsonString());
        Inputs(wf, "save")["images"]!.ToJsonString().Should().Be(Link("dec").ToJsonString());
    }

    [Fact]
    public void Генерация_Апсемплер_ПараметрыT2I()
    {
        var ups = Inputs(ComfyWorkflows.KleinGenerate("борщ", 1328, 1328, 5, 1, "p"), "ups");

        ups["prompt"]!.GetValue<string>().Should().Be("борщ");
        ups["max_length"]!.GetValue<int>().Should().Be(3584);
        ups["sampling_mode"]!.GetValue<string>().Should().Be("on");
        ups["sampling_mode.temperature"]!.GetValue<double>().Should().Be(1.0);
        ups["sampling_mode.top_k"]!.GetValue<int>().Should().Be(1);
        ups["sampling_mode.top_p"]!.GetValue<double>().Should().Be(0.95);
        ups["sampling_mode.min_p"]!.GetValue<double>().Should().Be(0);
        ups["sampling_mode.repetition_penalty"]!.GetValue<double>().Should().Be(1.0);
        ups["sampling_mode.presence_penalty"]!.GetValue<double>().Should().Be(1.5);
        ups["sampling_mode.seed"]!.GetValue<long>().Should().Be(5);
        ups["thinking"]!.GetValue<bool>().Should().BeFalse();
        ups["use_default_template"]!.GetValue<bool>().Should().BeTrue();
        ups["mtp"]!.GetValue<string>().Should().Be("off");
        ups["system_prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.KleinSystemT2I);
        ups.ContainsKey("image").Should().BeFalse();
    }

    [Fact]
    public void СистемныеПромпты_ДословноИзГрафовBFL()
    {
        ComfyWorkflows.KleinSystemT2I.Should().StartWith("You are the prompt upsampler of a text-to-image model.")
            .And.EndWith("Output only the caption.").And.Contain("\n\nFidelity and planning:");
        ComfyWorkflows.KleinSystemI2I.Should().StartWith("You are the prompt upsampler of an image-editing model.")
            .And.EndWith("Output only the instruction.").And.Contain(@"with line breaks as \n,");
        ComfyWorkflows.KleinSystemT2I.Length.Should().Be(3404);
        ComfyWorkflows.KleinSystemI2I.Length.Should().Be(2677);
    }

    [Fact]
    public void Правка_ОбразыИдутИАпсемплеру_ИВReferenceMultiLatent()
    {
        var wf = ComfyWorkflows.KleinEdit("добавь лодку", ["a.png", "b.png", "c.png"], (1328, 1328), 9, "p");

        Inputs(wf, "img1")["image"]!.GetValue<string>().Should().Be("a.png");
        Inputs(wf, "img3")["image"]!.GetValue<string>().Should().Be("c.png");

        var batch = Inputs(wf, "ups_imgs");
        batch.Select(p => p.Key).Should().Equal("images.image0", "images.image1", "images.image2");
        batch["images.image0"]!.ToJsonString().Should().Be(Link("img1").ToJsonString());

        var fmt = Inputs(wf, "ups_fmt");
        fmt["values.a"]!.GetValue<string>().Should().Be("добавь лодку");
        fmt["f_string"]!.GetValue<string>().Should().Be(
            "Reference images follow in order (the first image, the second image, ...).\nUser request: {a}\nOutput the caption.");

        var ups = Inputs(wf, "ups");
        ups["prompt"]!.ToJsonString().Should().Be(Link("ups_fmt").ToJsonString());
        ups["image"]!.ToJsonString().Should().Be(Link("ups_imgs").ToJsonString());
        ups["sampling_mode.presence_penalty"]!.GetValue<double>().Should().Be(0);
        ups["system_prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.KleinSystemI2I);

        var refs = Inputs(wf, "ref");
        Class(wf, "ref").Should().Be("ReferenceMultiLatent");
        refs["resolution"]!.GetValue<string>().Should().Be("resize");
        refs["resolution.megapixels"]!.GetValue<double>().Should().Be(1.0);
        refs["images.image2"]!.ToJsonString().Should().Be(Link("img3").ToJsonString());
        refs["positive"]!.ToJsonString().Should().Be(Link("pos").ToJsonString());
        refs["negative"]!.ToJsonString().Should().Be(Link("neg").ToJsonString());

        Inputs(wf, "ks")["positive"]!.ToJsonString().Should().Be(Link("ref").ToJsonString());
        Inputs(wf, "ks")["negative"]!.ToJsonString().Should().Be(Link("ref", 1).ToJsonString());
        Inputs(wf, "lat")["batch_size"]!.GetValue<int>().Should().Be(1);
        Inputs(wf, "lat")["width"]!.GetValue<int>().Should().Be(1328);
    }

    [Fact]
    public void Правка_БольшеДесятиКартинок_Отказ()
    {
        var act = () => ComfyWorkflows.KleinEdit("x", Enumerable.Range(0, 11).Select(i => $"{i}.png").ToList(), (1024, 1024), 1, "p");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Промпт_ОстаётсяСтрокой_ДажеСФигурнымиСкобками()
    {
        const string hostile = "\"}, \"evil\": {\"class_type\": \"LoadImageFromPath\"} {a} {0}";
        var wf = JsonNode.Parse(ComfyWorkflows.KleinEdit(hostile, ["x.png"], (1024, 1024), 1, "p").ToJsonString())!.AsObject();

        wf.ContainsKey("evil").Should().BeFalse();
        Inputs(wf, "ups_fmt")["values.a"]!.GetValue<string>().Should().Be(hostile, "промпт — значение, а не шаблон f_string");
    }

    [Theory]
    [InlineData(1328, 1328, 1328, 1328)]
    [InlineData(1000, 750, 992, 736)]
    [InlineData(4000, 3000, 2304, 1728)]
    [InlineData(10, 10, 16, 16)]
    public void ХолстПравки_ПоИсходнику_Кратно16_НеБольше4Мп(int w, int h, int ew, int eh)
    {
        var (cw, ch) = ComfyWorkflows.KleinCanvas(w, h);

        (cw, ch).Should().Be((ew, eh));
        (cw % 16).Should().Be(0);
        ((double)cw * ch).Should().BeLessThanOrEqualTo(4_000_000);
    }

    // ─── Запасной путь на Qwen «качественно» ──────────────────────────────────

    private static readonly byte[] Png = [1];

    private static LocalImageRequest Klein(int images = 1) =>
        new(LocalImageOp.Edit, "x", Enumerable.Range(0, images).Select(_ => Png).ToList(), null, 1, Model: LocalImageModels.Klein);

    public static IEnumerable<object[]> QwenCases() => new (string, LocalImageRequest)[]
    {
        ("lora", Klein() with { Lora = new LocalImageLora("timur.safetensors", 1) }),
        ("face-refs", Klein() with { FaceReferences = [Png] }),
        ("crop", Klein() with { Crop = new LocalImageCrop(0, 0, 8, 8, 16, 16) }),
        ("erase", Klein() with { EraseMask = Png }),
        ("keep", Klein() with { KeepMask = Png }),
        ("veil", Klein() with { VeilMask = Png }),
        ("pad", Klein() with { Pad = new LocalImagePad(16, 0, 0, 0) }),
        ("11-images", Klein(11)),
    }.Select(c => new object[] { c.Item1, c.Item2 });

    [Theory]
    [MemberData(nameof(QwenCases))]
    public void Klein_НепроверенныйСлучай_ИдётНаQwenКачественно(string name, LocalImageRequest request)
    {
        LocalImageModels.Effective(request).Should().Be(LocalImageModels.Quality, name);
    }

    [Fact]
    public void Klein_ОбычнаяПравка_ОстаётсяKlein_ДругиеМоделиНеТрогаются()
    {
        LocalImageModels.Effective(Klein(10)).Should().Be(LocalImageModels.Klein);
        LocalImageModels.Effective(Klein() with { Model = LocalImageModels.Turbo, EraseMask = Png }).Should().Be(LocalImageModels.Turbo);
        LocalImageModels.Effective(Klein() with { Model = null, Lora = new LocalImageLora("a", 1) }).Should().BeNull();
        LocalImageModels.Effective(Klein() with { EraseMask = [] }).Should().Be(LocalImageModels.Klein, "пустой массив — маски нет");
        LocalImageModels.IsKnown(LocalImageModels.Klein).Should().BeTrue();
    }
}
