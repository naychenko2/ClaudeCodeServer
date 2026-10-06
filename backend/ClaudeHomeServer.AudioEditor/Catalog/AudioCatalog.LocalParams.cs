using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.AudioEditor.Catalog;

// Частные параметры локальных движков в форме схемы «Дополнительно» (ADR-021 §2): автоформа и проверка
// params те же, что у fal. Два сорта полей:
// - передаётся — аргументы инструментов local-media, их принимает шов ILocalAudioMedia; пределы и
//   умолчания — как в LocalMediaService.Audio (меняются там — правится здесь; расхождение ловит сторож
//   LocalAudioLimitsSyncTests);
// - не передаётся (Passed=false) — параметры, зашитые в шаблоны ComfyWorkflows.Audio и воркеры
//   CcsAudioWorker (матрица аудио-возможностей, раздел 2.6). Форма их показывает честно серыми, а запуск
//   с ними отказывает с именем поля, а не теряет значение молча. Умолчание — из шаблона; границы — только
//   там, где они известны, остальное не выдумываем.
public static partial class AudioCatalog
{
    public const string LocalNotPassed = "local-media задаёт его в шаблоне движка, шов пока не принимает";

    // Общие поля запроса под именами local-media и ссылки на входы: в params их не дублируют
    private static readonly string[] LocalReserved =
    [
        "text", "prompt", "lyrics", "language", "duration_seconds", "start_seconds", "end_seconds", "seed",
        "audio", "reference", "audios", "voice_model", "voice_index",
    ];

    private sealed record LocalParam(AudioParamField Field, IReadOnlyList<AudioOp>? Ops = null);

    private static JsonNode V(object value) => value switch
    {
        string str => JsonValue.Create(str),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        _ => throw new ArgumentException("Тип умолчания не поддержан: " + value.GetType()),
    };

    private static IReadOnlyList<JsonNode> Values(params object[] values) => [.. values.Select(V)];

    private static LocalParam P(AudioParamField field, params AudioOp[] ops) => new(field, ops.Length == 0 ? null : ops);

    private static AudioParamField Num(string key, string title, double? def, double? min = null, double? max = null,
        bool integer = false, bool passed = true) =>
        new(key, integer ? AudioParamTypes.Integer : AudioParamTypes.Number, title, Default: def is { } d ? V(d) : null,
            Min: min, Max: max, Passed: passed, NotPassed: passed ? null : LocalNotPassed);

    private static AudioParamField Hidden(string key, string title, double? def, double? min = null, double? max = null,
        bool integer = false) => Num(key, title, def, min, max, integer, passed: false);

    private static AudioParamField Choice(string key, string title, object def, IReadOnlyList<JsonNode> values, bool passed = true) =>
        new(key, values[0].GetValueKind() == System.Text.Json.JsonValueKind.String ? AudioParamTypes.String : AudioParamTypes.Integer,
            title, Default: V(def), Enum: values, Passed: passed, NotPassed: passed ? null : LocalNotPassed);

    private static readonly IReadOnlyList<JsonNode> AceTrackValues = Values(
        "vocals", "backing_vocals", "drums", "bass", "guitar", "keyboard", "percussion", "strings", "synth", "fx", "brass",
        "woodwinds");

    private static readonly IReadOnlyList<JsonNode> AceKeyValues = Values(
        "C major", "C# major", "Db major", "D major", "D# major", "Eb major", "E major", "F major", "F# major",
        "Gb major", "G major", "G# major", "Ab major", "A major", "A# major", "Bb major", "B major", "C minor",
        "C# minor", "Db minor", "D minor", "D# minor", "Eb minor", "E minor", "F minor", "F# minor", "Gb minor",
        "G minor", "G# minor", "Ab minor", "A minor", "A# minor", "Bb minor", "B minor");

