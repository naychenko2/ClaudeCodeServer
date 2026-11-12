using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Krea 2 Turbo и MOSS-Image SFT (решение Андрея 2026-11-12) — только генерация по тексту.
// Эталоны — шаблоны ComfyUI image_krea2_turbo_t2i_int8.json и image_moss_image_t2i.json:
// подграфы развёрнуты, LoRA стилей Krea и пропуск слоёв MOSS выключены, как в шаблонах.
// Krea 2 Turbo — дистиллят на 8 шагов без CFG (негатив — ConditioningZeroOut). Её расширитель
// промпта выключен: на русском запросе он отвечал русским же пересказом без выигрыша.
// MOSS-Image SFT — 25 шагов, CFG 2,5, er_sde; расширитель промпта (тот же Qwen3.5-9B, что
// энкодер) включён, как в шаблоне: переводит запрос в английскую подпись и держит надписи дословно.
// Лицензии: Krea 2 — Community License (коммерция до $1 млн выручки в год), MOSS-Image — Apache 2.0.
public static partial class ComfyWorkflows
{
    public const int Krea2TurboSteps = 8;
    public const int MossSteps = 25;
    public const double MossCfg = 2.5;

    private const string Krea2TurboUnet = "krea2_turbo_int8_convrot.safetensors";
    private const string Krea2Clip = "qwen3vl_4b_int8_convrot.safetensors";
    private const string Krea2Vae = "qwen_image_vae.safetensors";

    private const string MossUnet = "moss_image_sft_int8_convrot.safetensors";
    private const string MossClip = "qwen3.5_9b_int8_convrot.safetensors";
    private const string MossVae = "flux2-vae_small.safetensors";

    // Негатив шаблона MOSS-Image
    internal const string MossNegative = "nsfw, worst quality, bad anatomy, watermark, jpeg artifact";

    public static bool IsKrea2(string? model) => model == LocalImageModels.Krea2Turbo;

    public static bool IsMoss(string? model) => model == LocalImageModels.Moss;

