using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Catalog;

// Каталог модуля «Звук» (ADR-021 §2) — данные, а не код: отобранные модели поставщиков с AudioCaps и
// их привязка к операциям поставщика. Здесь локальные модели (local-media), fal — в AudioCatalog.Fal.cs;
// Higgsfield и Яндекс добавятся строками на этапах 3–4. Лицензии и языки — по docs/features/local-media.md
// и белым спискам LocalMediaService.Audio: меняются там — правится и здесь.
public static partial class AudioCatalog
{
    public const string AutoModelId = "auto";
    public const string AutoModelLabel = "Авто";

    // Порядок поставщиков как у картинок: в списке и при умолчании «Авто» — первый доступный.
    // local последний намеренно: доступность GPU мигает, и «Авто» с local первым прыгало бы в облако
    public static readonly string[] ProviderOrder = ["fal", "higgsfield", "local"];

    public static int OrderOf(string key)
    {
        var i = Array.FindIndex(ProviderOrder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? int.MaxValue : i;
    }

    public static bool IsAuto(string? model) =>
        string.IsNullOrWhiteSpace(model) || string.Equals(model.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase);

    // Разворот «Авто» в модель поставщика: первая модель каталога, умеющая операцию; явный id — как
    // есть, если он у поставщика есть. null — поставщик операцию не умеет или модели такой нет.
    // Серая модель (DisabledReason) не разворачивается никогда
    public static AudioModelInfo? Resolve(IReadOnlyList<AudioModelInfo> models, AudioOp op, string? model) =>
        IsAuto(model)
            ? models.FirstOrDefault(m => m.DisabledReason is null && m.Caps.Ops.Contains(op))
            : models.FirstOrDefault(m => string.Equals(m.Id, model!.Trim(), StringComparison.OrdinalIgnoreCase)
                                         && m.DisabledReason is null && m.Caps.Ops.Contains(op));

    // Доступные поставщики в порядке показа
    public static IReadOnlyList<IAudioEngine> Available(IEnumerable<IAudioEngine> engines) =>
        engines.Where(e => Safe(() => e.Enabled)).OrderBy(e => OrderOf(e.Key)).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

    // Кандидаты «Авто» в порядке перебора: доступные и работающие в этой области. С флагом владельца
    // local-media-default локальные модели идут первыми (ADR-021 §2): человек просил по умолчанию
    // свою видеокарту. Единственная точка порядка «Авто» — её зовут и котировка, и каталог для фронта
    public static IReadOnlyList<IAudioEngine> AutoCandidates(IEnumerable<IAudioEngine> engines, AudioEditScope scope,
        bool preferLocal) =>
        Available(engines).Where(e => Safe(() => e.ScopeRefusal(scope) is null))
            .OrderBy(e => preferLocal && string.Equals(e.Key, Engines.LocalAudioEngine.ProviderKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

    // Заведённые поставщики (в том числе лежащие сейчас) в порядке показа
    public static IReadOnlyList<IAudioEngine> Registered(IEnumerable<IAudioEngine> engines) =>
        engines.Where(e => Safe(() => e.Registered)).OrderBy(e => OrderOf(e.Key)).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

    // Сбой проверки доступности — поставщик недоступен, а не 500 у всего каталога
    private static bool Safe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    // ── Локальные модели (local-media) ─────────────────────────────────────────────

    public const string QwenTts = "qwen3-tts-1.7b";
    public const string MossTts = "moss-tts-v1.5";
    public const string Chatterbox = "chatterbox-multilingual";
    public const string AceStep = "ace-step-1.5-xl";
    public const string Yue2 = "yue2-3b";
    public const string MiniMaxMusic = "minimax-music-3";
    public const string BsRoformer = "bs-roformer";
    public const string HtDemucs4 = "htdemucs-ft-4stems";
    public const string HtDemucs6 = "htdemucs-6stems";
    public const string MelRoformer = "mel-roformer-karaoke";
    public const string DeepFilterNet = "deepfilternet-3";
    public const string AudioSr = "audiosr";
    public const string Matchering = "matchering";
    public const string Whisper = "whisper-large-v3-turbo";
    public const string BasicPitch = "basic-pitch";
    public const string SeedVc = "seed-vc";
    public const string Rvc = "rvc";

    // Привязка операции модуля к операции шва ILocalAudioMedia: фиксированные аргументы (engine, task,
    // mode) — под именами инструментов local-media; Params запроса их не перебивают
    public sealed record LocalBinding(LocalAudioOp Op, IReadOnlyDictionary<string, string> Args);

    public sealed record LocalModel(AudioModelInfo Info, IReadOnlyDictionary<AudioOp, LocalBinding> Bindings);

    // Пределы local-media (LocalMediaService.Audio): текст речи и слова песни, длина песни,
    // образец клона, вход AudioSR и общий потолок входного звука (LocalMedia:MaxAudioInputSeconds)
    private const int SpeechChars = 5000;
    private const int LyricsChars = 5000;
    private const int SongMinSec = 10;
    private const int SongMaxSec = 240;
    private const int ReferenceMaxSec = 60;
    private const int UpsampleMaxSec = 120;
    private const int InputMaxSec = 600;

    private static readonly string[] QwenLanguages = ["ru", "en", "zh", "ja", "ko", "de", "fr", "pt", "es", "it"];

    private static readonly string[] MossLanguages =
    [
        "zh", "yue", "en", "ar", "cs", "da", "nl", "fi", "fr", "de", "el", "he", "hi", "hu", "it", "ja", "ko", "mk", "ms",
        "fa", "pl", "pt", "ro", "ru", "es", "sw", "sv", "tl", "th", "tr", "vi",
    ];

    private static readonly string[] ChatterboxLanguages =
    [
        "ar", "da", "de", "el", "en", "es", "fi", "fr", "he", "hi", "it", "ja", "ko", "ms", "nl", "no", "pl", "pt",
        "ru", "sv", "sw", "tr", "zh",
    ];

    private static readonly string[] AceLanguages =
    [
        "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hr", "ht",
        "hu", "id", "is", "it", "ja", "ko", "la", "lt", "ms", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sa",
        "sk", "sr", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "vi", "yue", "zh",
    ];

    // У YuE2 и MiniMax Music 3 язык параметром не задаётся; русский подтверждён замером песни на
    // стенде (docs/features/local-media.md, таблица замеров), английский — родной язык моделей
    private static readonly string[] SungRuEn = ["ru", "en"];

    // Whisper large-v3-turbo распознаёт любой из 99 языков Whisper; в каталоге — те, что важны продукту
    private static readonly string[] WhisperLanguages = ["ru", "en", "uk", "de", "fr", "es", "it", "pt", "zh", "ja", "ko"];

    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    private static LocalBinding Bind(LocalAudioOp op, params (string Key, string Value)[] args) =>
        new(op, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    private static readonly LocalBinding Ace = Bind(LocalAudioOp.MusicGenerate, ("engine", "ace"));

    private static LocalBinding AceEdit(string task) => Bind(LocalAudioOp.MusicEdit, ("task", task), ("engine", "ace"));

    // Операции модели — ключи её привязок: расхождение Caps.Ops и привязок невозможно по построению
    private static LocalModel Model(
        string id, string label, IReadOnlyDictionary<AudioOp, LocalBinding> bindings, Func<IReadOnlyList<AudioOp>, AudioCaps> caps) =>
        new(new AudioModelInfo(id, label, caps([.. bindings.Keys]), new AudioPriceHint(0, AudioPriceUnits.Free, "run")), bindings);

    public static readonly IReadOnlyList<LocalModel> Local =
    [
        // Голос
        Model(QwenTts, "Qwen3-TTS 1.7B", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Speak] = Bind(LocalAudioOp.Speech, ("engine", "qwen")),
                [AudioOp.DesignVoice] = Bind(LocalAudioOp.Speech, ("engine", "qwen")),
                [AudioOp.CloneVoice] = Bind(LocalAudioOp.Speech, ("engine", "qwen")),
            },
            ops => new AudioCaps(ops, QwenLanguages,
                [AudioVoiceKind.Preset, AudioVoiceKind.Description, AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Apache2, AudioPriceUnits.Free, MaxTextChars: SpeechChars, InputMaxSec: ReferenceMaxSec)),
        Model(MossTts, "MOSS-TTS v1.5", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Speak] = Bind(LocalAudioOp.Speech, ("engine", "moss")),
                [AudioOp.CloneVoice] = Bind(LocalAudioOp.Speech, ("engine", "moss")),
            },
            ops => new AudioCaps(ops, MossLanguages, [AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Apache2, AudioPriceUnits.Free, MaxTextChars: SpeechChars, InputMaxSec: ReferenceMaxSec)),
        Model(Chatterbox, "Chatterbox Multilingual", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Speak] = Bind(LocalAudioOp.Speech, ("engine", "chatterbox")),
                [AudioOp.CloneVoice] = Bind(LocalAudioOp.Speech, ("engine", "chatterbox")),
            },
            ops => new AudioCaps(ops, ChatterboxLanguages, [AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Mit, AudioPriceUnits.Free, MaxTextChars: SpeechChars, InputMaxSec: ReferenceMaxSec)),
        Model(SeedVc, "Seed-VC", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.ConvertVoice] = Bind(LocalAudioOp.VoiceConvert, ("engine", "seedvc")),
            },
            ops => new AudioCaps(ops, [], [AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Gpl3, AudioPriceUnits.Free, InputMaxSec: InputMaxSec, LanguageNeutral: true)),
        Model(Rvc, "RVC (Applio)", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.ConvertVoice] = Bind(LocalAudioOp.VoiceConvert, ("engine", "rvc")),
                [AudioOp.TrainVoice] = Bind(LocalAudioOp.VoiceTrain),
            },
            ops => new AudioCaps(ops, [], [AudioVoiceKind.Rvc], [AudioOutputs.Audio, AudioOutputs.Model, AudioOutputs.Index],
                AudioLicenses.Mit, AudioPriceUnits.Free, InputMaxSec: InputMaxSec, LanguageNeutral: true,
                HeavyOps: [AudioOp.TrainVoice])),

        // Музыка
        Model(AceStep, "ACE-Step 1.5 XL", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Song] = Ace,
                [AudioOp.Cover] = AceEdit("cover"),
                [AudioOp.Repaint] = AceEdit("repaint"),
                [AudioOp.Extract] = AceEdit("extract"),
                [AudioOp.Lego] = AceEdit("lego"),
                [AudioOp.Complete] = AceEdit("complete"),
            },
            ops => new AudioCaps(ops, AceLanguages, [], [AudioOutputs.Audio],
                AudioLicenses.Mit, AudioPriceUnits.Free, MaxTextChars: LyricsChars, MinDurationSec: SongMinSec,
                MaxDurationSec: SongMaxSec, InputMaxSec: InputMaxSec,
                HeavyOps: [AudioOp.Extract, AudioOp.Lego, AudioOp.Complete])),
        Model(Yue2, "YuE2-3B", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Song] = Bind(LocalAudioOp.MusicGenerate, ("engine", "yue2")),
                [AudioOp.Cover] = Bind(LocalAudioOp.MusicEdit, ("task", "cover"), ("engine", "yue2")),
            },
            ops => new AudioCaps(ops, SungRuEn, [], [AudioOutputs.Audio, AudioOutputs.Score],
                AudioLicenses.CcByNc4, AudioPriceUnits.Free, MaxTextChars: LyricsChars, MinDurationSec: SongMinSec,
                MaxDurationSec: SongMaxSec, InputMaxSec: SongMaxSec)),
        Model(MiniMaxMusic, "MiniMax Music 3", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Song] = Bind(LocalAudioOp.MusicGenerate, ("engine", "minimax")),
            },
            ops => new AudioCaps(ops, SungRuEn, [], [AudioOutputs.Audio],
                AudioLicenses.NotStated, AudioPriceUnits.Free, MaxTextChars: LyricsChars, MinDurationSec: SongMinSec,
                MaxDurationSec: SongMaxSec)),

        // Обработка: стемы (audio-separator, MIT)
        Model(BsRoformer, "BS-RoFormer · вокал и минус", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Separate] = Bind(LocalAudioOp.Separate, ("mode", "vocals")),
            },
            ops => Processing(ops, [AudioOutputs.Stems], AudioLicenses.Mit)),
        Model(HtDemucs4, "HTDemucs ft · 4 стема", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Separate] = Bind(LocalAudioOp.Separate, ("mode", "4stems")),
            },
            ops => Processing(ops, [AudioOutputs.Stems], AudioLicenses.Mit)),
        Model(HtDemucs6, "HTDemucs 6s · 6 стемов", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Separate] = Bind(LocalAudioOp.Separate, ("mode", "6stems")),
            },
            ops => Processing(ops, [AudioOutputs.Stems], AudioLicenses.Mit)),
        Model(MelRoformer, "Mel-RoFormer · караоке", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Separate] = Bind(LocalAudioOp.Separate, ("mode", "karaoke")),
            },
            ops => Processing(ops, [AudioOutputs.Stems], AudioLicenses.Mit)),
        Model(DeepFilterNet, "DeepFilterNet 3", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Denoise] = Bind(LocalAudioOp.Enhance, ("mode", "denoise")),
            },
            ops => Processing(ops, [AudioOutputs.Audio], AudioLicenses.MitApache2)),
        Model(AudioSr, "AudioSR · до 48 кГц", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Upsample] = Bind(LocalAudioOp.Enhance, ("mode", "upsample")),
            },
            // Код MIT, веса Apache-2.0
            ops => Processing(ops, [AudioOutputs.Audio], AudioLicenses.MitApache2, UpsampleMaxSec)),
        Model(Matchering, "Matchering · мастеринг по образцу", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Master] = Bind(LocalAudioOp.Enhance, ("mode", "master")),
            },
            ops => Processing(ops, [AudioOutputs.Audio], AudioLicenses.Gpl3)),
        Model(Whisper, "Whisper large-v3-turbo", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.Transcribe] = new(LocalAudioOp.Transcribe, NoArgs),
            },
            ops => new AudioCaps(ops, WhisperLanguages, [], [AudioOutputs.Text, AudioOutputs.Subtitles, AudioOutputs.Lyrics],
                AudioLicenses.Mit, AudioPriceUnits.Free, InputMaxSec: InputMaxSec)),
        Model(BasicPitch, "Basic Pitch · в MIDI", new Dictionary<AudioOp, LocalBinding>
            {
                [AudioOp.ToMidi] = new(LocalAudioOp.ToMidi, NoArgs),
            },
            ops => Processing(ops, [AudioOutputs.Midi], AudioLicenses.Apache2)),
    ];

    private static AudioCaps Processing(IReadOnlyList<AudioOp> ops, IReadOnlyList<string> outputs, AudioLicense license,
        int inputMaxSec = InputMaxSec) =>
        new(ops, [], [], outputs, license, AudioPriceUnits.Free, InputMaxSec: inputMaxSec, LanguageNeutral: true);

    public static LocalModel? FindLocal(string modelId) =>
        Local.FirstOrDefault(m => string.Equals(m.Info.Id, modelId, StringComparison.OrdinalIgnoreCase));

    // Готовые дикторы Qwen3-TTS (LocalMediaService.Audio.QwenSpeakers): меняются там — правится и здесь
    public static readonly IReadOnlyList<string> QwenSpeakers =
        ["Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee"];

    // Частные параметры операций local-media под именами её инструментов — то, что адаптер шва читает
    // из аргументов сверх общих полей запроса (текст, слова, язык, длительность, кусок). Фиксированные
    // аргументы привязки (engine, task, mode у разбора и улучшения) сюда не входят: их Params не перебивают
    private static readonly IReadOnlyDictionary<LocalAudioOp, IReadOnlySet<string>> LocalParams =
        new Dictionary<LocalAudioOp, IReadOnlySet<string>>
        {
            [LocalAudioOp.Speech] = new HashSet<string>(StringComparer.Ordinal) { "speaker", "voice", "reference_text", "expressiveness" },
            [LocalAudioOp.VoiceConvert] = new HashSet<string>(StringComparer.Ordinal) { "mode", "pitch_shift" },
            [LocalAudioOp.VoiceTrain] = new HashSet<string>(StringComparer.Ordinal) { "epochs" },
            [LocalAudioOp.MusicGenerate] = new HashSet<string>(StringComparer.Ordinal) { "bpm", "key", "abc" },
            [LocalAudioOp.MusicEdit] = new HashSet<string>(StringComparer.Ordinal) { "strength", "track", "tracks" },
            [LocalAudioOp.Separate] = new HashSet<string>(StringComparer.Ordinal) { "format" },
            [LocalAudioOp.Enhance] = new HashSet<string>(StringComparer.Ordinal) { "model" },
        };

    public static IReadOnlySet<string> LocalParamNames(LocalAudioOp op) =>
        LocalParams.TryGetValue(op, out var names) ? names : new HashSet<string>();
}
