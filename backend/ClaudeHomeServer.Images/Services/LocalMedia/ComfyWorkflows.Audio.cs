using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Аудио. Музыка по тексту — нативные ноды ComfyUI по официальным шаблонам (workflow_templates:
// audio_ace_step1_5_xl_sft, audio_yue2_text2music, audio_minimax_music_3). Всё остальное (голос,
// стемы, MIDI, реставрация, правка ACE-Step) — один узел CcsAudioWorker: он выгружает модели
// ComfyUI и запускает модель в её venv (deploy/comfyui/audio). Узел сам держит белый список
// операций; снаружи в него попадают только операция из нашего списка, параметры JSON и имена
// файлов, которые мы сами загрузили в input ComfyUI.
public static partial class ComfyWorkflows
{
    public const int MinMusicSeconds = 10;
    public const int MaxMusicSeconds = 240;
    public const int MaxLyricsLength = 5000;

    public const string AceUnet = "acestep_v1.5_xl_sft_bf16.safetensors";
    public const string AceLm = "qwen_4b_ace15.safetensors";
    private const string AceEmbed = "qwen_0.6b_ace15.safetensors";
    private const string AceVae = "ace_1.5_vae.safetensors";
    public const string YuE2Checkpoint = "yue2_3b_int8_convrot.safetensors";
    private const string MiniMaxUnet = "minimax_music3_dit_fp16.safetensors";
    private const string MiniMaxClip = "minimax_music3_text_encoder_pruned_int8_convrot.safetensors";
    private const string MiniMaxVae = "minimax_music3_dav.safetensors";

    // Языки вокала ACE-Step 1.5 — список COMBO ноды TextEncodeAceStepAudio1.5
    public static readonly IReadOnlySet<string> AceLanguages = new HashSet<string>(StringComparer.Ordinal)
    {
        "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hr", "ht",
        "hu", "id", "is", "it", "ja", "ko", "la", "lt", "ms", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sa",
        "sk", "sr", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "vi", "yue", "zh", "unknown",
    };

    public static readonly IReadOnlyList<string> AceKeys =
    [
        "C major", "C# major", "Db major", "D major", "D# major", "Eb major", "E major", "F major", "F# major",
        "Gb major", "G major", "G# major", "Ab major", "A major", "A# major", "Bb major", "B major", "C minor",
        "C# minor", "Db minor", "D minor", "D# minor", "Eb minor", "E minor", "F minor", "F# minor", "Gb minor",
        "G minor", "G# minor", "Ab minor", "A minor", "A# minor", "Bb minor", "B minor",
    ];

    // ACE-Step 1.5 XL-sft + LM 4B: шаблон audio_ace_step1_5_xl_sft (50 шагов, cfg 7, shift 3).
    // Пустые слова — инструментал: тег [Instrumental], как в документации ACE-Step
    public static JsonObject AceMusic(string tags, string lyrics, double seconds, string language, int bpm,
        string key, long seed, string filenamePrefix)
    {
        return new JsonObject
        {
            ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = AceUnet, ["weight_dtype"] = "default" }),
            ["msamp"] = Node("ModelSamplingAuraFlow", new JsonObject { ["model"] = Link("unet"), ["shift"] = 3.0 }),
            ["clip"] = Node("DualCLIPLoader", new JsonObject
            {
                ["clip_name1"] = AceEmbed,
                ["clip_name2"] = AceLm,
                ["type"] = "ace",
                ["device"] = "default",
            }),
            ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = AceVae }),
            ["enc"] = Node("TextEncodeAceStepAudio1.5", new JsonObject
            {
                ["clip"] = Link("clip"),
                ["tags"] = tags,
                ["lyrics"] = lyrics.Length == 0 ? "[Instrumental]" : lyrics,
                ["seed"] = seed,
                ["bpm"] = bpm,
                ["duration"] = seconds,
                ["timesignature"] = "4",
                ["language"] = language,
                ["keyscale"] = key,
                ["generate_audio_codes"] = true,
                ["cfg_scale"] = 2.0,
                ["temperature"] = 0.85,
                ["top_p"] = 0.9,
                ["top_k"] = 0,
                ["min_p"] = 0.0,
            }),
            ["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("enc") }),
            ["lat"] = Node("EmptyAceStep1.5LatentAudio", new JsonObject { ["seconds"] = seconds, ["batch_size"] = 1 }),
            ["ks"] = Node("KSampler", new JsonObject
            {
                ["model"] = Link("msamp"),
                ["positive"] = Link("enc"),
                ["negative"] = Link("neg"),
                ["latent_image"] = Link("lat"),
                ["seed"] = seed,
                ["steps"] = 50,
                ["cfg"] = 7.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0,
            }),
            ["dec"] = Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link("ks"), ["vae"] = Link("vae") }),
            ["save"] = SaveMp3("dec", filenamePrefix),
        };
    }

    // YuE2-3B int8: шаблон audio_yue2_text2music. Партитура ABC — ступень между словами и звуком:
    // без готовой abc её пишет YuE2GenerateABC, а PreviewAny отдаёт её текстом в истории — так
    // партитура доезжает до проекта, её можно поправить и передать обратно
    public static JsonObject YuE2Music(string style, string lyrics, string? abc, double seconds, long seed,
        string filenamePrefix)
    {
        var wf = new JsonObject
        {
            ["ckpt"] = Node("CheckpointLoaderSimple", new JsonObject { ["ckpt_name"] = YuE2Checkpoint }),
        };
        JsonNode score;
        if (string.IsNullOrWhiteSpace(abc))
        {
            wf["abc"] = Node("YuE2GenerateABC", new JsonObject
            {
                ["clip"] = Link("ckpt", 1),
                ["style"] = style,
                ["lyrics"] = lyrics,
                ["seed"] = seed,
                ["mode"] = "full",
                ["max_abc_tokens"] = 8192,
                ["temperature"] = 0.7,
                ["top_p"] = 0.9,
                ["top_k"] = 30,
                ["repetition_penalty"] = 1.005,
                ["penalty_window"] = 100,
            });
            wf["score"] = Node("PreviewAny", new JsonObject { ["source"] = Link("abc") });
            score = Link("abc");
        }
        else
        {
            score = abc;
        }
        wf["gen"] = Node("YuE2GenerateMusic", new JsonObject
        {
            ["clip"] = Link("ckpt", 1),
            ["style"] = style,
            ["lyrics"] = lyrics,
            ["abc"] = score,
            ["seed"] = seed,
            ["mode"] = "full",
            ["max_duration"] = seconds,
            ["temperature"] = 1.0,
            ["top_p"] = 0.95,
            ["top_k"] = 100,
            ["repetition_penalty"] = 1.2,
        });
        wf["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("gen") });
        wf["lat"] = Node("EmptyYuE2LatentAudio", new JsonObject { ["seconds"] = Link("gen", 1), ["batch_size"] = 1 });
        wf["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("ckpt"),
            ["positive"] = Link("gen"),
            ["negative"] = Link("neg"),
            ["latent_image"] = Link("lat"),
            ["seed"] = seed,
            ["steps"] = 32,
            ["cfg"] = 1.0,
            ["sampler_name"] = "dpm_2",
            ["scheduler"] = "sgm_uniform",
            ["denoise"] = 1.0,
        });
        wf["dec"] = Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link("ks"), ["vae"] = Link("ckpt", 2) });
        wf["save"] = SaveMp3("dec", filenamePrefix);
        return wf;
    }

    // MiniMax Music 3: шаблон audio_minimax_music_3 (30 шагов, cfg 1.7, тайловый декод)
    public static JsonObject MiniMaxMusic(string caption, string lyrics, double seconds, long seed, string filenamePrefix) => new()
    {
        ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = MiniMaxUnet, ["weight_dtype"] = "default" }),
        ["clip"] = Node("CLIPLoader", new JsonObject
        {
            ["clip_name"] = MiniMaxClip,
            ["type"] = "minimax",
            ["device"] = "default",
        }),
        ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = MiniMaxVae }),
        ["enc"] = Node("MiniMaxMusic3TextEncode", new JsonObject
        {
            ["clip"] = Link("clip"),
            ["caption"] = caption,
            ["lyrics"] = lyrics,
            ["seed"] = seed,
            ["max_duration"] = seconds,
            ["cfg_scale"] = 1.7,
            ["top_k"] = 50,
        }),
        ["neg"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("enc") }),
        ["lat"] = Node("EmptyMiniMaxMusic3LatentAudio", new JsonObject { ["seconds"] = Link("enc", 1), ["batch_size"] = 1 }),
        ["ks"] = Node("KSampler", new JsonObject
        {
            ["model"] = Link("unet"),
            ["positive"] = Link("enc"),
            ["negative"] = Link("neg"),
            ["latent_image"] = Link("lat"),
            ["seed"] = seed,
            ["steps"] = 30,
            ["cfg"] = 1.7,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0,
        }),
        ["dec"] = Node("VAEDecodeAudioTiled", new JsonObject
        {
            ["samples"] = Link("ks"),
            ["vae"] = Link("vae"),
            ["tile_size"] = 1536,
            ["overlap"] = 64,
        }),
        ["save"] = SaveMp3("dec", filenamePrefix),
    };

    // Операции узла CcsAudioWorker — зеркало OPS в deploy/comfyui/audio/ccs_audio_worker
    public static readonly IReadOnlySet<string> WorkerOps = new HashSet<string>(StringComparer.Ordinal)
    {
        "separate", "tts", "voice_clone", "tts_chatterbox", "voice_convert", "rvc_convert", "rvc_train",
        "audio_to_midi", "denoise", "upsample", "master", "music_edit", "transcribe",
    };

    // Префикс файлов воркера — jobId: результат ляжет как ccs-local-media/{jobId}_{хвост}
    public static JsonObject AudioWorker(string op, JsonObject parameters, IReadOnlyList<string> inputs, string jobId,
        int timeoutMinutes)
    {
        if (!WorkerOps.Contains(op)) throw new ArgumentOutOfRangeException(nameof(op), op, "Неизвестная операция воркера");
        return new JsonObject
        {
            ["worker"] = Node("CcsAudioWorker", new JsonObject
            {
                ["op"] = op,
                ["params_json"] = parameters.ToJsonString(),
                ["inputs"] = string.Join('\n', inputs),
                ["filename_prefix"] = jobId,
                ["timeout_minutes"] = Math.Clamp(timeoutMinutes, 1, 240),
            }),
        };
    }

    private static JsonObject SaveMp3(string source, string filenamePrefix) => Node("SaveAudioMP3", new JsonObject
    {
        ["audio"] = Link(source),
        ["filename_prefix"] = filenamePrefix,
        ["quality"] = "V0",
    });
}
