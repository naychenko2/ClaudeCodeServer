using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;

namespace ClaudeHomeServer.Services.Mcp.Http;

// Аудио-инструменты local-media (ADR-020). Аргументы уходят в LocalMediaService как есть
// (LocalMediaRequest.Args), белые списки и пределы — там. Музыка и озвучка идут с тем же запретом
// «только по явной просьбе», что картинки и видео: у них есть облачные аналоги, а GPU общая.
// Обработка звука (стемы, MIDI, реставрация, смена голоса, распознавание) работает с файлами
// пользователя и облачной замены в чате не имеет — у неё запрета нет.
public sealed partial class LocalMediaToolset
{
    private static readonly Dictionary<string, string> AudioOps = new(StringComparer.Ordinal)
    {
        ["local_music_generate"] = LocalMediaOps.MusicGenerate,
        ["local_music_edit"] = LocalMediaOps.MusicEdit,
        ["local_speech"] = LocalMediaOps.Speech,
        ["local_voice_convert"] = LocalMediaOps.VoiceConvert,
        ["local_voice_train"] = LocalMediaOps.VoiceTrain,
        ["local_audio_separate"] = LocalMediaOps.AudioSeparate,
        ["local_audio_to_midi"] = LocalMediaOps.AudioToMidi,
        ["local_audio_enhance"] = LocalMediaOps.AudioEnhance,
        ["local_transcribe"] = LocalMediaOps.Transcribe,
    };

    // Первое предложение инструментов обработки звука
    internal const string AudioLocal =
        "НАША модель на своей GPU: бесплатно, но очередь общая с картинками и видео — задачи ждут друг друга. ";

    private const string AudioRefDescription =
        "Звук из проекта: путь WAV/MP3/FLAC/OGG от корня проекта или job_id завершённой задачи local_* этого проекта";

    private const string AudioTail = " Возвращает job_id; жди через local_jobs_wait, результат — в поле audio/files.";

