using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Fibo 2.0 от Bria (решение Андрея 2026-11-12). Эталоны — шаблоны ComfyUI 0.45.2
// image_fibo2_edit_{dev,turbo}_pe.json и карточка briaai/Fibo-2.0-dev: подграфы развёрнуты,
// расширитель промпта (TextGenerateV2 в VGL-JSON) выключен — на замере 2026-11-12 он добавлял
// 30–35 с, а кириллицу на картинке и правку модель и без него держит. Веса — репак Comfy-Org
// int8_convrot; энкодер и VAE — те же файлы, что у Klein.
//
// Dev — 50 шагов с CFG 5 (негатив генерации — VGL-подпись «мыльной» картинки из карточки модели,
// у правки — пустой, это null-подпись обучения). Turbo — дистиллят Dev на 4 шага без CFG.
// Узел условий TextEncodeFibo2 сам кодирует текст и картинки и отдаёт латент результата.
// Лицензия весов — CC BY-NC 4.0: только некоммерческое использование.
public static partial class ComfyWorkflows
{
    public const int Fibo2DevSteps = 50;
    public const double Fibo2DevCfg = 5.0;
    public const int Fibo2TurboSteps = 4;

    private const string Fibo2DevUnet = "fibo2_dev_int8_convrot.safetensors";
    private const string Fibo2TurboUnet = "fibo2_edit_turbo_int8_convrot.safetensors";
    private const string Fibo2Clip = "qwen3vl_4b_int8_convrot.safetensors";
    private const string Fibo2Vae = "flux2-vae.safetensors";

    // Негатив генерации Dev — дословно из карточки briaai/Fibo-2.0-dev (NEGATIVE_T2I)
    internal const string Fibo2NegativeT2I =
        """{"short_description":"","objects":[{"description":"the main subject","texture":"smooth, featureless, plastic-like, airbrushed, no fine surface detail","appearance_details":"soft blurred edges, simplified shapes, no fine detail","skin_tone_and_texture":"waxy airbrushed skin, poreless, plastic smooth"}],"background_setting":"a plain seamless studio backdrop, perfectly smooth gradient, blurred and featureless, no visible texture","lighting":{},"aesthetics":{"artifacts":["oversaturated colours","neon colour cast","overexposed highlights","lens flare","slight blur","motion blur","haze"]},"photographic_characteristics":{"depth_of_field":"shallow, heavy bokeh obscuring most of the frame","focus":"soft, slightly out of focus, lacking fine texture"},"style_medium":"","context":"","artistic_style":""}""";

    public static bool IsFibo2(string? model) => model is LocalImageModels.Fibo2 or LocalImageModels.Fibo2Turbo;

    // Обучена на ~1 Мп: выше — промпт «утекает» в угол и послушность падает (заметка шаблона).
    // Размеры — trained_size узла TextEncodeFibo2 (кратно 16, в пределах 10 % от 1 Мп).
    // Зеркало — LocalImageEditor.Fibo2Sizes (сверка в тестах)
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> Fibo2Sizes =
        new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal)
        {
            ["1:1"] = (1024, 1024),
            ["16:9"] = (1424, 800),
            ["9:16"] = (800, 1424),
            ["4:3"] = (1152, 864),
            ["3:4"] = (864, 1152),
            ["3:2"] = (1248, 832),
            ["2:3"] = (832, 1248),
        };

    // Генерация по тексту; count > 1 — пачкой (batch_size узла условий)
    public static JsonObject Fibo2Generate(string prompt, int width, int height, long seed, int count, string filenamePrefix,
        bool turbo = false)
    {
        var wf = Fibo2Loaders(turbo);
        wf["cond"] = Node("TextEncodeFibo2", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = prompt,
            ["negative_prompt"] = turbo ? "" : Fibo2NegativeT2I,
            ["width"] = width,
            ["height"] = height,
            ["batch_size"] = Math.Max(1, count),
        });
        AddFibo2Sampler(wf, seed, turbo);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Правка и генерация по образцам: картинки идут в узел условий по порядку (<image_1> — холст
    // правки). canvas null — размер выбирает узел: обученный размер под пропорцию первой картинки
    // (одна картинка ужимается и режется по центру ровно под него). faceReferences — эталон
    // персонажа для узла сходства (CcsFaceSimilarity), в модель не идёт
    public static JsonObject Fibo2Edit(string prompt, IReadOnlyList<string> images, (int Width, int Height)? canvas,
        long seed, string filenamePrefix, bool turbo, IReadOnlyList<string>? faceReferences = null, int count = 1)
    {
        if (images.Count is < 1 or > LocalImageModels.Fibo2MaxImages)
            throw new ArgumentOutOfRangeException(nameof(images), $"Нужно от 1 до {LocalImageModels.Fibo2MaxImages} картинок");

        var wf = Fibo2Loaders(turbo);
        var cond = new JsonObject
        {
            ["clip"] = Link("clip"),
            ["vae"] = Link("vae"),
            ["prompt"] = Fibo2Prompt(prompt, images.Count),
            ["negative_prompt"] = "",
            ["width"] = canvas?.Width ?? 0,
            ["height"] = canvas?.Height ?? 0,
            ["batch_size"] = Math.Max(1, count),
        };
        for (var k = 0; k < images.Count; k++)
        {
            wf[$"img{k + 1}"] = Node("LoadImage", new JsonObject { ["image"] = images[k] });
            cond[$"images.image_{k + 1}"] = Link($"img{k + 1}");
        }
        wf["cond"] = Node("TextEncodeFibo2", cond);
        AddFibo2Sampler(wf, seed, turbo);
        AddDecodeAndSave(wf, filenamePrefix);
        if (faceReferences is { Count: > 0 })
            wf["sim"] = Node("CcsFaceSimilarity", new JsonObject
            {
                ["image"] = Link("dec"),
                ["references"] = string.Join("\n", faceReferences),
            });
        return wf;
    }

    // Модель адресует входные картинки метками <image_n> (карточка), а промпты редактора называют
    // их по номерам. Одну картинку узел берёт как холст без меток
    internal static string Fibo2Prompt(string prompt, int images)
    {
        if (images < 2) return prompt;
        var labels = string.Join(", ", Enumerable.Range(1, images).Select(n => $"картинка {n} = <image_{n}>"));
        return $"Входные картинки по порядку: {labels}.\n{prompt}";
    }

    private static JsonObject Fibo2Loaders(bool turbo) => new()
    {
        ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = turbo ? Fibo2TurboUnet : Fibo2DevUnet, ["weight_dtype"] = "default" }),
        ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = Fibo2Clip, ["type"] = "fibo2", ["device"] = "default" }),
        ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = Fibo2Vae }),
    };

    // Негатив и латент — второй и третий выходы узла условий
    private static void AddFibo2Sampler(JsonObject wf, long seed, bool turbo) =>
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("cond"),
            ["negative"] = Link("cond", 1),
            ["latent_image"] = Link("cond", 2),
            ["seed"] = seed,
            ["steps"] = turbo ? Fibo2TurboSteps : Fibo2DevSteps,
            ["cfg"] = turbo ? 1.0 : Fibo2DevCfg,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
}
