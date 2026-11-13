using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// «Увеличить» готовый ролик ×2 / ×4: RelayVSR на узлах ядра ComfyUI 0.45.2 (custom_nodes не нужны). Рабочий граф —
// раннер ночного замера ~/media-bench/bin/mb.py (graph_relay). Звук в граф не пускаем: RelayVSR его не переносит,
// адаптер возвращает дорожку исходника ffmpeg-ом уже после прогона. Веса — секция video_extra в extra_model_paths.yaml
public static partial class ComfyWorkflows
{
    public const string RelayVsrX2Unet = "relayvsr_x2_lightx2v_int8_convrot.safetensors";
    public const string RelayVsrX4Unet = "relayvsr_x4_int8_convrot.safetensors";

    public static bool IsRelayVsrScale(int scale) => scale is 2 or 4;

    // inputVideo — имя mp4 во input ComfyUI (с подпапкой: «ccs-local-media/<id>-enh.mp4»)
    public static JsonObject RelayVsr(string inputVideo, int scale, long seed, string filenamePrefix)
    {
        if (!IsRelayVsrScale(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "Увеличение — ×2 или ×4.");
        return new JsonObject
        {
            ["lv"] = Node("LoadVideo", new JsonObject { ["file"] = inputVideo }),
            ["comp"] = Node("GetVideoComponents", new JsonObject { ["video"] = Link("lv") }),
            ["unet"] = Node("UNETLoader", new JsonObject
            {
                ["unet_name"] = scale == 2 ? RelayVsrX2Unet : RelayVsrX4Unet,
                ["weight_dtype"] = "default",
            }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = $"relayvsr_flash_vae_x{scale}_fp32.safetensors" }),
            ["cond"] = Node("RelayVSRConditioning", new JsonObject { ["model"] = Link("unet"), ["images"] = Link("comp") }),
            ["ks"] = Node("KSampler", new JsonObject
            {
                ["model"] = Link("unet"),
                ["positive"] = Link("cond"),
                ["negative"] = Link("cond"),
                ["latent_image"] = Link("cond", 1),
                ["seed"] = seed,
                ["steps"] = 1,
                ["cfg"] = 1.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0,
            }),
            ["dec"] = Node("VSRDecode", new JsonObject
            {
                ["vae"] = Link("vae"),
                ["samples"] = Link("ks"),
                ["images"] = Link("comp"),
                ["detail"] = 1.0,
            }),
            ["vid"] = Node("CreateVideo", new JsonObject { ["images"] = Link("dec"), ["fps"] = Link("comp", 2) }),
            ["save"] = Node("SaveVideo", new JsonObject
            {
                ["video"] = Link("vid"),
                ["filename_prefix"] = filenamePrefix,
                ["format"] = "mp4",
                ["codec"] = "h264",
            }),
        };
    }
}
