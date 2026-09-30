using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Аудио-операции local-media. Аргументы инструмента приходят как есть (LocalMediaRequest.Args):
// здесь они проходят белые списки и пределы, входы грузятся в input ComfyUI, собирается граф.
// Музыка по тексту — нативные ноды ComfyUI, остальное — узел CcsAudioWorker с воркером в своём
// venv (deploy/comfyui/audio). Время (ETA) — по замерам стенда 2026-09-30 (docs/features/local-media.md,
// раздел «Аудио»); где замера нет — null, время не обещаем.
public sealed partial class LocalMediaService
{
    public const int MaxSpeechTextLength = 5000;
    public const int MaxTrainClips = 20;
    public const int MaxAbcLength = 20000;

    public static readonly IReadOnlyList<string> MusicEngines = ["ace", "yue2", "minimax"];
    public static readonly IReadOnlyList<string> MusicEditTasks = ["cover", "repaint", "extract", "lego", "complete"];
    public static readonly IReadOnlyList<string> AceTracks =
        ["vocals", "backing_vocals", "drums", "bass", "guitar", "keyboard", "percussion", "strings", "synth", "fx",
         "brass", "woodwinds"];
    public static readonly IReadOnlyList<string> SeparateModes = ["vocals", "4stems", "6stems", "karaoke"];
    public static readonly IReadOnlyList<string> EnhanceModes = ["denoise", "upsample", "master"];
    public static readonly IReadOnlyList<string> QwenSpeakers =
        ["Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee"];

