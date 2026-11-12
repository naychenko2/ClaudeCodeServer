using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Fibo 2.0 от Bria (решение Андрея 2026-11-12). Эталоны — шаблоны ComfyUI 0.45.2
// image_fibo2_edit_{dev,turbo}_pe.json и карточка briaai/Fibo-2.0-dev: подграфы развёрнуты,
// у правки расширитель промпта (TextGenerateV2 в VGL-JSON) выключен — на замере 2026-11-12 он добавлял
// 30–35 с, а правку модель и без него держит. Веса — репак Comfy-Org
// int8_convrot; энкодер и VAE — те же файлы, что у Klein.
//
// Turbo — дистиллят Dev на 4 шага без CFG. Dev с Ш8 (2026-11-12) идёт через турбо-LoRA Comfy-Org: 8 шагов,
// CFG 1, без негатива (у правки негатив и так пустой — это null-подпись обучения). Замер Ш5: качество Dev
// (50 шагов, CFG 5) за 5 с против 52,5 с; правка — 10–15 с против ~120 с, проверка на трёх правках 12.11 без
// потерь. Генерация Dev по тексту, кроме того, идёт через расширитель VGL: запрос человека превращается в JSON-описание
// сцены (свет, ракурс, объекты, стиль отдельными полями), оно уходит в историю ComfyUI узлом PreviewAny и
// становится памятью версии — по нему доводка перерисовывает картинку с тем же сидом.
// Узел условий TextEncodeFibo2 сам кодирует текст и картинки и отдаёт латент результата.
// Лицензия весов — CC BY-NC 4.0: только некоммерческое использование.
public static partial class ComfyWorkflows
{
    public const int Fibo2TurboSteps = 4;
    public const int Fibo2LoraSteps = 8;
    public const double Fibo2LoraStrength = 1.0;
    // JSON-описание длиннее — не наш формат: у расширителя потолок 3000–4096 токенов
    public const int MaxVglLength = 30000;

    // Турбо-LoRA Dev (Comfy-Org/Fibo-2.0, loras/, sha256 ce149058…d72ac0), лежит в ~/ai-data/image-extra/loras
    internal const string Fibo2TurboLora = "fibo_2.0_turbo_lora_avg_rank_270_bf16.safetensors";
    // Расширитель VGL — Qwen3-VL 8B (энкодер модели — 4B: на нём JSON беднее, проверено на шаблоне ComfyUI)
    private const string Fibo2VglClip = "qwen3vl_8b_int8_convrot.safetensors";

    private const string Fibo2DevUnet = "fibo2_dev_int8_convrot.safetensors";
    private const string Fibo2TurboUnet = "fibo2_edit_turbo_int8_convrot.safetensors";
    private const string Fibo2Clip = "qwen3vl_4b_int8_convrot.safetensors";
    private const string Fibo2Vae = "flux2-vae.safetensors";

    public static bool IsFibo2(string? model) => model is LocalImageModels.Fibo2 or LocalImageModels.Fibo2Turbo;

    // Размеры ~1 Мп — у Fibo 2.0 (выше промпт «утекает» в угол, заметка шаблона), Krea 2 Turbo и
    // MOSS-Image (шаблоны: ResolutionSelector на 1 Мп). Значения — trained_size узла TextEncodeFibo2
    // (кратно 16, в пределах 10 % от 1 Мп). Зеркало — LocalImageEditor.OneMegapixelSizes (сверка в тестах)
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> OneMegapixelSizes =
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

