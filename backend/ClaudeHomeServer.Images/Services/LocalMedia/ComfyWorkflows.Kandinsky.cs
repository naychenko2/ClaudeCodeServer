using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Kandinsky 6 Lite (distill, 5 с) — вторая локальная видеомодель, экспериментальная (Ш5, 2026-11-13). Эталон — граф
// ночного замера media-bench (блок V, ~/media-bench/bin/mb.py::graph_k6) на ноде kandinsky6: текст или текст плюс
// один кадр (ссылочный латент), видео и звук 44,1 кГц одним прогоном. Кадр B, длительность кроме 5 с, портрет
// и разрешение выше 480p не проверены и не даются: веса «Lite-distill-5s», проверено только 864×480.
public static partial class ComfyWorkflows
{
    public const string Kandinsky6UnetName =
        "Kandinsky-6.0-Lite-distill-5s-Diffusers/transformer/diffusion_pytorch_model.safetensors";

    // 121 кадр = 4·30+1 при 24 к/с — ровно 5 с (условие ноды: 4n+1 кадров)
    public const int Kandinsky6Frames = 121;
    public const int Kandinsky6Seconds = 5;
    public static readonly (int Width, int Height) Kandinsky6Size = (864, 480);

    // Негатив из шаблона ноды и ночного замера
    private const string Kandinsky6Negative =
        "Static, 2D cartoon, cartoon, 2d animation, paintings, images, worst quality, low quality, ugly, deformed, walking backwards";

    // Описание звука по умолчанию: поле сцены одно, а узел просит отдельный «аудио-промпт» (пустой — «звука нет»).
    // Реплики человек пишет в самом тексте сцены между <S>…<E>
    public const string Kandinsky6AudioCaption = "Natural sound that matches the scene: ambient noise and voices if someone speaks.";

    // firstFrame — имя картинки в input ComfyUI; null — съёмка по тексту. Звук есть всегда, как у H3
    public static JsonObject Kandinsky6Video(string prompt, string? firstFrame, long seed, string filenamePrefix)
    {
        var (width, height) = Kandinsky6Size;
        var wf = new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = Kandinsky6UnetName, ["weight_dtype"] = "default" }),
            ["clip"] = Node("DualCLIPLoader", new JsonObject
            {
                ["clip_name1"] = "qwen_2.5_vl_7b.safetensors",
                ["clip_name2"] = "clip_l.safetensors",
                ["type"] = "kandinsky5",
                ["device"] = "default",
            }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = "hunyuan_video_vae_bf16.safetensors" }),
            ["avae"] = Node("Kandinsky6AudioVAELoader", new JsonObject
            {
                ["tod_vae"] = "v1-44.pth",
                ["bigvgan_dir"] = "bigvgan_vocoder",
                ["mode"] = "44k",
                ["scaling_factor"] = 0.417,
            }),
            ["pos"] = Node("Kandinsky6TextEncode", new JsonObject
            {
                ["clip"] = Link("clip"),
                ["video_caption"] = prompt,
                ["audio_caption"] = Kandinsky6AudioCaption,
            }),
            ["neg"] = Node("Kandinsky6TextEncode", new JsonObject
            {
                ["clip"] = Link("clip"),
                ["video_caption"] = Kandinsky6Negative,
                ["audio_caption"] = "",
            }),
            ["lat"] = Node("Kandinsky6EmptyLatent", new JsonObject
            {
                ["width"] = width,
                ["height"] = height,
                ["length"] = Kandinsky6Frames,
                ["fps"] = (double)VideoFps,
                ["batch_size"] = 1,
            }),
        };

        var positive = Link("pos");
        var negative = Link("neg");
        var latent = Link("lat");
        if (firstFrame is not null)
        {
            wf["img"] = Node("LoadImage", new JsonObject { ["image"] = firstFrame });
            // Кадр приводится к 864×480 с обрезкой по центру: пропорции ролика не следуют за кадром
            wf["isc"] = Node("ImageScale", new JsonObject
            {
                ["image"] = Link("img"),
                ["upscale_method"] = "bilinear",
                ["width"] = width,
                ["height"] = height,
                ["crop"] = "center",
            });
            wf["ienc"] = Node("VAEEncode", new JsonObject { ["pixels"] = Link("isc"), ["vae"] = Link("vae") });
            wf["i2v"] = Node("Kandinsky6ImageToVideoAudio", new JsonObject
            {
                ["positive"] = positive,
                ["negative"] = negative,
                ["empty_latent"] = latent,
                ["reference_latent"] = Link("ienc"),
            });
            positive = Link("i2v", 0);
            negative = Link("i2v", 1);
            latent = Link("i2v", 2);
        }

        wf["smp"] = Node("Kandinsky6Sampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["seed"] = seed,
            ["steps"] = 10,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["positive"] = positive,
            ["negative"] = negative,
            ["latent_image"] = latent,
            ["denoise"] = 1.0,
        });
        var joint = Link("smp");
        if (firstFrame is not null)
        {
            wf["rr"] = Node("Kandinsky6RemoveReferenceLatent", new JsonObject { ["joint_latent"] = Link("smp") });
            joint = Link("rr");
        }

        wf["vdec"] = Node("VAEDecode", new JsonObject { ["samples"] = joint, ["vae"] = Link("vae") });
        wf["adec"] = Node("Kandinsky6AudioVAEDecode", new JsonObject { ["audio_vae"] = Link("avae"), ["joint_latent"] = joint });
        wf["video"] = Node("CreateVideo", new JsonObject { ["images"] = Link("vdec"), ["fps"] = VideoFps, ["audio"] = Link("adec") });
        wf["save"] = Node("SaveVideo", new JsonObject
        {
            ["video"] = Link("video"),
            ["filename_prefix"] = filenamePrefix,
            ["format"] = "auto",
            ["format.codec"] = "auto",
        });
        return wf;
    }
}
