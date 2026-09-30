using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Аудио local-media (ADR-020): белые списки аргументов, граф — один узел CcsAudioWorker или
// нативные ноды музыки, сбор звука и партитуры в проект, тяжёлые задачи, длина входа
public class LocalMediaAudioTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string ProjectId = "proj-1";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly FakeComfy _comfy = new();
    private readonly FakeProjectAccess _projects = new();

    public LocalMediaAudioTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "local_audio_" + Guid.NewGuid().ToString("N"));
        _root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects.Roots[(Owner, ProjectId)] = _root;
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private (LocalMediaService Service, LocalMediaJobStore Store) Build(bool audio = true, int maxAudioSeconds = 600)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:AudioEnabled"] = audio ? "true" : "false",
            ["LocalMedia:MaxAudioInputSeconds"] = maxAudioSeconds.ToString(),
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:PollIntervalMs"] = "500",
        }).Build();
        var store = new LocalMediaJobStore(config);
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        return (new LocalMediaService(client, store, _projects, config, NullLogger<LocalMediaService>.Instance), store);
    }

    private static LocalMediaRequest Audio(string op, JsonObject args, string? prompt = null) =>
        new(Owner, ProjectId, "session-1", op, Prompt: prompt, Args: args);

    // Настоящий WAV: 16 кГц, моно, 16 бит — AudioProbe читает длину из заголовка
    private static byte[] Wav(double seconds)
    {
        var data = (int)(seconds * 16000) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    private string Put(string relative, byte[] bytes)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return relative;
    }

    private JsonObject Worker(int k = 0) => _comfy.Prompts[k]["prompt"]!["worker"]!["inputs"]!.AsObject();

    private JsonObject WorkerParams(int k = 0) => JsonNode.Parse(Worker(k)["params_json"]!.GetValue<string>())!.AsObject();

    [Fact]
    public async Task Выключено_ЧестныйОтказ_ДоComfy()
    {
        var (service, _) = Build(audio: false);

        var result = await service.SubmitAsync(Audio(LocalMediaOps.Speech, new JsonObject { ["text"] = "Привет" }), default);

        result.Error.Should().Contain("не установлены");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Речь_ПоОписанию_ОдинУзелВоркера_ЯзыкНазваниемQwen()
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.Speech, new JsonObject
        {
            ["text"] = "Добрый вечер",
            ["voice"] = "тёплый баритон",
        }), default);

        result.Error.Should().BeNull();
        var graph = _comfy.Prompts.Should().ContainSingle().Subject["prompt"]!.AsObject();
        graph.Select(p => p.Key).Should().Equal("worker");
        graph["worker"]!["class_type"]!.GetValue<string>().Should().Be("CcsAudioWorker");
        Worker()["op"]!.GetValue<string>().Should().Be("tts");
        Worker()["filename_prefix"]!.GetValue<string>().Should().Be(result.View!.Job.Id,
            "файлы воркера — ccs-local-media/{jobId}_…: по этому префиксу их находит чистка");
        WorkerParams()["language"]!.GetValue<string>().Should().Be("russian");
        WorkerParams()["voice"]!.GetValue<string>().Should().Be("тёплый баритон");
        result.View.EtaSeconds.Should().BePositive();
    }

    [Fact]
    public async Task Речь_Клон_ОбразецЗагруженВInput_БезРасшифровкиДольше()
    {
        var (service, _) = Build();
        Put("voice/ref.wav", Wav(8));

        var withText = await service.SubmitAsync(Audio(LocalMediaOps.Speech, new JsonObject
        {
            ["text"] = "Проверка клона", ["reference"] = "voice/ref.wav", ["reference_text"] = "Раз два три",
        }), default);
        _comfy.Complete(_comfy.Pending[0]);
        var noText = await service.SubmitAsync(Audio(LocalMediaOps.Speech, new JsonObject
        {
            ["text"] = "Проверка клона", ["reference"] = "voice/ref.wav",
        }), default);

        Worker()["op"]!.GetValue<string>().Should().Be("voice_clone");
        Worker()["inputs"]!.GetValue<string>().Should().Be($"ccs-local-media/{withText.View!.Job.Id}-ref.wav");
        WorkerParams()["ref_text"]!.GetValue<string>().Should().Be("Раз два три");
        noText.View!.EtaSeconds.Should().Be(withText.View.EtaSeconds + 12, "без расшифровки образец распознаёт whisper");
        withText.View.Job.InputSeconds.Should().Be(8);
    }

    [Theory]
    [InlineData("chatterbox", "voice", "тёплый", "только образец")]
    [InlineData("qwen", "language", "xx", "language для qwen")]
    [InlineData("qwen", "speaker", "Nobody", "speaker")]
    [InlineData("gpt", "voice", "тёплый", "engine")]
    public async Task Речь_ВнеБелогоСписка_Отказ(string engine, string key, string value, string error)
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.Speech, new JsonObject
        {
            ["text"] = "Текст", ["engine"] = engine, [key] = value,
        }), default);

        result.Error.Should().Contain(error);
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Музыка_Ace_НативныйГраф_ПустыеСлова_Инструментал()
    {
        var (service, store) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject
        {
            ["duration_seconds"] = 180, ["language"] = "ru", ["key"] = "A minor", ["bpm"] = 96,
        }, prompt: "indie rock"), default);

        result.Error.Should().BeNull();
        var graph = _comfy.Prompts.Single()["prompt"]!.AsObject();
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>().Should().Be("acestep_v1.5_xl_sft_bf16.safetensors");
        var enc = graph["enc"]!["inputs"]!;
        enc["lyrics"]!.GetValue<string>().Should().Be("[Instrumental]");
        enc["keyscale"]!.GetValue<string>().Should().Be("A minor");
        enc["duration"]!.GetValue<double>().Should().Be(180);
        graph["save"]!["inputs"]!["filename_prefix"]!.GetValue<string>().Should().Be($"ccs-local-media/{result.View!.Job.Id}");
        result.View.EtaSeconds.Should().Be(LocalMediaService.AceMusicEta(180));
        store.Get(result.View.Job.Id, Owner)!.Engine.Should().Be("ace");
    }

    [Theory]
    [InlineData("engine", "suno", "engine")]
    [InlineData("key", "H major", "key")]
    [InlineData("language", "klingon", "language")]
    public async Task Музыка_ВнеБелогоСписка_Отказ(string key, string value, string error)
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject { [key] = value },
            prompt: "pop"), default);

        result.Error.Should().Contain(error);
    }

    [Fact]
    public async Task Музыка_ДлинаВнеПредела_Отказ()
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject { ["duration_seconds"] = 900 },
            prompt: "pop"), default);

        result.Error.Should().Contain("duration_seconds");
    }

    [Fact]
    public async Task Музыка_YuE2_ПравленаяПартитура_БезГенератораABC()
    {
        var (service, _) = Build();

        var noLyrics = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject { ["engine"] = "yue2" },
            prompt: "pop"), default);
        var withAbc = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject
        {
            ["engine"] = "yue2", ["lyrics"] = "[Verse]\nла-ла", ["abc"] = "X:1\nK:C\nCDEF|",
        }, prompt: "pop"), default);

        noLyrics.Error.Should().Contain("lyrics");
        withAbc.Error.Should().BeNull();
        var graph = _comfy.Prompts.Single()["prompt"]!.AsObject();
        graph.ContainsKey("abc").Should().BeFalse("готовая партитура идёт прямо в YuE2GenerateMusic");
        graph["gen"]!["inputs"]!["abc"]!.GetValue<string>().Should().Be("X:1\nK:C\nCDEF|");
    }

    [Fact]
    public async Task Сбор_ЗвукВПроект_ПартитураОтдельнымФайлом()
    {
        var (service, _) = Build();
        var submitted = await service.SubmitAsync(Audio(LocalMediaOps.MusicGenerate, new JsonObject
        {
            ["engine"] = "yue2", ["lyrics"] = "[Verse]\nла-ла",
        }, prompt: "pop"), default);
        var job = submitted.View!.Job;
        var mp3 = Encoding.ASCII.GetBytes("ID3 fake mp3");

        _comfy.CompleteAudio(job.PromptId, ["X:1\nK:Am\nABcd|"], ($"{job.Id}_00001_.mp3", mp3));
        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Completed);
        view.Job.Outputs.Select(o => o.ContentType).Should().Equal("audio/mpeg", "text/plain");
        File.ReadAllBytes(Path.Combine(_root, view.Job.Outputs[0].Path)).Should().Equal(mp3);
        File.ReadAllText(Path.Combine(_root, view.Job.Outputs[1].Path)).Should().Be("X:1\nK:Am\nABcd|");
        view.Job.Outputs[1].Path.Should().EndWith($"{job.Id}-score.abc");
    }

    [Fact]
    public async Task Вход_JobIdПрошлойАудиоЗадачи_ПервыйЗвуковойФайл()
    {
        var (service, _) = Build();
        Put("song.wav", Wav(30));
        var stems = await service.SubmitAsync(Audio(LocalMediaOps.AudioSeparate, new JsonObject { ["audio"] = "song.wav" }), default);
        var stemsJob = stems.View!.Job;
        _comfy.CompleteAudio(stemsJob.PromptId, [], ($"{stemsJob.Id}_vocals.wav", Wav(30)), ($"{stemsJob.Id}_instrumental.wav", Wav(30)));
        (await service.GetAsync(Owner, stemsJob.Id, default))!.Job.Status.Should().Be(LocalMediaStatuses.Completed);

        var midi = await service.SubmitAsync(Audio(LocalMediaOps.AudioToMidi, new JsonObject { ["audio"] = stemsJob.Id }), default);

        midi.Error.Should().BeNull();
        _comfy.UploadPaths.Last().Should().Contain(midi.View!.Job.Id);
        midi.View.EtaSeconds.Should().Be(5 + (int)Math.Ceiling(30 * 0.07));
    }

    [Fact]
    public async Task ДлинныйЗвук_ВышеПотолка_Отказ()
    {
        var (service, _) = Build(maxAudioSeconds: 20);
        Put("long.wav", Wav(25));

        var result = await service.SubmitAsync(Audio(LocalMediaOps.AudioSeparate, new JsonObject { ["audio"] = "long.wav" }), default);

        result.Error.Should().Contain("до 20 с");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task ОбучениеГолоса_Тяжёлая_ВтораяОтказ()
    {
        var (service, _) = Build();
        Put("a.wav", Wav(3));

        var first = await service.SubmitAsync(Audio(LocalMediaOps.VoiceTrain, new JsonObject
        {
            ["audios"] = new JsonArray("a.wav"),
        }), default);
        var second = await service.SubmitAsync(Audio(LocalMediaOps.VoiceTrain, new JsonObject
        {
            ["audios"] = new JsonArray("a.wav"),
        }), default);

        first.View!.Job.Heavy.Should().BeTrue();
        second.Error.Should().Contain("тяжёлая");
    }

    [Fact]
    public async Task СменаГолоса_Rvc_НеМодель_Отказ_МодельИИндекс_ВоВходах()
    {
        var (service, _) = Build();
        Put("src.wav", Wav(5));
        Put("voice/bad.pth", Encoding.ASCII.GetBytes("not a zip"));
        Put("voice/me.pth", [.. "PK"u8.ToArray(), 3, 4, 0, 0]);
        Put("voice/me.index", [.. "IxMI"u8.ToArray(), 0, 0]);

        var bad = await service.SubmitAsync(Audio(LocalMediaOps.VoiceConvert, new JsonObject
        {
            ["engine"] = "rvc", ["audio"] = "src.wav", ["voice_model"] = "voice/bad.pth",
        }), default);
        var good = await service.SubmitAsync(Audio(LocalMediaOps.VoiceConvert, new JsonObject
        {
            ["engine"] = "rvc", ["audio"] = "src.wav", ["voice_model"] = "voice/me.pth", ["voice_index"] = "voice/me.index",
            ["pitch_shift"] = -3,
        }), default);

        bad.Error.Should().Contain("не модель голоса");
        good.Error.Should().BeNull();
        Worker()["op"]!.GetValue<string>().Should().Be("rvc_convert");
        Worker()["inputs"]!.GetValue<string>().Split('\n').Should().HaveCount(3)
            .And.Contain(n => n.EndsWith("-voice.pth")).And.Contain(n => n.EndsWith("-voice.index"));
        WorkerParams()["pitch_shift"]!.GetValue<int>().Should().Be(-3);
    }

    [Fact]
    public async Task ПутьВнеПроекта_Отказ()
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Audio(LocalMediaOps.AudioToMidi, new JsonObject { ["audio"] = "../../etc/passwd" }), default);

        result.Error.Should().Contain("вне папки проекта");
    }

    [Fact]
    public void Воркер_ОперацияВнеСписка_Исключение()
    {
        var act = () => ComfyWorkflows.AudioWorker("rm -rf", new JsonObject(), [], "lm_x", 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void История_ЗвукИТекстовыеВыходы()
    {
        var entry = JsonNode.Parse("""
            {"status":{"status_str":"success","completed":true},
             "outputs":{"save":{"audio":[{"filename":"lm_1_00001_.mp3","subfolder":"ccs-local-media","type":"output"}]},
                        "score":{"text":["X:1\nK:C"]}}}
            """)!.AsObject();

        var parsed = ComfyClient.ParseHistory(entry);

        parsed.Files.Should().ContainSingle().Which.FileName.Should().Be("lm_1_00001_.mp3");
        parsed.Texts.Should().Equal("X:1\nK:C");
    }

    [Fact]
    public void Длительность_Wav() => AudioProbe.Seconds(Wav(12.5)).Should().BeApproximately(12.5, 0.01);

    [Fact]
    public void Длительность_Flac()
    {
        // fLaC, заголовок блока, STREAMINFO: с 10-го байта блока — 20 бит частоты (44100 = 0x0AC44),
        // 3 бита каналов, 5 бит bps и 36 бит числа сэмплов (441000 = 0x6BAA8) → 10 с
        var b = new byte[42];
        "fLaC"u8.CopyTo(b);
        b[7] = 34;
        byte[] info = [0x0A, 0xC4, 0x42, 0xF0, 0x00, 0x06, 0xBA, 0xA8];
        info.CopyTo(b, 18);

        AudioProbe.Seconds(b).Should().BeApproximately(10, 0.01);
    }

    [Fact]
    public void Длительность_Mp3_Cbr()
    {
        // MPEG1 Layer III, 128 кбит/с, 44,1 кГц: 16 000 байт кадров ≈ 1 с
        var b = new byte[16000];
        b[0] = 0xFF; b[1] = 0xFB; b[2] = 0x90; b[3] = 0x00;

        AudioProbe.Seconds(b).Should().BeApproximately(1.0, 0.01);
    }
}