    private static IEnumerable<McpToolSchema> AudioSchemas()
    {
        yield return Tool("local_music_generate",
            ExplicitOnly + "Сгенерировать песню или инструментал НАШЕЙ моделью на своей GPU. engine=ace — ACE-Step 1.5 XL "
            + "(по умолчанию; 50+ языков вокала, 3 мин ≈ 1,5 мин генерации), yue2 — YuE2-3B (разборчивее вокал, пишет "
            + "партитуру .abc, которую можно поправить и передать в abc; лицензия CC BY-NC — только некоммерческое "
            + "использование), minimax — MiniMax Music 3 (самый разборчивый вокал, но 3 мин ≈ 4,5 мин генерации). Слова размечай секциями [Verse], [Chorus], [Bridge]; без слов — "
            + "инструментал (только ace и minimax). Результат — mp3." + AudioTail,
            Obj(["prompt"], new JsonObject
            {
                ["prompt"] = Str("Стиль: жанр, настроение, инструменты, голос, темп — по-английски модели понимают лучше"),
                ["lyrics"] = Str("Слова песни с секциями [Verse], [Chorus]…; пусто — инструментал"),
                ["engine"] = Enum(LocalMediaService.MusicEngines, "ace (по умолчанию), yue2 или minimax"),
                ["duration_seconds"] = Int(ComfyWorkflows.MinMusicSeconds, ComfyWorkflows.MaxMusicSeconds,
                    "Длина, с (по умолчанию 120; yue2 и minimax могут закончить раньше)"),
                ["language"] = Str("Только ace: язык вокала кодом (ru, en, …), по умолчанию unknown"),
                ["bpm"] = Int(40, 220, "Только ace: темп (по умолчанию 120)"),
                ["key"] = Str("Только ace: тональность вида «A minor» или «F# major» (по умолчанию C major)"),
                ["abc"] = Str("Только yue2: партитура ABC прошлой генерации (файл .abc из результата), поправленная — "
                    + "мелодию и аккорды возьмём из неё"),
                ["seed"] = Seed(),
            }));

        yield return Tool("local_music_edit",
            AudioLocal + "Правка готового трека моделью ACE-Step 1.5: task=cover — переаранжировать в другом стиле "
            + "(prompt, strength — насколько держаться исходника), repaint — перегенерировать кусок start_seconds..end_seconds, "
            + "extract — вытащить одну дорожку (track), lego — дописать дорожку (track) поверх трека, complete — "
            + "доаранжировать инструментами (tracks). extract, lego и complete — тяжёлые: модель xl-base, одна задача за раз."
            + AudioTail,
            Obj(["audio", "task"], new JsonObject
            {
                ["audio"] = Str(AudioRefDescription),
                ["task"] = Enum(LocalMediaService.MusicEditTasks, "cover, repaint, extract, lego или complete"),
                ["prompt"] = Str("Для cover и repaint — стиль и содержание результата; для lego — характер новой дорожки"),
                ["lyrics"] = Str("Слова для перегенерируемого куска или кавера (необязательно)"),
                ["strength"] = Num(0, 1, "Только cover: 0 — свободно, 1 — близко к исходнику (по умолчанию 0,6)"),
                ["start_seconds"] = Num(0, 600, "Только repaint: начало куска, с"),
                ["end_seconds"] = Num(-1, 600, "Только repaint: конец куска, с; −1 — до конца трека"),
                ["track"] = Enum(LocalMediaService.AceTracks, "Для extract и lego: какая дорожка"),
                ["tracks"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Enum(LocalMediaService.AceTracks, "Дорожка"),
                    ["description"] = "Только complete: какими инструментами доаранжировать (по умолчанию drums, bass)",
                },
                ["seed"] = Seed(),
            }));

        yield return Tool("local_speech",
            ExplicitOnly + "Озвучить текст НАШЕЙ моделью на своей GPU. engine=qwen — Qwen3-TTS 1.7B (по умолчанию, 10 языков): "
            + "голос по описанию voice («тёплый баритон диктора радио, спокойно»), готовый диктор speaker или клон по "
            + "образцу reference (5–15 с чистой речи; reference_text — ТОЧНАЯ расшифровка образца, без неё распознаем сами). "
            + "engine=chatterbox — Chatterbox Multilingual (23 языка, голос только по образцу reference, быстрее, тембр ближе "
            + "к образцу). Длинный текст режется по предложениям, голос держится. Результат — wav." + AudioTail,
            Obj(["text"], new JsonObject
            {
                ["text"] = Str($"Что озвучить, до {LocalMediaService.MaxSpeechTextLength} символов; числа лучше словами"),
                ["language"] = Str("Язык кодом ISO: ru (по умолчанию), en, de, fr, es, it, pt, zh, ja, ko; у chatterbox ещё "
                    + "ar, da, el, fi, he, hi, ms, nl, no, pl, sv, sw, tr"),
                ["voice"] = Str("Только qwen: описание голоса словами (пол, возраст, тембр, манера)"),
                ["speaker"] = Enum(LocalMediaService.QwenSpeakers, "Только qwen: готовый диктор (родные языки — китайский, "
                    + "английский, японский, корейский); voice тогда — указание манеры"),
                ["reference"] = Str("Образец голоса для клона: " + AudioRefDescription),
                ["reference_text"] = Str("Точная расшифровка образца (только qwen); не знаешь — не передавай"),
                ["engine"] = Enum(["qwen", "chatterbox"], "qwen (по умолчанию) или chatterbox"),
                ["expressiveness"] = Num(0.25, 2, "Только chatterbox: выразительность (по умолчанию 0,5)"),
                ["seed"] = Seed(),
            }));

        yield return Tool("local_voice_convert",
            AudioLocal + "Сменить голос в записи с сохранением слов и интонации. engine=seedvc — Seed-VC без обучения, "
            + "по образцу reference (mode=speech — речь, singing — пение; лицензия GPL-3.0). engine=rvc — моделью голоса из "
            + "local_voice_train (voice_model .pth и voice_index .index). pitch_shift — сдвиг в полутонах (пение в другом "
            + "регистре). Из песни сначала выдели вокал local_audio_separate." + AudioTail,
            Obj(["audio"], new JsonObject
            {
                ["audio"] = Str("Исходная запись: " + AudioRefDescription),
                ["engine"] = Enum(["seedvc", "rvc"], "seedvc (по умолчанию) или rvc"),
                ["reference"] = Str("Только seedvc: образец целевого голоса 1–30 с — " + AudioRefDescription),
                ["mode"] = Enum(["speech", "singing"], "Только seedvc: speech (по умолчанию) или singing"),
                ["voice_model"] = Str("Только rvc: путь .pth в проекте или job_id задачи local_voice_train"),
                ["voice_index"] = Str("Только rvc: путь .index в проекте или job_id той же задачи (необязательно, "
                    + "точнее тембр)"),
                ["pitch_shift"] = Int(-24, 24, "Сдвиг высоты в полутонах (по умолчанию 0)"),
            }));

        yield return Tool("local_voice_train",
            AudioLocal + "Обучить модель голоса RVC (Applio) по записям одного человека: 1–20 файлов, в сумме от 2 до 30 "
            + "минут чистой речи или пения без музыки (музыку сначала убери local_audio_separate). Результат — voice.pth и "
            + "voice.index в проекте, дальше — local_voice_convert с engine=rvc. Тяжёлая задача: одна за раз." + AudioTail,
            Obj(["audios"], new JsonObject
            {
                ["audios"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = LocalMediaService.MaxTrainClips,
                    ["items"] = Str(AudioRefDescription),
                    ["description"] = "Записи голоса",
                },
                ["epochs"] = Int(20, 1000, "Эпохи обучения (по умолчанию 200; больше — точнее и дольше)"),
            }));

        yield return Tool("local_audio_separate",
            AudioLocal + "Разделить трек на стемы: mode=vocals — вокал и минус (BS-RoFormer, по умолчанию), 4stems — вокал, "
            + "барабаны, бас, прочее (HTDemucs), 6stems — плюс гитара и пианино, karaoke — основной вокал отдельно от "
            + "бэк-вокала и музыки." + AudioTail,
            Obj(["audio"], new JsonObject
            {
                ["audio"] = Str(AudioRefDescription),
                ["mode"] = Enum(LocalMediaService.SeparateModes, "vocals (по умолчанию), 4stems, 6stems или karaoke"),
                ["format"] = Enum(["mp3", "wav", "flac"], "Формат стемов (по умолчанию mp3)"),
            }));

        yield return Tool("local_audio_to_midi",
            AudioLocal + "Снять ноты из звука в MIDI (Basic Pitch, полифония). Точнее всего на одном инструменте или "
            + "вокале — полный микс сначала раздели local_audio_separate." + AudioTail,
            Obj(["audio"], new JsonObject { ["audio"] = Str(AudioRefDescription) }));

        yield return Tool("local_audio_enhance",
            AudioLocal + "Улучшить звук: mode=denoise — убрать шум из речи (DeepFilterNet, по умолчанию), upsample — "
            + $"восстановить верхние частоты до 48 кГц (AudioSR; записи до {LocalMediaService.UpsampleMaxSeconds} с, медленно), "
            + "master — мастеринг по референсу reference: громкость и АЧХ как у образцового трека (Matchering)." + AudioTail,
            Obj(["audio"], new JsonObject
            {
                ["audio"] = Str(AudioRefDescription),
                ["mode"] = Enum(LocalMediaService.EnhanceModes, "denoise (по умолчанию), upsample или master"),
                ["reference"] = Str("Только master: трек-образец — " + AudioRefDescription),
                ["model"] = Enum(["basic", "speech"], "Только upsample: basic — музыка и любые звуки (по умолчанию), "
                    + "speech — речь"),
            }));

        yield return Tool("local_transcribe",
            AudioLocal + "Распознать речь или слова песни (Whisper large-v3-turbo): текст .txt, субтитры .srt и .lrc со "
            + "временем строк. У песни сначала выдели вокал local_audio_separate — на полном миксе слова теряются."
            + AudioTail,
            Obj(["audio"], new JsonObject
            {
                ["audio"] = Str(AudioRefDescription),
                ["language"] = Str("Код языка ISO 639-1 (ru, en, …); пусто — определим сами"),
            }));
    }

