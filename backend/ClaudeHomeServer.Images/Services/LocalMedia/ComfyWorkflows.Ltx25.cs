using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// LTX-2.5 (Lightricks, 22B, видео со звуком) — третья локальная видеомодель (решение Андрея 15.11.2026). Эталон —
// шаблоны ComfyUI 0.45.2 «Text to Video (LTX-2.5)» и «Image to Video (LTX-2.5)» (blueprints), замер 14.11 на 3090:
// distilled int8, два прохода — половинное разрешение (8 сигм), латентный апскейл ×2 и доводка (3 сигмы).
// Улучшатель промпта шаблона (Gemma 4 E2B) выключен: текст сцены идёт в кодировщик как есть.
// Проверено 1280×704 / 5 с: пик VRAM 23,9 ГБ на 24 ГБ, поэтому длиннее 5 с и крупнее 720p модель не даёт.
public static partial class ComfyWorkflows
{
    public const string Ltx25UnetName = "ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors";
    public const string Ltx25TextEncoder = "gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors";
    public const string Ltx25VideoVae = "ltx-2.5-video-vae-bf16.safetensors";
    public const string Ltx25AudioVae = "ltx-2.5-audio-vae-bf16.safetensors";
    public const string Ltx25SpatialUpscaler = "ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors";

    public const int Ltx25MinSeconds = 3;
    public const int Ltx25MaxSeconds = 5;

    // Размер ролика 720p: латент LTX кратен 32, поэтому 720 становится 704 (первый проход — половина, 640×352)
    public static readonly (int Width, int Height) Ltx25Landscape = (1280, 704);
    public static readonly (int Width, int Height) Ltx25Portrait = (704, 1280);

    // Негатив и сигмы — из шаблонов ComfyUI
    private const string Ltx25Negative = "pc game, console game, video game, cartoon, childish, ugly";
    private const string Ltx25FirstPassSigmas = "1.0, 0.99375, 0.9875, 0.98125, 0.975, 0.909375, 0.725, 0.421875, 0.0";
    private const string Ltx25SecondPassSigmas = "0.85, 0.7250, 0.4219, 0.0";

    // Второй проход в шаблоне идёт с постоянным зерном
    private const long Ltx25RefineSeed = 42;

    // Кадров 24·с+1 (ровно столько даёт шаблон: 121 на 5 с; LTX нужно 8n+1 — для 3–5 с выполняется)
    public static int Ltx25Frames(int seconds) => seconds * VideoFps + 1;

    public static (int Width, int Height) Ltx25Size(bool portrait) => portrait ? Ltx25Portrait : Ltx25Landscape;