    // Язык речи — код ISO. Qwen3-TTS понимает 10 языков (названием), Chatterbox — 23 (кодом)
    public static readonly IReadOnlyDictionary<string, string> QwenLanguages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ru"] = "russian", ["en"] = "english", ["zh"] = "chinese", ["ja"] = "japanese", ["ko"] = "korean",
        ["de"] = "german", ["fr"] = "french", ["pt"] = "portuguese", ["es"] = "spanish", ["it"] = "italian",
    };

    public static readonly IReadOnlySet<string> ChatterboxLanguages = new HashSet<string>(StringComparer.Ordinal)
    {
        "ar", "da", "de", "el", "en", "es", "fi", "fr", "he", "hi", "it", "ja", "ko", "ms", "nl", "no", "pl", "pt",
        "ru", "sv", "sw", "tr", "zh",
    };

    // Тяжёлые аудио-операции держат GPU десятки минут: обучение голоса и правка трека моделью
    // xl-base (20 ГБ весов fp32)
    public static bool IsHeavyAudio(LocalMediaRequest request) =>
        request.Op == LocalMediaOps.VoiceTrain
        || (request.Op == LocalMediaOps.MusicEdit
            && Str(request.Args, "task") is "extract" or "lego" or "complete");

    private async Task<(JsonObject Graph, int? EtaSeconds)> BuildAudioAsync(LocalMediaRequest request, LocalMediaJob job,
        string root, string prompt, long seed, string prefix, LocalMediaOptions options, CancellationToken ct)
    {
        var a = request.Args ?? new JsonObject();
        switch (request.Op)
        {
            case LocalMediaOps.MusicGenerate:
                return BuildMusic(a, job, prompt, seed, prefix);

            case LocalMediaOps.MusicEdit:
            {
                var task = OneOf(a, "task", "cover", MusicEditTasks);
                if (task is "cover" or "repaint" && prompt.Length == 0)
                    throw new LocalMediaInputException("Для cover и repaint нужен prompt — стиль и содержание результата.");
                var (name, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                var p = new JsonObject { ["task"] = task, ["prompt"] = prompt, ["seed"] = seed };
                if (Str(a, "lyrics") is { } lyrics) p["lyrics"] = Limit(lyrics, ComfyWorkflows.MaxLyricsLength, "lyrics");
                switch (task)
                {
                    case "cover":
                        p["strength"] = Range(a, "strength", 0.6, 0, 1);
                        break;
                    case "repaint":
                        var start = Range(a, "start_seconds", 0, 0, 600);
                        var end = Range(a, "end_seconds", -1, -1, 600);
                        if (end >= 0 && end <= start)
                            throw new LocalMediaInputException("end_seconds должен быть больше start_seconds (или −1 — до конца).");
                        p["start"] = start;
                        p["end"] = end;
                        break;
                    case "extract" or "lego":
                        p["track"] = OneOf(a, "track", "vocals", AceTracks);
                        break;
                    default:
                        var tracks = List(a, "tracks");
                        if (tracks.Count == 0) tracks = ["drums", "bass"];
                        if (tracks.Any(t => !AceTracks.Contains(t)))
                            throw new LocalMediaInputException("tracks — из списка: " + string.Join(", ", AceTracks) + ".");
                        p["tracks"] = new JsonArray([.. tracks.Select(t => (JsonNode)t)]);
                        break;
                }
                job.Engine = task is "extract" or "lego" or "complete" ? "ace-xl-base" : "ace-turbo";
                return (ComfyWorkflows.AudioWorker("music_edit", p, [name], job.Id, 60), MusicEditEta(task, seconds));
            }

            case LocalMediaOps.Speech:
                return await BuildSpeechAsync(request, a, job, root, seed, options, ct);

            case LocalMediaOps.VoiceConvert:
            {
                var engine = OneOf(a, "engine", "seedvc", ["seedvc", "rvc"]);
                var (source, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                var shift = (int)Range(a, "pitch_shift", 0, -24, 24);
                job.Engine = engine;
                if (engine == "seedvc")
                {
                    var mode = OneOf(a, "mode", "speech", ["speech", "singing"]);
                    var (target, _) = await AudioInputAsync(request, job, root, Required(a, "reference"), "ref", options, ct);
                    var p = new JsonObject { ["mode"] = mode, ["pitch_shift"] = shift };
                    return (ComfyWorkflows.AudioWorker("voice_convert", p, [source, target], job.Id, 30),
                        VoiceConvertEta(mode, seconds));
                }
                var model = await UploadInputAsync(request, job, root, Required(a, "voice_model"), MediaKind.VoiceModel,
                    $"{job.Id}-voice", ct);
                var inputs = new List<string> { source, model };
                if (Str(a, "voice_index") is { } index)
                    inputs.Add(await UploadInputAsync(request, job, root, index, MediaKind.VoiceIndex, $"{job.Id}-voice", ct));
                return (ComfyWorkflows.AudioWorker("rvc_convert", new JsonObject { ["pitch_shift"] = shift }, inputs, job.Id, 30),
                    RvcConvertEta(seconds));
            }

            case LocalMediaOps.VoiceTrain:
            {
                var clips = List(a, "audios");
                if (clips.Count is < 1 or > MaxTrainClips)
                    throw new LocalMediaInputException($"Нужно от 1 до {MaxTrainClips} записей голоса (audios).");
                var names = new List<string>();
                double total = 0;
                for (var k = 0; k < clips.Count; k++)
                {
                    var (name, seconds) = await AudioInputAsync(request, job, root, clips[k], $"clip{k + 1}", options, ct);
                    names.Add(name);
                    total += seconds ?? 0;
                }
                var epochs = (int)Range(a, "epochs", 200, 20, 1000);
                job.Engine = "rvc";
                job.InputSeconds = total;
                return (ComfyWorkflows.AudioWorker("rvc_train", new JsonObject { ["epochs"] = epochs }, names, job.Id, 240),
                    VoiceTrainEta(total, epochs));
            }

            case LocalMediaOps.AudioSeparate:
            {
                var mode = OneOf(a, "mode", "vocals", SeparateModes);
                var format = OneOf(a, "format", "mp3", ["mp3", "wav", "flac"]);
                var (name, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                job.Engine = mode;
                return (ComfyWorkflows.AudioWorker("separate", new JsonObject { ["mode"] = mode, ["format"] = format },
                    [name], job.Id, 30), SeparateEta(mode, seconds));
            }

            case LocalMediaOps.AudioToMidi:
            {
                var (name, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                return (ComfyWorkflows.AudioWorker("audio_to_midi", new JsonObject(), [name], job.Id, 15),
                    seconds is { } s ? 4 + (int)Math.Ceiling(s * 0.01) : null);
            }

            case LocalMediaOps.AudioEnhance:
            {
                var mode = OneOf(a, "mode", "denoise", EnhanceModes);
                var (name, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                job.Engine = mode;
                switch (mode)
                {
                    case "denoise":
                        return (ComfyWorkflows.AudioWorker("denoise", new JsonObject(), [name], job.Id, 15),
                            seconds is { } d ? 5 + (int)Math.Ceiling(d * 0.05) : null);
                    case "upsample":
                        if (seconds > UpsampleMaxSeconds)
                            throw new LocalMediaInputException($"Расширение частот — для записей до {UpsampleMaxSeconds} с: "
                                + "оно медленное. Отрежь нужный кусок.");
                        var model = OneOf(a, "model", "basic", ["basic", "speech"]);
                        return (ComfyWorkflows.AudioWorker("upsample", new JsonObject { ["model"] = model }, [name], job.Id, 30),
                            UpsampleEta(seconds));
                    default:
                        var (reference, _) = await AudioInputAsync(request, job, root, Required(a, "reference"), "ref", options, ct);
                        return (ComfyWorkflows.AudioWorker("master", new JsonObject(), [name, reference], job.Id, 15),
                            seconds is { } m ? 18 + (int)Math.Ceiling(m * 0.02) : null);
                }
            }

            case LocalMediaOps.Transcribe:
            {
                var (name, seconds) = await AudioInputAsync(request, job, root, Required(a, "audio"), "src", options, ct);
                var p = new JsonObject();
                if (Str(a, "language") is { } language)
                {
                    if (language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetterLower))
                        throw new LocalMediaInputException("language — код ISO 639-1 (ru, en, …) или пусто для автоопределения.");
                    p["language"] = language;
                }
                return (ComfyWorkflows.AudioWorker("transcribe", p, [name], job.Id, 30), TranscribeEta(seconds));
            }

            default:
                throw new LocalMediaInputException("Неизвестная операция.");
        }
    }

    private static (JsonObject Graph, int? EtaSeconds) BuildMusic(JsonObject a, LocalMediaJob job, string prompt, long seed,
        string prefix)
    {
        if (prompt.Length == 0)
            throw new LocalMediaInputException("Нужен prompt — жанр, настроение, инструменты и голос.");
        var engine = OneOf(a, "engine", "ace", MusicEngines);
        var lyrics = Limit(Str(a, "lyrics") ?? "", ComfyWorkflows.MaxLyricsLength, "lyrics");
        var seconds = (int)Range(a, "duration_seconds", 120, ComfyWorkflows.MinMusicSeconds, ComfyWorkflows.MaxMusicSeconds);
        job.Engine = engine;
        job.DurationSeconds = seconds;
        switch (engine)
        {
            case "ace":
            {
                var language = Str(a, "language") ?? "unknown";
                if (!ComfyWorkflows.AceLanguages.Contains(language))
                    throw new LocalMediaInputException("language — код языка вокала (ru, en, …) или unknown.");
                var bpm = (int)Range(a, "bpm", 120, 40, 220);
                var key = Str(a, "key") ?? "C major";
                if (!ComfyWorkflows.AceKeys.Contains(key))
                    throw new LocalMediaInputException("key — тональность вида «A minor» или «F# major».");
                return (ComfyWorkflows.AceMusic(prompt, lyrics, seconds, language, bpm, key, seed, prefix), AceMusicEta(seconds));
            }
            case "yue2":
            {
                if (lyrics.Length == 0)
                    throw new LocalMediaInputException("YuE2 поёт по словам — передай lyrics (для инструментала возьми engine=ace).");
                var abc = Str(a, "abc");
                if (abc is not null) Limit(abc, MaxAbcLength, "abc");
                return (ComfyWorkflows.YuE2Music(prompt, lyrics, abc, seconds, seed, prefix), YuE2MusicEta(seconds));
            }
            default:
                return (ComfyWorkflows.MiniMaxMusic(prompt, lyrics, seconds, seed, prefix), MiniMaxMusicEta(seconds));
        }
    }

    private async Task<(JsonObject Graph, int? EtaSeconds)> BuildSpeechAsync(LocalMediaRequest request, JsonObject a,
        LocalMediaJob job, string root, long seed, LocalMediaOptions options, CancellationToken ct)
    {
        var text = Limit(Str(a, "text") ?? "", MaxSpeechTextLength, "text");
        if (text.Length == 0) throw new LocalMediaInputException("Нужен text — что озвучить.");
        var engine = OneOf(a, "engine", "qwen", ["qwen", "chatterbox"]);
        var language = Str(a, "language") ?? "ru";
        var voice = Str(a, "voice");
        var speaker = Str(a, "speaker");
        var reference = Str(a, "reference");
        var refText = Str(a, "reference_text");
        job.Engine = engine;
        job.InputSeconds = null;

        var p = new JsonObject { ["text"] = text, ["seed"] = seed };
        var inputs = new List<string>();
        if (reference is not null)
        {
            if (voice is not null || speaker is not null)
                throw new LocalMediaInputException("Голос задаётся чем-то одним: reference (образец), voice (описание) или speaker.");
            var (name, seconds) = await AudioInputAsync(request, job, root, reference, "ref", options, ct);
            if (seconds is > 60)
                throw new LocalMediaInputException("Образец голоса — до 60 секунд (хватает 5–15 с чистой речи).");
            inputs.Add(name);
        }

        string op;
        if (engine == "chatterbox")
        {
            if (voice is not null || speaker is not null)
                throw new LocalMediaInputException("У engine=chatterbox голос задаёт только образец reference; описание голоса "
                    + "(voice) и дикторы (speaker) — у engine=qwen.");
            if (!ChatterboxLanguages.Contains(language))
                throw new LocalMediaInputException("language для chatterbox — одно из: " + string.Join(", ", ChatterboxLanguages) + ".");
            p["language"] = language;
            p["exaggeration"] = Range(a, "expressiveness", 0.5, 0.25, 2.0);
            op = "tts_chatterbox";
        }
        else
        {
            if (!QwenLanguages.TryGetValue(language, out var qwenLanguage))
                throw new LocalMediaInputException("language для qwen — одно из: " + string.Join(", ", QwenLanguages.Keys) + ".");
            p["language"] = qwenLanguage;
            if (reference is not null)
            {
                op = "voice_clone";
                if (refText is not null) p["ref_text"] = Limit(refText, 2000, "reference_text");
            }
            else
            {
                op = "tts";
                if (speaker is not null)
                {
                    if (!QwenSpeakers.Contains(speaker))
                        throw new LocalMediaInputException("speaker — один из: " + string.Join(", ", QwenSpeakers) + ".");
                    p["speaker"] = speaker;
                    if (voice is not null) p["instruct"] = Limit(voice, 500, "voice");
                }
                else if (voice is not null)
                {
                    p["voice"] = Limit(voice, 500, "voice");
                }
            }
        }
        return (ComfyWorkflows.AudioWorker(op, p, inputs, job.Id, 20),
            SpeechEta(engine, text.Length, reference is not null && refText is null && engine == "qwen"));
    }

    // Звук из проекта (или job_id прошлой задачи) — в input ComfyUI; длина — для потолка и ETA
    private async Task<(string Name, double? Seconds)> AudioInputAsync(LocalMediaRequest request, LocalMediaJob job,
        string root, string reference, string stem, LocalMediaOptions options, CancellationToken ct)
    {
        var input = ReadInput(request, root, reference, MediaKind.Audio);
        var seconds = AudioProbe.Seconds(input.Bytes);
        if (seconds > options.MaxAudioInputSeconds)
            throw new LocalMediaInputException($"Звук «{reference}» — ≈{seconds:0} с, а локально берём до "
                + $"{options.MaxAudioInputSeconds} с.");
        job.InputSeconds ??= seconds is { } s ? Math.Round(s, 1) : null;
        return (await UploadAsync(job, input.Bytes, $"{job.Id}-{stem}{input.Extension}", ct), seconds);
    }

    // --- Время по замерам стенда (RTX 3090, холодная загрузка модели включена) ---

    public const int UpsampleMaxSeconds = 120;

    // ACE-Step XL-sft + LM 4B: 180 с песни — 74 с
    public static int AceMusicEta(int seconds) => 20 + (int)Math.Ceiling(seconds * 0.3);

    // YuE2 int8: 180 с — 60 с
    public static int YuE2MusicEta(int seconds) => 10 + (int)Math.Ceiling(seconds * 0.28);

    public static int? MiniMaxMusicEta(int seconds) => null;

    public static int? MusicEditEta(string task, double? seconds) => null;

    // Qwen3-TTS: ~12 знаков русского текста на секунду речи, синтез ≈ длине речи плюс загрузка;
    // без расшифровки образца её делает whisper на CPU (+12 с). Chatterbox быстрее ≈ на треть
    public static int SpeechEta(string engine, int chars, bool transcribeReference)
    {
        var speech = chars / 12.0;
        return engine == "chatterbox"
            ? 20 + (int)Math.Ceiling(speech * 0.7)
            : 15 + (int)Math.Ceiling(speech * 1.0) + (transcribeReference ? 12 : 0);
    }

    // Seed-VC с холодной загрузкой: речь 17 с — 13,8 с, пение 30 с — 15,8 с
    public static int? VoiceConvertEta(string mode, double? seconds) =>
        seconds is { } s ? 8 + (int)Math.Ceiling(s * 0.3) : null;

    // RVC: 17 с речи — 11 с
    public static int? RvcConvertEta(double? seconds) => seconds is { } s ? 8 + (int)Math.Ceiling(s * 0.2) : null;

    // RVC-обучение на 101 с речи, 100 эпох: подготовка и признаки ≈30 с, эпоха ≈2 с — время эпохи
    // растёт с длиной записей (≈0,02 с на секунду звука)
    public static int? VoiceTrainEta(double totalSeconds, int epochs) =>
        totalSeconds <= 0 ? null : 40 + (int)Math.Ceiling(totalSeconds * 0.2 + totalSeconds * 0.02 * epochs);

    // Стемы трека 180 с: vocals (BS-RoFormer) 71 с, 4stems (HTDemucs ft) 38 с, 6stems 23 с,
    // karaoke (Mel-RoFormer) 74 с
    public static int? SeparateEta(string mode, double? seconds) => seconds is not { } s ? null : mode switch
    {
        "vocals" => 5 + (int)Math.Ceiling(s * 0.37),
        "4stems" => 3 + (int)Math.Ceiling(s * 0.2),
        "6stems" => 3 + (int)Math.Ceiling(s * 0.11),
        "karaoke" => 5 + (int)Math.Ceiling(s * 0.38),
        _ => null,
    };

    // AudioSR кусками по 20 с: 30 с — 53 с, 120 с — 85 с (загрузка ≈15 с)
    public static int? UpsampleEta(double? seconds) => seconds is { } s ? 20 + (int)Math.Ceiling(s * 0.6) : null;

    // Whisper large-v3-turbo на GPU: 180 с вокала — 6,5 с, 101 с речи — 4,8 с
    public static int? TranscribeEta(double? seconds) => seconds is { } s ? 3 + (int)Math.Ceiling(s * 0.02) : null;

    // --- Разбор аргументов: только строки, числа в пределах и значения из белых списков ---

    private static string? Str(JsonObject? a, string name) =>
        a?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Required(JsonObject a, string name) =>
        Str(a, name) ?? throw new LocalMediaInputException($"Нужен параметр {name}.");

    private static string Limit(string value, int max, string name) =>
        value.Length <= max ? value : throw new LocalMediaInputException($"{name} длиннее {max} символов.");

    private static string OneOf(JsonObject a, string name, string fallback, IReadOnlyCollection<string> allowed)
    {
        var value = Str(a, name) ?? fallback;
        return allowed.Contains(value)
            ? value
            : throw new LocalMediaInputException($"{name} — одно из: {string.Join(", ", allowed)}.");
    }

    private static double Range(JsonObject a, string name, double fallback, double min, double max)
    {
        if (a[name] is null) return fallback;
        // Узел числа бывает double, int или long — JsonValue не приводит одно к другому
        double? value = a[name] is not JsonValue v ? null
            : v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<long>(out var l) ? l
            : null;
        return value is { } x && x >= min && x <= max
            ? x
            : throw new LocalMediaInputException($"{name} — число от {min} до {max}.");
    }

    private static List<string> List(JsonObject a, string name) =>
        a[name] is JsonArray list
            ? list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : null)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : [];
}
