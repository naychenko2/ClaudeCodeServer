using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов локальных аудио-моделей модуля «Звук» поверх фейкового ComfyUI: тумблеры, постановка,
// опрос с файлами по ролям, отмена, и пределы/ETA — из общей статики LocalMediaService.Audio,
// а не своей копии
public class LocalAudioMediaAdapterTests
{
    private readonly FakeComfy _comfy = new();

    private LocalAudioMediaAdapter Build(bool enabled = true, bool audio = true, int maxQueue = 4, int maxAudioSeconds = 600)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:AudioEnabled"] = audio ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
            ["LocalMedia:MaxAudioInputSeconds"] = maxAudioSeconds.ToString(),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalAudioMediaAdapter(client, config, images, NullLogger<LocalAudioMediaAdapter>.Instance);
    }

    private static LocalAudioRequest Speech(string text) =>
        new(LocalAudioOp.Speech, Args: new JsonObject { ["text"] = text });

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

    private JsonObject WorkerInputs(int k = 0) => _comfy.Prompts[k]["prompt"]!["worker"]!["inputs"]!.AsObject();

    private JsonObject WorkerParams(int k = 0) =>
        JsonNode.Parse(WorkerInputs(k)["params_json"]!.GetValue<string>())!.AsObject();

    // id задачи адаптера — из имени загруженного входа (la_<32 hex>-src.wav)
    private string JobId() => _comfy.Uploads[0][..35];

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Выключенный_тумблер_недоступен_и_в_ComfyUI_не_ставит(bool enabled, bool audio)
    {
        var adapter = Build(enabled, audio);

        adapter.Configured.Should().BeFalse();
        adapter.Available.Should().BeFalse();
        (await adapter.QueueLengthAsync(default)).Should().BeNull();
        var submitted = await adapter.SubmitAsync(Speech("привет"), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().NotBeNullOrEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Занятая_очередь_GPU_честный_отказ_busy_без_постановки()
    {
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("чужой-2");
        var adapter = Build(maxQueue: 2);

        var submitted = await adapter.SubmitAsync(Speech("привет"), default);

        submitted.Busy.Should().BeTrue();
        submitted.Error.Should().Contain("Очередь локальной видеокарты занята (2 задач)");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Озвучка_ставится_воркером_и_собирается_одним_файлом_main()
    {
        _comfy.Running.Add("чужой");
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Speech("Привет, мир"), default);

        submitted.Error.Should().BeNull();
        submitted.Ticket.Should().NotBeNullOrEmpty();
        submitted.EtaSeconds.Should().Be(LocalMediaService.SpeechEta("qwen", "Привет, мир".Length, false));
        WorkerInputs()["op"]!.GetValue<string>().Should().Be("tts");
        WorkerParams()["text"]!.GetValue<string>().Should().Be("Привет, мир");
        WorkerParams()["language"]!.GetValue<string>().Should().Be("russian");

        var waiting = await adapter.PollAsync(submitted.Ticket!, default);
        waiting.State.Should().Be(LocalAudioState.Queued, "впереди чужой прогон");
        waiting.QueuePosition.Should().Be(1);

        var wav = Wav(1);
        _comfy.CompleteAudio(submitted.Ticket!, [], ("la_x_speech.wav", wav));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalAudioState.Completed);
        var file = done.Files.Should().ContainSingle().Subject;
        file.Role.Should().Be(LocalAudioRoles.Main);
        file.Extension.Should().Be(".wav");
        file.ContentType.Should().Be("audio/wav");
        file.Bytes.Should().Equal(wav);
    }

    [Fact]
    public async Task Стемы_загружают_байты_входа_и_собираются_ролями_stem()
    {
        var adapter = Build();
        var source = Wav(2);

        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.Separate,
            Args: new JsonObject { ["mode"] = "4stems" }, Audio: source), default);

        submitted.Error.Should().BeNull();
        _comfy.Uploads.Should().ContainSingle().Which.Should().MatchRegex("^la_[0-9a-f]{32}-src\\.wav$");
        _comfy.UploadedBytes.Values.Should().ContainSingle().Which.Should().Equal(source);
        WorkerInputs()["op"]!.GetValue<string>().Should().Be("separate");
        WorkerInputs()["filename_prefix"]!.GetValue<string>().Should().Be(JobId());

        var id = JobId();
        _comfy.CompleteAudio(submitted.Ticket!, [],
            ($"{id}_vocals.mp3", [1]), ($"{id}_drums.mp3", [2]), ($"{id}_bass.mp3", [3]), ($"{id}_other.mp3", [4]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Select(f => f.Role).Should().Equal("stem:vocals", "stem:drums", "stem:bass", "stem:other");
        done.Files[0].Bytes.Should().Equal([1]);
    }

    [Fact]
    public async Task Распознавание_отдаёт_текст_субтитры_и_строки_песни()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.Transcribe, Audio: Wav(1)), default);
        var id = JobId();

        _comfy.CompleteAudio(submitted.Ticket!, [],
            ($"{id}_text.txt", "привет"u8.ToArray()), ($"{id}_subtitles.srt", [1]), ($"{id}_lyrics.lrc", [2]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Select(f => (f.Role, f.Extension))
            .Should().Equal(("text", ".txt"), ("subtitles", ".srt"), ("lyrics", ".lrc"));
    }

    [Fact]
    public async Task Обучение_голоса_отдаёт_модель_и_индекс()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.VoiceTrain,
            Clips: [Wav(1), Wav(1)]), default);
        submitted.Error.Should().BeNull();
        _comfy.Uploads.Should().HaveCount(2);
        var id = JobId();

        _comfy.CompleteAudio(submitted.Ticket!, [], ($"{id}_voice.pth", [1]), ($"{id}_voice.index", [2]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Select(f => f.Role).Should().Equal(LocalAudioRoles.Model, LocalAudioRoles.Index);
    }

    [Fact]
    public async Task Песня_YuE2_отдаёт_звук_main_и_партитуру_score()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.MusicGenerate, Prompt: "поп",
            Args: new JsonObject { ["engine"] = "yue2", ["lyrics"] = "[Verse]\nля-ля", ["duration_seconds"] = 30 }), default);
        submitted.Error.Should().BeNull();
        submitted.EtaSeconds.Should().Be(LocalMediaService.YuE2MusicEta(30));

        _comfy.CompleteAudio(submitted.Ticket!, ["X:1\nK:C\nCDEF|"], ("la_song_00001_.mp3", [7]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Select(f => f.Role).Should().Equal(LocalAudioRoles.Main, LocalAudioRoles.Score);
        Encoding.UTF8.GetString(done.Files[1].Bytes).Should().Be("X:1\nK:C\nCDEF|");
        done.Files[1].Extension.Should().Be(".abc");
    }

    [Fact]
    public async Task MIDI_получает_роль_midi_а_не_звука()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.ToMidi, Audio: Wav(1)), default);

        _comfy.CompleteAudio(submitted.Ticket!, [], ($"{JobId()}_notes.mid", [1]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.Files.Should().ContainSingle().Which.Role.Should().Be(LocalAudioRoles.Midi);
    }

    // Ссылка на вход в Args (путь файла) выбрасывается: вход — только байты запроса
    [Fact]
    public async Task Путь_в_Args_не_становится_входом()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.Separate,
            Args: new JsonObject { ["audio"] = "/etc/passwd" }), default);

        submitted.Error.Should().Be("Нужен параметр audio.");
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    // Пределы — из общей статики LocalMediaService.Audio: своя копия в адаптере разошлась бы с ней
    [Fact]
    public async Task Длина_текста_озвучки_по_общему_пределу()
    {
        var adapter = Build();
        var max = LocalMediaService.MaxSpeechTextLength;

        (await adapter.SubmitAsync(Speech(new string('а', max)), default)).Error.Should().BeNull();
        var over = await adapter.SubmitAsync(Speech(new string('а', max + 1)), default);

        over.Error.Should().Be($"text длиннее {max} символов.");
        _comfy.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task Число_записей_для_обучения_по_общему_пределу()
    {
        var adapter = Build();
        var clip = Wav(0.1);

        var over = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.VoiceTrain,
            Clips: [.. Enumerable.Repeat(clip, LocalMediaService.MaxTrainClips + 1)]), default);

        over.Error.Should().Contain($"от 1 до {LocalMediaService.MaxTrainClips} записей");
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Длина_входного_звука_по_общему_потолку_конфига()
    {
        var adapter = Build(maxAudioSeconds: 5);

        var over = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.Separate, Audio: Wav(6)), default);

        over.Error.Should().Contain("локально берём до 5 с");
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Образец_голоса_по_общему_пределу_длины()
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(Speech("привет") with
        {
            Reference = Wav(LocalMediaService.MaxReferenceSeconds + 1),
        }, default);

        over.Error.Should().Contain($"до {LocalMediaService.MaxReferenceSeconds} секунд");
        _comfy.Prompts.Should().BeEmpty();
    }

    // Отказы общей сборки графа доходят и до адаптера: проверки не живут только в local_* инструментах
    [Fact]
    public async Task Описание_стиля_ACE_по_общему_пределу()
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.MusicEdit,
            Prompt: new string('а', LocalMediaService.AceCaptionMaxChars + 1),
            Args: new JsonObject { ["task"] = "cover" }, Audio: Wav(1)), default);

        over.Error.Should().Be($"Описание стиля для ACE-Step — не длиннее {LocalMediaService.AceCaptionMaxChars} "
            + $"символов, сейчас {LocalMediaService.AceCaptionMaxChars + 1}.");
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task SeedVC_speech_со_сдвигом_высоты_отказ()
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.VoiceConvert,
            Args: new JsonObject { ["pitch_shift"] = 3 }, Audio: Wav(1), Reference: Wav(1)), default);

        over.Error.Should().StartWith("Seed-VC в режиме speech сдвиг высоты не применяет");
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public void ETA_по_общим_формулам_без_загрузок_в_ComfyUI()
    {
        var adapter = Build();

        adapter.EtaSeconds(new LocalAudioRequest(LocalAudioOp.Separate,
                Args: new JsonObject { ["mode"] = "4stems" }, Audio: Wav(180)))
            .Should().Be(LocalMediaService.SeparateEta("4stems", 180));
        adapter.EtaSeconds(Speech("Привет")).Should().Be(LocalMediaService.SpeechEta("qwen", 6, false));
        adapter.EtaSeconds(Speech(new string('а', LocalMediaService.MaxSpeechTextLength + 1)))
            .Should().BeNull("неверный запрос времени не обещает");

        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_снимает_ждущую_и_не_трогает_идущую()
    {
        _comfy.Running.Add("идёт");
        _comfy.Pending.Add("ждёт");
        var adapter = Build();

        (await adapter.CancelAsync("идёт", default)).Should().BeFalse();
        (await adapter.CancelAsync("ждёт", default)).Should().BeTrue();
        _comfy.Pending.Should().BeEmpty();
        _comfy.Running.Should().Equal("идёт");
    }

    [Fact]
    public async Task Упавшая_задача_ComfyUI_отдаёт_ошибку()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Speech("привет"), default);

        _comfy.Fail(submitted.Ticket!, "CUDA out of memory");
        var failed = await adapter.PollAsync(submitted.Ticket!, default);

        failed.State.Should().Be(LocalAudioState.Failed);
        failed.Error.Should().Contain("CUDA out of memory");
    }
}