    // firstFrame — имя картинки в input ComfyUI; null — съёмка по тексту. Звук есть всегда
    public static JsonObject Ltx25Video(string prompt, string? firstFrame, int seconds, bool portrait, long seed, string filenamePrefix)
    {
        var (width, height) = Ltx25Size(portrait);
        var frames = Ltx25Frames(seconds);
        var wf = new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = Ltx25UnetName, ["weight_dtype"] = "default" }),
            ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = Ltx25TextEncoder, ["type"] = "ltxv", ["device"] = "default" }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = Ltx25VideoVae }),
            ["avae"] = Node("VAELoader", new JsonObject { ["vae_name"] = Ltx25AudioVae }),
            ["ups"] = Node("LatentUpscaleModelLoader", new JsonObject { ["model_name"] = Ltx25SpatialUpscaler }),
            ["pos"] = Node("CLIPTextEncode", new JsonObject { ["text"] = prompt, ["clip"] = Link("clip") }),
            ["neg"] = Node("CLIPTextEncode", new JsonObject { ["text"] = Ltx25Negative, ["clip"] = Link("clip") }),
            ["cond"] = Node("LTXVConditioning", new JsonObject
            {
                ["frame_rate"] = (double)VideoFps,
                ["positive"] = Link("pos"),
                ["negative"] = Link("neg"),
            }),
            ["vlat"] = Node("EmptyLTXVLatentVideo", new JsonObject
            {
                ["width"] = width / 2,
                ["height"] = height / 2,
                ["length"] = frames,
                ["batch_size"] = 1,
            }),
            ["alat"] = Node("LTXVEmptyLatentAudio", new JsonObject
            {
                ["frames_number"] = frames,
                ["frame_rate"] = VideoFps,
                ["batch_size"] = 1,
                ["audio_vae"] = Link("avae"),
            }),
            ["g1"] = Guider(),
            ["g2"] = Guider(),
            ["s1"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler_ancestral" }),
            ["s2"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler_ancestral" }),
            ["sg1"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = Ltx25FirstPassSigmas }),
            ["sg2"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = Ltx25SecondPassSigmas }),
            ["n1"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed }),
            ["n2"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = Ltx25RefineSeed }),
        };

        // Первый проход: латент видео (по кадру — с вписанной картинкой, сила 0,7) плюс пустой латент звука
        var video1 = "vlat";
        if (firstFrame is not null)
        {
            wf["img"] = Node("LoadImage", new JsonObject { ["image"] = firstFrame });
            wf["rsz"] = Node("ResizeImageMaskNode", new JsonObject
            {
                ["input"] = Link("img"),
                ["resize_type"] = "scale longer dimension",
                ["resize_type.longer_size"] = 1536,
                ["scale_method"] = "lanczos",
            });
            wf["pre"] = Node("LTXVPreprocess", new JsonObject { ["image"] = Link("rsz"), ["img_compression"] = 18 });
            wf["i2v1"] = Node("LTXVImgToVideoInplace", new JsonObject
            {
                ["vae"] = Link("vae"),
                ["image"] = Link("pre"),
                ["latent"] = Link("vlat"),
                ["strength"] = 0.7,
                ["bypass"] = false,
            });
            video1 = "i2v1";
        }
        wf["av1"] = Node("LTXVConcatAVLatent", new JsonObject { ["video_latent"] = Link(video1), ["audio_latent"] = Link("alat") });
        wf["run1"] = Node("SamplerCustomAdvanced", new JsonObject
        {
            ["noise"] = Link("n1"),
            ["guider"] = Link("g1"),
            ["sampler"] = Link("s1"),
            ["sigmas"] = Link("sg1"),
            ["latent_image"] = Link("av1"),
        });
        wf["sep1"] = Node("LTXVSeparateAVLatent", new JsonObject { ["av_latent"] = Link("run1") });

        // Апскейл латента видео ×2, по кадру — кадр вписывается ещё раз в полную силу; звук идёт дальше как есть
        wf["up"] = Node("LTXVLatentUpsampler", new JsonObject
        {
            ["samples"] = Link("sep1", 0),
            ["upscale_model"] = Link("ups"),
            ["vae"] = Link("vae"),
        });
        var video2 = "up";
        if (firstFrame is not null)
        {
            wf["i2v2"] = Node("LTXVImgToVideoInplace", new JsonObject
            {
                ["vae"] = Link("vae"),
                ["image"] = Link("pre"),
                ["latent"] = Link("up"),
                ["strength"] = 1.0,
                ["bypass"] = false,
            });
            video2 = "i2v2";
        }
        wf["av2"] = Node("LTXVConcatAVLatent", new JsonObject { ["video_latent"] = Link(video2), ["audio_latent"] = Link("sep1", 1) });
        wf["run2"] = Node("SamplerCustomAdvanced", new JsonObject
        {
            ["noise"] = Link("n2"),
            ["guider"] = Link("g2"),
            ["sampler"] = Link("s2"),
            ["sigmas"] = Link("sg2"),
            ["latent_image"] = Link("av2"),
        });
        wf["sep2"] = Node("LTXVSeparateAVLatent", new JsonObject { ["av_latent"] = Link("run2") });
        wf["vdec"] = Node("VAEDecodeTiled", new JsonObject
        {
            ["samples"] = Link("sep2", 0),
            ["vae"] = Link("vae"),
            ["tile_size"] = 512,
            ["overlap"] = 64,
            ["temporal_size"] = 64,
            ["temporal_overlap"] = 16,
        });
        wf["adec"] = Node("LTXVAudioVAEDecode", new JsonObject { ["samples"] = Link("sep2", 1), ["audio_vae"] = Link("avae") });
        wf["video"] = Node("CreateVideo", new JsonObject
        {
            ["images"] = Link("vdec"),
            ["fps"] = (double)VideoFps,
            ["audio"] = Link("adec"),
        });
        wf["save"] = Node("SaveVideo", new JsonObject
        {
            ["video"] = Link("video"),
            ["filename_prefix"] = filenamePrefix,
            ["format"] = "auto",
            ["format.codec"] = "auto",
        });
        return wf;

        static JsonObject Guider() => Node("LTXVDualCFGGuider", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("cond", 0),
            ["negative"] = Link("cond", 1),
            ["video_cfg"] = 1.0,
            ["audio_cfg"] = 1.0,
        });
    }
}
