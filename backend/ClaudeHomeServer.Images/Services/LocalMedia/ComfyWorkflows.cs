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
//   видео H3    — ~/ComfyUI-h3-tools/workflows/ (build_workflows.py): одна карта, режимы A и AS,
//                 кэш весов энкодера выключен; копии эталонов лежат в тестах (ComfyReference/)
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

    // --- MiniMax H3 -----------------------------------------------------------------------------
    // Эталоны — ~/ComfyUI-h3-tools/workflows/{t2v,i2v,upscale_lat,inpaint}/*.json (генератор
    // build_workflows.py, все прошли validate_prompt ComfyUI). Имена нод совпадают с эталоном:
    // тесты сверяют граф с ним целиком.

    private const string H3Fl2va = "minimax_h3_fl2va_pruned_int8_convrot.safetensors";
    private const string H3Ref2va = "minimax_h3_ref2va_pruned_int8_convrot.safetensors";
    private const string H3LoraFl2v8 = "minimax_h3_fl2v_turbo_8step_v1.0_comfyui_bf16.safetensors";
    private const string H3LoraRef2v4 = "minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors";
    private const string H3TextEncoder = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors";
    private const string H3VideoVae = "minimax_h3_video_vae_int8_convrot.safetensors";
    private const string H3AudioVae = "minimax_h3_audio_vae_fp32.safetensors";
    private const string H3FunControlNet = "minimax_h3_fun_controlnet_union_pruned_int8_convrot.safetensors";
    private const string H3LatentUpscaler = "minimax_h3_latent_upscaler_3d_conv_v1_bf16.safetensors";

    // Донастройка после латентного апскейла — «3 step Sigmas» из примера апскейлера
    public const string RefineSigmas = "0.9035, 0.6316, 0.3158, 0.0000";

    public const int MaxRefImages = 9;
    public const int MaxRefVideos = 3;
    public const int MaxRefAudios = 3;

    // Размеры апскейла из эталонов upscale_lat (база — full 1344×768)
    public static readonly (int Width, int Height) Upscale1440 = (2528, 1440);
    public static readonly (int Width, int Height) Upscale2k = (2688, 1536);

    // Видео по тексту: MiniMaxH3ImageToVideo без first_frame (шаблон t2v стенда)
    public static JsonObject TextToVideo(string prompt, int width, int height, int length, long seed, bool fast,
        string filenamePrefix, string latentPrefix)
    {
        var wf = H3Stack(fast);
        wf["t2v"] = Node("MiniMaxH3ImageToVideo", H3Conditioning(prompt, width, height, length));
        AddSampler(wf, "ms", Link("t2v"), Link("t2v", 1), 8, seed);
        AddH3DecodeAndSave(wf, "sampler", "vsplit", "adev", filenamePrefix);
        AddLatentSave(wf, latentPrefix);
        return wf;
    }

    // Видео от первого (и необязательно последнего) кадра. fast — режим AS бенча: sol-attn между
    // Sage и MultiStream. Звук генерирует сама модель, CreateVideo склеивает кадры и аудио
    public static JsonObject ImageToVideo(string image, string? lastFrame, string prompt, int width, int height,
        int length, long seed, bool fast, string filenamePrefix, string latentPrefix)
    {
        var wf = H3Stack(fast);
        wf["img"] = Node("LoadImage", new JsonObject { ["image"] = image });
        var cond = H3Conditioning(prompt, width, height, length);
        cond["first_frame"] = Link("img");
        if (lastFrame is not null)
        {
            wf["img_last"] = Node("LoadImage", new JsonObject { ["image"] = lastFrame });
            cond["last_frame"] = Link("img_last");
        }
        wf["i2v"] = Node("MiniMaxH3ImageToVideo", cond);
        AddSampler(wf, "ms", Link("i2v"), Link("i2v", 1), 8, seed);
        AddH3DecodeAndSave(wf, "sampler", "vsplit", "adev", filenamePrefix);
        AddLatentSave(wf, latentPrefix);
        return wf;
    }

    // Апскейл по сохранённому латенту прошлой задачи: LoadLatent ×2 → латентный апскейлер →
    // донастройка на целевом разрешении. Conditioning пересобирается под новый размер (латент
    // первого кадра привязан к размеру), поэтому нужны промпт и первый кадр исходной задачи
    // (у видео по тексту кадра нет). tiled — MMH3SplitUpscale тайлами 768×768
    public static JsonObject UpscaleVideo(string latentVideo, string latentAudio, string? firstFrame, string prompt,
        int width, int height, int length, long seed, bool tiled, string filenamePrefix)
    {
        var wf = H3Stack(fast: false);
        if (firstFrame is not null)
            wf["img"] = Node("LoadImage", new JsonObject { ["image"] = firstFrame });
        wf["lat_v"] = Node("LoadLatent", new JsonObject { ["latent"] = latentVideo });
        wf["lat_a"] = Node("LoadLatent", new JsonObject { ["latent"] = latentAudio });
        wf["up"] = Node("MinimaxH3LatentUpscaler3D", new JsonObject
        {
            ["latent"] = Link("lat_v"),
            ["model_name"] = H3LatentUpscaler,
            ["mode"] = "target dimensions",
            ["mode.width"] = width,
            ["mode.height"] = height,
            ["align"] = 32,
            ["enable_temporal_chunking"] = true,
            ["force_unload"] = true,
            ["device"] = "cuda",
            ["precision"] = "bf16",
        });
        wf["up_av"] = Node("LTXVConcatAVLatent", new JsonObject
        {
            ["video_latent"] = Link("up"),
            ["audio_latent"] = Link("lat_a"),
        });
        var cond = H3Conditioning(prompt, width, height, length);
        if (firstFrame is not null) cond["first_frame"] = Link("img");
        wf["i2v_hi"] = Node("MiniMaxH3ImageToVideo", cond);

        if (tiled)
        {
            wf["refine_sched"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = RefineSigmas });
            wf["refine_ksel"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler" });
            wf["refine_noise"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed });
            wf["tsplit"] = Node("MMH3TemporalSplitParamsV10", new JsonObject
            {
                ["chunk_frames"] = 73,
                ["temporal_overlap_frames"] = 22,
                ["anchor_strength"] = 0.999,
                ["motion_anchor_frames"] = "22",
                ["identity_anchor_frames"] = 24,
            });
            wf["ssplit"] = Node("MMH3SpatialSplitParamsV10", new JsonObject
            {
                ["tile_width"] = 768,
                ["tile_height"] = 768,
                ["overlap_ratio"] = 0.25,
                ["fade_ratio"] = 0.5,
                ["min_tile_size"] = 256,
                ["seam_denoise"] = 0.6,
            });
            wf["refine"] = Node("MMH3SplitUpscale", new JsonObject
            {
                ["model"] = Link("ms"),
                ["conditioning"] = Link("i2v_hi"),
                ["latent"] = Link("up_av"),
                ["noise"] = Link("refine_noise"),
                ["sampler"] = Link("refine_ksel"),
                ["sigmas"] = Link("refine_sched"),
                ["cfg"] = 1.0,
                ["temporal_split_param"] = Link("tsplit"),
                ["spatial_split_param"] = Link("ssplit"),
                ["seam_polish"] = "off",
                ["color_match"] = true,
            });
        }
        else
        {
            AddSampler(wf, "ms", Link("i2v_hi"), Link("up_av"), 0, seed, "refine", "euler", RefineSigmas);
        }
        AddH3DecodeAndSave(wf, "refine", "vsplit", "adev", filenamePrefix);
        return wf;
    }

    // Инпейнт по маске (белое — перегенерировать): Fun ControlNet Union поверх ref2va +
    // turbo-LoRA 4 шага, ReferenceToVideo без референсов (эталон inpaint стенда)
    public static JsonObject InpaintVideo(string video, string mask, string prompt, int width, int height,
        int length, long seed, string filenamePrefix)
    {
        var wf = Ref2vLoaders();
        wf["patch"] = Node("ModelPatchLoader", new JsonObject { ["name"] = H3FunControlNet });
        wf["src"] = Node("LoadVideo", new JsonObject { ["file"] = video });
        wf["src_parts"] = Node("GetVideoComponents", new JsonObject { ["video"] = Link("src") });
        wf["mask"] = Node("LoadImageMask", new JsonObject { ["image"] = mask, ["channel"] = "red" });
        wf["fun"] = Node("MiniMaxH3FunControlNetApply", new JsonObject
        {
            ["model"] = Link("lora"),
            ["model_patch"] = Link("patch"),
            ["vae"] = Link("vvae"),
            ["strength"] = 1.0,
            ["start_percent"] = 0.0,
            ["end_percent"] = 1.0,
            ["mask"] = Link("mask"),
            ["source_video"] = Link("src_parts"),
        });
        wf["r2v"] = Node("MiniMaxH3ReferenceToVideo", R2vConditioning(prompt, width, height, length, "match"));
        AddSampler(wf, "fun", Link("r2v"), Link("r2v", 1), 4, seed);
        AddH3DecodeAndSave(wf, "sampler", "vvae", "avae", filenamePrefix);
        return wf;
    }

    // Видео по референсам: картинки (до 9), видео (до 3, звук каждого — в парный вход
    // ref_video_audio) и отдельный звук (до 3); проводка — официальный шаблон
    // video_minimax_h3_r2v с turbo-LoRA 4 шага, как в инпейнте. Входы autogrow нумеруются с нуля.
    // max в ref_image_size держит референсы в 2048 px по короткой стороне — в разы медленнее
    public static JsonObject ReferenceToVideo(string prompt, IReadOnlyList<string> refImages,
        IReadOnlyList<string> refVideos, IReadOnlyList<string> refAudios, int width, int height, int length,
        bool maxIdentity, long seed, string filenamePrefix)
    {
        if (refImages.Count is < 1 or > MaxRefImages)
            throw new ArgumentOutOfRangeException(nameof(refImages), "Нужно от 1 до 9 картинок-референсов");
        if (refVideos.Count > MaxRefVideos) throw new ArgumentOutOfRangeException(nameof(refVideos));
        if (refAudios.Count > MaxRefAudios) throw new ArgumentOutOfRangeException(nameof(refAudios));

        var wf = Ref2vLoaders();
        var cond = R2vConditioning(prompt, width, height, length, maxIdentity ? "max" : "match");
        for (var k = 0; k < refImages.Count; k++)
        {
            wf[$"ref_img{k}"] = Node("LoadImage", new JsonObject { ["image"] = refImages[k] });
            cond[$"ref_images.ref_image_{k}"] = Link($"ref_img{k}");
        }
        for (var k = 0; k < refVideos.Count; k++)
        {
            wf[$"ref_vid{k}"] = Node("LoadVideo", new JsonObject { ["file"] = refVideos[k] });
            wf[$"ref_vid{k}_parts"] = Node("GetVideoComponents", new JsonObject { ["video"] = Link($"ref_vid{k}") });
            cond[$"ref_videos.ref_video_{k}"] = Link($"ref_vid{k}_parts");
            cond[$"ref_video_audios.ref_video_audio_{k}"] = Link($"ref_vid{k}_parts", 1);
        }
        for (var k = 0; k < refAudios.Count; k++)
        {
            wf[$"ref_aud{k}"] = Node("LoadAudio", new JsonObject { ["audio"] = refAudios[k] });
            cond[$"ref_audios.ref_audio_{k}"] = Link($"ref_aud{k}");
        }
        wf["r2v"] = Node("MiniMaxH3ReferenceToVideo", cond);
        AddSampler(wf, "lora", Link("r2v"), Link("r2v", 1), 4, seed);
        AddH3DecodeAndSave(wf, "sampler", "vvae", "avae", filenamePrefix);
        return wf;
    }

    // Стек H3 на одной карте: ноды MultiStream ради кэшей, а не сплита (H3MSGPUSet max_gpus=1,
    // MultiStream enabled=true держит pinned-кэш весов DiT). fast — режим AS: BlockSparseAttention
    // (sol-attn, tau 1.3, с 20 % шагов) плюс sparse_attention у MultiStream
    private static JsonObject H3Stack(bool fast)
    {
        const string device = "default";
        var wf = new JsonObject
        {
            ["gpuset"] = Node("H3MSGPUSet", new JsonObject
            {
                ["gpus"] = "auto",
                ["exclude"] = "",
                ["shares"] = "",
                ["min_free_vram_gb"] = 0.0,
                ["max_gpus"] = 1,
            }),
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = H3Fl2va, ["weight_dtype"] = "default" }),
            ["lora"] = Node("LoraLoaderModelOnly", new JsonObject
            {
                ["model"] = Link("unet"),
                ["lora_name"] = H3LoraFl2v8,
                ["strength_model"] = 1.0,
            }),
            ["mdev"] = Node("SelectModelDevice", new JsonObject { ["model"] = Link("lora"), ["device"] = device }),
            ["sage"] = Node("PathchSageAttentionKJ", new JsonObject
            {
                ["model"] = Link("mdev"),
                ["sage_attention"] = "auto",
                ["allow_compile"] = false,
            }),
        };
        if (fast)
            wf["sparse"] = Node("BlockSparseAttention", new JsonObject
            {
                ["model"] = Link("sage"),
                ["selection"] = "sol-attn",
                ["selection.tau"] = 1.3,
                ["start_percent"] = 0.2,
                ["end_percent"] = 1.0,
                ["dense_blocks"] = "",
                ["min_tokens"] = 12288,
                ["extra_tokens"] = 256,
                ["sink_conditioning"] = "exact_kv_and_rows",
                ["verbose"] = false,
            });
        wf["ms"] = Node("H3MultiStream", new JsonObject
        {
            ["model"] = Link(fast ? "sparse" : "sage"),
            ["enabled"] = true,
            ["second_gpu"] = -1,
            ["exchange"] = "host",
            ["exchange_chunks"] = 0,
            ["sparse_attention"] = fast,
            ["dynamic_vram"] = "keep",
            ["vram_block_cache"] = false,
            ["vram_reserve_gb"] = 2.0,
            ["sparse_vsa"] = false,
            ["weight_cache"] = true,
            ["cache_ram_reserve_gb"] = 8.0,
            ["gpus"] = Link("gpuset"),
        });
        wf["clip"] = H3ClipLoader();
        wf["cdev"] = Node("SelectCLIPDevice", new JsonObject { ["clip"] = Link("clip"), ["device"] = device });
        wf["tec"] = Node("H3MSTextEncoderCache", new JsonObject
        {
            ["clip"] = Link("cdev"),
            ["enabled"] = true,
            // pinned-кэш энкодера на одной карте упирается в RAM хоста (README стенда)
            ["weight_cache"] = false,
            ["cond_cache_entries"] = 64,
            ["cache_ram_reserve_gb"] = 8.0,
        });
        wf["vvae"] = Node("VAELoader", new JsonObject { ["vae_name"] = H3VideoVae });
        wf["vdev"] = Node("SelectVAEDevice", new JsonObject { ["vae"] = Link("vvae"), ["device"] = device });
        wf["vsplit"] = Node("H3MSVAESplitDecode", new JsonObject
        {
            ["vae"] = Link("vdev"),
            ["enabled"] = false,
            ["second_gpu"] = -1,
            ["gpus"] = Link("gpuset"),
            ["max_gpus"] = 1,
        });
        wf["avae"] = Node("VAELoader", new JsonObject { ["vae_name"] = H3AudioVae });
        wf["adev"] = Node("SelectVAEDevice", new JsonObject { ["vae"] = Link("avae"), ["device"] = device });
        return wf;
    }

    // Загрузчики ref2va без нод MultiStream — как в эталоне инпейнта
    private static JsonObject Ref2vLoaders() => new()
    {
        ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = H3Ref2va, ["weight_dtype"] = "default" }),
        ["lora"] = Node("LoraLoaderModelOnly", new JsonObject
        {
            ["model"] = Link("unet"),
            ["lora_name"] = H3LoraRef2v4,
            ["strength_model"] = 1.0,
        }),
        ["vvae"] = Node("VAELoader", new JsonObject { ["vae_name"] = H3VideoVae }),
        ["avae"] = Node("VAELoader", new JsonObject { ["vae_name"] = H3AudioVae }),
        ["clip"] = H3ClipLoader(),
    };

    private static JsonObject H3ClipLoader() => Node("CLIPLoader", new JsonObject
    {
        ["clip_name"] = H3TextEncoder,
        ["type"] = "minimax",
        ["device"] = "default",
    });

    private static JsonObject H3Conditioning(string prompt, int width, int height, int length) => new()
    {
        ["clip"] = Link("tec"),
        ["vae"] = Link("vsplit"),
        ["prompt"] = prompt,
        ["width"] = width,
        ["height"] = height,
        ["length"] = length,
    };

    private static JsonObject R2vConditioning(string prompt, int width, int height, int length, string refImageSize) => new()
    {
        ["clip"] = Link("clip"),
        ["vae"] = Link("vvae"),
        ["audio_vae"] = Link("avae"),
        ["prompt"] = prompt,
        ["width"] = width,
        ["height"] = height,
        ["length"] = length,
        ["ref_image_size"] = refImageSize,
    };

    // BasicGuider + (BasicScheduler | ManualSigmas) + SamplerCustomAdvanced, как в шаблонах H3
    private static void AddSampler(JsonObject wf, string model, JsonArray conditioning, JsonArray latent, int steps,
        long seed, string id = "sampler", string sampler = "res_multistep", string? sigmas = null)
    {
        wf[$"{id}_guider"] = Node("BasicGuider", new JsonObject { ["model"] = Link(model), ["conditioning"] = conditioning });
        wf[$"{id}_sched"] = sigmas is null
            ? Node("BasicScheduler", new JsonObject
            {
                ["model"] = Link(model),
                ["scheduler"] = "simple",
                ["steps"] = steps,
                ["denoise"] = 1.0,
            })
            : Node("ManualSigmas", new JsonObject { ["sigmas"] = sigmas });
        wf[$"{id}_ksel"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = sampler });
        wf[$"{id}_noise"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed });
        wf[id] = Node("SamplerCustomAdvanced", new JsonObject
        {
            ["noise"] = Link($"{id}_noise"),
            ["guider"] = Link($"{id}_guider"),
            ["sampler"] = Link($"{id}_ksel"),
            ["sigmas"] = Link($"{id}_sched"),
            ["latent_image"] = latent,
        });
    }

    private static void AddH3DecodeAndSave(JsonObject wf, string latent, string vae, string audioVae, string filenamePrefix)
    {
        wf["vdec"] = Node("VAEDecode", new JsonObject { ["samples"] = Link(latent), ["vae"] = Link(vae) });
        wf["adec"] = Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link(latent), ["vae"] = Link(audioVae) });
        wf["video"] = Node("CreateVideo", new JsonObject
        {
            ["images"] = Link("vdec"),
            ["fps"] = VideoFps,
            ["audio"] = Link("adec"),
        });
        // format — dynamic combo: вложенный вход кодека в API-формате называется format.codec
        wf["save"] = Node("SaveVideo", new JsonObject
        {
            ["video"] = Link("video"),
            ["filename_prefix"] = filenamePrefix,
            ["format"] = "auto",
            ["format.codec"] = "auto",
        });
    }

    // AV-латент H3 — NestedTensor, SaveLatent пишет только обычный тензор, поэтому видео и
    // аудио ложатся двумя файлами: {latentPrefix}_video_00001_.latent и …_audio_…
    private static void AddLatentSave(JsonObject wf, string latentPrefix)
    {
        wf["lat_split"] = Node("LTXVSeparateAVLatent", new JsonObject { ["av_latent"] = Link("sampler") });
        wf["lat_save_v"] = Node("SaveLatent", new JsonObject
        {
            ["samples"] = Link("lat_split"),
            ["filename_prefix"] = latentPrefix + "_video",
        });
        wf["lat_save_a"] = Node("SaveLatent", new JsonObject
        {
            ["samples"] = Link("lat_split", 1),
            ["filename_prefix"] = latentPrefix + "_audio",
        });
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
