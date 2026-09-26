using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Фиксированные графы ComfyUI для локальной генерации. Произвольный граф снаружи не
// принимаем НИКОГДА: через custom nodes (KJNodes, Impact) граф читает и пишет файлы хоста,
// то есть «свой граф» — дорога к RCE. Снаружи в граф попадают только проверенные значения:
// текст промпта, seed, размеры из белого списка, шаги в пределах, длина по сетке H3,
// имена файлов, которые мы сами загрузили в input ComfyUI.
//
// Эталоны — рабочие скрипты стенда (сверять при правке модели или нод):
//   генерация   — ~/ComfyUI/qwen-image21-1gpu-api.json + run_gen.py
//   правка      — ~/ComfyUI/run_edit.py
//   лица        — ~/ComfyUI/run_facedetail.py (denoise 0.65, 16 шагов — выбор Андрея)
//   видео       — ~/ComfyUI-h3-tools/run_h3_bench.py workflow(): режим A, --single-gpu,
//                 Sage включён, кэш энкодера выключен (так его гасит сам --single-gpu)
public static class ComfyWorkflows
{
    // Подпапка output ComfyUI для наших выходов: чужие прогоны стенда не смешиваются с нашими
    public const string OutputFolder = "ccs-local-media";

    public const int MinSteps = 4;
    public const int MaxSteps = 40;
    public const int DefaultSteps = 25;
    public const int MaxCount = 4;
    public const int MaxEditImages = 16;
    public const int MaxPromptLength = 4000;
    public const int VideoFps = 24;

