using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Шаблоны графов ComfyUI сверяются с рабочими скриптами стенда по ключевым нодам:
// разошедшийся шаблон — это либо упавшая задача, либо тихо другая модель/настройка
public class ComfyWorkflowsTests
{
    // Все классы нод, которые вообще могут оказаться в графе. Новый класс — осознанное
    // решение: через custom nodes граф умеет читать и писать файлы хоста
    private static readonly HashSet<string> AllowedClasses =
    [
        "UNETLoaderMultiGPU", "CLIPLoaderMultiGPU", "VAELoader", "TextEncodeQwenImage21", "EmptyLatentImage",
        "KSampler", "VAEDecode", "SaveImage", "LoadImage", "UltralyticsDetectorProvider", "FaceDetailer",
        "UNETLoader", "LoraLoaderModelOnly", "SelectModelDevice", "PathchSageAttentionKJ", "H3MultiStream",
        "CLIPLoader", "SelectCLIPDevice", "H3MSTextEncoderCache", "SelectVAEDevice", "H3MSVAESplitDecode",
        "MiniMaxH3ImageToVideo", "BasicGuider", "BasicScheduler", "KSamplerSelect", "RandomNoise",
        "SamplerCustomAdvanced", "VAEDecodeAudio", "CreateVideo", "SaveVideo",
    ];

    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Class(JsonObject wf, string node) => wf[node]!["class_type"]!.GetValue<string>();

    [Fact]
    public void Генерация_СовпадаетСЭталономСтенда()
    {
        // Эталон — ~/ComfyUI/qwen-image21-1gpu-api.json, правки — как в run_gen.py
        var wf = ComfyWorkflows.GenerateImage("котик", "", 1328, 1328, 42, 25, 2, "ccs-local-media/job");

        wf.Select(p => p.Key).Should().BeEquivalentTo(["unet", "clip", "vae", "enc", "lat", "ks", "dec", "save"]);
        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be("qwen_image_2.1_bf16.safetensors");
        Inputs(wf, "clip")["clip_name"]!.GetValue<string>().Should().Be("qwen3vl_8b_bf16.safetensors");
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("qwen_image");
        Inputs(wf, "vae")["vae_name"]!.GetValue<string>().Should().Be("qwen_image_2.1_vae_bf16.safetensors");
        Class(wf, "enc").Should().Be("TextEncodeQwenImage21");
        Inputs(wf, "enc")["prompt"]!.GetValue<string>().Should().Be("котик");
        Inputs(wf, "enc")["resolution"]!.GetValue<int>().Should().Be(1024);
        Inputs(wf, "lat")["batch_size"]!.GetValue<int>().Should().Be(2);
        var ks = Inputs(wf, "ks");
        ks["seed"]!.GetValue<long>().Should().Be(42);
        ks["steps"]!.GetValue<int>().Should().Be(25);
        ks["cfg"]!.GetValue<double>().Should().Be(1.0);
        ks["sampler_name"]!.GetValue<string>().Should().Be("euler");
        ks["scheduler"]!.GetValue<string>().Should().Be("simple");
        ks["negative"]!.ToJsonString().Should().Be("[\"enc\",1]");
        Inputs(wf, "save")["filename_prefix"]!.GetValue<string>().Should().Be("ccs-local-media/job");
    }

    [Fact]
    public void Правка_БезРазмера_ХолстИзЭнкодера_РеференсыПоПорядку()
    {
        // Эталон — run_edit.py: resolution 0 и латент enc[2], если размер не задан
        var wf = ComfyWorkflows.EditImage("перекрась", ["ccs-local-media/a.png", "ccs-local-media/b.png"], null, 7, "p");

        var enc = Inputs(wf, "enc");
        enc["resolution"]!.GetValue<int>().Should().Be(0);
        enc["vae"]!.ToJsonString().Should().Be("[\"vae\",0]");
        enc["images.image_1"]!.ToJsonString().Should().Be("[\"img1\",0]");
        enc["images.image_2"]!.ToJsonString().Should().Be("[\"img2\",0]");
        Inputs(wf, "img1")["image"]!.GetValue<string>().Should().Be("ccs-local-media/a.png");
        Inputs(wf, "ks")["latent_image"]!.ToJsonString().Should().Be("[\"enc\",2]");
        wf.ContainsKey("lat").Should().BeFalse();
    }

    [Fact]
    public void Правка_СРазмером_СвойЛатентИResolution1024()
    {
        var wf = ComfyWorkflows.EditImage("x", ["a.png"], (1664, 928), 7, "p");

        Inputs(wf, "enc")["resolution"]!.GetValue<int>().Should().Be(1024);
        Inputs(wf, "lat")["width"]!.GetValue<int>().Should().Be(1664);
        Inputs(wf, "ks")["latent_image"]!.ToJsonString().Should().Be("[\"lat\",0]");
    }