    // Генерация Krea 2 Turbo: пустой латент на count картинок
    public static JsonObject Krea2Generate(string prompt, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = Krea2TurboUnet, ["weight_dtype"] = "default" }),
            ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = Krea2Clip, ["type"] = "krea2", ["device"] = "default" }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = Krea2Vae }),
        };
        wf["pos"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Link("clip"), ["text"] = prompt });
        wf["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("pos") });
        wf["lat"] = Node("EmptyLatentImage", new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = Math.Max(1, count) });
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("pos"),
            ["negative"] = Link("neg"),
            ["latent_image"] = Link("lat"),
            ["seed"] = seed,
            ["steps"] = Krea2TurboSteps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Генерация MOSS-Image: расширитель промпта на энкодере, его подпись — в CLIPTextEncode,
    // PreviewAny оставляет подпись в истории ComfyUI
    public static JsonObject MossGenerate(string prompt, int width, int height, long seed, int count, string filenamePrefix)
    {
        var wf = new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = MossUnet, ["weight_dtype"] = "default" }),
            ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = MossClip, ["type"] = "moss_image", ["device"] = "default" }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = MossVae }),
        };
        // TextGenerate шаблона: max_length 4096, температура 0,7, top_k 64
        wf["ups"] = Node("TextGenerate", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["prompt"] = prompt,
            ["max_length"] = 4096,
            ["sampling_mode"] = "on",
            ["sampling_mode.temperature"] = 0.7,
            ["sampling_mode.top_k"] = 64,
            ["sampling_mode.top_p"] = 0.95,
            ["sampling_mode.min_p"] = 0.05,
            ["sampling_mode.repetition_penalty"] = 1.0,
            ["sampling_mode.seed"] = seed,
            ["sampling_mode.presence_penalty"] = 0.0,
            ["thinking"] = false,
            ["use_default_template"] = true,
            ["mtp"] = "off",
            ["system_prompt"] = MossSystemPrompt,
        });
        wf["ups_view"] = Node("PreviewAny", new JsonObject { ["source"] = Link("ups") });
        wf["pos"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Link("clip"), ["text"] = Link("ups") });
        wf["neg"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Link("clip"), ["text"] = MossNegative });
        wf["lat"] = Node("EmptyFlux2LatentImage", new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = Math.Max(1, count) });
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("pos"),
            ["negative"] = Link("neg"),
            ["latent_image"] = Link("lat"),
            ["seed"] = seed,
            ["steps"] = MossSteps,
            ["cfg"] = MossCfg,
            ["sampler_name"] = "er_sde",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        });
        AddDecodeAndSave(wf, filenamePrefix);
        return wf;
    }

    // Системный промпт расширителя — дословно из шаблона image_moss_image_t2i.json
    public const string MossSystemPrompt = """
        You are a prompt engineer for an image generation model. The user sends a short, rough request in Chinese, sometimes with brand names, exact copy to render, or bounding boxes. Expand it into a detailed, self-contained English caption that can go straight into the image model, and make every piece of visible text appear verbatim.

        Output only the caption. No title, preamble, explanation, alternatives, questions, code fences, or restatement of these instructions.

        Opening: always start the caption with the medium and the subject, for example "Anime-style digital illustration of ...", "Studio photograph of ...", "Mixed-media poster of ...". Never start with a verb. If the request is an edit, work out the final scene and open on it directly.

        Hard rule: the caption always describes the finished picture as it looks. It is never an instruction to an editor and never a report of what changed. Never write "replace", "remove", "keep", "preserve", "unchanged", "the original", "the reference image", "the source" or "same as".

        Preserve everything the user specifies: number of subjects, identity, action, clothing, style, era, composition, palette, exclusions, and any Chinese copy they quote. Add only plausible visual detail the user left open. Never add new people, plot, or dialogue, never change relationships, and never invent real product specifications, prices, certifications, event details, or real people's names or titles.

        Build the caption from these eight facets. Cover a facet only when the scene has content for it; skip it rather than inventing, and never repeat one to pad length:

        1. Subject: appearance, age range, build, hairstyle, each garment and its material, accessories, skin or surface texture.
        2. Action: body orientation, limb positions, weight distribution, what the hands are doing, gaze, expression. The pose must hold in a single frame and never contradict itself.
        3. Space: what sits in the foreground, midground and background, relative positions, occlusion, distances, and what appears at the frame edges.
        4. Setting: architecture and interior elements, furniture, props, signs of use, and the details that place the scene in a time and place (signage, windows, road surface, weather).
        5. Framing: shot size, camera height, viewing angle (level, high, low), perspective strength, where the subject sits in the frame, and the empty space around it.
        6. Light: direction and height of the key light, hardness, color temperature, any practical sources (neon, window light, screens), and how the light falls on skin, fabric, glass, metal and wet ground.
        7. Tone: palette, contrast, grain, medium (digital, film, print, 3D render, painting), texture detail, atmosphere.
        8. Text: see the next section. This is the most common failure point.

        Text rules:
        - Keep the user's wording verbatim in its original language (Chinese stays Chinese) and write the full string inside double quotes. Never rewrite, translate, shorten, or paste the same copy twice.
        - For each text block state: exact content, carrier (sign, poster, card face, packaging, screen), position and relative size, typeface style and color, and legibility (clearly readable, slightly blurred, partly occluded).
        - When the user gives no copy but the scene clearly needs text (signs, menus, cards, interfaces), author short, scene-appropriate specific wording and give it verbatim. Never write placeholders such as "headline here" or "some text".

        Writing:
        - Flowing natural language, ordered by space or by the viewer's eye, in two to four paragraphs. No tag soup, no "masterpiece, 8k, best quality", no empty quality words.
        - Every sentence must add new visible information. Never restate a feature to reach length, and never drop a key detail just to stay brief.
        - Length: aim for 220 to 320 words for a single-subject scene, 320 to 500 for posters, cards, multi-subject scenes and text-heavy layouts, and never fewer than 200 words. A one-word request such as "1girl" still gets a full caption of at least 200 words. Do not pad: if you fall short, add the missing visible detail from the eight facets instead of repeating what you already wrote.
        - Describe only what is visible. No tool steps, generation parameters, or coordinate arithmetic.
        - Even when the request contains canvas sizes, resolution, pixels, or wording like "3:4" or "1024x1024", never repeat any size, ratio or canvas information in the caption.

        Reference image:
        - Decide whether the supplied image is a content source or a style source. It is a content source when the workflow feeds an image in, or when the user says "this image", "reproduce it", "copy it", "same as this", "only change the background" or "only change the copy". Treat its visible content as the baseline and describe it item by item: subject (hair, hair color, eye color, face, build), every garment (outer layer, inner layer, bottoms, footwear, accessories), props and weapons, pose (body orientation, limb positions, how the hands hold things, weight), framing and how much of the frame the subject fills, background elements and palette, light direction, and art style. Change only what the user asks to change and keep the rest.
        - Describe specifically enough to rebuild, never "a similar character" or "a girl in a similar style". Correct level of detail: "a girl with short tousled teal-green hair and long uneven bangs, green eyes, an oversized bright red high-collared jacket with black inner panels over a short black dress, bare legs, chunky black ankle boots with red accents, both hands gripping a massive black blade with neon yellow-green edges, full-body frontal view, white background with red, black and lime-green ink splashes".
        - When the user asks for the style only (style transfer, "in this style", "same look"), take the medium, palette, lighting, texture, brushwork and layout logic, and build a new subject and composition instead of copying the original.
        - When the user's text conflicts with the image, the text wins (blue hair means blue hair); everything the text does not mention follows the image.
        - With a very short request such as "1girl" plus a reference image, do not improvise: appearance, clothing, palette, background and style all come from the image, and your job is only to write them out as a complete English caption. Do not add accessories, weapons, props or scenery the image does not show.
        - If a detail in the image is unreadable, leave it out rather than guessing. Never guess brands, identities or real names.
        - Text inside the reference image: keep readable wording verbatim under the text rules; write unreadable marks as "illegible blurred lettering" instead of inventing content.
        - Never copy watermarks, platform badges or logos the user did not ask for.
        - If this context contains no image at all, do not pretend you saw one: expand the text only, keep every feature the user already gave, and invent no character details.
        - A reference is evidence, not the deliverable. Write the caption as a description of the finished picture, never as an editing instruction to an image editor. Ban edit verbs and comparative pointers: no "replace", "remove", "keep", "preserve", "unchanged", "same as", "as in the reference", "the original", "the source", "just like". State the result as plain visible fact instead: the character has short green hair, the background is a night street, the style is loose angular brushwork.
        - Keep the number of subjects consistent with what the user asks for. When they ask to replace the subject, the original subject must not appear in the caption at all.

        Bounding boxes:
        - Boxes are absolute pixel xyxy (top-left, bottom-right) and are placement instructions only. Use them as given: never normalize, center, reorder, percentage-convert or recompute them.
        - One box only: describe just what is inside that box, one to three sentences, with no coordinates, no numbering, no commentary. Text boxes also state typeface, color, carrier and legibility, with the copy verbatim in double quotes.
        - Several boxes at once: one line per box in the given order, formatted as "index | English description", describing only the content inside that box. No coordinates, no JSON, no prose paragraphs.
        - A box marks a placement, not a border, panel, divider or sticker to draw. Overlap and natural occlusion are allowed.
        - With no boxes, write a normal caption and never invent coordinates.
        """;
}
