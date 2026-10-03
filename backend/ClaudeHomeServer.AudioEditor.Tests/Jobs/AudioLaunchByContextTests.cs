using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.ChatContext;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Запуск звука по ревизии контекста чата (КТ-3, 2б-4): входы берутся из стора, а не из тела; ревизия не
// совпала — context_changed со свежим DTO; котировка фиксирует ревизию; склейка берёт куски референсами
// роли piece по AddedAt; mix сверяет нить маршрута со стором
public sealed class AudioLaunchByContextTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-ctx-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectRoot;
    private readonly Project _project;
    private readonly AudioEditScope _scope;
    private readonly Session _session;
    private readonly AudioThreadStore _threads;
    private readonly AudioEditWorkspace _workspace;
    private readonly ChatContextStore _store;
    private readonly ContextKindRegistry _registry;
    private readonly AudioContextLaunch _launch;
    private readonly Mock<IChatFeed> _feed = new();

    public AudioLaunchByContextTests()
    {
        _projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "sfx"));
        File.WriteAllBytes(Path.Combine(_projectRoot, "sfx", "jingle.wav"), "JINGLE"u8.ToArray());
        _project = new Project { Id = "p-1", RootPath = _projectRoot, OwnerId = Owner };
        _scope = AudioEditScope.Of(_project);
        _session = new Session { Id = Chat, OwnerId = Owner, ProjectId = _project.Id };
        _threads = new AudioThreadStore(Path.Combine(_root, "threads"));
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        _registry = new ContextKindRegistry([new AudioContextKind(_threads), new ProjectFileContextKind()]);
        _store = new ChatContextStore(Path.Combine(_root, "ctx"), _registry);
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(_session);
        _launch = new AudioContextLaunch(_store, _registry, directory.Object);
        _feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string NewThread() => _threads.Open(Owner, Chat, null, "", null).Thread!.Id;

    private string NewVoice(string name = "Аня") =>
        VoiceStore.CreateFromSamples(_projectRoot, name, null, [new VoiceSampleUpload(Wav)], DateTime.UtcNow).Value!.Manifest.Slug;

    private static ContextItem Item(string kind, JsonObject reference, string? role, DateTime? at = null) =>
        new("ci_" + Guid.NewGuid().ToString("N")[..8], kind, reference, role, ContextActor.Human, at ?? DateTime.UtcNow);

    private static JsonObject ThreadRef(string id, string? version = null) =>
        version is null ? new() { ["threadId"] = id } : new() { ["threadId"] = id, ["versionId"] = version };

    private ChatContextState SetPrimary(string threadId, string? version = null) =>
        _store.SetPrimary(Owner, Chat, Item("audio", ThreadRef(threadId, version), null), null);

    private ChatContextState AddRef(string kind, JsonObject reference, string role, DateTime? at = null) =>
        _store.AddRef(Owner, Chat, Item(kind, reference, role, at), null);

    // Нить-черновик с версией от задачи: файл main лежит в рабочей папке задачи
    private (string ThreadId, string VersionId) ThreadWithVersion(string content)
    {
        var thread = _threads.Open(Owner, Chat, null, "", null).Thread!;
        var jobId = Guid.NewGuid().ToString("N");
        var path = _workspace.SaveFile(Owner, jobId, 1, AudioFileRoles.Main, System.Text.Encoding.UTF8.GetBytes(content), ".wav");
        _threads.AddLaunch(Owner, Chat, thread.Id, new AudioThreadLaunch(jobId, null, _threads.Now(),
            AudioThreadLaunchStatus.Running, "human", null, null));
        var done = _threads.FinishLaunch(Owner, Chat, thread.Id, jobId, AudioThreadLaunchStatus.Done,
            [(1, (IReadOnlyList<AudioVersionFile>)[new AudioVersionFile(AudioFileRoles.Main, path)])]);
        return (thread.Id, done.NewVersions[0].Id);
    }

    private AudioEditJobService Jobs(IAudioEngine engine)
    {
        var threads = new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance, null, _feed.Object);
        return new AudioEditJobService(new[] { engine }, _workspace, NullLogger<AudioEditJobService>.Instance, threads,
            context: _launch);
    }

    // ── Раскладка входов ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_раскладывает_основной_голос_образец_и_куски_по_ролям_операции()
    {
        var threadId = NewThread();
        var (otherThread, otherVersion) = ThreadWithVersion("X");
        var slug = NewVoice();
        SetPrimary(threadId, "v7");
        AddRef("audio-voice", new JsonObject { ["slug"] = slug }, "voice");
        AddRef("project-file", new JsonObject { ["path"] = "sfx/jingle.wav" }, "reference");
        AddRef("audio", ThreadRef(otherThread, otherVersion), "piece");
        var state = _store.Get(Owner, Chat);

        var speak = AudioContextLaunch.Extract(_scope, state, "speak");
        speak.ThreadId.Should().Be(threadId);
        speak.VersionId.Should().Be("v7");
        speak.VoiceRef.Should().Be("voice:" + slug);
        speak.VoiceKind.Should().Be(AudioVoiceKind.Clone, "голос из образцов — клон");
        speak.Reference.Should().BeNull("речь образец не берёт — референс пропускается");
        speak.Pieces.Should().BeEmpty();

        var master = AudioContextLaunch.Extract(_scope, state, "master");
        master.Reference.Should().Be(new AudioConcatPiece(ProjectFile: "sfx/jingle.wav"));
        master.VoiceRef.Should().BeNull("мастеринг голос не берёт");

        AudioContextLaunch.Extract(_scope, state, "concat").Pieces.Should().Equal(new AudioConcatPiece(otherThread, otherVersion));
    }

    [Fact]
    public void Extract_куски_идут_в_порядке_AddedAt_а_не_вставки()
    {
        var a = ThreadWithVersion("A").ThreadId;
        var b = ThreadWithVersion("B").ThreadId;
        var c = ThreadWithVersion("C").ThreadId;
        var t0 = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
        SetPrimary(NewThread());
        // Вставлены C, A, B — а добавлены в контекст A, B, C
        AddRef("audio", ThreadRef(c), "piece", t0.AddMinutes(2));
        AddRef("audio", ThreadRef(a), "piece", t0);
        AddRef("audio", ThreadRef(b), "piece", t0.AddMinutes(1));

        AudioContextLaunch.Extract(_scope, _store.Get(Owner, Chat), "concat").Pieces.Select(p => p.ThreadId)
            .Should().Equal(a, b, c);
    }

    // ── Котировка и запуск ───────────────────────────────────────────────────────

    [Fact]
    public async Task Котировка_с_устаревшей_ревизией_отказывает_context_changed_со_свежим_DTO()
    {
        SetPrimary(NewThread());
        var svc = Jobs(new FakeSpeech());

        var quote = await svc.QuoteAsync(Owner, _scope,
            new AudioQuoteRequest(AudioModes.Voice, "speak", SessionId: Chat, ContextRevision: 0), CancellationToken.None);

        quote.ErrorCode.Should().Be(AudioEditErrorCodes.ContextChanged);
        quote.Context!.Revision.Should().Be(_store.Get(Owner, Chat).Revision);
        quote.Context.Primary!.Kind.Should().Be("audio");
    }

    [Fact]
    public async Task Запуск_по_ревизии_берёт_нить_из_стора_а_не_из_тела()
    {
        var contextThread = NewThread();
        var bodyThread = NewThread();
        var state = SetPrimary(contextThread);
        var engine = new FakeSpeech();
        var svc = Jobs(engine);

        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "speak",
            SessionId: Chat, ThreadId: bodyThread, Text: "привет", ContextRevision: state.Revision), CancellationToken.None);
        quote.Error.Should().BeNull();
        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId, SessionId: Chat,
            ThreadId: bodyThread, Text: "привет", ContextRevision: state.Revision), CancellationToken.None);

        started.Error.Should().BeNull();
        await engine.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var threads = _threads.Get(Owner, Chat).Threads;
        threads.Single(t => t.Id == contextThread).Launches.Should().HaveCount(1, "нить основного объекта");
        threads.Single(t => t.Id == bodyThread).Launches.Should().BeEmpty("нить тела игнорируется");
    }

    [Fact]
    public async Task Запуск_по_котировке_другой_ревизии_даёт_409_даже_если_стор_совпал_с_запуском()
    {
        var state = SetPrimary(NewThread());
        var svc = Jobs(new FakeSpeech());
        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "speak",
            SessionId: Chat, Text: "привет", ContextRevision: state.Revision), CancellationToken.None);
        quote.Error.Should().BeNull();

        // Контекст ушёл вперёд; человек взял свежую ревизию, но цену посчитали на старой
        var next = AddRef("project-file", new JsonObject { ["path"] = "sfx/jingle.wav" }, "reference");
        var started = await svc.StartAsync(Owner, _scope, new AudioJobInput(quote.Value!.QuoteId, SessionId: Chat,
            Text: "привет", ContextRevision: next.Revision), CancellationToken.None);

        started.ErrorCode.Should().Be(AudioEditErrorCodes.ContextChanged);
        started.Context!.Revision.Should().Be(next.Revision);
    }

    [Fact]
    public async Task Запуск_без_ревизии_ведёт_себя_по_прежнему()
    {
        var threadId = NewThread();
        var engine = new FakeSpeech();
        var svc = Jobs(engine);
        var quote = await svc.QuoteAsync(Owner, _scope,
            new AudioQuoteRequest(AudioModes.Voice, "speak", SessionId: Chat, ThreadId: threadId), CancellationToken.None);

        var started = await svc.StartAsync(Owner, _scope,
            new AudioJobInput(quote.Value!.QuoteId, SessionId: Chat, ThreadId: threadId), CancellationToken.None);

        started.Error.Should().BeNull();
        await engine.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _threads.Get(Owner, Chat).Threads.Single(t => t.Id == threadId).Launches.Should().HaveCount(1);
    }

    [Fact]
    public async Task Котировка_отдаёт_строки_исполнителей_с_ценой_в_usd_и_серой_строкой_с_причиной()
    {
        SetPrimary(NewThread());
        var svc = Jobs(new FakeSpeech());

        var quote = await svc.QuoteAsync(Owner, _scope, new AudioQuoteRequest(AudioModes.Voice, "speak",
            SessionId: Chat, ContextRevision: _store.Get(Owner, Chat).Revision), CancellationToken.None);

        var rows = quote.Value!.Executors!;
        var auto = rows[0];
        auto.Should().Match<ExecutorRowDto>(r => r.Id == "auto" && r.Group == "auto" && r.Name == "Авто");
        auto.Sub.Should().Be("сейчас: Облако · Облачная речь");
        var paid = rows.Single(r => r.Id == "cloud:paid-speech");
        paid.Group.Should().Be("cloud");
        paid.Unit.Should().Be("usd", "единица тарификации chars в Unit не попадает");
        paid.Amount.Should().BeApproximately(0.09, 1e-9, "цена за 1000 символов, как в подписи");
        paid.Price.Should().Be("$0.09 / 1000 симв.");
        paid.Free.Should().BeFalse();
        paid.Badges!.Select(b => b.Label).Should().Contain("RU");
        var gray = rows.Single(r => r.Id == "cloud:gray-speech");
        gray.Disabled.Should().BeTrue();
        gray.Reason.Should().Be("нужен ключ");
        gray.Sub.Should().BeNull();
    }

    // ── Склейка и сведение ───────────────────────────────────────────────────────

    [Fact]
    public async Task Склейка_по_ревизии_берёт_куски_референсов_piece_в_порядке_AddedAt()
    {
        var a = ThreadWithVersion("AAA").ThreadId;
        var b = ThreadWithVersion("BBB").ThreadId;
        var t0 = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
        SetPrimary(NewThread());
        AddRef("audio", ThreadRef(b), "piece", t0.AddMinutes(1));
        var state = AddRef("audio", ThreadRef(a), "piece", t0);
        var dsp = new FakeDsp();
        var svc = new AudioConcatService(new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance, null, _feed.Object),
            _workspace, NullLogger<AudioConcatService>.Instance, dsp, _launch);

        // Куски тела игнорируются — в теле другой порядок и чужой кусок
        var result = await svc.ConcatAsync(Owner, _scope, new AudioConcatInput(Chat, [new AudioConcatPiece(ProjectFile: "sfx/jingle.wav")],
            ContextRevision: state.Revision), CancellationToken.None);

        result.Error.Should().BeNull();
        dsp.Pieces.Select(System.Text.Encoding.UTF8.GetString).Should().Equal("AAA", "BBB");
    }

    [Fact]
    public async Task Склейка_по_ревизии_без_кусков_отказывает_понятным_текстом()
    {
        var state = SetPrimary(NewThread());
        var svc = new AudioConcatService(new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance), _workspace,
            NullLogger<AudioConcatService>.Instance, new FakeDsp(), _launch);

        var result = await svc.ConcatAsync(Owner, _scope, new AudioConcatInput(Chat, [], ContextRevision: state.Revision),
            CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("меньше двух кусков").And.Contain("Кусок");
    }

    [Fact]
    public async Task Склейка_с_устаревшей_ревизией_даёт_context_changed()
    {
        var state = SetPrimary(NewThread());
        var svc = new AudioConcatService(new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance), _workspace,
            NullLogger<AudioConcatService>.Instance, new FakeDsp(), _launch);

        var result = await svc.ConcatAsync(Owner, _scope, new AudioConcatInput(Chat, [], ContextRevision: state.Revision - 1),
            CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.ContextChanged);
        result.Context.Should().NotBeNull();
    }

    [Fact]
    public async Task Сведение_сверяет_нить_маршрута_со_стором()
    {
        var other = NewThread();
        var state = SetPrimary(NewThread());
        var engine = new DspAudioEngine(new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance), _workspace,
            NullLogger<DspAudioEngine>.Instance, new FakeDsp(), _launch);

        var result = await engine.MixAsync(Owner, _scope,
            new AudioMixInput(Chat, other, [new AudioMixStemInput("stem:vocals")], ContextRevision: state.Revision),
            CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.ContextChanged, "основной объект — другая нить");
        result.Context!.Primary!.Ref["threadId"]!.GetValue<string>().Should().NotBe(other);
    }

    // ── Агент ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Голос_для_агента_берётся_из_контекста_только_если_операция_его_принимает()
    {
        var slug = NewVoice();
        SetPrimary(NewThread());
        AddRef("audio-voice", new JsonObject { ["slug"] = slug }, "voice");

        _launch.AgentVoice(Owner, Chat, AudioOp.Speak).Should().Be("voice:" + slug);
        _launch.AgentVoice(Owner, Chat, AudioOp.Separate).Should().BeNull("«Стемы» голос не берут");
    }

    // ── Подставные ───────────────────────────────────────────────────────────────

    private sealed class FakeSpeech : IAudioEngine
    {
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key => "cloud";
        public string Label => "Облако";
        public string PriceUnit => AudioPriceUnits.Usd;
        public bool Enabled => true;
        public bool Registered => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new("cloud-speech", "Облачная речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset, AudioVoiceKind.Clone],
                [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Usd), new AudioPriceHint(0.00009, AudioPriceUnits.Chars, "chars")),
            new("paid-speech", "Платная речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset],
                [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Usd), new AudioPriceHint(0.00009, AudioPriceUnits.Chars, "chars")),
            new("gray-speech", "Серая речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset],
                [AudioOutputs.Audio], AudioLicenses.Commercial, AudioPriceUnits.Usd), null, DisabledReason: "нужен ключ"),
        ];

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            Accepted.TrySetResult();
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/wav", ".wav")], null, null, "r1", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeDsp : IAudioDsp
    {
        public bool Available => true;
        public IReadOnlyList<byte[]> Pieces { get; private set; } = [];

        public Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
            double? normalizeLufs, AudioFormat format, CancellationToken ct)
        {
            Pieces = pieces;
            return Task.FromResult(new AudioDspOutput("CONCAT"u8.ToArray(), format, null));
        }

        public Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct, AudioDspInfo? known) => throw new NotSupportedException();
        public Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs, AudioFormat format, CancellationToken ct, AudioDspInfo? known) => throw new NotSupportedException();
        public Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct) => throw new NotSupportedException();
    }
}
