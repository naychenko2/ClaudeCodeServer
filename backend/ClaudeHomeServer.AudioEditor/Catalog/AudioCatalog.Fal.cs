using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.AudioEditor.Catalog;

// Каталог fal для звука (ADR-021 §2, решение «отобранные + Дополнительно»): курируемые эндпоинты по
// операциям и НАША раскладка общих полей запроса на поля fal. Id модели — id эндпоинта: он же уходит в
// подпись траты. Схемы, цены и единицы сверены с живым каталогом fal 2026-10-01 (get_model_schema,
// get_pricing); ориентир цены — за ОДНУ нашу единицу (символ, секунду, минуту, запуск), точную сумму
// даёт котировка по прайсу fal. Редкие параметры идут в Params под именами fal; разбор их по схеме
// модели («Дополнительно») — шаг 3.3. Модель вне отбора — одна строка здесь.
public static partial class AudioCatalog
{
    // Голос
    public const string FalMiniMaxHd = "fal-ai/minimax/speech-2.8-hd";
    public const string FalMiniMaxTurbo = "fal-ai/minimax/speech-2.8-turbo";
    public const string FalElevenV4 = "elevenlabs/tts/eleven-v4";
    public const string FalElevenV3 = "fal-ai/elevenlabs/tts/eleven-v3";
    public const string FalQwenTts = "fal-ai/qwen-3-tts/text-to-speech/1.7b";
    public const string FalQwenDesign = "fal-ai/qwen-3-tts/voice-design/1.7b";
    public const string FalQwenClone = "fal-ai/qwen-3-tts/clone-voice/1.7b";
    public const string FalKokoro = "fal-ai/kokoro/american-english";
    public const string FalInworld = "fal-ai/inworld-tts";
    public const string FalChatterbox = "fal-ai/chatterbox/text-to-speech/multilingual";
    public const string FalMiniMaxClone = "fal-ai/minimax/voice-clone";
    public const string FalVoiceChanger = "fal-ai/elevenlabs/voice-changer";
    // Распознавание
    public const string FalWizper = "fal-ai/wizper";
    public const string FalScribe = "fal-ai/elevenlabs/speech-to-text/scribe-v2";
    // Музыка
    public const string FalElevenMusic = "elevenlabs/music/v2.5";
    public const string FalMiniMaxMusic = "minimax/music-3";
    public const string FalLyria = "google/lyria-3.5";
    public const string FalStableAudio = "fal-ai/stable-audio-3/medium/text-to-audio";
    public const string FalAceStep = "fal-ai/ace-step";
    public const string FalSonilo = "sonilo/v1.1/text-to-music";
    public const string FalAceCover = "fal-ai/ace-step/audio-to-audio";
    public const string FalAceInpaint = "fal-ai/ace-step/audio-inpaint";
    public const string FalAceOutpaint = "fal-ai/ace-step/audio-outpaint";
    public const string FalStableCover = "fal-ai/stable-audio-3/medium/audio-to-audio";
    public const string FalStableInpaint = "fal-ai/stable-audio-3/medium/audio-inpainting";
    public const string FalStableOutpaint = "fal-ai/stable-audio-3/medium/audio-outpainting";
    // Обработка
    public const string FalDemucs = "fal-ai/demucs";
    public const string FalDeepFilterNet = "fal-ai/deepfilternet3";
    // Звуковые эффекты
    public const string FalElevenSfx = "fal-ai/elevenlabs/sound-effects/v2";
    public const string FalMirelo = "mirelo-ai/sfx1.6/text-to-audio";
    public const string FalStableSfx = "fal-ai/stable-audio-3/small/sfx/text-to-audio";
    public const string FalSoniloSfx = "sonilo/v1.1/text-to-sound-effects";

