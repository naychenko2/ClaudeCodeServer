using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// FLUX 3 Image Klein (решение Андрея 2026-10-22, A/B против Qwen-Image 2.1). Эталоны — официальные
// графы BFL flux3_klein_int8_{t2i,i2i,mi2i}.json (~/llm/flashnext-logs/media-ab/klein-ab/workflows)
// и их API-вариант в драйвере A/B klein_ab.py; подграфы развёрнуты, ветки разметки рамками
// (Flux3LayoutPrompt) выключены, как в эталоне по умолчанию.
//
// Апсемплер промпта обязателен: без него Klein теряет смысл русских промптов (ёжик выходит
// хомяком). TextGenerate стоит прямо в графе, его ответ идёт в CLIPTextEncode, а PreviewAny
// оставляет подпись в истории ComfyUI.
public static partial class ComfyWorkflows
{
    public const int KleinSteps = 16;

    // Потолок холста правки — как у узла «Output size» эталона (ImageScaleToTotalPixels, 4 Мп)
    public const double KleinMaxMegapixels = 4.0;

    private const string KleinUnet = "flux3-image-klein-int8-convrot.safetensors";
    private const string KleinClip = "qwen3vl_4b_int8_convrot.safetensors";
    private const string KleinVae = "flux2-vae.safetensors";
    private const string KleinUpsampler = "qwen3.5_9b_flux3_prompt_upsampler_int8_convrot.safetensors";

    // alpha эталона — 16, сдвиг ModelSamplingFlux — log(alpha) (ComfyMathExpression «log(a)»)
    private static readonly double KleinShift = Math.Log(16);

    // Обёртка запроса правки — f_string узла StringFormat эталона i2i
    public const string KleinEditRequestFormat =
        "Reference images follow in order (the first image, the second image, ...).\nUser request: {a}\nOutput the caption.";

    public static bool IsKlein(string? model) => model == LocalImageModels.Klein;

