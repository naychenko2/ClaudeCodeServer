using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// MiniMax-H3-Image (решение Андрея 2026-11-11 после замера против Qwen Turbo). Эталоны — шаблоны
// ComfyUI 0.45.2 image_minimax_h3_image_{t2i,edit}.json: подграфы развёрнуты, расширитель промпта
// (TextGenerateV2 на Qwen3.5-9B) выключен, как в шаблоне генерации по умолчанию. Энкодер — тот же
// Qwen3-VL-32B nvfp4, что у видео H3 (шаблон по умолчанию берёт int8 на 27 ГБ; на 3090 рядом с
// DiT 21 ГБ не помещается без выгрузки).
//
// Модель дистиллирована до 6 шагов без CFG: негатив — ConditioningZeroOut положительного. Узел
// условий сам кодирует текст и картинки (до 16, в промпте — <Picture 1>…) и отдаёт латент холста.
public static partial class ComfyWorkflows
{
    public const int H3ImageSteps = 6;

    // Родная площадь модели — 2048² (подсказка узла и SGLang: «tuned for 2048x2048 pixels of area»).
    // Холст правки — исходник, приведённый к этой площади (шаблон правки: «scale total pixels» 4 Мп)
    public const double H3ImageCanvasMegapixels = 2048.0 * 2048 / 1_000_000;

    // Сторона холста кратна 32 (шаблон: «scale to multiple» 32)
    public const int H3ImageStep = 32;

    private const string H3ImageUnet = "minimax_h3_image_pruned_int8_convrot.safetensors";
    private const string H3ImageVae = "minimax_h3_image_vae_fp16.safetensors";

    public static bool IsH3Image(string? model) => model == LocalImageModels.H3Image;

    // Размеры генерации по пропорциям: таблица Qwen-Image (ImageSizes), приведённая к родной
    // площади H3 (H3ImageCanvas). Зеркало — LocalImageEditor.H3Sizes (сверка в тестах)
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> H3ImageSizes =
        ImageSizes.ToDictionary(kv => kv.Key, kv => H3ImageCanvas(kv.Value.Width, kv.Value.Height), StringComparer.Ordinal);

    // Генерация по тексту: латент нужного размера даёт сам узел условий; count > 1 — пачкой
    public static JsonObject H3ImageGenerate(string prompt, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = H3ImageLoaders();
        wf["cond"] = Node("MiniMaxH3ImageConditioning", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["vae"] = Link("vae"),
            ["prompt"] = prompt,
            ["width"] = width,
            ["height"] = height,
        });
        AddH3ImageSampler(wf, seed, count);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Правка и генерация по образцам: картинки идут в узел условий по порядку (<Picture 1> — холст
    // правки, дальше образцы). Холст — canvas (кратно 32): у правки — исходник, приведённый к
    // родной площади (H3ImageCanvas), у генерации — из таблицы размеров. faceReferences — эталон
    // персонажа для узла сходства (CcsFaceSimilarity), в модель не идёт
    public static JsonObject H3ImageEdit(string prompt, IReadOnlyList<string> images, (int Width, int Height) canvas,
        long seed, string filenamePrefix, IReadOnlyList<string>? faceReferences = null, int count = 1)
    {
        if (images.Count is < 1 or > LocalImageModels.H3ImageMaxImages)
            throw new ArgumentOutOfRangeException(nameof(images), $"Нужно от 1 до {LocalImageModels.H3ImageMaxImages} картинок");

        var wf = H3ImageLoaders();
        var cond = new JsonObject
        {
            ["clip"] = Link("clip"),
            ["vae"] = Link("vae"),
            ["prompt"] = H3ImagePrompt(prompt, images.Count),
            ["width"] = canvas.Width,
            ["height"] = canvas.Height,
        };
        for (var k = 0; k < images.Count; k++)
        {
            wf[$"img{k + 1}"] = Node("LoadImage", new JsonObject { ["image"] = images[k] });
            cond[$"images.image_{k + 1}"] = Link($"img{k + 1}");
        }
        wf["cond"] = Node("MiniMaxH3ImageConditioning", cond);
        AddH3ImageSampler(wf, seed, count);
        AddDecodeAndSave(wf, filenamePrefix);
        if (faceReferences is { Count: > 0 })
            wf["sim"] = Node("CcsFaceSimilarity", new JsonObject
            {
                ["image"] = Link("dec"),
                ["references"] = string.Join("\n", faceReferences),
            });
        return wf;
    }

    // Модель адресует входные картинки метками <Picture n>, а промпты редактора называют их по
    // номерам («1) исходная картинка…»). Строка-ключ связывает одно с другим
    internal static string H3ImagePrompt(string prompt, int images)
    {
        if (images == 0) return prompt;
        var labels = string.Join(", ", Enumerable.Range(1, images).Select(n => $"картинка {n} = <Picture {n}>"));
        return $"Входные картинки по порядку: {labels}.\n{prompt}";
    }

    // Холст правки по размеру исходника: площадь приводится к родной (и вверх, и вниз — так
    // шаблон), стороны кратны 32. Пропорция сохраняется с точностью до шага
    public static (int Width, int Height) H3ImageCanvas(int width, int height)
    {
        var scale = Math.Sqrt(H3ImageCanvasMegapixels * 1_000_000 / ((double)width * height));
        return (Snap(width * scale), Snap(height * scale));

        static int Snap(double side) => Math.Max(H3ImageStep, (int)Math.Round(side / H3ImageStep) * H3ImageStep);
    }

    private static JsonObject H3ImageLoaders() => new()
    {
        ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = H3ImageUnet, ["weight_dtype"] = "default" }),
        ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = H3TextEncoder, ["type"] = "minimax", ["device"] = "default" }),
        ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = H3ImageVae }),
    };

    private static void AddH3ImageSampler(JsonObject wf, long seed, int count)
    {
        wf["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("cond") });
        var latent = Link("cond", 1);
        if (count > 1)
        {
            wf["lat"] = Node("RepeatLatentBatch", new JsonObject { ["samples"] = Link("cond", 1), ["amount"] = count });
            latent = Link("lat");
        }
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("cond"),
            ["negative"] = Link("neg"),
            ["latent_image"] = latent,
            ["seed"] = seed,
            ["steps"] = H3ImageSteps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
    }
}
