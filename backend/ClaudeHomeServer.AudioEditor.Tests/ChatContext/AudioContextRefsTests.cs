using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.ChatContext;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.ChatContext;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.ChatContext;

// Роли референсов, вид audio-voice и «Чем» звука (ADR-023 §1, 2б-1)
public sealed class AudioContextRefsTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-refs-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly AudioThreadStore _threads;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public AudioContextRefsTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_project);
        _threads = new AudioThreadStore(Path.Combine(_root, "audio"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioContextKind Kind(params IAudioEngine[] engines) => new(_threads, engines);

    private ContextScope Scope => new(Owner, _session, new Project { Id = "p-1", RootPath = _project, OwnerId = Owner });
    private ContextScope Personal => new(Owner, _session, null);

    private static JsonObject Slug(string slug) => new() { ["slug"] = slug };

    private static ContextItem Primary(string threadId) =>
        new("p", "audio", new JsonObject { ["threadId"] = threadId }, null, ContextActor.Human, DateTime.UtcNow);

    private string NewThread(AudioThreadSettings? settings = null) =>
        _threads.Open(Owner, Chat, null, "", null, settings).Thread!.Id;

    private string NewVoice(string name = "Аня") =>
        VoiceStore.CreateFromSamples(_project, name, null, [new VoiceSampleUpload(Wav)], DateTime.UtcNow).Value!.Manifest.Slug;

    [Fact]
    public void Validate_голоса_принимает_свой_и_отказывает_пропавшему_битому_и_личному_чату()
    {
        var slug = NewVoice();
        var kind = Kind();

        kind.Validate(Scope, "audio-voice", Slug(slug)).Should().BeNull();
        kind.Validate(Scope, "audio-voice", Slug("net-takogo")).Should().Contain("не найден");
        kind.Validate(Scope, "audio-voice", Slug("../x")).Should().NotBeNull("слаг только по белому списку");
        kind.Validate(Scope, "audio-voice", new JsonObject()).Should().NotBeNull();
        kind.Validate(Personal, "audio-voice", Slug(slug)).Should().Contain("личном чате");
    }

    [Fact]
    public void Describe_голоса_называет_по_имени_и_помечает_пропавший()
    {
        var slug = NewVoice("Аня");
        var kind = Kind();
        ContextItem Item(string s) => new("v", "audio-voice", Slug(s), "voice", ContextActor.Human, DateTime.UtcNow);

        kind.Describe(Scope, Item(slug)).Should().Be(new ContextItemSummary("Аня", null, null, false));
        kind.Describe(Scope, Item("net-takogo")).Missing.Should().BeTrue();
        kind.Describe(Personal, Item(slug)).Missing.Should().BeTrue();
    }

    [Fact]
    public void Validate_audio_отказывает_чужой_нити_и_чужому_владельцу()
    {
        var id = NewThread();
        var kind = Kind();
        kind.Validate(Scope, "audio", new JsonObject { ["threadId"] = id }).Should().BeNull();
        kind.Validate(Scope, "audio", new JsonObject { ["threadId"] = "чужая" }).Should().NotBeNull();
        kind.Validate(Scope with { OwnerId = "чужой" }, "audio", new JsonObject { ["threadId"] = id }).Should().NotBeNull();
    }

    [Fact]
    public void AcceptedRefs_без_операции_отдаёт_полную_таблицу_ролей_по_таблице_ADR()
    {
        var table = Kind().AcceptedRefs(Scope, Primary(NewThread()), null);

        table.Select(r => r.Role).Should().Equal("voice", "reference", "piece");
        table.Single(r => r.Role == "voice").Kinds.Should().Equal("audio-voice");
        table.Single(r => r.Role == "voice").Ops.Should().Equal("speak", "dialogue", "convertVoice");
        table.Single(r => r.Role == "reference").Kinds.Should().Equal("audio", "project-file");
        table.Single(r => r.Role == "piece").Ops.Should().Equal("concat");
    }

    [Fact]
    public void AcceptedRefs_по_операции_оставляет_только_берущие_её_роли_и_серая_операция_пуста()
    {
        var kind = Kind();
        var primary = Primary(NewThread());

        kind.AcceptedRefs(Scope, primary, "convertVoice").Select(r => r.Role).Should().Equal("voice", "reference");
        kind.AcceptedRefs(Scope, primary, "speak").Select(r => r.Role).Should().Equal("voice");
        kind.AcceptedRefs(Scope, primary, "separate").Should().BeEmpty();
        kind.AcceptedRefs(Scope, new ContextItem("i", "image", new JsonObject(), null, ContextActor.Human, DateTime.UtcNow), null)
            .Should().BeEmpty("основной вид чужой");
    }

    [Fact]
    public void DescribeExecutor_берёт_поставщика_и_модель_нити_и_цену_из_каталога()
    {
        var info = AudioCatalog.Local[0].Info;
        var engine = new Mock<IAudioEngine>();
        engine.SetupGet(e => e.Key).Returns("local");
        engine.SetupGet(e => e.Label).Returns("Локально");
        engine.SetupGet(e => e.Models).Returns([info]);
        var id = NewThread(new AudioThreadSettings(AudioModes.Voice, "speak", "local", info.Id, null));

        Kind(engine.Object).DescribeExecutor(Scope, Primary(id)).Should().Be($"Локально · {info.Label} · бесплатно");
    }

    [Fact]
    public void DescribeExecutor_без_настроек_честно_скромен()
    {
        Kind().DescribeExecutor(Scope, Primary(NewThread())).Should().Be("Авто · Авто");
    }

    private (AudioContextKind Kind, AudioPrefsService Prefs, AudioModelInfo Info) KindWithLocalEngine()
    {
        var info = AudioCatalog.Local[0].Info;
        var engine = new Mock<IAudioEngine>();
        engine.SetupGet(e => e.Key).Returns("local");
        engine.SetupGet(e => e.Label).Returns("Локально");
        engine.SetupGet(e => e.Models).Returns([info]);
        var prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_root, "prefs")));
        return (new AudioContextKind(_threads, [engine.Object], prefs), prefs, info);
    }

    [Fact]
    public void DescribeExecutor_префы_голоса_пусты_а_у_музыки_заданы_берёт_голос_а_не_музыку()
    {
        var (kind, prefs, info) = KindWithLocalEngine();
        prefs.Save(Owner, AudioEditScope.Of(_session), AudioModes.Music, new AudioModePrefs("song", "local", info.Id, null, null));

        kind.DescribeExecutor(Scope, Primary(NewThread())).Should().Be("Авто · Авто");
    }

    [Fact]
    public void DescribeExecutor_у_нити_режим_без_провайдера_берёт_провайдера_из_префов()
    {
        var (kind, prefs, info) = KindWithLocalEngine();
        prefs.Save(Owner, AudioEditScope.Of(_session), AudioModes.Voice, new AudioModePrefs("speak", "local", info.Id, null, null));
        var id = NewThread(new AudioThreadSettings(AudioModes.Voice, null, null, null, null, 2));

        kind.DescribeExecutor(Scope, Primary(id)).Should().Be($"Локально · {info.Label} · бесплатно");
    }

    [Fact]
    public void DescribeExecutor_режим_нити_определяет_какие_префы_брать()
    {
        var (kind, prefs, info) = KindWithLocalEngine();
        prefs.Save(Owner, AudioEditScope.Of(_session), AudioModes.Music, new AudioModePrefs("song", "local", info.Id, null, null));
        var id = NewThread(new AudioThreadSettings(AudioModes.Music, null, null, null, null));

        kind.DescribeExecutor(Scope, Primary(id)).Should().Be($"Локально · {info.Label} · бесплатно");
    }

    [Fact]
    public void DescribeExecutor_платная_модель_fal_показывает_usd_и_единицу()
    {
        var chars = new AudioModelInfo("tts-chars", "TTS", AudioCatalog.Local[0].Info.Caps, new AudioPriceHint(0.00009, AudioPriceUnits.Chars, "char"));
        var secs = new AudioModelInfo("tts-sec", "Sec", AudioCatalog.Local[0].Info.Caps, new AudioPriceHint(0.018, AudioPriceUnits.Sec, "sec"));
        var engine = new Mock<IAudioEngine>();
        engine.SetupGet(e => e.Key).Returns("fal");
        engine.SetupGet(e => e.Label).Returns("fal");
        engine.SetupGet(e => e.Models).Returns([chars, secs]);
        var kind = Kind(engine.Object);

        kind.DescribeExecutor(Scope, Primary(NewThread(new AudioThreadSettings(AudioModes.Voice, "speak", "fal", "tts-chars", null))))
            .Should().Be("fal · TTS · $0.09 / 1000 симв.");
        kind.DescribeExecutor(Scope, Primary(NewThread(new AudioThreadSettings(AudioModes.Voice, "speak", "fal", "tts-sec", null))))
            .Should().Be("fal · Sec · $0.018 / с");
    }

    [Fact]
    public void PriceText_запуск_fal_показывается_в_usd_а_не_сырым_run()
    {
        AudioContextKind.PriceText(new AudioPriceHint(0.1, AudioPriceUnits.Run, "run")).Should().Be("$0.1 / запуск");
    }

    [Fact]
    public void PriceText_ни_одна_цена_каталога_fal_не_содержит_сырых_единиц()
    {
        var texts = AudioCatalog.Fal.Where(m => m.Info.PriceHint is not null)
            .Select(m => (m.Info.Id, Text: AudioContextKind.PriceText(m.Info.PriceHint!))).ToList();

        texts.Should().NotBeEmpty();
        foreach (var (id, text) in texts)
            text.Should().NotMatchRegex(@"\b(run|chars|sec|min)\b", $"модель {id}");
    }

    [Fact]
    public void DescribeExecutor_настройки_нити_главнее_префов_режима()
    {
        var local = AudioCatalog.Local[0].Info;
        var fal = new AudioModelInfo("tts-chars", "TTS", local.Caps, new AudioPriceHint(0.00009, AudioPriceUnits.Chars, "char"));
        var localEngine = new Mock<IAudioEngine>();
        localEngine.SetupGet(e => e.Key).Returns("local");
        localEngine.SetupGet(e => e.Label).Returns("Локально");
        localEngine.SetupGet(e => e.Models).Returns([local]);
        var falEngine = new Mock<IAudioEngine>();
        falEngine.SetupGet(e => e.Key).Returns("fal");
        falEngine.SetupGet(e => e.Label).Returns("fal");
        falEngine.SetupGet(e => e.Models).Returns([fal]);
        var prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_root, "prefs")));
        prefs.Save(Owner, AudioEditScope.Of(_session), AudioModes.Voice, new AudioModePrefs("speak", "local", local.Id, null, null));
        var kind = new AudioContextKind(_threads, [localEngine.Object, falEngine.Object], prefs);
        var id = NewThread(new AudioThreadSettings(AudioModes.Voice, "speak", "fal", "tts-chars", null));

        kind.DescribeExecutor(Scope, Primary(id)).Should().Be("fal · TTS · $0.09 / 1000 симв.");
    }

    [Fact]
    public void Основным_бывает_только_звук()
    {
        var kind = Kind();
        kind.CanBePrimary("audio").Should().BeTrue();
        kind.CanBePrimary("audio-voice").Should().BeFalse();
    }

    // Роль спрашивается у владельца ОСНОВНОГО объекта, а не у вида референса: картинка в контексте звука
    // роли «style» не получает, даже если вид картинки сам готов её принять
    [Fact]
    public void Usedby_картинка_в_контексте_звука_серая_а_голос_берёт_операции_звука()
    {
        var voice = NewVoice();
        var registry = new ContextKindRegistry([Kind(), new AnyPrimaryImageStub(), new ProjectFileContextKind()]);
        var state = new ChatContextState(1, Primary(NewThread()),
        [
            new ContextItem("img", "image", new JsonObject { ["threadId"] = "t" }, "style", ContextActor.Human, DateTime.UtcNow),
            new ContextItem("v", "audio-voice", Slug(voice), "voice", ContextActor.Human, DateTime.UtcNow),
            new ContextItem("v-stems", "audio-voice", Slug(voice), null, ContextActor.Human, DateTime.UtcNow),
        ]);

        var dto = ChatContextDtoBuilder.Build(registry, Scope, state);

        dto.Refs.Single(r => r.Id == "img").UsedBy.Should().BeEmpty();
        dto.Refs.Single(r => r.Id == "v").UsedBy.Should().Equal("speak", "dialogue", "convertVoice");
        dto.Refs.Single(r => r.Id == "v-stems").UsedBy.Should().BeEmpty("голос при «Стемах» не вход");
    }

    // Вид «image», который принимает style при любом основном: наблюдает, у кого спросили роль
    private sealed class AnyPrimaryImageStub : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["image"];
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("img", null, null, false);

        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) =>
            [new ContextRoleSpec("style", "Стиль", ["image"], ["generate"])];

        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }
}