    [Fact]
    public void Правка_БольшеШестнадцатиРеференсов_Отказ()
    {
        var act = () => ComfyWorkflows.EditImage("x", Enumerable.Repeat("a.png", 17).ToList(), null, 1, "p");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ДоводкаЛиц_ПараметрыВыбранныеНаСтенде()
    {
        // Эталон — run_facedetail.py: denoise 0.65 и 16 шагов — выбор Андрея
        var wf = ComfyWorkflows.FaceDetail("ccs-local-media/in.png", 5, "p");

        var fd = Inputs(wf, "fd");
        fd["denoise"]!.GetValue<double>().Should().Be(0.65);
        fd["steps"]!.GetValue<int>().Should().Be(16);
        fd["guide_size"]!.GetValue<int>().Should().Be(768);
        fd["bbox_detector"]!.ToJsonString().Should().Be("[\"det\",0]");
        Inputs(wf, "det")["model_name"]!.GetValue<string>().Should().Be("bbox/face_yolov8m.pt");
        Inputs(wf, "enc")["prompt"]!.GetValue<string>().Should().Be(ComfyWorkflows.FacePrompt);
        Inputs(wf, "save")["images"]!.ToJsonString().Should().Be("[\"fd\",0]");
    }

    [Fact]
    public void Видео_РежимОднойКарты_КакВБенчеСтенда()
    {
        // Эталон — run_h3_bench.py workflow(mode A, single=True, sage=True, te_cache=False)
        var wf = ComfyWorkflows.ImageToVideo("ccs-local-media/frame.png", "она улыбается", 1344, 768, 124, 9, "p");

        foreach (var node in new[] { "mdev", "cdev", "vdev", "adev" })
            Inputs(wf, node)["device"]!.GetValue<string>().Should().Be("default", $"{node} на одной карте");
        Inputs(wf, "ms")["enabled"]!.GetValue<bool>().Should().BeFalse("сплита на две карты нет");
        Inputs(wf, "vsplit")["enabled"]!.GetValue<bool>().Should().BeFalse();
        Inputs(wf, "tec")["weight_cache"]!.GetValue<bool>().Should().BeFalse("pinned-кэш энкодера упирается в RAM");
        Inputs(wf, "ms")["model"]!.ToJsonString().Should().Be("[\"sage\",0]");
        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be("minimax_h3_fl2v_turbo_8step_v1.0_comfyui_bf16.safetensors");
        Inputs(wf, "sched")["steps"]!.GetValue<int>().Should().Be(8);
        Inputs(wf, "ksel")["sampler_name"]!.GetValue<string>().Should().Be("res_multistep");
        Inputs(wf, "i2v")["length"]!.GetValue<int>().Should().Be(124);
        Inputs(wf, "i2v")["first_frame"]!.ToJsonString().Should().Be("[\"img\",0]");
        Inputs(wf, "video")["fps"]!.GetValue<int>().Should().Be(24);
        Inputs(wf, "video")["audio"]!.ToJsonString().Should().Be("[\"adec\",0]");
        Class(wf, "save").Should().Be("SaveVideo");
        Inputs(wf, "noise")["noise_seed"]!.GetValue<long>().Should().Be(9);
    }

    [Theory]
    [InlineData(5, 124)]
    [InlineData(10, 243)]
    [InlineData(1, 39)]
    public void ДлинаКлипа_СеткаСемнадцатьКПлюсПять(int seconds, int frames)
    {
        ComfyWorkflows.FramesFor(seconds).Should().Be(frames);
        (ComfyWorkflows.FramesFor(seconds) % 17).Should().Be(5);
    }

    [Fact]
    public void ВсеШаблоны_ТолькоРазрешённыеНоды()
    {
        var graphs = new[]
        {
            ComfyWorkflows.GenerateImage("a", "", 1328, 1328, 1, 25, 1, "p"),
            ComfyWorkflows.EditImage("a", ["x.png"], null, 1, "p"),
            ComfyWorkflows.FaceDetail("x.png", 1, "p"),
            ComfyWorkflows.ImageToVideo("x.png", "a", 1344, 768, 124, 1, "p"),
        };

        foreach (var wf in graphs)
            wf.Select(p => Class(wf, p.Key)).Should().OnlyContain(c => AllowedClasses.Contains(c));
    }

    [Fact]
    public void Промпт_ОстаётсяСтрокойГрафа_АНеСтруктурой()
    {
        // Текст, похожий на JSON графа, не должен превращаться в ноды
        const string hostile = "\"}, \"evil\": {\"class_type\": \"LoadImageFromPath\"";
        var wf = ComfyWorkflows.GenerateImage(hostile, "", 1328, 1328, 1, 25, 1, "p");

        var reparsed = JsonNode.Parse(wf.ToJsonString())!.AsObject();
        reparsed.ContainsKey("evil").Should().BeFalse();
        Inputs(reparsed, "enc")["prompt"]!.GetValue<string>().Should().Be(hostile);
    }
}
