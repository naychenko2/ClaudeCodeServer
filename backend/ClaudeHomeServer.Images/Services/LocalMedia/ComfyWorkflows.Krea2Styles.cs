using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Стили Krea 2 Turbo (Ш4 эпика 8b23149d, 2026-11-12). Эталоны — шаблоны ComfyUI
// image_krea2_turbo_t2i_int8.json (стилевая LoRA) и image_krea2_turbo_int8_image_style_reference.json
// («стиль по образцу» ostris). Основа та же, что у Krea2Generate: файлы моделей, 8 шагов, без CFG.
public static partial class ComfyWorkflows
{
    // ModelSamplingFlux шаблона «стиль по образцу»: max_shift 1,15, base_shift 0,5
    internal const double Krea2StyleRefMaxShift = 1.15;
    internal const double Krea2StyleRefBaseShift = 0.5;

    // Стилевая LoRA: узел LoraLoaderModelOnly между UNETLoader и сэмплером, триггер стиля — в конец
    // промпта через «, » (StringConcatenate шаблона). Стиль null — обычная генерация Krea2Generate
    public static JsonObject Krea2StyleGenerate(string prompt, Krea2Style? style, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = Krea2Generate(Krea2StylePrompt(prompt, style), width, height, seed, count, filenamePrefix);
        if (style is null) return wf;
        wf["lora"] = Node("LoraLoaderModelOnly", new JsonObject
        {
            ["model"] = Link("unet"),
            ["lora_name"] = style.LoraFile,
            ["strength_model"] = Krea2Styles.Strength,
        });
        wf["ks"]!["inputs"]!["model"] = Link("lora");
        return wf;
    }

    // Промпт со стилем: «<промпт>, <триггер>»; пустой промпт — один триггер
    internal static string Krea2StylePrompt(string prompt, Krea2Style? style)
    {
        if (style is null) return prompt;
        return string.IsNullOrWhiteSpace(prompt) ? style.Trigger : $"{prompt.TrimEnd()}, {style.Trigger}";
    }

    // «Стиль по образцу»: картинка уходит в TextEncodeQwenImageEditPlus (энкодер Krea видит её как
    // образец), метод опорных латентов index_timestep_zero, LoRA ostris на силе 1, ModelSamplingFlux,
    // CFGGuider с cfg 1 и негативом ConditioningZeroOut, 8 шагов euler/simple. Расширитель промпта
    // выключен (как и у обычной генерации Krea)
    public static JsonObject Krea2StyleReference(string prompt, string styleImage, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = Krea2TurboUnet, ["weight_dtype"] = "default" }),
            ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = Krea2Clip, ["type"] = "krea2", ["device"] = "default" }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = Krea2Vae }),
            ["img"] = Node("LoadImage", new JsonObject { ["image"] = styleImage }),
        };
        wf["lora"] = Node("LoraLoaderModelOnly", new JsonObject
        {
            ["model"] = Link("unet"),
            ["lora_name"] = Krea2Styles.StyleReferenceLora,
            ["strength_model"] = Krea2Styles.StyleReferenceStrength,
        });
        wf["sampling"] = Node("ModelSamplingFlux", new JsonObject
        {
            ["model"] = Link("lora"),
            ["max_shift"] = Krea2StyleRefMaxShift,
            ["base_shift"] = Krea2StyleRefBaseShift,
            ["width"] = width,
            ["height"] = height,
        });
        wf["enc"] = Node("TextEncodeQwenImageEditPlus", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = prompt,
            ["vae"] = Link("vae"),
            ["image1"] = Link("img"),
        });
        wf["pos"] = Node("FluxKontextMultiReferenceLatentMethod", new JsonObject
        {
            ["conditioning"] = Link("enc"),
            ["reference_latents_method"] = "index_timestep_zero",
        });
        wf["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("pos") });
        wf["guider"] = Node("CFGGuider", new JsonObject
        {
            ["model"] = Link("sampling"),
            ["positive"] = Link("pos"),
            ["negative"] = Link("neg"),
            ["cfg"] = 1.0,
        });
        wf["noise"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed });
        wf["sampler"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler" });
        wf["sigmas"] = Node("BasicScheduler", new JsonObject
        {
            ["model"] = Link("sampling"),
            ["scheduler"] = "simple",
            ["steps"] = Krea2TurboSteps,
            ["denoise"] = 1.0,
        });
        wf["lat"] = Node("EmptyLatentImage", new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = Math.Max(1, count) });
        wf["ks"] = Node("SamplerCustomAdvanced", new JsonObject
        {
            ["noise"] = Link("noise"),
            ["guider"] = Link("guider"),
            ["sampler"] = Link("sampler"),
            ["sigmas"] = Link("sigmas"),
            ["latent_image"] = Link("lat"),
        });
        // SamplerCustomAdvanced отдаёт два латента: 0 — output, 1 — denoised_output (шаблон берёт первый)
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }
}