    private static readonly Dictionary<string, LocalParam[]> LocalParamTable = new(StringComparer.OrdinalIgnoreCase)
    {
        [QwenTts] =
        [
            P(Choice("speaker", "Диктор", "Vivian",
                Values("Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee")), AudioOp.Speak),
            P(new AudioParamField("voice", AudioParamTypes.String, "Голос словами или манера диктора", MaxLength: 500),
                AudioOp.Speak, AudioOp.DesignVoice),
            P(new AudioParamField("reference_text", AudioParamTypes.String, "Расшифровка образца", MaxLength: 2000),
                AudioOp.CloneVoice),
        ],
        [Chatterbox] =
        [
            P(Num("expressiveness", "Выразительность", 0.5, 0.25, 2)),
            P(Hidden("cfg_weight", "Следование образцу", null, 0, 1)),
        ],
        [SeedVc] =
        [
            P(Choice("mode", "Речь или пение", "speech", Values("speech", "singing"))),
            P(Num("pitch_shift", "Сдвиг высоты, полутоны", 0, -24, 24, integer: true)),
            P(Hidden("diffusion_steps", "Шаги диффузии", null, 4, 100, integer: true)),
            P(new AudioParamField("convert_style", AudioParamTypes.Boolean, "Переносить манеру образца",
                Passed: false, NotPassed: LocalNotPassed)),
        ],
        [Rvc] =
        [
            P(Num("pitch_shift", "Сдвиг высоты, полутоны", 0, -24, 24, integer: true), AudioOp.ConvertVoice),
            P(Hidden("index_rate", "Вес индекса тембра", null, 0, 1), AudioOp.ConvertVoice),
            P(Num("epochs", "Эпохи обучения", 200, 20, 1000, integer: true), AudioOp.TrainVoice),
            P(Choice("sample_rate", "Частота модели", 40000, Values(32000, 40000, 48000), passed: false), AudioOp.TrainVoice),
            P(Hidden("batch_size", "Размер пакета", null, 1, 32, integer: true), AudioOp.TrainVoice),
        ],
        [AceStep] =
        [
            P(Num("bpm", "Темп, BPM", 120, 40, 220, integer: true), AudioOp.Song),
            P(Choice("key", "Тональность", "C major", AceKeyValues), AudioOp.Song),
            P(new AudioParamField("timesignature", AudioParamTypes.String, "Размер такта", Default: V("4"),
                Passed: false, NotPassed: LocalNotPassed), AudioOp.Song),
            P(Hidden("cfg_scale", "CFG языковой модели", 2.0), AudioOp.Song),
            P(Hidden("temperature", "Температура", 0.85), AudioOp.Song),
            P(Hidden("top_p", "top_p", 0.9), AudioOp.Song),
            P(Hidden("steps", "Шаги", 50, integer: true), AudioOp.Song),
            P(Hidden("cfg", "CFG", 7.0), AudioOp.Song),
            P(Hidden("shift", "Сдвиг расписания", 3.0), AudioOp.Song),
            P(Num("strength", "Близость к исходнику", 0.6, 0, 1), AudioOp.Cover),
            P(Choice("track", "Дорожка", "vocals", AceTrackValues), AudioOp.Extract, AudioOp.Lego),
            P(new AudioParamField("tracks", AudioParamTypes.Array, "Инструменты",
                Default: new JsonArray("drums", "bass"),
                Items: new AudioParamField("item", AudioParamTypes.String, Enum: AceTrackValues)), AudioOp.Complete),
            P(Hidden("steps", "Шаги", null, integer: true),
                AudioOp.Cover, AudioOp.Repaint, AudioOp.Extract, AudioOp.Lego, AudioOp.Complete),
            P(Hidden("guidance", "CFG", 7.0),
                AudioOp.Cover, AudioOp.Repaint, AudioOp.Extract, AudioOp.Lego, AudioOp.Complete),
            P(Hidden("shift", "Сдвиг расписания", 3.0),
                AudioOp.Cover, AudioOp.Repaint, AudioOp.Extract, AudioOp.Lego, AudioOp.Complete),
        ],
        [Yue2] =
        [
            P(new AudioParamField("abc", AudioParamTypes.String, "Партитура ABC прошлой генерации", MaxLength: 20000), AudioOp.Song),
            P(Hidden("temperature", "Температура", 1.0)),
            P(Hidden("top_k", "top_k", 100, integer: true)),
            P(Hidden("repetition_penalty", "Штраф за повторы", 1.2)),
        ],
        [MiniMaxMusic] =
        [
            P(Hidden("cfg", "CFG", 1.7)),
            P(Hidden("top_k", "top_k", 50, integer: true)),
            P(Hidden("steps", "Шаги", 30, integer: true)),
        ],
        [BsRoformer] = [P(Choice("format", "Формат стемов", "mp3", Values("mp3", "wav", "flac")))],
        [HtDemucs4] = [P(Choice("format", "Формат стемов", "mp3", Values("mp3", "wav", "flac")))],
        [HtDemucs6] = [P(Choice("format", "Формат стемов", "mp3", Values("mp3", "wav", "flac")))],
        [MelRoformer] = [P(Choice("format", "Формат стемов", "mp3", Values("mp3", "wav", "flac")))],
        [DeepFilterNet] = [P(Hidden("atten_db", "Предел подавления, дБ", null, 0, 100))],
        [AudioSr] =
        [
            P(Choice("model", "Модель", "basic", Values("basic", "speech"))),
            P(Hidden("ddim_steps", "Шаги DDIM", null, 10, 200, integer: true)),
            P(Hidden("guidance_scale", "CFG", null, 1, 10)),
        ],
        [BasicPitch] =
        [
            P(Hidden("onset_threshold", "Порог начала ноты", null)),
            P(Hidden("frame_threshold", "Порог кадра", null)),
            P(Hidden("min_note_ms", "Кратчайшая нота, мс", null)),
        ],
        [Whisper] =
        [
            P(Hidden("beam_size", "Ширина луча", 5, integer: true)),
            P(new AudioParamField("vad_filter", AudioParamTypes.Boolean, "Отсекать тишину (VAD)",
                Passed: false, NotPassed: LocalNotPassed)),
        ],
    };

    // Схема «Дополнительно» локальной модели для операции; null — модель операцию не умеет. Фиксированные
    // аргументы привязки (engine, task, mode) — тоже не для params: их выбирает каталог
    public static AudioParamSchema? LocalSchema(string modelId, AudioOp op)
    {
        if (FindLocal(modelId) is not { } model || !model.Bindings.TryGetValue(op, out var binding)) return null;
        var fields = LocalParamTable.TryGetValue(model.Info.Id, out var all)
            ? all.Where(p => p.Ops is null || p.Ops.Contains(op)).Select(p => p.Field).ToList()
            : [];
        var reserved = LocalReserved.Concat(binding.Args.Keys).Distinct(StringComparer.Ordinal).ToList();
        return new AudioParamSchema("local", model.Info.Id, AudioSchemaSources.LocalCatalog, fields, reserved);
    }
}