    // Рекомендованные размеры Qwen-Image по соотношению сторон
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> ImageSizes =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["1:1"] = (1328, 1328),
            ["16:9"] = (1664, 928),
            ["9:16"] = (928, 1664),
            ["4:3"] = (1472, 1104),
            ["3:4"] = (1104, 1472),
            ["3:2"] = (1584, 1056),
            ["2:3"] = (1056, 1584),
        };

    // Размеры H3 из бенча стенда (альбомная ориентация; портретную даёт перестановка сторон)
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> VideoSizes =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["full"] = (1344, 768),
            ["half"] = (864, 480),
        };

    // Длина клипа — сетка 17k+5 при 24 fps, та же формула, что в официальном шаблоне:
    // 5 с = 124 кадра, 10 с = 243
    public static int FramesFor(int seconds)
    {
        var n = Math.Max(5, seconds * VideoFps);
        return n + (((5 - n % 17) % 17) + 17) % 17;
    }

    private const string QwenUnet = "qwen_image_2.1_bf16.safetensors";
    private const string QwenClip = "qwen3vl_8b_bf16.safetensors";
    private const string QwenVae = "qwen_image_2.1_vae_bf16.safetensors";

    // Промпт доводки лиц — из run_facedetail.py: он описывает ЛИЦО, а не сцену
    public const string FacePrompt =
        "Photorealistic close-up of a human face, natural skin texture with fine pores, "
        + "sharp detailed eyes, natural relaxed expression, realistic soft lighting, subtle film grain.";

    public static JsonObject GenerateImage(string prompt, string negativePrompt, int width, int height,
        long seed, int steps, int count, string filenamePrefix)
    {
        var wf = QwenLoaders();
        wf["enc"] = Node("TextEncodeQwenImage21", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = prompt,
            ["negative_prompt"] = negativePrompt,
            ["resolution"] = 1024,
        });
        wf["lat"] = Node("EmptyLatentImage", new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["batch_size"] = count,
        });
        wf["ks"] = KSampler(Link("lat"), seed, steps);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Первая картинка — основная (холст по ней), остальные — дополнительные референсы.
    // Без размера холст берётся из энкодера (resolution 0 — каждый референс в своём размере)
    public static JsonObject EditImage(string prompt, IReadOnlyList<string> images, (int Width, int Height)? size,
        long seed, string filenamePrefix)
    {
        if (images.Count is < 1 or > MaxEditImages)
            throw new ArgumentOutOfRangeException(nameof(images), "Нужно от 1 до 16 картинок");

        var wf = QwenLoaders();
        var enc = new JsonObject
        {
            ["clip"] = Link("clip"),
            ["vae"] = Link("vae"),
            ["prompt"] = prompt,
            ["negative_prompt"] = "",
            ["resolution"] = size is null ? 0 : 1024,
        };
        for (var k = 1; k <= images.Count; k++)
        {
            wf[$"img{k}"] = Node("LoadImage", new JsonObject { ["image"] = images[k - 1] });
            enc[$"images.image_{k}"] = Link($"img{k}");
        }
        wf["enc"] = Node("TextEncodeQwenImage21", enc);

        JsonArray latent;
        if (size is { } s)
        {
            wf["lat"] = Node("EmptyLatentImage", new JsonObject
            {
                ["width"] = s.Width,
                ["height"] = s.Height,
                ["batch_size"] = 1,
            });
            latent = Link("lat");
        }
        else
        {
            latent = Link("enc", 2);
        }
        wf["ks"] = KSampler(latent, seed, DefaultSteps);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    public static JsonObject FaceDetail(string image, long seed, string filenamePrefix)
    {
        var wf = QwenLoaders();
        wf["enc"] = Node("TextEncodeQwenImage21", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = FacePrompt,
            ["negative_prompt"] = "",
            ["resolution"] = 1024,
        });
        wf["det"] = Node("UltralyticsDetectorProvider", new JsonObject { ["model_name"] = "bbox/face_yolov8m.pt" });
        wf["img"] = Node("LoadImage", new JsonObject { ["image"] = image });
        wf["fd"] = Node("FaceDetailer", new JsonObject
        {
            ["image"] = Link("img"),
            ["model"] = Link("unet"),
            ["clip"] = Link("clip"),
            ["vae"] = Link("vae"),
            ["guide_size"] = 768,
            ["guide_size_for"] = true,
            ["max_size"] = 1024,
            ["seed"] = seed,
            ["steps"] = 16,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["positive"] = Link("enc"),
            ["negative"] = Link("enc", 1),
            ["denoise"] = 0.65,
            ["feather"] = 5,
            ["noise_mask"] = true,
            ["force_inpaint"] = true,
            ["bbox_threshold"] = 0.4,
            ["bbox_dilation"] = 10,
            ["bbox_crop_factor"] = 3.0,
            ["sam_detection_hint"] = "center-1",
            ["sam_dilation"] = 0,
            ["sam_threshold"] = 0.93,
            ["sam_bbox_expansion"] = 0,
            ["sam_mask_hint_threshold"] = 0.7,
            ["sam_mask_hint_use_negative"] = "False",
            ["drop_size"] = 10,
            ["bbox_detector"] = Link("det"),
            ["wildcard"] = "",
            ["cycle"] = 1,
        });
        wf["save"] = Node("SaveImage", new JsonObject
        {
            ["images"] = Link("fd"),
            ["filename_prefix"] = filenamePrefix,
        });
        return wf;
    }

    // MiniMax H3 fl2va + turbo-LoRA 8 шагов, одна карта (режим A бенча, все узлы на default).
    // Звук генерирует сама модель, CreateVideo склеивает кадры и аудио
    public static JsonObject ImageToVideo(string image, string prompt, int width, int height, int length,
        long seed, string filenamePrefix)
    {
        const string device = "default";
        return new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject
            {
                ["unet_name"] = "minimax_h3_fl2va_pruned_int8_convrot.safetensors",
                ["weight_dtype"] = "default",
            }),
            ["lora"] = Node("LoraLoaderModelOnly", new JsonObject
            {
                ["model"] = Link("unet"),
                ["lora_name"] = "minimax_h3_fl2v_turbo_8step_v1.0_comfyui_bf16.safetensors",
                ["strength_model"] = 1.0,
            }),
            ["mdev"] = Node("SelectModelDevice", new JsonObject { ["model"] = Link("lora"), ["device"] = device }),
            ["sage"] = Node("PathchSageAttentionKJ", new JsonObject
            {
                ["model"] = Link("mdev"),
                ["sage_attention"] = "auto",
                ["allow_compile"] = false,
            }),
            ["ms"] = Node("H3MultiStream", new JsonObject
            {
                ["model"] = Link("sage"),
                ["enabled"] = false,
                ["second_gpu"] = -1,
                ["exchange"] = "host",
                ["exchange_chunks"] = 0,
                ["sparse_attention"] = false,
                ["dynamic_vram"] = "keep",
                ["vram_block_cache"] = false,
                ["vram_reserve_gb"] = 2.0,
                ["sparse_vsa"] = false,
                ["weight_cache"] = true,
                ["cache_ram_reserve_gb"] = 8.0,
            }),
            ["clip"] = Node("CLIPLoader", new JsonObject
            {
                ["clip_name"] = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors",
                ["type"] = "minimax",
                ["device"] = "default",
            }),
            ["cdev"] = Node("SelectCLIPDevice", new JsonObject { ["clip"] = Link("clip"), ["device"] = device }),
            ["tec"] = Node("H3MSTextEncoderCache", new JsonObject
            {
                ["clip"] = Link("cdev"),
                ["enabled"] = true,
                // pinned-кэш энкодера на одной карте упирается в RAM хоста (README стенда)
                ["weight_cache"] = false,
                ["cond_cache_entries"] = 64,
                ["cache_ram_reserve_gb"] = 8.0,
            }),
            ["vvae"] = Node("VAELoader", new JsonObject { ["vae_name"] = "minimax_h3_video_vae_int8_convrot.safetensors" }),
            ["vdev"] = Node("SelectVAEDevice", new JsonObject { ["vae"] = Link("vvae"), ["device"] = device }),
            ["vsplit"] = Node("H3MSVAESplitDecode", new JsonObject
            {
                ["vae"] = Link("vdev"),
                ["enabled"] = false,
                ["second_gpu"] = -1,
                ["max_gpus"] = 4,
            }),
            ["avae"] = Node("VAELoader", new JsonObject { ["vae_name"] = "minimax_h3_audio_vae_fp32.safetensors" }),
            ["adev"] = Node("SelectVAEDevice", new JsonObject { ["vae"] = Link("avae"), ["device"] = device }),
            ["img"] = Node("LoadImage", new JsonObject { ["image"] = image }),
            ["i2v"] = Node("MiniMaxH3ImageToVideo", new JsonObject
            {
                ["clip"] = Link("tec"),
                ["vae"] = Link("vsplit"),
                ["prompt"] = prompt,
                ["width"] = width,
                ["height"] = height,
                ["length"] = length,
                ["first_frame"] = Link("img"),
            }),
            ["guider"] = Node("BasicGuider", new JsonObject { ["model"] = Link("ms"), ["conditioning"] = Link("i2v") }),
            ["sched"] = Node("BasicScheduler", new JsonObject
            {
                ["model"] = Link("ms"),
                ["scheduler"] = "simple",
                ["steps"] = 8,
                ["denoise"] = 1.0,
            }),
            ["ksel"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "res_multistep" }),
            ["noise"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed }),
            ["sampler"] = Node("SamplerCustomAdvanced", new JsonObject
            {
                ["noise"] = Link("noise"),
                ["guider"] = Link("guider"),
                ["sampler"] = Link("ksel"),
                ["sigmas"] = Link("sched"),
                ["latent_image"] = Link("i2v", 1),
            }),
            ["vdec"] = Node("VAEDecode", new JsonObject { ["samples"] = Link("sampler"), ["vae"] = Link("vsplit") }),
            ["adec"] = Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link("sampler"), ["vae"] = Link("adev") }),
            ["video"] = Node("CreateVideo", new JsonObject
            {
                ["images"] = Link("vdec"),
                ["fps"] = VideoFps,
                ["audio"] = Link("adec"),
            }),
            ["save"] = Node("SaveVideo", new JsonObject
            {
                ["video"] = Link("video"),
                ["filename_prefix"] = filenamePrefix,
                ["format"] = "auto",
                ["codec"] = "auto",
            }),
        };
    }

    private static JsonObject QwenLoaders() => new()
    {
        ["unet"] = Node("UNETLoaderMultiGPU", new JsonObject
        {
            ["unet_name"] = QwenUnet,
            ["weight_dtype"] = "default",
            ["device"] = "cuda:0",
        }),
        ["clip"] = Node("CLIPLoaderMultiGPU", new JsonObject
        {
            ["clip_name"] = QwenClip,
            ["type"] = "qwen_image",
            ["device"] = "cuda:0",
        }),
        ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = QwenVae }),
    };

    private static JsonObject KSampler(JsonArray latent, long seed, int steps) =>
        Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("enc"),
            ["negative"] = Link("enc", 1),
            ["latent_image"] = latent,
            ["seed"] = seed,
            ["steps"] = steps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });

    private static void AddDecodeAndSave(JsonObject wf, string filenamePrefix)
    {
        wf["dec"] = Node("VAEDecode", new JsonObject { ["samples"] = Link("ks"), ["vae"] = Link("vae") });
        wf["save"] = Node("SaveImage", new JsonObject
        {
            ["images"] = Link("dec"),
            ["filename_prefix"] = filenamePrefix,
        });
    }

    private static JsonObject Node(string classType, JsonObject inputs) =>
        new() { ["class_type"] = classType, ["inputs"] = inputs };

    private static JsonArray Link(string node, int output = 0) => new(node, output);
}