    // Форма языка в поле fal: ISO 639-1 как есть, ISO 639-2 («rus», Scribe), английское имя («Russian»,
    // Qwen), имя строчными («russian», Chatterbox), имя MiniMax (language_boost: «Chinese,Yue», «Nynorsk»)
    public enum FalLanguageForm { Iso, Iso3, EnglishName, LowerName, MiniMax }

    // Что лежит в ответе fal: один звук (поле audio, audio_file или первый элемент массива), стемы
    // (каждое поле-файл верхнего уровня — стем с его именем), расшифровка (text плюс chunks или words)
    public enum FalOutputKind { Audio, Stems, Transcript }

    // Раскладка общих полей AudioRequest на поля fal; null — модели это поле не нужно. Defaults ставятся,
    // только если поля нет ни в Params, ни в раскладке; Fixed перебивает всё (output_format=url у MiniMax:
    // по умолчанию он отдаёт hex)
    public sealed record FalFields(
        string? Text = null,
        string? Prompt = null,
        string? Lyrics = null,
        string? Language = null,
        FalLanguageForm LanguageForm = FalLanguageForm.Iso,
        string? Duration = null,
        bool DurationInMs = false,
        string? Start = null,
        string? End = null,
        string? Source = null,
        string? Reference = null,
        string? Seed = null,
        IReadOnlyDictionary<string, JsonNode?>? Defaults = null,
        IReadOnlyDictionary<string, JsonNode?>? Fixed = null);

    // Второй прогон цепочки: файл LinkFrom из ответа первого уходит ссылкой в поле LinkTo второго
    public sealed record FalNext(AudioModelInfo Info, FalFields Fields, string LinkFrom, string LinkTo);

    public sealed record FalModel(AudioModelInfo Info, FalFields Fields, FalOutputKind Output = FalOutputKind.Audio,
        FalNext? Next = null);

