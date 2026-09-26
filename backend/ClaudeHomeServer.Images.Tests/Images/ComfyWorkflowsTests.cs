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
        "SamplerCustomAdvanced", "VAEDecodeAudio", "CreateVideo", "SaveVideo", "H3MSGPUSet", "BlockSparseAttention",
        "LTXVSeparateAVLatent", "SaveLatent", "LoadLatent", "MinimaxH3LatentUpscaler3D", "LTXVConcatAVLatent",
        "ManualSigmas", "MMH3TemporalSplitParamsV10", "MMH3SpatialSplitParamsV10", "MMH3SplitUpscale",
        "ModelPatchLoader", "LoadVideo", "GetVideoComponents", "LoadImageMask", "MiniMaxH3FunControlNetApply",
        "MiniMaxH3ReferenceToVideo", "LoadAudio",
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

    [Theory]
    [InlineData(5, 124)]
    [InlineData(10, 243)]
    [InlineData(1, 39)]
    public void ДлинаКлипа_СеткаСемнадцатьКПлюсПять(int seconds, int frames)
    {
        ComfyWorkflows.FramesFor(seconds).Should().Be(frames);
        (ComfyWorkflows.FramesFor(seconds) % 17).Should().Be(5);
    }

    // Все шаблоны во всех ветках: и разрешённые ноды, и валидатор по object_info
    public static IEnumerable<object[]> AllGraphs() =>
        new (string Name, JsonObject Graph)[]
        {
            ("generate", ComfyWorkflows.GenerateImage("a", "", 1328, 1328, 1, 25, 1, "p")),
            ("edit", ComfyWorkflows.EditImage("a", ["x.png", "y.png"], null, 1, "p")),
            ("edit-size", ComfyWorkflows.EditImage("a", ["x.png"], (1664, 928), 1, "p")),
            ("face", ComfyWorkflows.FaceDetail("x.png", 1, "p")),
            ("t2v", ComfyWorkflows.TextToVideo("a", 1344, 768, 124, 1, false, "p", "l")),
            ("t2v-fast", ComfyWorkflows.TextToVideo("a", 768, 1344, 243, 1, true, "p", "l")),
            ("i2v", ComfyWorkflows.ImageToVideo("x.png", null, "a", 1344, 768, 124, 1, false, "p", "l")),
            ("i2v-last-fast", ComfyWorkflows.ImageToVideo("x.png", "y.png", "a", 864, 480, 124, 1, true, "p", "l")),
            ("upscale-single", ComfyWorkflows.UpscaleVideo("v.latent", "a.latent", "x.png", "a", 2528, 1440, 124, 1, false, "p")),
            ("upscale-tiled-t2v", ComfyWorkflows.UpscaleVideo("v.latent", "a.latent", null, "a", 1536, 2688, 243, 1, true, "p")),
            ("inpaint", ComfyWorkflows.InpaintVideo("v.mp4", "m.png", "a", 864, 480, 124, 1, "p")),
            ("r2v-images", ComfyWorkflows.ReferenceToVideo("a", ["x.png"], [], [], 1344, 768, 124, false, 1, "p")),
            ("r2v-all", ComfyWorkflows.ReferenceToVideo("a", Enumerable.Range(0, 9).Select(i => $"r{i}.png").ToList(),
                ["v1.mp4", "v2.mp4", "v3.mp4"], ["a1.wav", "a2.wav", "a3.wav"], 768, 1344, 243, true, 1, "p")),
        }.Select(g => new object[] { g.Name, g.Graph });

    [Theory]
    [MemberData(nameof(AllGraphs))]
    public void ВсеШаблоны_ТолькоРазрешённыеНоды(string name, JsonObject wf)
    {
        wf.Select(p => Class(wf, p.Key)).Should().OnlyContain(c => AllowedClasses.Contains(c), name);
    }

    // Замена validate_prompt ComfyUI без живой очереди: входы, типы связей, значения списков и
    // диапазоны — по снимку object_info стенда (офлайн, CPU; сам снимок — dump_object_info.py)
    [Theory]
    [MemberData(nameof(AllGraphs))]
    public void ВсеШаблоны_ПроходятСхемуНодСтенда(string name, JsonObject wf)
    {
        ComfyGraphValidator.Validate(wf, ComfyReferenceFiles.ObjectInfo()).Should().BeEmpty(name);
    }

    [Fact]
    public void Валидатор_ЛовитОшибкуСхемы()
    {
        // Сам сторож обязан краснеть: чужой вход, неверный тип связи, значение вне списка, пропуск
        var wf = ComfyWorkflows.TextToVideo("a", 1344, 768, 124, 1, false, "p", "l");
        wf["save"]!["inputs"]!["crf"] = 23;
        wf["t2v"]!["inputs"]!["vae"] = new JsonArray("gpuset", 0);
        wf["sampler_ksel"]!["inputs"]!["sampler_name"] = "нет-такого";
        wf["t2v"]!["inputs"]!.AsObject().Remove("length");

        var errors = ComfyGraphValidator.Validate(wf, ComfyReferenceFiles.ObjectInfo());

        errors.Should().Contain(e => e.Contains("save") && e.Contains("crf"));
        errors.Should().Contain(e => e.Contains("t2v") && e.Contains("vae"));
        errors.Should().Contain(e => e.Contains("sampler_ksel") && e.Contains("sampler_name"));
        errors.Should().Contain(e => e.Contains("t2v") && e.Contains("length"));
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

    // ─── MiniMax H3: снимки против эталонов стенда ───────────────────────────
    // Копии ~/ComfyUI-h3-tools/workflows/* (прошли validate_prompt ComfyUI) лежат в
    // ComfyReference/. Шаблон строится с теми же значениями, что в эталоне, и обязан совпасть
    // с ним целиком — нода в ноду, вход во вход

    private const long RefSeed = 20260925;
    private const string RefImage = "portrait_1344x768.png";

    [Theory]
    [InlineData("i2v_A_1344x768_124", false)]
    [InlineData("i2v_AS_1344x768_124", true)]
    public void КартинкаВВидео_СовпадаетСЭталономСтенда(string name, bool fast)
    {
        var reference = ComfyReferenceFiles.Graph(name);

        var wf = ComfyWorkflows.ImageToVideo(RefImage, null, RefPrompt(reference, "i2v"), 1344, 768, 124, RefSeed, fast,
            $"h3bench/{name}", $"h3bench/latents/{name}");

        ComfyGraphDiff.Compare(wf, reference).Should().BeEmpty();
    }

    [Fact]
    public void ТекстВВидео_СовпадаетСЭталономСтенда()
    {
        var reference = ComfyReferenceFiles.Graph("t2v_A_1344x768_124");
        // SaveConditioning в эталоне — для замеров стенда; апскейл пересобирает conditioning сам
        reference.Remove("cond_save");

        var wf = ComfyWorkflows.TextToVideo(RefPrompt(reference, "t2v"), 1344, 768, 124, RefSeed, false,
            "h3bench/t2v_A_1344x768_124", "h3bench/latents/t2v_A_1344x768_124");

        ComfyGraphDiff.Compare(wf, reference).Should().BeEmpty();
    }

    [Theory]
    [InlineData("uplat_1440_single", 2528, 1440, false)]
    [InlineData("uplat_1440_tiled", 2528, 1440, true)]
    [InlineData("uplat_2k_tiled", 2688, 1536, true)]
    public void Апскейл_СовпадаетСЭталономСтенда(string name, int width, int height, bool tiled)
    {
        var reference = ComfyReferenceFiles.Graph(name);

        var wf = ComfyWorkflows.UpscaleVideo("h3_upscale_src_video.latent", "h3_upscale_src_audio.latent", RefImage,
            RefPrompt(reference, "i2v_hi"), width, height, 124, RefSeed, tiled, $"h3bench/{name}");

        ComfyGraphDiff.Compare(wf, reference).Should().BeEmpty();
    }

    [Fact]
    public void Инпейнт_СовпадаетСЭталономСтенда()
    {
        var reference = ComfyReferenceFiles.Graph("inpaint_864x480_124");

        var wf = ComfyWorkflows.InpaintVideo("h3_inpaint_src_864x480.mp4", "h3_inpaint_mask_864x480.png",
            RefPrompt(reference, "r2v"), 864, 480, 124, RefSeed, "h3bench/inpaint_864x480_124");

        ComfyGraphDiff.Compare(wf, reference).Should().BeEmpty();
    }

    [Fact]
    public void Сверка_ЛовитРасхождениеСЭталоном()
    {
        var reference = ComfyReferenceFiles.Graph("i2v_A_1344x768_124");
        var wf = ComfyWorkflows.ImageToVideo(RefImage, null, RefPrompt(reference, "i2v"), 1344, 768, 124, RefSeed, true,
            "h3bench/i2v_A_1344x768_124", "h3bench/latents/i2v_A_1344x768_124");

        ComfyGraphDiff.Compare(wf, reference).Should().Contain(d => d.Contains("sparse"));
    }

    [Fact]
    public void КартинкаВВидео_ПоследнийКадр_ОтдельнаяЗагрузка()
    {
        var wf = ComfyWorkflows.ImageToVideo("a.png", "b.png", "x", 1344, 768, 124, 1, false, "p", "l");

        Inputs(wf, "i2v")["first_frame"]!.ToJsonString().Should().Be("[\"img\",0]");
        Inputs(wf, "i2v")["last_frame"]!.ToJsonString().Should().Be("[\"img_last\",0]");
        Inputs(wf, "img_last")["image"]!.GetValue<string>().Should().Be("b.png");
    }

    [Fact]
    public void Видео_ЛатентыВидеоИАудио_ПодПрефиксомЗадачи()
    {
        var wf = ComfyWorkflows.TextToVideo("x", 1344, 768, 124, 1, false, "p", "ccs-local-media/latents/lm_1");

        Inputs(wf, "lat_save_v")["filename_prefix"]!.GetValue<string>().Should().Be("ccs-local-media/latents/lm_1_video");
        Inputs(wf, "lat_save_a")["filename_prefix"]!.GetValue<string>().Should().Be("ccs-local-media/latents/lm_1_audio");
        Inputs(wf, "lat_save_a")["samples"]!.ToJsonString().Should().Be("[\"lat_split\",1]");
    }

    [Fact]
    public void Апскейл_ВидеоПоТексту_БезПервогоКадра()
    {
        var wf = ComfyWorkflows.UpscaleVideo("v.latent", "a.latent", null, "x", 2528, 1440, 124, 1, false, "p");

        wf.ContainsKey("img").Should().BeFalse();
        Inputs(wf, "i2v_hi").ContainsKey("first_frame").Should().BeFalse();
    }

    [Fact]
    public void Референсы_НумерацияAutogrowСНуля_ЗвукВидеоВПарныйВход()
    {
        var wf = ComfyWorkflows.ReferenceToVideo("x", ["a.png", "b.png"], ["v.mp4"], ["s.wav"], 1344, 768, 124, true, 1, "p");

        var r2v = Inputs(wf, "r2v");
        r2v["ref_images.ref_image_0"]!.ToJsonString().Should().Be("[\"ref_img0\",0]");
        r2v["ref_images.ref_image_1"]!.ToJsonString().Should().Be("[\"ref_img1\",0]");
        r2v["ref_videos.ref_video_0"]!.ToJsonString().Should().Be("[\"ref_vid0_parts\",0]");
        r2v["ref_video_audios.ref_video_audio_0"]!.ToJsonString().Should().Be("[\"ref_vid0_parts\",1]");
        r2v["ref_audios.ref_audio_0"]!.ToJsonString().Should().Be("[\"ref_aud0\",0]");
        r2v["ref_image_size"]!.GetValue<string>().Should().Be("max");
        Inputs(wf, "sampler_sched")["steps"]!.GetValue<int>().Should().Be(4);
        Inputs(wf, "lora")["lora_name"]!.GetValue<string>().Should().Be("minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors");
    }

    [Fact]
    public void Референсы_ДесятьКартинок_Отказ()
    {
        var act = () => ComfyWorkflows.ReferenceToVideo("x", Enumerable.Repeat("a.png", 10).ToList(), [], [],
            1344, 768, 124, false, 1, "p");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static string RefPrompt(JsonObject reference, string node) => Inputs(reference, node)["prompt"]!.GetValue<string>();
}
