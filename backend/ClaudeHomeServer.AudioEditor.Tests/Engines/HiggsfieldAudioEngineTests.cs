using System.Net;
using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

// Драйвер Higgsfield для звука на фейковом MCP: живой каталог с кешем, серые модели игрового
// конвейера, состав generate_audio по схеме модели, загрузка образца, скачивание, траты в кредитах
public sealed class HiggsfieldAudioEngineTests
{
    private static readonly AudioEditScope Scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = "/srv/p" });

    // Ответ models_explore(list, audio) от 2026-10-01, варианты длинных списков урезаны
    internal const string CatalogFixture = """
        {"items":[
         {"id":"seed_audio","name":"Seed Audio 1.0","description":"Seed Audio 1.0 — text-to-audio synthesis with optional voice/audio reference or a single image reference.","output_type":"audio",
          "parameters":[{"name":"format","required":"optional","type":"string","options":["wav","mp3","pcm","ogg_opus"],"default":"wav"},
           {"name":"sample_rate","required":"optional","type":"number","options":[8000,16000,24000,32000,44100,48000],"default":24000},
           {"name":"speech_rate","required":"optional","type":"number","min":-50,"max":100,"default":0},
           {"name":"loudness_rate","required":"optional","type":"number","min":-50,"max":100,"default":0},
           {"name":"pitch_rate","required":"optional","type":"number","min":-12,"max":12,"default":0},
           {"name":"voice_type","required":"optional","type":"string","options":["preset","element"],"nullable":true},
           {"name":"voice_id","required":"optional","type":"string","nullable":true}],
          "medias":[{"name":"medias","type":"image","roles":["image_references","audio_references"]}],
          "tags":["audio","text-to-speech","tts","bytedance","seed-audio","unlim"]},
         {"id":"qwen_audio_tts","name":"Qwen Audio 3.0 TTS Flash","description":"Qwen Audio 3.0 TTS Flash.","output_type":"audio",
          "parameters":[{"name":"voice_type","required":"required","type":"string","options":["preset","element"]},
           {"name":"voice_id","required":"required","type":"string"},
           {"name":"language","required":"optional","type":"string","options":["zh","en","ru"],"nullable":true},
           {"name":"seed","required":"optional","type":"number","min":0,"max":65535,"default":0},
           {"name":"batch_size","required":"optional","type":"number","min":1,"max":4,"default":1}],
          "medias":[],"tags":["audio","speech","tts","text-to-speech","voice"]},
         {"id":"elevenlabs_v4","name":"Eleven v4","description":"Eleven v4 speech generation with up to 10 preset or reference-element voices. Dialogue text is limited to 10,000 characters in total.","output_type":"audio",
          "parameters":[{"name":"dialogue","required":"required","type":"string_array"},
           {"name":"stability","required":"optional","type":"number","min":0,"max":1,"nullable":true},
           {"name":"similarity_boost","required":"optional","type":"number","min":0,"max":1,"nullable":true}],
          "medias":[],"tags":["audio","speech","tts","text-to-speech","voice","dialogue","elevenlabs"]},
         {"id":"sonilo_music","name":"Sonilo Music","description":"Text-to-music generation with controllable duration. Game pipeline only.","output_type":"audio",
          "parameters":[{"name":"duration","required":"required","type":"number"}],"medias":[],"tags":["audio","music","text-to-music","fal","sonilo"]},
         {"id":"mirelo_text_to_audio","name":"Mirelo Text to Audio","description":"Text-to-audio sound effect generation with controllable duration. Game pipeline only.","output_type":"audio",
          "parameters":[{"name":"duration","required":"required","type":"number"}],"medias":[],"tags":["audio","sfx","sound-effects","text-to-audio","fal","mirelo","unlim"]},
         {"id":"inworld_text_to_speech","name":"Inworld Text to Speech","description":"Text-to-speech audio generation. Game pipeline only.","output_type":"audio",
          "parameters":[{"name":"voice","required":"required","type":"string","options":["Svetlana (ru)","Hank (en)"]}],"medias":[],"tags":["audio","speech","tts","text-to-speech","fal","inworld","unlim"]},
         {"id":"text2speech_v2","name":"Text to Speech V2","description":"Text-to-speech with a selectable engine.","output_type":"audio",
          "parameters":[{"name":"variant","required":"required","type":"string","options":["elevenlabs","minimax","seed_speech","vibe_voice","cozy_voice"]},
           {"name":"voice_type","required":"required","type":"string","options":["preset","element"]},
           {"name":"voice_id","required":"required","type":"string"}],
          "medias":[],"tags":["audio","speech","tts","text-to-speech","voice","preset","reference-element","unlim"]}
        ],"has_more":false}
        """;

    private const string JobId = "8c516bb9-f774-4523-9777-d9f5cb2ca3bf";
    private const string MediaId = "a9ee96f2-1edc-47af-809a-395fdfd3f400";

    private const string UploadFixture =
        $"Upload URLs:\n- {MediaId}: run curl -X PUT -H 'Content-Type: audio/wav' " +
        "--data-binary @reference.wav 'https://s3.test/user/ref.wav?X-Amz-Signature=abc'. Then call media_confirm.";

    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 1, 2, 3];

    private static JsonObject? GenerateArgs(FakeHttp.Call c) => FakeHttp.Arguments(c)?["params"] as JsonObject;

    private static bool IsCost(FakeHttp.Call c) => FakeHttp.Tool(c) == "generate_audio" && GenerateArgs(c)?["get_cost"] is not null;

    private static FakeHttp.Call[] Launches(FakeHttp http) =>
        [.. http.Calls.Where(c => FakeHttp.Tool(c) == "generate_audio" && !IsCost(c))];

    private static HttpResponseMessage Happy(FakeHttp.Call c) => c switch
    {
        _ when c.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.OK),
        _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(Mp3, "audio/mpeg"),
        _ => FakeHttp.Tool(c) switch
        {
            "models_explore" => FakeHttp.McpText(CatalogFixture),
            "media_upload" => FakeHttp.McpText(UploadFixture),
            "media_confirm" => FakeHttp.McpText("Media confirmed."),
            "generate_audio" when GenerateArgs(c) is null => FakeHttp.McpText("params: Invalid input", isError: true),
            "generate_audio" when IsCost(c) => FakeHttp.McpText($"Cost preflight for {GenerateArgs(c)!["model"]}: 0.2 credits (0.2 exact). No job submitted."),
            "generate_audio" => FakeHttp.McpText($$"""{"results":[{"id":"{{JobId}}","type":"audio","status":"pending"}]}"""),
            "jobs_wait" => FakeHttp.McpText($$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"completed","result_url":"https://cdn.test/out.mp3"}],"all_terminal":true}"""),
            _ => FakeHttp.McpText("unknown tool", isError: true),
        },
    };

    private static (HiggsfieldAudioEngine Engine, FakeHttp Http, StepTime Time) Create(
        Func<FakeHttp.Call, HttpResponseMessage>? route = null, string? token = "admin-token")
    {
        var http = new FakeHttp(route ?? Happy);
        var time = new StepTime();
        return (new HiggsfieldAudioEngine(FakeHttp.Client(http, token), time) { PollInterval = TimeSpan.Zero }, http, time);
    }

    private static async Task<HiggsfieldAudioEngine> Loaded(FakeHttp http)
    {
        var engine = new HiggsfieldAudioEngine(FakeHttp.Client(http)) { PollInterval = TimeSpan.Zero };
        await engine.RefreshModelsAsync(CancellationToken.None);
        return engine;
    }

    private static AudioRequest Speak(string model, JsonObject? fields = null, string text = "Привет, мир") =>
        new(AudioOp.Speak, model, Scope, Text: text, Params: fields);

    private static readonly IProgress<AudioProgress> NoProgress = new Progress<AudioProgress>();

    // ── Каталог ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_ParsedFromLiveResponse()
    {
        var (engine, _, _) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Models.Select(m => m.Id).Should().Equal("seed_audio", "qwen_audio_tts", "elevenlabs_v4", "sonilo_music",
            "mirelo_text_to_audio", "inworld_text_to_speech", "text2speech_v2");

        var seed = engine.Models.Single(m => m.Id == "seed_audio");
        seed.Caps.Ops.Should().BeEquivalentTo([AudioOp.Speak, AudioOp.CloneVoice]);
        seed.Caps.VoiceKinds.Should().BeEquivalentTo([AudioVoiceKind.Preset, AudioVoiceKind.Element, AudioVoiceKind.Clone]);
        seed.Caps.PriceUnit.Should().Be(AudioPriceUnits.Credits);
        seed.DisabledReason.Should().BeNull();

        var eleven = engine.Models.Single(m => m.Id == "elevenlabs_v4");
        eleven.Caps.Ops.Should().BeEquivalentTo([AudioOp.Speak, AudioOp.Dialogue]);
        eleven.Caps.MaxTextChars.Should().Be(10_000);

        engine.Models.Single(m => m.Id == "qwen_audio_tts").Caps.SpeaksRu.Should().BeTrue();
        engine.Models.Single(m => m.Id == "sonilo_music").Caps.Ops.Should().Equal(AudioOp.Song);
        engine.Models.Single(m => m.Id == "mirelo_text_to_audio").Caps.Ops.Should().Equal(AudioOp.Sfx);
    }

    [Fact]
    public async Task Catalog_GamePipelineModels_GreyWithReason()
    {
        var (engine, _, _) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Models.Where(m => m.DisabledReason is not null).Select(m => m.Id)
            .Should().BeEquivalentTo(["sonilo_music", "mirelo_text_to_audio", "inworld_text_to_speech"]);
        engine.Models.Where(m => m.DisabledReason is not null)
            .Should().OnlyContain(m => m.DisabledReason == HiggsfieldAudioCatalog.GamePipelineReason);
        HiggsfieldAudioCatalog.GamePipelineReason.Should().Be("Только для игрового конвейера Higgsfield — эта модель есть у fal");
    }

    [Fact]
    public async Task Catalog_GamePipelineModels_NeverResolved()
    {
        var (engine, _, _) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);

        AudioCatalog.Resolve(engine.Models, AudioOp.Speak, "inworld_text_to_speech").Should().BeNull();
        AudioCatalog.Resolve(engine.Models, AudioOp.Song, AudioCatalog.AutoModelId).Should().BeNull();
        AudioCatalog.Resolve(engine.Models, AudioOp.Speak, AudioCatalog.AutoModelId)!.Id.Should().Be("seed_audio");
    }

    [Fact]
    public async Task Catalog_CachedForTtl_ThenReloaded()
    {
        var (engine, http, time) = Create();
        await engine.RefreshModelsAsync(CancellationToken.None);
        await engine.RefreshModelsAsync(CancellationToken.None);
        http.Calls.Count(c => FakeHttp.Tool(c) == "models_explore").Should().Be(1);

        time.Advance(HiggsfieldAudioEngine.ModelsTtl + TimeSpan.FromSeconds(1));
        await engine.RefreshModelsAsync(CancellationToken.None);

        var explores = http.Calls.Where(c => FakeHttp.Tool(c) == "models_explore").ToList();
        explores.Should().HaveCount(2);
        FakeHttp.Arguments(explores[0])!["type"]!.ToString().Should().Be("audio");
    }

    [Fact]
    public async Task Catalog_FailedReload_KeepsPreviousList()
    {
        var broken = false;
        var (engine, _, time) = Create(c => broken && FakeHttp.Tool(c) == "models_explore"
            ? FakeHttp.McpText("boom", isError: true)
            : Happy(c));
        await engine.RefreshModelsAsync(CancellationToken.None);

        broken = true;
        time.Advance(HiggsfieldAudioEngine.ModelsTtl + TimeSpan.FromSeconds(1));
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Models.Should().HaveCount(7);
    }

    [Fact]
    public async Task NoAccess_Disabled_NoCalls()
    {
        var (engine, http, _) = Create(token: null);
        await engine.RefreshModelsAsync(CancellationToken.None);

        engine.Enabled.Should().BeFalse();
        engine.Models.Should().BeEmpty();
        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);
        result.Outcome.Should().Be(AudioOutcome.Unavailable);
        http.Calls.Should().BeEmpty();
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_SeedAudio_ArgsInsideParams_DownloadsAndCostsCredits()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);
        var fields = new JsonObject
        {
            ["voice_type"] = "preset", ["voice_id"] = "v-1", ["format"] = "mp3", ["sample_rate"] = 48000,
            ["speech_rate"] = 20, ["loudness_rate"] = -10, ["pitch_rate"] = 3,
        };

        var result = await engine.RunAsync(Speak("seed_audio", fields), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Files.Should().ContainSingle();
        result.Files[0].Role.Should().Be(AudioOutputs.Audio);
        result.Files[0].Bytes.Should().Equal(Mp3);
        result.Files[0].ContentType.Should().Be("audio/mpeg");
        result.Files[0].Extension.Should().Be(".mp3");
        result.ActualCost.Should().Be(new AudioCost(0.2, AudioPriceUnits.Credits));
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be(JobId);

        var args = GenerateArgs(Launches(http).Single())!;
        args["model"]!.ToString().Should().Be("seed_audio");
        args["prompt"]!.ToString().Should().Be("Привет, мир");
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        args["voice_type"]!.ToString().Should().Be("preset");
        args["voice_id"]!.ToString().Should().Be("v-1");
        args["sample_rate"]!.GetValue<int>().Should().Be(48000);
        args["pitch_rate"]!.GetValue<int>().Should().Be(3);
        args.ContainsKey("get_cost").Should().BeFalse();
    }

    [Fact]
    public async Task Run_ParamsOutsideSchemaOrReserved_Dropped()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);
        var fields = new JsonObject
        {
            ["voice_type"] = "preset", ["voice_id"] = "v-1", ["seed"] = 5,
            ["use_unlim"] = true, ["batch_size"] = 4, ["get_cost"] = true, ["folder_id"] = "x", ["stranger"] = 1,
        };

        var result = await engine.RunAsync(Speak("qwen_audio_tts", fields), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        var args = GenerateArgs(Launches(http).Single())!;
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        args.ContainsKey("batch_size").Should().BeFalse();
        args.ContainsKey("get_cost").Should().BeFalse();
        args.ContainsKey("folder_id").Should().BeFalse();
        args.ContainsKey("stranger").Should().BeFalse();
        args["seed"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task Run_Text2SpeechV2_VariantAndVoice()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("text2speech_v2",
            new JsonObject { ["variant"] = "minimax", ["voice_type"] = "preset", ["voice_id"] = "v-2" }), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        GenerateArgs(Launches(http).Single())!["variant"]!.ToString().Should().Be("minimax");
    }

    [Theory]
    [InlineData("""{"variant":"minimax"}""", "Выберите диктора")]
    [InlineData("""{"variant":"whisper","voice_type":"preset","voice_id":"v"}""", "Недопустимое значение «variant»")]
    public async Task Run_BadOrMissingParams_RejectedBeforeSpending(string fields, string reason)
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("text2speech_v2", (JsonObject)JsonNode.Parse(fields)!), NoProgress,
            CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Error.Should().StartWith(reason);
        result.Charged.Should().BeFalse();
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_audio");
    }

    [Fact]
    public async Task Run_NumberOutOfRange_Rejected()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("seed_audio", new JsonObject { ["pitch_rate"] = 40 }), NoProgress,
            CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_audio");
    }

    [Fact]
    public async Task Run_Dialogue_BuildsTurnFromTextAndVoice()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("elevenlabs_v4",
            new JsonObject { ["voice_type"] = "preset", ["voice_id"] = "11111111-2222-3333-4444-555555555555", ["stability"] = 0.4 }),
            NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        var args = GenerateArgs(Launches(http).Single())!;
        var turn = args["dialogue"]!.AsArray().Single()!.AsObject();
        turn["text"]!.ToString().Should().Be("Привет, мир");
        turn["voice_id"]!.ToString().Should().Be("11111111-2222-3333-4444-555555555555");
        args.ContainsKey("voice_id").Should().BeFalse();
        args["stability"]!.GetValue<double>().Should().Be(0.4);
    }

    [Fact]
    public async Task Run_Clone_UploadsReferenceAsAudio()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);
        var request = new AudioRequest(AudioOp.CloneVoice, "seed_audio", Scope, Text: "Скажи это моим голосом",
            Reference: new AudioBytes([1, 2, 3], "audio/wav"));

        var result = await engine.RunAsync(request, NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        http.Calls.Should().Contain(c => c.Method == HttpMethod.Put && c.Url.StartsWith("https://s3.test/"));
        var confirm = http.Calls.Single(c => FakeHttp.Tool(c) == "media_confirm");
        FakeHttp.Arguments(confirm)!["type"]!.ToString().Should().Be("audio");
        var media = GenerateArgs(Launches(http).Single())!["medias"]!.AsArray().Single()!;
        media["role"]!.ToString().Should().Be("audio_references");
        media["value"]!.ToString().Should().Be(MediaId);
    }

    // Адрес загрузки приходит в ответе media_upload, то есть извне: внутренний адрес — отказ,
    // образец голоса туда не уходит, media_confirm и запуск не вызываются
    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1:9000/bucket/ref.wav")]
    public async Task Run_Clone_UploadUrlToInternalNetwork_RefusedWithoutSendingBytes(string uploadUrl)
    {
        var http = new FakeHttp(c => FakeHttp.Tool(c) == "media_upload"
            ? FakeHttp.McpText($"Upload URLs:\n- {MediaId}: run curl -X PUT --data-binary @reference.wav '{uploadUrl}'.")
            : Happy(c));
        // Загрузчик с настоящим SsrfGuard: адрес проверяется так же, как в бою
        var engine = new HiggsfieldAudioEngine(FakeHttp.Client(http, downloader: new SafeMediaDownloader(http)))
            { PollInterval = TimeSpan.Zero };
        await engine.RefreshModelsAsync(CancellationToken.None);
        var request = new AudioRequest(AudioOp.CloneVoice, "seed_audio", Scope, Text: "Скажи это моим голосом",
            Reference: new AudioBytes([1, 2, 3], "audio/wav"));

        var result = await engine.RunAsync(request, NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Unavailable);
        http.Calls.Should().NotContain(c => c.Method == HttpMethod.Put);
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "media_confirm" || c.Url == uploadUrl);
        Launches(http).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_CloneWithoutReference_Rejected()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(new AudioRequest(AudioOp.CloneVoice, "seed_audio", Scope, Text: "текст"), NoProgress,
            CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_audio" || FakeHttp.Tool(c) == "media_upload");
    }

    [Theory]
    [InlineData("sonilo_music", AudioOp.Song)]
    [InlineData("mirelo_text_to_audio", AudioOp.Sfx)]
    [InlineData("inworld_text_to_speech", AudioOp.Speak)]
    public async Task Run_GamePipelineModel_NeverLaunched(string model, AudioOp op)
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);

        var result = await engine.RunAsync(new AudioRequest(op, model, Scope, Text: "текст", Prompt: "текст", DurationSec: 5,
            Params: new JsonObject { ["voice"] = "Svetlana (ru)", ["duration"] = 5 }), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Error.Should().Be(HiggsfieldAudioCatalog.GamePipelineReason);
        result.Charged.Should().BeFalse();
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_audio");
    }

    [Fact]
    public async Task Run_InsufficientCredits_Classified()
    {
        var http = new FakeHttp(c => FakeHttp.Tool(c) == "generate_audio" && !IsCost(c)
            ? FakeHttp.McpText("Insufficient credits: top up your balance", isError: true)
            : Happy(c));
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
    }

    [Fact]
    public async Task Run_JobFailed_ChargedWithCostAndReason()
    {
        var http = new FakeHttp(c => FakeHttp.Tool(c) == "jobs_wait"
            ? FakeHttp.McpText($$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"failed","error":"voice not found"}],"all_terminal":true}""")
            : Happy(c));
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.ActualCost.Should().Be(new AudioCost(0.2, AudioPriceUnits.Credits));
        result.Error.Should().Contain("voice not found");
    }

    [Fact]
    public async Task Run_ContentTypeUnnamed_FormatFromUrl()
    {
        var http = new FakeHttp(c => c.Url.StartsWith("https://cdn.test/")
            ? FakeHttp.Bytes([1, 2], null)
            : FakeHttp.Tool(c) == "jobs_wait"
                ? FakeHttp.McpText($$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"completed","result_url":"https://cdn.test/out.wav?sig=1"}],"all_terminal":true}""")
                : Happy(c));
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);

        result.Files.Single().Extension.Should().Be(".wav");
        result.Files.Single().ContentType.Should().Be("audio/wav");
    }

    // Ссылку на результат прислал поставщик: на внутренний адрес бэкенд не идёт вовсе
    [Fact]
    public async Task Run_ResultUrlPrivate_NotDownloaded()
    {
        var http = new FakeHttp(c => FakeHttp.Tool(c) == "jobs_wait"
            ? FakeHttp.McpText($$"""{"jobs":[{"index":0,"job_id":"{{JobId}}","status":"completed","result_url":"https://127.0.0.1/out.mp3"}],"all_terminal":true}""")
            : c.Url.StartsWith("https://127.0.0.1/") ? FakeHttp.Bytes(Mp3, "audio/mpeg") : Happy(c));
        var engine = new HiggsfieldAudioEngine(FakeHttp.Client(http, downloader: new SafeMediaDownloader(http)))
        {
            PollInterval = TimeSpan.Zero,
        };
        await engine.RefreshModelsAsync(CancellationToken.None);

        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.Files.Should().BeEmpty();
        http.Calls.Should().NotContain(c => c.Url.StartsWith("https://127.0.0.1/"));
    }

    // Потолок звука свой: больше картиночного проходит, больше звукового — нет
    [Theory]
    [InlineData(SafeMediaDownloader.ImageMaxBytes + 1, AudioOutcome.Ok)]
    [InlineData(SafeMediaDownloader.AudioMaxBytes + 1, AudioOutcome.Failed)]
    public async Task Run_ResultSize_AudioCeiling(long declaredLength, AudioOutcome expected)
    {
        var http = new FakeHttp(c =>
        {
            if (!c.Url.StartsWith("https://cdn.test/")) return Happy(c);
            var response = FakeHttp.Bytes(Mp3, "audio/mpeg");
            response.Content.Headers.ContentLength = declaredLength;
            return response;
        });
        var engine = await Loaded(http);

        var result = await engine.RunAsync(Speak("seed_audio"), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(expected);
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estimate_CreditsFromGetCost()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);
        var model = engine.Models.Single(m => m.Id == "seed_audio");

        var estimate = await engine.EstimateAsync(model, Speak("seed_audio"), CancellationToken.None);

        estimate.Amount.Should().Be(0.2);
        estimate.Unit.Should().Be(AudioPriceUnits.Credits);
        estimate.Source.Should().Be(AudioEstimateSources.Provider);
        estimate.Approx.Should().BeFalse();
        Launches(http).Should().BeEmpty();
    }

    [Fact]
    public async Task Estimate_GamePipelineModel_Refused()
    {
        var http = new FakeHttp(Happy);
        var engine = await Loaded(http);
        var model = engine.Models.Single(m => m.Id == "inworld_text_to_speech");

        var act = () => engine.EstimateAsync(model, Speak(model.Id), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioEngineUnavailableException>()).WithMessage(HiggsfieldAudioCatalog.GamePipelineReason);
        http.Calls.Should().NotContain(c => FakeHttp.Tool(c) == "generate_audio");
    }

    [Fact]
    public void SpendSource_Higgsfield()
    {
        var (engine, _, _) = Create();
        engine.SpendSource.Should().Be(SpendSources.Higgsfield);
        engine.PriceUnit.Should().Be(AudioPriceUnits.Credits);
    }

    // ── Дикторы ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListVoices_ParsedTolerantly()
    {
        var http = new FakeHttp(c => FakeHttp.Tool(c) == "list_voices"
            ? FakeHttp.McpText("""{"voices":[{"voice_id":"v-1","name":"Anna","language":"ru"},{"id":"v-2","label":"Bob"},{"name":"без id"}]}""")
            : Happy(c));
        var engine = new HiggsfieldAudioEngine(FakeHttp.Client(http));

        var voices = await engine.ListVoicesAsync("seed_audio", CancellationToken.None);

        voices.Should().Equal(new HiggsfieldVoice("v-1", "Anna", "ru", null), new HiggsfieldVoice("v-2", "Bob", null, null));
        FakeHttp.Arguments(http.Calls.Single())!["model"]!.ToString().Should().Be("seed_audio");
    }
}