    private static IReadOnlyDictionary<string, JsonNode?> Json(params (string Key, JsonNode? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static AudioModelInfo FalInfo(string id, string label, AudioCaps caps, double perUnit) =>
        new(id, label, caps, new AudioPriceHint(perUnit, caps.PriceUnit, PerOf(caps.PriceUnit)));

    private static string PerOf(string unit) => unit switch
    {
        AudioPriceUnits.Chars => "char",
        AudioPriceUnits.Sec => "sec",
        AudioPriceUnits.Min => "min",
        _ => "run",
    };

    // Языки по схемам и документации поставщиков; где схема языков не перечисляет — список, в котором
    // поставщик заявляет русский, иначе пусто («умеет ru» не обещаем)
    private static readonly string[] MiniMaxSpeechLanguages =
    [
        "zh", "yue", "en", "ar", "ru", "es", "fr", "pt", "de", "tr", "nl", "uk", "vi", "id", "ja", "it", "ko", "th", "pl",
        "ro", "el", "cs", "fi", "hi", "bg", "da", "he", "ms", "sk", "sv", "hr", "hu", "no", "sl", "ca", "nn", "af",
    ];

    private static readonly string[] ElevenLanguages =
    [
        "en", "ru", "uk", "de", "fr", "es", "it", "pt", "pl", "nl", "sv", "da", "fi", "no", "cs", "sk", "hu", "ro", "bg",
        "hr", "el", "tr", "ar", "he", "hi", "zh", "ja", "ko", "vi", "id", "ms", "th",
    ];

    private static readonly string[] InworldLanguages = ["en", "zh", "nl", "fr", "de", "it", "ja", "ko", "pl", "pt", "es", "ru", "hi", "he", "ar"];
    private static readonly string[] LyriaLanguages = ["en", "de", "es", "fr", "hi", "ja", "ko", "pt"];
    private static readonly string[] AceV1Languages = ["en", "zh", "ru", "es", "ja", "de", "fr", "pt", "it", "ko"];

    // Пределы схем fal: текст озвучки и описания
    private const int MiniMaxChars = 10000;
    private const int ElevenChars = 5000;
    private const int InworldChars = 2000;
    private const int ChatterboxChars = 300;
    private const int MiniMaxPreviewChars = 1000;
    private const int ElevenMusicPromptChars = 4100;
    private const int ElevenSfxChars = 450;

    private static AudioCaps Voice(AudioOp[] ops, IReadOnlyList<string> languages, AudioVoiceKind[] kinds, AudioLicense license,
        string unit, int? maxChars = null, int? inputMaxSec = null) =>
        new(ops, languages, kinds, [AudioOutputs.Audio], license, unit, MaxTextChars: maxChars, InputMaxSec: inputMaxSec);

    private static AudioCaps Music(AudioOp[] ops, IReadOnlyList<string> languages, AudioLicense license, string unit,
        int? minSec = null, int? maxSec = null, int? maxChars = null, bool instrumental = false, int? inputMaxSec = null) =>
        new(ops, languages, [], [AudioOutputs.Audio], license, unit, MaxTextChars: maxChars, MinDurationSec: minSec,
            MaxDurationSec: maxSec, InputMaxSec: inputMaxSec, LanguageNeutral: instrumental);

    // Список строится лениво: языковые массивы локальных моделей живут в другой части partial-класса,
    // а порядок инициализации статических полей между файлами не определён
    private static readonly Lazy<IReadOnlyList<FalModel>> FalModels = new(BuildFal);

    public static IReadOnlyList<FalModel> Fal => FalModels.Value;

    private static IReadOnlyList<FalModel> BuildFal()
    {
        var qwenTtsFields = new FalFields(Text: "text", Prompt: "prompt", Language: "language",
            LanguageForm: FalLanguageForm.EnglishName);
        var qwenTtsInfo = FalInfo(FalQwenTts, "Qwen3-TTS 1.7B",
            Voice([AudioOp.Speak], QwenLanguages, [AudioVoiceKind.Preset], AudioLicenses.Apache2, AudioPriceUnits.Chars), 0.00009);

        return
        [
            // ── Голос: озвучка ────────────────────────────────────────────────────────
            new(FalInfo(FalMiniMaxHd, "MiniMax Speech 2.8 HD",
                    Voice([AudioOp.Speak], MiniMaxSpeechLanguages, [AudioVoiceKind.Preset], AudioLicenses.Commercial,
                        AudioPriceUnits.Chars, MiniMaxChars), 0.0001),
                new FalFields(Text: "prompt", Language: "language_boost", LanguageForm: FalLanguageForm.MiniMax,
                    Fixed: Json(("output_format", "url")))),
            new(FalInfo(FalMiniMaxTurbo, "MiniMax Speech 2.8 Turbo",
                    Voice([AudioOp.Speak], MiniMaxSpeechLanguages, [AudioVoiceKind.Preset], AudioLicenses.Commercial,
                        AudioPriceUnits.Chars, MiniMaxChars), 0.00006),
                new FalFields(Text: "prompt", Language: "language_boost", LanguageForm: FalLanguageForm.MiniMax,
                    Fixed: Json(("output_format", "url")))),
            new(FalInfo(FalElevenV4, "ElevenLabs v4",
                    Voice([AudioOp.Speak], ElevenLanguages, [AudioVoiceKind.Preset], AudioLicenses.Commercial,
                        AudioPriceUnits.Chars, ElevenChars), 0.00008),
                new FalFields(Text: "text", Language: "language_code", Seed: "seed")),
            new(FalInfo(FalElevenV3, "ElevenLabs v3",
                    Voice([AudioOp.Speak], ElevenLanguages, [AudioVoiceKind.Preset], AudioLicenses.Commercial,
                        AudioPriceUnits.Chars, ElevenChars), 0.0001),
                new FalFields(Text: "text", Language: "language_code")),
            new(qwenTtsInfo, qwenTtsFields),
            new(FalInfo(FalKokoro, "Kokoro (американский английский)",
                    Voice([AudioOp.Speak], ["en"], [AudioVoiceKind.Preset], AudioLicenses.Apache2, AudioPriceUnits.Chars), 0.00002),
                new FalFields(Text: "prompt")),
            new(FalInfo(FalInworld, "Inworld TTS-1.5 Max",
                    Voice([AudioOp.Speak], InworldLanguages, [AudioVoiceKind.Preset], AudioLicenses.Commercial,
                        AudioPriceUnits.Chars, InworldChars), 0.00001),
                new FalFields(Text: "text")),
            // Язык уходит в voice именем («russian»); образец клона — тоже в voice, ссылкой, и перебивает язык
            new(FalInfo(FalChatterbox, "Chatterbox Multilingual",
                    Voice([AudioOp.Speak, AudioOp.CloneVoice], ChatterboxLanguages, [AudioVoiceKind.Preset, AudioVoiceKind.Clone],
                        AudioLicenses.MitWatermark, AudioPriceUnits.Chars, ChatterboxChars), 0.000025),
                new FalFields(Text: "text", Language: "voice", LanguageForm: FalLanguageForm.LowerName, Reference: "voice",
                    Seed: "seed")),

            // ── Голос: по описанию, клон, смена ──────────────────────────────────────
            new(FalInfo(FalQwenDesign, "Qwen3-TTS 1.7B · голос по описанию",
                    Voice([AudioOp.DesignVoice], QwenLanguages, [AudioVoiceKind.Description], AudioLicenses.Apache2,
                        AudioPriceUnits.Chars), 0.00009),
                new FalFields(Text: "text", Prompt: "prompt", Language: "language", LanguageForm: FalLanguageForm.EnglishName)),
            // Клон Qwen — два прогона: эмбеддинг голоса по образцу, затем озвучка с ним
            new(FalInfo(FalQwenClone, "Qwen3-TTS 1.7B · клон",
                    Voice([AudioOp.CloneVoice], QwenLanguages, [AudioVoiceKind.Clone], AudioLicenses.Apache2,
                        AudioPriceUnits.Min, inputMaxSec: 300), 0.0008),
                new FalFields(Reference: "audio_url"),
                Next: new FalNext(qwenTtsInfo, qwenTtsFields, "speaker_embedding", "speaker_voice_embedding_file_url")),
            // Клон MiniMax: образец от 10 с, озвученный превью-текст — результат; голос живёт у MiniMax 7 дней
            new(FalInfo(FalMiniMaxClone, "MiniMax · клон голоса",
                    Voice([AudioOp.CloneVoice], MiniMaxSpeechLanguages, [AudioVoiceKind.Clone], AudioLicenses.Commercial,
                        AudioPriceUnits.Run, MiniMaxPreviewChars), 1.5),
                new FalFields(Text: "text", Reference: "audio_url", Defaults: Json(("model", "speech-02-hd")))),
            new(FalInfo(FalVoiceChanger, "ElevenLabs · смена голоса",
                    new AudioCaps([AudioOp.ConvertVoice], [], [AudioVoiceKind.Preset], [AudioOutputs.Audio], AudioLicenses.Commercial,
                        AudioPriceUnits.Min, LanguageNeutral: true), 0.3),
                new FalFields(Source: "audio_url", Seed: "seed")),

            // ── Распознавание ────────────────────────────────────────────────────────
            new(FalInfo(FalWizper, "Wizper (Whisper v3)",
                    new AudioCaps([AudioOp.Transcribe], WhisperLanguages, [], [AudioOutputs.Text, AudioOutputs.Subtitles],
                        AudioLicenses.Mit, AudioPriceUnits.Sec), 0.000625),
                new FalFields(Language: "language", Source: "audio_url"), FalOutputKind.Transcript),
            new(FalInfo(FalScribe, "ElevenLabs Scribe v2",
                    new AudioCaps([AudioOp.Transcribe], WhisperLanguages, [], [AudioOutputs.Text, AudioOutputs.Subtitles],
                        AudioLicenses.Commercial, AudioPriceUnits.Min), 0.008),
                new FalFields(Language: "language_code", LanguageForm: FalLanguageForm.Iso3, Source: "audio_url"),
                FalOutputKind.Transcript),

            // ── Музыка: песня ────────────────────────────────────────────────────────
            new(FalInfo(FalElevenMusic, "ElevenLabs Music v2.5",
                    Music([AudioOp.Song], [], AudioLicenses.Commercial, AudioPriceUnits.Min, 3, 600, ElevenMusicPromptChars), 0.6),
                new FalFields(Prompt: "prompt", Duration: "music_length_ms", DurationInMs: true)),
            // Слова обязательны: без них — инструментал тегом
            new(FalInfo(FalMiniMaxMusic, "MiniMax Music 3",
                    Music([AudioOp.Song], SungRuEn, AudioLicenses.Commercial, AudioPriceUnits.Sec, 1, 300), 0.002),
                new FalFields(Prompt: "prompt", Lyrics: "lyrics", Duration: "duration", Seed: "seed",
                    Defaults: Json(("lyrics", "[instrumental]")))),
            // Длина задаётся только текстом промпта
            new(FalInfo(FalLyria, "Lyria 3.5",
                    Music([AudioOp.Song], LyriaLanguages, AudioLicenses.SynthId, AudioPriceUnits.Run, maxChars: 5000), 0.1),
                new FalFields(Prompt: "prompt")),
            new(FalInfo(FalStableAudio, "Stable Audio 3 Medium",
                    Music([AudioOp.Song], [], AudioLicenses.Commercial, AudioPriceUnits.Run, 1, 380, instrumental: true), 0.0376),
                new FalFields(Prompt: "prompt", Duration: "duration", Seed: "seed")),
            new(FalInfo(FalAceStep, "ACE-Step",
                    Music([AudioOp.Song], AceV1Languages, AudioLicenses.Apache2, AudioPriceUnits.Sec, 5, 240), 0.0002),
                new FalFields(Prompt: "tags", Lyrics: "lyrics", Duration: "duration", Seed: "seed",
                    Defaults: Json(("lyrics", "[inst]")))),
            new(FalInfo(FalSonilo, "Sonilo v1.1",
                    Music([AudioOp.Song], [], AudioLicenses.CommercialWatermark, AudioPriceUnits.Sec, 1, 600, instrumental: true), 0.0025),
                new FalFields(Prompt: "prompt", Duration: "duration")),

            // ── Музыка: кавер, кусок, продолжение ───────────────────────────────────
            new(FalInfo(FalAceCover, "ACE-Step · кавер",
                    Music([AudioOp.Cover], AceV1Languages, AudioLicenses.Apache2, AudioPriceUnits.Sec, inputMaxSec: 240), 0.0002),
                new FalFields(Prompt: "tags", Lyrics: "lyrics", Source: "audio_url", Seed: "seed",
                    Defaults: Json(("original_tags", "music")))),
            new(FalInfo(FalStableCover, "Stable Audio 3 Medium · кавер",
                    Music([AudioOp.Cover], [], AudioLicenses.Commercial, AudioPriceUnits.Run, 1, 380, instrumental: true,
                        inputMaxSec: 380), 0.0417),
                new FalFields(Prompt: "prompt", Duration: "duration", Source: "audio_url", Seed: "seed")),
            new(FalInfo(FalAceInpaint, "ACE-Step · перегенерировать кусок",
                    Music([AudioOp.Repaint], AceV1Languages, AudioLicenses.Apache2, AudioPriceUnits.Sec, inputMaxSec: 240), 0.0002),
                new FalFields(Prompt: "tags", Lyrics: "lyrics", Start: "start_time", End: "end_time", Source: "audio_url",
                    Seed: "seed")),
            new(FalInfo(FalStableInpaint, "Stable Audio 3 Medium · перегенерировать кусок",
                    Music([AudioOp.Repaint], [], AudioLicenses.Commercial, AudioPriceUnits.Run, instrumental: true,
                        inputMaxSec: 380), 0.0442),
                new FalFields(Prompt: "prompt", Start: "mask_start_seconds", End: "mask_end_seconds", Source: "audio_url",
                    Seed: "seed")),
            // Продолжить в конец на DurationSec; «в начало» — extend_* в Params
            new(FalInfo(FalAceOutpaint, "ACE-Step · продолжить",
                    Music([AudioOp.Outpaint], AceV1Languages, AudioLicenses.Apache2, AudioPriceUnits.Sec, 1, 107,
                        inputMaxSec: 240), 0.0002),
                new FalFields(Prompt: "tags", Lyrics: "lyrics", Duration: "extend_after_duration", Source: "audio_url",
                    Seed: "seed")),
            new(FalInfo(FalStableOutpaint, "Stable Audio 3 Medium · продолжить",
                    Music([AudioOp.Outpaint], [], AudioLicenses.Commercial, AudioPriceUnits.Run, 1, 380, instrumental: true,
                        inputMaxSec: 380), 0.0446),
                new FalFields(Prompt: "prompt", Duration: "extend_seconds_after", Source: "audio_url", Seed: "seed")),

            // ── Обработка ────────────────────────────────────────────────────────────
            new(FalInfo(FalDemucs, "Demucs · стемы",
                    new AudioCaps([AudioOp.Separate], [], [], [AudioOutputs.Stems], AudioLicenses.Mit, AudioPriceUnits.Sec,
                        LanguageNeutral: true), 0.0007),
                new FalFields(Source: "audio_url"), FalOutputKind.Stems),
            // Чистит речь и поднимает частоту до 48 кГц (моно)
            new(FalInfo(FalDeepFilterNet, "DeepFilterNet 3",
                    new AudioCaps([AudioOp.Denoise, AudioOp.Upsample], [], [], [AudioOutputs.Audio], AudioLicenses.MitApache2,
                        AudioPriceUnits.Sec, LanguageNeutral: true), 0.001),
                new FalFields(Source: "audio_url")),

            // ── Звуковые эффекты ─────────────────────────────────────────────────────
            new(FalInfo(FalElevenSfx, "ElevenLabs Sound Effects v2",
                    new AudioCaps([AudioOp.Sfx], [], [], [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Sec,
                        MaxTextChars: ElevenSfxChars, MaxDurationSec: 22, LanguageNeutral: true), 0.002),
                new FalFields(Prompt: "text", Duration: "duration_seconds")),
            new(FalInfo(FalMirelo, "Mirelo SFX 1.6",
                    new AudioCaps([AudioOp.Sfx], [], [], [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Sec,
                        MaxDurationSec: 60, LanguageNeutral: true), 0.01),
                new FalFields(Prompt: "text_prompt", Duration: "duration", Seed: "seed", Fixed: Json(("num_samples", 1)))),
            new(FalInfo(FalStableSfx, "Stable Audio 3 Small SFX",
                    new AudioCaps([AudioOp.Sfx], [], [], [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Run,
                        MinDurationSec: 1, MaxDurationSec: 120, LanguageNeutral: true), 0.0206),
                new FalFields(Prompt: "prompt", Duration: "duration", Seed: "seed")),
            new(FalInfo(FalSoniloSfx, "Sonilo SFX v1.1",
                    new AudioCaps([AudioOp.Sfx], [], [], [AudioOutputs.Audio], AudioLicenses.CommercialWatermark, AudioPriceUnits.Sec,
                        MaxDurationSec: 180, LanguageNeutral: true), 0.0018),
                new FalFields(Prompt: "prompt", Duration: "duration")),
        ];
    }

    public static FalModel? FindFal(string modelId) =>
        Fal.FirstOrDefault(m => string.Equals(m.Info.Id, modelId?.Trim(), StringComparison.OrdinalIgnoreCase));
}
