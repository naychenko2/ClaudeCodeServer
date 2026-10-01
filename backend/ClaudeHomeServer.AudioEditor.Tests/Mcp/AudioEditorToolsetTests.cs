using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.AudioEditor.Mcp;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Mcp;

// Инструменты агента для звука (ADR-021 §5): состав не зависит от хода и нитей, запуск только в нить по
// threadId, не больше двух платных запусков за ход, делегированный ход — отказ fail-closed, выбор
// поставщика человеком не подменяется, params — только по схеме модели, сохранения у агента нет.
public sealed class AudioEditorToolsetTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Stranger = "owner-2";
    private const string ChatId = "chat-1";
    private const string PersonalChat = "chat-personal";
    private const string ProjectId = "p-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "audio-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly AudioThreadStore _store;
    private readonly AudioPrefsService _prefs;
    private readonly AudioEditWorkspace _workspace;
    private readonly Mock<IDelegatedTurnGate> _turnGate = new();
    private readonly FakeTurnBus _bus = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly List<AudioEditJobService> _services = [];
    private AudioEditJobService _jobs = null!;

    public AudioEditorToolsetTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        File.WriteAllBytes(Path.Combine(_root, "audio", "intro.wav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "audio", "outro.wav"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "не звук");
        _store = new AudioThreadStore(Path.Combine(_dir, AudioThreadStore.DirName));
        _prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_dir, AudioPrefsStore.DirName)));
        _workspace = new AudioEditWorkspace(Path.Combine(_dir, "work"));
        _sessions[ChatId] = new Session { Id = ChatId, OwnerId = Owner, ProjectId = ProjectId };
        _sessions[PersonalChat] = new Session { Id = PersonalChat, OwnerId = Owner };
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private AudioEditorToolset Toolset(IAudioEngine[]? engines = null, bool agentLaunch = true, bool withGate = true,
        IAudioDsp? dsp = null, VoiceLibrary? library = null, bool withEdits = false)
    {
        engines ??= [new FakeEngine("fal")];
        var threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance);
        _jobs = new AudioEditJobService(engines, _workspace, NullLogger<AudioEditJobService>.Instance, threads, _prefs,
            voices: library);
        _services.Add(_jobs);
        var concat = new AudioConcatService(threads, _workspace, NullLogger<AudioConcatService>.Instance, dsp);

        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(It.IsAny<string>(), FeatureFlagKeys.AudioEditor)).Returns(true);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AudioEditorToolset.AgentLaunchKey] = agentLaunch ? "true" : "false" })
            .Build();
        return new AudioEditorToolset(accessor.Object, flags.Object, projects.Object, engines, threads, _jobs, _prefs,
            _workspace, withGate ? _turnGate.Object : null, concat,
            edits: withEdits ? new AudioAgentEdits(new DspAudioEngine(threads, _workspace, NullLogger<DspAudioEngine>.Instance, dsp)) : null,
            library: library, events: _bus, config: config);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string tail = ChatId) => new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(AudioEditorToolset toolset, string tool, JsonObject? args = null,
        McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    private static JsonObject Parse(McpToolCallResult result) => JsonNode.Parse(result.Text)!.AsObject();

    private string Draft(string chat = ChatId) => _store.Open(Owner, chat, null, "", null).Thread!.Id;

    private static JsonObject Gen(string threadId, string text = "Привет") => new() { ["threadId"] = threadId, ["text"] = text };

    private string[] ProjectFiles() =>
        [.. Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f)).Order()];

    // ── Состав ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Состав_без_сохранения_и_ровно_список_автодопуска()
    {
        var names = Toolset().ToolsFor(Ctx()).Select(t => t.Name).ToList();

        names.Should().BeEquivalentTo([
            AudioEditorToolset.ToolState, AudioEditorToolset.ToolFocus, AudioEditorToolset.ToolNew,
            AudioEditorToolset.ToolVoices, AudioEditorToolset.ToolGenerate, AudioEditorToolset.ToolConcat,
            AudioEditorToolset.ToolSuggestPrompt, AudioEditorToolset.ToolCancel]);
        names.Should().NotContain(n => n.Contains("save") || n.Contains("download"));
        names.Select(n => $"mcp__{AudioEditorToolset.ServerName}__{n}").Should().BeEquivalentTo(AudioEditorAgentTools.AutoAllowTools);
    }

    [Fact]
    public void Без_запуска_агентом_остаются_только_инструменты_без_запуска()
    {
        Toolset(agentLaunch: false).ToolsFor(Ctx()).Select(t => t.Name).Should().BeEquivalentTo([
            AudioEditorToolset.ToolState, AudioEditorToolset.ToolFocus, AudioEditorToolset.ToolNew,
            AudioEditorToolset.ToolVoices, AudioEditorToolset.ToolSuggestPrompt]);
    }

    [Fact]
    public async Task Состав_не_зависит_от_нитей_фокуса_и_запусков()
    {
        var toolset = Toolset();
        string Snapshot() => string.Join("\n", toolset.ToolsFor(Ctx()).Select(t => t.Name + t.Description + t.InputSchema.ToJsonString()));
        var before = Snapshot();

        var draft = Draft();
        (await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "audio/intro.wav" })).IsError.Should().BeFalse();
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft))).IsError.Should().BeFalse();

        Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Чужой_чат_пустой_состав_и_отказ()
    {
        var toolset = Toolset();

        toolset.ToolsFor(Ctx(owner: Stranger)).Should().BeEmpty();
        (await Call(toolset, AudioEditorToolset.ToolState, ctx: Ctx(owner: Stranger))).IsError.Should().BeTrue();
    }

    // ── Запуск: threadId, гейт хода, лимит ─────────────────────────────────────

    [Fact]
    public async Task Запуск_без_threadId_отказ()
    {
        var engine = new FakeEngine("fal");
        var result = await Call(Toolset([engine]), AudioEditorToolset.ToolGenerate, new JsonObject { ["text"] = "Привет" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("threadId");
        engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Делегированный_ход_отказ_fail_closed()
    {
        var engine = new FakeEngine("fal");
        var withoutGate = Toolset([engine], withGate: false);
        var draft = Draft();

        var noGate = await Call(withoutGate, AudioEditorToolset.ToolGenerate, Gen(draft));
        noGate.IsError.Should().BeTrue();
        noGate.Text.Should().Contain("отказ по построению");

        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>())).Returns("Делегированный ход не запускает");
        var toolset = Toolset([engine]);
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft))).Text.Should().Be("Делегированный ход не запускает");
        (await Call(toolset, AudioEditorToolset.ToolConcat, new JsonObject
        {
            ["pieces"] = new JsonArray(new JsonObject { ["file"] = "audio/intro.wav" }, new JsonObject { ["file"] = "audio/outro.wav" }),
        })).Text.Should().Be("Делегированный ход не запускает");
        engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Не_больше_двух_запусков_за_ход_сброс_по_концу_хода()
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine]);
        var draft = Draft();

        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "раз"))).IsError.Should().BeFalse();
        await WaitIdleAsync();
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "два"))).IsError.Should().BeFalse();
        await WaitIdleAsync();
        var third = await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "три"));
        third.IsError.Should().BeTrue();
        third.Text.Should().Contain($"не больше {AudioEditorToolset.MaxLaunchesPerTurn}");

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("other-chat", Owner, 1, 0), "success"));
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "три"))).IsError.Should().BeTrue("чужой ход счётчик не сбрасывает");
        await _bus.PublishAsync(new TurnCompleted(new TurnContext(ChatId, Owner, 1, 0), "success"));
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "три"))).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Отказ_запуска_не_расходует_лимит_хода()
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine]);
        var draft = Draft();

        (await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = draft, ["text"] = "x", ["params"] = new JsonObject { ["nope"] = 1 },
        })).IsError.Should().BeTrue();
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "раз"))).IsError.Should().BeFalse();
        await WaitIdleAsync();
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft, "два"))).IsError.Should().BeFalse();
    }

    // ── Выбор человека ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Без_provider_берётся_выбор_человека_из_настроек_звука()
    {
        var fal = new FakeEngine("fal");
        var higgs = new FakeEngine("higgsfield");
        var toolset = Toolset([fal, higgs]);
        var draft = Draft();
        _store.SetSettings(Owner, ChatId, draft, new AudioThreadSettings(AudioModes.Voice, "speak", "higgsfield", null, null), null);

        var result = Parse(await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft)));
        await WaitIdleAsync();

        result["quote"]!["provider"]!.GetValue<string>().Should().Be("higgsfield");
        higgs.Runs.Should().Be(1);
        fal.Runs.Should().Be(0, "«Авто» поставило бы fal первым — выбор человека не подменён");
    }

    [Fact]
    public async Task Без_provider_берётся_выбор_человека_из_полосы_режима()
    {
        var fal = new FakeEngine("fal");
        var higgs = new FakeEngine("higgsfield");
        var toolset = Toolset([fal, higgs]);
        var draft = Draft();
        _prefs.Save(Owner, AudioEditScope.Of(new Project { Id = ProjectId, RootPath = _root }), AudioModes.Voice,
            new AudioModePrefs("speak", "higgsfield", null, null, null));

        var result = Parse(await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft)));
        await WaitIdleAsync();

        result["quote"]!["provider"]!.GetValue<string>().Should().Be("higgsfield");
        fal.Runs.Should().Be(0);
    }

    // ── params по схеме модели ─────────────────────────────────────────────────

    [Fact]
    public async Task Params_с_неизвестным_ключом_отказ_с_именем_поля()
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine]);
        var draft = Draft();

        var result = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = draft, ["text"] = "Привет", ["params"] = new JsonObject { ["speed"] = 1.2, ["temperature"] = 0.5 },
        });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«temperature»").And.Contain("speed");
        engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Params_по_схеме_и_диктор_доезжают_до_поставщика()
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine]);
        var draft = Draft();

        var result = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = draft, ["text"] = "Привет", ["voice"] = "alena", ["params"] = new JsonObject { ["speed"] = 1.2 },
        });
        await WaitIdleAsync();

        result.IsError.Should().BeFalse(result.Text);
        engine.LastRequest!.Params!["speed"]!.GetValue<double>().Should().Be(1.2);
        engine.LastRequest.Params["voice"]!.GetValue<string>().Should().Be("alena");
        engine.LastRequest.Text.Should().Be("Привет");
    }

    // Котировка агента несёт то же, что уйдёт в запуск (сверка «Котировка не соответствует запросу»):
    // у песни текст — подводка, плюс слова и длительность; op из настроек нити — тоже
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Песня_со_словами_котируется_тем_же_что_запускается(bool explicitOp)
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine]);
        var draft = Draft();
        if (!explicitOp)
            _store.SetSettings(Owner, ChatId, draft, new AudioThreadSettings(AudioModes.Music, "song", "fal", null, null), null);
        var args = new JsonObject
        {
            ["threadId"] = draft, ["text"] = "весёлый поп", ["lyrics"] = "[Verse]\nла-ла", ["durationSeconds"] = 30,
        };
        if (explicitOp) args["op"] = "song";

        var result = await Call(toolset, AudioEditorToolset.ToolGenerate, args);
        await WaitIdleAsync();

        result.IsError.Should().BeFalse(result.Text);
        engine.LastRequest!.Op.Should().Be(AudioOp.Song);
        engine.LastRequest.Text.Should().BeNull();
        engine.LastRequest.Prompt.Should().Be("весёлый поп");
        engine.LastRequest.Lyrics.Should().Be("[Verse]\nла-ла");
        engine.LastRequest.DurationSec.Should().Be(30);
    }

    // Голос из библиотеки агент передаёт slug'ом: тулсет не подменяет его диктором, а исполнитель
    // разворачивает его у поставщика (ADR-021 §5)
    [Fact]
    public async Task Голос_из_библиотеки_доезжает_до_поставщика()
    {
        var library = new VoiceLibrary();
        var scope = AudioEditScope.Of(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });
        byte[] wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1];
        var slug = library.CreateFromSamples(scope, "Аня", "текст", [new VoiceSampleUpload(wav)]).Value!.Manifest.Slug;
        var engine = new FakeEngine("fal") { TakesLibraryVoices = true };
        var toolset = Toolset([engine], library: library);

        var result = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = Draft(), ["text"] = "Привет", ["voice"] = slug,
        });
        await WaitIdleAsync();

        result.IsError.Should().BeFalse(result.Text);
        engine.LastRequest!.Voice!.Slug.Should().Be(slug);
        engine.LastRequest.Voice.Sample!.Bytes.Should().Equal(wav);
        engine.LastRequest.Params?.ContainsKey("voice").Should().NotBe(true);
    }

    // ── Сохранения у агента нет ────────────────────────────────────────────────

    [Fact]
    public async Task Агент_не_пишет_в_проект_ни_запуском_ни_склейкой()
    {
        var engine = new FakeEngine("fal");
        var toolset = Toolset([engine], dsp: new FakeDsp());
        var before = ProjectFiles();
        var draft = Draft();

        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(draft))).IsError.Should().BeFalse();
        await WaitIdleAsync();
        var concat = await Call(toolset, AudioEditorToolset.ToolConcat, new JsonObject
        {
            ["pieces"] = new JsonArray(new JsonObject { ["file"] = "audio/intro.wav" }, new JsonObject { ["file"] = "audio/outro.wav" }),
            ["name"] = "full",
        });

        concat.IsError.Should().BeFalse(concat.Text);
        var created = Parse(concat)["threadId"]!.GetValue<string>();
        var thread = _store.Get(Owner, ChatId).Threads.Single(t => t.Id == created);
        thread.File.Should().BeNull("склейка — черновик, сохраняет его человек");
        thread.Launches.Single().Initiator.Should().Be("agent");
        ProjectFiles().Should().Equal(before);
    }

    // ── Прочее ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Монтаж_без_ИИ_без_шва_отказ_без_траты_лимита()
    {
        var toolset = Toolset();
        var draft = Draft();

        var trim = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject { ["threadId"] = draft, ["op"] = "trim" });
        trim.IsError.Should().BeTrue();
        trim.Text.Should().Contain("Монтаж без ИИ");
        (await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject { ["threadId"] = draft, ["op"] = "concat" }))
            .Text.Should().Contain(AudioEditorToolset.ToolConcat);
    }

    [Fact]
    public async Task Монтаж_без_ИИ_обрезка_и_громкость_новые_версии_от_агента_без_траты_лимита()
    {
        var dsp = new FakeDsp();
        var toolset = Toolset(dsp: dsp, withEdits: true);
        var threadId = _store.Open(Owner, ChatId, "audio/intro.wav", null, null).Thread!.Id;

        var trim = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = threadId, ["op"] = "trim", ["range"] = new JsonObject { ["start"] = 1, ["end"] = 2.5 },
        });
        trim.IsError.Should().BeFalse(trim.Text);
        var trimmed = Parse(trim)["versionId"]!.GetValue<string>();
        dsp.Edit.Should().Be(new AudioEdit(1, 2.5, Format: AudioFormat.Wav));

        var gain = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = threadId, ["op"] = "gainFade",
            ["params"] = new JsonObject { ["fadeInSeconds"] = 0.5, ["gainDb"] = -3, ["format"] = "mp3" },
        });
        gain.IsError.Should().BeFalse(gain.Text);
        dsp.Edit.Should().Be(new AudioEdit(FadeInSeconds: 0.5, GainDb: -3, Format: AudioFormat.Mp3));

        var thread = _store.Get(Owner, ChatId).Threads.Single(t => t.Id == threadId);
        thread.Versions.Should().HaveCount(3);
        thread.Version(Parse(gain)["versionId"]!.GetValue<string>())!.BaseVersionId.Should().Be(trimmed, "основа — текущая версия");
        _store.Get(Owner, ChatId).Events.Where(e => e.Kind == AudioThreadEventKinds.Edited)
            .Should().HaveCount(2).And.OnlyContain(e => e.Text.StartsWith("Ты "));
        // Монтаж лимит платных запусков хода не тратит
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(Draft()))).IsError.Should().BeFalse();
        (await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(Draft()))).IsError.Should().BeFalse();
        await WaitIdleAsync();
    }

    // Монтаж пишет версию — делегированному ходу он закрыт так же, как запуск и склейка
    [Theory]
    [InlineData("trim")]
    [InlineData("gainFade")]
    [InlineData("normalize")]
    [InlineData("mixStems")]
    public async Task Монтаж_без_ИИ_делегированный_ход_отказ_fail_closed(string op)
    {
        var dsp = new FakeDsp();
        var threadId = _store.Open(Owner, ChatId, "audio/intro.wav", null, null).Thread!.Id;
        var args = new JsonObject { ["threadId"] = threadId, ["op"] = op };

        var noGate = await Call(Toolset(dsp: dsp, withGate: false, withEdits: true), AudioEditorToolset.ToolGenerate, args.DeepClone().AsObject());
        noGate.IsError.Should().BeTrue();
        noGate.Text.Should().Contain("отказ по построению");

        _turnGate.Setup(g => g.Deny(Owner, ChatId, It.IsAny<string>())).Returns("Делегированный ход не запускает");
        (await Call(Toolset(dsp: dsp, withEdits: true), AudioEditorToolset.ToolGenerate, args.DeepClone().AsObject()))
            .Text.Should().Be("Делегированный ход не запускает");

        _store.Get(Owner, ChatId).Threads.Single(t => t.Id == threadId).Versions.Should().HaveCount(1);
        dsp.Edit.Should().BeNull();
    }

    [Fact]
    public async Task Монтаж_без_ИИ_сведение_стемов_и_отказы_по_params()
    {
        var dsp = new FakeDsp();
        var toolset = Toolset(dsp: dsp, withEdits: true);
        var threadId = _store.Open(Owner, ChatId, "audio/intro.wav", null, null).Thread!.Id;
        var jobId = Guid.NewGuid().ToString("N");
        IReadOnlyList<AudioVersionFile> files = [.. new[] { "vocals", "drums" }.Select(name => new AudioVersionFile(
            AudioFileRoles.Stem(name), _workspace.SaveFile(Owner, jobId, 1, AudioFileRoles.Stem(name), [7], ".wav")))];
        _store.AddLaunch(Owner, ChatId, threadId,
            new AudioThreadLaunch(jobId, AudioThreadVersion.OriginId, _store.Now(), AudioThreadLaunchStatus.Running, "human", null, null));
        _store.FinishLaunch(Owner, ChatId, threadId, jobId, AudioThreadLaunchStatus.Done, [(1, files)]);

        var unknown = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = threadId, ["op"] = "normalize", ["params"] = new JsonObject { ["lufs"] = -14 },
        });
        unknown.IsError.Should().BeTrue();
        unknown.Text.Should().Contain("lufs").And.Contain("targetLufs");
        (await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject { ["threadId"] = threadId, ["op"] = "mixStems" }))
            .Text.Should().Contain("params.stems");

        var mix = await Call(toolset, AudioEditorToolset.ToolGenerate, new JsonObject
        {
            ["threadId"] = threadId, ["op"] = "mixStems",
            ["params"] = new JsonObject
            {
                ["stems"] = new JsonArray("vocals", new JsonObject { ["name"] = "drums", ["gainDb"] = -6 }),
            },
        });

        mix.IsError.Should().BeFalse(mix.Text);
        dsp.Stems.Select(s => s.GainDb).Should().Equal(0, -6);
        _store.Get(Owner, ChatId).Threads.Single(t => t.Id == threadId).Versions.Should().HaveCount(3);
    }

    [Fact]
    public async Task Личный_чат_файлы_проекта_отказ_до_диска()
    {
        var toolset = Toolset();

        var focus = await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "audio/intro.wav" },
            Ctx(tail: PersonalChat));
        var draft = await Call(toolset, AudioEditorToolset.ToolNew, new JsonObject { ["folder"] = "audio" }, Ctx(tail: PersonalChat));

        focus.IsError.Should().BeTrue();
        draft.IsError.Should().BeTrue();
        _store.Get(Owner, PersonalChat).Threads.Should().BeEmpty();
    }

    [Fact]
    public async Task Фокус_по_файлу_только_звуковой_файл_проекта()
    {
        var toolset = Toolset();

        (await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "notes.txt" })).IsError.Should().BeTrue();
        (await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "../outside.wav" })).IsError.Should().BeTrue();
        var ok = Parse(await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "audio/intro.wav" }));

        ok["thread"]!["file"]!.GetValue<string>().Should().Be("audio/intro.wav");
        _store.Get(Owner, ChatId).Focus.Should().Be(ok["focus"]!.GetValue<string>());
    }

    [Fact]
    public async Task Фокус_с_несуществующей_версией_отказ_с_версиями_нити()
    {
        var toolset = Toolset();
        var thread = Parse(await Call(toolset, AudioEditorToolset.ToolFocus, new JsonObject { ["file"] = "audio/intro.wav" }))
            ["focus"]!.GetValue<string>();
        var before = _store.Get(Owner, ChatId);

        var refused = await Call(toolset, AudioEditorToolset.ToolFocus,
            new JsonObject { ["threadId"] = thread, ["versionId"] = "v-нет" });

        refused.IsError.Should().BeTrue();
        refused.Text.Should().Contain("нет версии v-нет").And.Contain($"{AudioThreadVersion.OriginId} (исходник)");
        _store.Get(Owner, ChatId).Revision.Should().Be(before.Revision);
    }

    [Fact]
    public async Task Отмена_чужой_задачи_отказ()
    {
        var engine = new FakeEngine("fal") { Hold = true };
        var toolset = Toolset([engine]);
        var job = Parse(await Call(toolset, AudioEditorToolset.ToolGenerate, Gen(Draft())))["jobId"]!.GetValue<string>();

        (await Call(toolset, AudioEditorToolset.ToolCancel, new JsonObject { ["jobId"] = job }, Ctx(tail: PersonalChat)))
            .IsError.Should().BeTrue();
        (await Call(toolset, AudioEditorToolset.ToolCancel, new JsonObject { ["jobId"] = job })).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Дикторы_поставщика_по_audio_voices()
    {
        var voices = Parse(await Call(Toolset(), AudioEditorToolset.ToolVoices));

        voices["providers"]![0]!["voices"]![0]!["id"]!.GetValue<string>().Should().Be("alena");
    }

    private async Task WaitIdleAsync()
    {
        for (var i = 0; i < 500; i++)
        {
            var running = _store.Get(Owner, ChatId).Threads.SelectMany(t => t.Launches)
                .Any(l => l.Status == AudioThreadLaunchStatus.Running);
            if (!running) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    // Поставщик-подставка: речь с одним параметром speed и диктором в voice
    private sealed class FakeEngine(string key) : IAudioEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public bool Hold { get; init; }
        public bool TakesLibraryVoices { get; init; }
        public int Runs => _runs;

        public string? LibraryVoicesRefusal => TakesLibraryVoices ? null : "не умеет";

        public string? LibraryVoiceRefusal(AudioModelInfo model, AudioOp op, AudioVoiceUse voice) => LibraryVoicesRefusal;
        public AudioRequest? LastRequest { get; private set; }

        public string Key => key;
        public string Label => key;
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new("fake-speech", "Речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
                AudioLicenses.Mit, AudioPriceUnits.Free)),
            new("fake-song", "Песня", new AudioCaps([AudioOp.Song], ["ru"], [], [AudioOutputs.Audio],
                AudioLicenses.Mit, AudioPriceUnits.Free)),
        ];

        public IReadOnlySet<string>? ParamNames(AudioModelInfo model, AudioOp op) => new HashSet<string> { "speed", "voice" };

        public JsonObject? VoiceParams(AudioModelInfo model, AudioOp op, string voice) => new() { ["voice"] = voice };

        public Task<IReadOnlyList<AudioVoiceInfo>?> ListVoicesAsync(string? model, string? language, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AudioVoiceInfo>?>([new AudioVoiceInfo("alena", "Алёна", "ru")]);

        public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            LastRequest = req;
            if (Hold) await _release.Task.WaitAsync(ct);
            return new AudioResult(AudioOutcome.Ok, [new AudioFile(AudioOutputs.Audio, [1], "audio/wav", ".wav")],
                new AudioCost(0, AudioPriceUnits.Free), false, null, null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeTurnBus : ITurnEventBus
    {
        private readonly List<Func<TurnCompleted, Task>> _completed = [];

        public void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification
        {
            if (handler is Func<TurnCompleted, Task> h) _completed.Add(h);
        }

        public void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter { }

        public Task PublishAsync<T>(T e) where T : ITurnNotification =>
            e is TurnCompleted c ? Task.WhenAll(_completed.Select(h => h(c))) : Task.CompletedTask;

        public Task<T> ApplyAsync<T>(T e) where T : ITurnFilter => Task.FromResult(e);
    }

    private sealed class FakeDsp : IAudioDsp
    {
        public bool Available => true;
        public AudioEdit? Edit { get; private set; }
        public IReadOnlyList<AudioStem> Stems { get; private set; } = [];

        public Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
            double? normalizeLufs, AudioFormat format, CancellationToken ct) =>
            Task.FromResult(new AudioDspOutput("CONCAT"u8.ToArray(), format, null));

        public Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct, AudioDspInfo? known)
        {
            Edit = edit;
            return Task.FromResult(new AudioDspOutput("EDIT"u8.ToArray(), edit.Format, null));
        }
        public Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs, AudioFormat format, CancellationToken ct,
            AudioDspInfo? known) => throw new NotSupportedException();
        public Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct)
        {
            Stems = stems;
            return Task.FromResult(new AudioDspOutput("MIX"u8.ToArray(), format, null));
        }
        public Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