    private static JsonNode[] AudioOperationsJson() =>
    [
        AudioOp("local_music_generate", "ACE-Step 1.5 XL-sft + LM 4B / YuE2-3B int8 (CC BY-NC) / MiniMax Music 3",
            "песня или инструментал по стилю и словам, 10–240 с",
            "ace: 3 мин песни ≈ 74 с; yue2: 3 мин ≈ 60 с; minimax: 3 мин ≈ 4,5 мин"),
        AudioOp("local_music_edit", "ACE-Step 1.5 turbo / xl-base", "cover, repaint, extract, lego, complete",
            "трек 60 с: cover ≈ 19 с, repaint ≈ 10 с, extract/lego/complete ≈ 30–37 с"),
        AudioOp("local_speech", "Qwen3-TTS 1.7B / Chatterbox Multilingual",
            "озвучка по описанию голоса, диктором или клоном по образцу",
            "qwen: ≈ длина речи + 15 с (25 с речи ≈ 40 с); chatterbox ≈ на треть быстрее"),
        AudioOp("local_voice_convert", "Seed-VC (GPL-3.0) / RVC (Applio)", "смена голоса: по образцу или моделью голоса",
            "речь 17 с ≈ 14 с, пение 30 с ≈ 16 с; rvc 17 с ≈ 11 с"),
        AudioOp("local_voice_train", "RVC (Applio)", "обучение модели голоса; тяжёлая", "101 с записи, 100 эпох ≈ 4 мин"),
        AudioOp("local_audio_separate", "BS-RoFormer / HTDemucs ft / Mel-RoFormer karaoke", "стемы",
            "трек 3 мин: vocals ≈ 72 с, 4stems ≈ 39 с, 6stems ≈ 23 с, karaoke ≈ 74 с"),
        AudioOp("local_audio_to_midi", "Basic Pitch", "ноты в MIDI", "3 мин ≈ 5–15 с"),
        AudioOp("local_audio_enhance", "DeepFilterNet 3 / AudioSR / Matchering", "шумодав, верхние частоты, мастеринг",
            "denoise ≈ 5–20 с, master ≈ 17 с, upsample: 30 с ≈ 1 мин, 120 с ≈ 1,5 мин"),
        AudioOp("local_transcribe", "faster-whisper large-v3-turbo", "текст, SRT и LRC", "3 мин ≈ 7 с"),
    ];

    private static JsonObject AudioOp(string tool, string model, string what, string eta) => new()
    {
        ["tool"] = tool,
        ["model"] = model,
        ["what"] = what,
        ["eta"] = eta,
    };

    private static JsonObject Obj(string[] required, JsonObject properties) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
        ["properties"] = properties,
    };

    private static JsonObject Enum(IEnumerable<string> values, string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]),
        ["description"] = description,
    };

    private static JsonObject Int(int min, int max, string description) => new()
    {
        ["type"] = "integer",
        ["minimum"] = min,
        ["maximum"] = max,
        ["description"] = description,
    };

    private static JsonObject Num(double min, double max, string description) => new()
    {
        ["type"] = "number",
        ["minimum"] = min,
        ["maximum"] = max,
        ["description"] = description,
    };
}