    // Генерация по тексту: апсемплер с системным промптом t2i и пустой латент Flux2 на count картинок
    public static JsonObject KleinGenerate(string prompt, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = KleinLoaders();
        wf["ups"] = KleinUpsample(prompt, image: null, seed, presencePenalty: 1.5, KleinSystemT2I);
        AddKleinDiffusion(wf, width, height, seed, count);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Правка и генерация по образцам: первая картинка — основная, все идут и апсемплеру
    // (BatchImagesNode), и в ReferenceMultiLatent (каждая ужимается до 1 Мп). Холст — canvas,
    // кратно 16: у правки — размер исходника (KleinCanvas), у генерации — из таблицы пропорций
    public static JsonObject KleinEdit(string prompt, IReadOnlyList<string> images, (int Width, int Height) canvas,
        long seed, string filenamePrefix)
    {
        if (images.Count is < 1 or > LocalImageModels.KleinMaxImages)
            throw new ArgumentOutOfRangeException(nameof(images), $"Нужно от 1 до {LocalImageModels.KleinMaxImages} картинок");

        var wf = KleinLoaders();
        var batch = new JsonObject();
        var refs = new JsonObject
        {
            ["positive"] = Link("pos"),
            ["negative"] = Link("neg"),
            ["vae"] = Link("vae"),
            ["resolution"] = "resize",
            ["resolution.megapixels"] = 1.0,
        };
        for (var k = 0; k < images.Count; k++)
        {
            wf[$"img{k + 1}"] = Node("LoadImage", new JsonObject { ["image"] = images[k] });
            batch[$"images.image{k}"] = Link($"img{k + 1}");
            refs[$"images.image{k}"] = Link($"img{k + 1}");
        }
        wf["ups_imgs"] = Node("BatchImagesNode", batch);
        wf["ups_fmt"] = Node("StringFormat", new JsonObject
        {
            ["values.a"] = prompt,
            ["f_string"] = KleinEditRequestFormat,
        });
        wf["ups"] = KleinUpsample(Link("ups_fmt"), Link("ups_imgs"), seed, presencePenalty: 0, KleinSystemI2I);
        AddKleinDiffusion(wf, canvas.Width, canvas.Height, seed, 1);
        wf["ref"] = Node("ReferenceMultiLatent", refs);
        wf["ks"]!["inputs"]!["positive"] = Link("ref");
        wf["ks"]!["inputs"]!["negative"] = Link("ref", 1);
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Холст правки по размеру исходника: стороны кратны 16, площадь не больше KleinMaxMegapixels
    public static (int Width, int Height) KleinCanvas(int width, int height)
    {
        var scale = Math.Min(1.0, Math.Sqrt(KleinMaxMegapixels * 1_000_000 / ((double)width * height)));
        return (Snap(width * scale), Snap(height * scale));

        // Вниз, чтобы потолок площади держался и после округления
        static int Snap(double side) => Math.Max(16, (int)Math.Floor(side / 16) * 16);
    }

    private static JsonObject KleinLoaders() => new()
    {
        ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = KleinUnet, ["weight_dtype"] = "default" }),
        ["ms"] = Node("ModelSamplingFlux", new JsonObject
        {
            ["model"] = Link("unet"),
            ["max_shift"] = KleinShift,
            ["base_shift"] = KleinShift,
            ["width"] = 1024,
            ["height"] = 1024,
        }),
        ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = KleinClip, ["type"] = "flux3", ["device"] = "default" }),
        ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = KleinVae }),
        ["ups_clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = KleinUpsampler, ["type"] = "flux3", ["device"] = "default" }),
    };

    // TextGenerate эталона: жадная выборка (top_k 1), presence_penalty 1.5 у t2i и 0 у правки.
    // Сид — тот же, что у диффузии: повтор с тем же сидом даёт ту же подпись
    private static JsonObject KleinUpsample(JsonNode prompt, JsonArray? image, long seed, double presencePenalty, string systemPrompt)
    {
        var inputs = new JsonObject
        {
            ["clip"] = Link("ups_clip"),
            ["prompt"] = prompt,
            ["max_length"] = 3584,
            ["sampling_mode"] = "on",
            ["sampling_mode.temperature"] = 1.0,
            ["sampling_mode.top_k"] = 1,
            ["sampling_mode.top_p"] = 0.95,
            ["sampling_mode.min_p"] = 0.0,
            ["sampling_mode.repetition_penalty"] = 1.0,
            ["sampling_mode.seed"] = seed,
            ["sampling_mode.presence_penalty"] = presencePenalty,
            ["thinking"] = false,
            ["use_default_template"] = true,
            ["mtp"] = "off",
            ["system_prompt"] = systemPrompt,
        };
        if (image is not null) inputs["image"] = image;
        return Node("TextGenerate", inputs);
    }

    private static void AddKleinDiffusion(JsonObject wf, int width, int height, long seed, int count)
    {
        wf["ups_view"] = Node("PreviewAny", new JsonObject { ["source"] = Link("ups") });
        wf["pos"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Link("clip"), ["text"] = Link("ups") });
        wf["neg"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Link("clip"), ["text"] = "" });
        wf["lat"] = Node("EmptyFlux2LatentImage", new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["batch_size"] = count,
        });
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("ms"),
            ["positive"] = Link("pos"),
            ["negative"] = Link("neg"),
            ["latent_image"] = Link("lat"),
            ["seed"] = seed,
            ["steps"] = KleinSteps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
    }

    // Системные промпты апсемплера — дословно из узлов PrimitiveStringMultiline эталонов BFL
    // (ветка без рамок): t2i — у генерации, i2i — у правки и мультиреференса
    public const string KleinSystemT2I = """
        You are the prompt upsampler of a text-to-image model. Expand the user's short request into ONE detailed, self-contained caption: the exact text the image model receives. Output only the caption, with no greetings, explanations, refusals, questions, Markdown, headings, JSON, coordinates, ids or angle-bracket tokens, and no quotes around it. If the input is not clearly an image request, caption a visualization of its concept. The request may begin with a "TARGET CANVAS: W:H" line: compose for that canvas (wide spreads content sideways, tall stacks it) and never mention the ratio or the canvas.

        Fidelity and planning: keep every explicit subject, attribute, count, spatial constraint, style cue and piece of wording; never contradict, substitute, drop or narrow the request, and keep an abstract concept abstract. Decide everything left open as ONE concrete, consistent plan; never offer alternatives or hedge. Never re-express a stated constraint through another mechanism; add camera optics (depth of field, bokeh, blur) only when asked for or implied. Without a stated style, plan an actual photograph and open with the medium convention ("Full-color outdoor photograph, ...", "Full-color indoor photograph, ...", "Studio photograph, ...", "Close-up photograph, ..."); keep "Photorealistic" for renders, and turn "photorealistic" or "realistic" requests for photos into the photograph convention. Give a photograph a few ordinary, non-ideal specifics drawn from the scene's own objects instead of the stock-photo ideal. Do not invent names, brands, logos or celebrities; keep user-named ones. A long request keeps its content but not its wording: drop appraisal and sales vocabulary, turn moods into observable physical facts, and add only new concrete content with positions. Time of day is lighting, not rendered text.

        Caption format: one continuous prose block in the generative voice, never "this image shows", "we can see" or "is visible". Lead with medium and style, then scene, lighting and camera, then the subjects with concrete colors, materials and state. Every person in focus gets an observable profile: ethnicity, gender, age range, skin tone ("fair skin", "brown skin"), hair and clothing. Make the layout reconstructable from words: frame-relative positions (frame-left, frame-right, upper, lower, foreground, midground, background), sentence-initial anchors ("On the left, ...", "In the background, ..."), relations (holds, faces, behind) and a consistent order; describe up to about ten instances one by one, beyond that give the exact count and arrangement. Out-of-focus or distant planes are shapes, colors and silhouettes, never identities or readable text, and the background takes about a sixth of the caption. Be definite: no hedging adverbs, no quality or emotion judgments, no closing sentence about mood; state negations as what is present. Rendered text: transcribe every wording verbatim inside double quotes in its original language and script, keep the line breaks of multi-line text, and give its typography, color and placement; author concrete, domain-correct wording when the request needs text it does not supply, and name canonical structures (calendar, keyboard, periodic table) exactly with their full layout. All prose is English; only quoted text keeps its language. Typical length is 100 to 350 words, more for complex scenes.

        Output only the caption.
        """;

    public const string KleinSystemI2I = """
        You are the prompt upsampler of an image-editing model. You see the reference image(s), attached in order (the first image, the second image, ...), and the user's short request. Expand it into ONE detailed, self-contained instruction: the caption the editing model receives together with the same images. Output only the instruction as one continuous English prose block, with no greetings, explanations, Markdown, JSON, coordinates, ids or angle-bracket tokens. Read a vague or conversational request as the most plausible edit of the supplied images.

        Pick the voice by task shape. When one reference stays and gets bounded changes, write an imperative editing instruction on it ("Remove the ...", "Change the ... to ...", "Zoom in on ..."); a whole-image transformation opens with its scope ("Convert the entire image into ...", "Apply a ... filter over the entire scene", "Horizontally flip the entire image so that ...", "Shift the camera viewpoint to ...", then where the major elements should now be). When the references are drawn into a newly composed frame (combining subjects, a new scene, a garment, object or style donor), write a generation request ("Create a photograph of ...", "Create an anime-style illustration of ...", "Generate a new image featuring ...") that says what to take from each image, by ordinal, and what is newly generated.

        Keep every explicit edit, constraint, count, position, style cue and piece of wording, and decide what is left open as one concrete plan. Ground everything in what is actually visible: identify each referent unambiguously by its real appearance, never mention content that is not there, and use every image the user attached. State every end state concretely (which color, how large, which pose, which material) and give new content enough appearance to be generated. Cover the knock-on effects of the change (shadows, reflections, occlusion, perspective, scale, lighting) and say what fills a removed region. A zoom-in names what stays in frame and which neighbors are cropped out; an outpainting continues the scene in the same medium. Describe the change, not the whole scene, and close an editing instruction with one compact clause naming what stays unchanged; transferred people keep their identity, face and accessories unless asked otherwise. Transcribe on-image text verbatim in its original language inside double quotes, with line breaks as \n, and match the original font, size and color when replacing it. Name recognizable public figures, brands and landmarks you can see. Directions are frame-relative; use no evaluative or quality words. Typical length is 50 to 150 words.

        Output only the instruction.
        """;
}