    // Генерация по тексту; count > 1 — пачкой (batch_size узла условий). Dev — расширитель VGL и турбо-LoRA;
    // Turbo (дистиллят правки) — прежний граф без расширителя
    public static JsonObject Fibo2Generate(string prompt, int width, int height, long seed, int count, string filenamePrefix,
        bool turbo = false)
    {
        var wf = Fibo2Loaders(turbo);
        if (turbo)
        {
            wf["cond"] = Fibo2Cond(prompt, width, height, count);
            AddFibo2Sampler(wf, seed, Fibo2Mode.Turbo);
            AddDecodeAndSave(wf, filenamePrefix);
            return wf;
        }
        AddFibo2Lora(wf);
        // Расширитель bria-vgl: температура 0,3 и выборка шаблона ComfyUI; сид выборки — сид картинки
        AddVglWriter(wf, Fibo2VglGenerateFinal.Replace("{user_prompt}", prompt), Fibo2VglGenerateSystem, 3000, new JsonObject
        {
            ["sampling_mode"] = "on",
            ["sampling_mode.temperature"] = 0.3,
            ["sampling_mode.top_k"] = 20,
            ["sampling_mode.top_p"] = 0.95,
            ["sampling_mode.min_p"] = 0.05,
            ["sampling_mode.repetition_penalty"] = 1.05,
            ["sampling_mode.presence_penalty"] = 0.0,
            ["sampling_mode.seed"] = seed,
        });
        wf["cond"] = Fibo2Cond(Link("vgl"), width, height, count);
        AddFibo2Sampler(wf, seed, Fibo2Mode.Lora);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Рендер готового JSON-описания (VGL) без расширителя: тот же JSON и сид дают ту же картинку
    public static JsonObject Fibo2Render(string vgl, int width, int height, long seed, string filenamePrefix)
    {
        var wf = Fibo2Loaders(false);
        AddFibo2Lora(wf);
        wf["cond"] = Fibo2Cond(vgl, width, height, 1);
        AddFibo2Sampler(wf, seed, Fibo2Mode.Lora);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Доводка: расширитель переписывает JSON под одно изменение (aspect — что меняем, value — на что),
    // согласуя с ним описание сцены и не трогая остальное; выборка жадная, сид картинки — прежний.
    // Новый JSON уходит в историю (PreviewAny) и становится памятью новой версии
    public static JsonObject Fibo2Refine(string vgl, string aspect, string value, int width, int height, long seed,
        string filenamePrefix)
    {
        var wf = Fibo2Loaders(false);
        AddFibo2Lora(wf);
        AddVglWriter(wf, $"Current JSON:\n{vgl}\n\nChange: {aspect} -> {value}", Fibo2VglRefineSystem, 4096,
            new JsonObject { ["sampling_mode"] = "off" });
        wf["cond"] = Fibo2Cond(Link("vgl"), width, height, 1);
        AddFibo2Sampler(wf, seed, Fibo2Mode.Lora);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    private static JsonObject Fibo2Cond(JsonNode prompt, int width, int height, int count) =>
        Node("TextEncodeFibo2", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = prompt,
            ["negative_prompt"] = "",
            ["width"] = width,
            ["height"] = height,
            ["batch_size"] = Math.Max(1, count),
        });

    // Расширитель VGL на Qwen3-VL 8B: выход — JSON-строка в узел условий и в историю (PreviewAny)
    private static void AddVglWriter(JsonObject wf, string prompt, string system, int maxTokens, JsonObject sampling)
    {
        wf["vgl_clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = Fibo2VglClip, ["type"] = "fibo2", ["device"] = "default" });
        var inputs = new JsonObject
        {
            ["clip"] = Link("vgl_clip"),
            ["prompt"] = prompt,
            ["max_length"] = maxTokens,
        };
        foreach (var (key, val) in sampling) inputs[key] = val?.DeepClone();
        inputs["thinking"] = false;
        inputs["use_default_template"] = true;
        inputs["mtp"] = "off";
        inputs["system_prompt"] = system;
        wf["vgl"] = Node("TextGenerateV2", inputs);
        wf["vgl_view"] = Node("PreviewAny", new JsonObject { ["source"] = Link("vgl") });
    }

    private static void AddFibo2Lora(JsonObject wf) =>
        wf["lora"] = Node("LoraLoaderModelOnly", new JsonObject
        {
            ["model"] = Link("unet"),
            ["lora_name"] = Fibo2TurboLora,
            ["strength_model"] = Fibo2LoraStrength,
        });

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
        if (!turbo) AddFibo2Lora(wf);
        AddFibo2Sampler(wf, seed, turbo ? Fibo2Mode.Turbo : Fibo2Mode.Lora);
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

    // Turbo — дистиллят правки на 4 шага; Lora — Dev с турбо-LoRA на 8 шагов
    private enum Fibo2Mode { Turbo, Lora }

    // Негатив и латент — второй и третий выходы узла условий
    private static void AddFibo2Sampler(JsonObject wf, long seed, Fibo2Mode mode) =>
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link(mode == Fibo2Mode.Lora ? "lora" : "unet"),
            ["positive"] = Link("cond"),
            ["negative"] = Link("cond", 1),
            ["latent_image"] = Link("cond", 2),
            ["seed"] = seed,
            ["steps"] = mode == Fibo2Mode.Turbo ? Fibo2TurboSteps : Fibo2LoraSteps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
}
