using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

public sealed class AudioEditJobServiceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Session = "chat-1";

    private static readonly Project Project = new() { Id = "p-1", RootPath = "/srv/p" };
    private static readonly AudioEditScope Scope = AudioEditScope.Of(Project);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTime _time = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly List<SpendRecord> _spend = [];
    private readonly Mock<IChatFeed> _feed = new();
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;

    public AudioEditJobServiceTests()
    {
        _store = new AudioThreadStore(Path.Combine(_root, "threads"));
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        _feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service(params IAudioEngine[] engines)
    {
        var spend = new Mock<ISpendCollector>();
        spend.Setup(s => s.Record(It.IsAny<SpendRecord>())).Callback<SpendRecord>(r => { lock (_spend) _spend.Add(r); });
        var threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance, null, _feed.Object, _broadcaster);
        return new AudioEditJobService(engines, _workspace, NullLogger<AudioEditJobService>.Instance, threads, null,
            spend.Object, _broadcaster, _time);
    }

    private static AudioQuoteRequest Speak(string? provider = null) =>
        new(AudioModes.Voice, "speak", provider);

    private async Task<string> QuoteAsync(AudioEditJobService svc, AudioQuoteRequest request, string owner = Owner)
    {
        var quote = await svc.QuoteAsync(owner, Scope, request, CancellationToken.None);
        quote.Error.Should().BeNull();
        return quote.Value!.QuoteId;
    }

    private async Task<AudioEditCallResult<AudioJobCreatedDto>> LaunchAsync(AudioEditJobService svc, AudioQuoteRequest request,
        string owner = Owner, AudioJobInput? input = null)
    {
        var quoteId = await QuoteAsync(svc, request, owner);
        return await svc.StartAsync(owner, Scope, (input ?? new AudioJobInput("")) with { QuoteId = quoteId }, CancellationToken.None);
    }

    // ── Котировка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_ExpiredQuote_Refused()
    {
        var engine = new FakeEngine("fal");
        var svc = Service(engine);
        var quoteId = await QuoteAsync(svc, Speak());

        _time.Advance(AudioEditJobService.QuoteTtl + TimeSpan.FromSeconds(1));
        var started = await svc.StartAsync(Owner, Scope, new AudioJobInput(quoteId), CancellationToken.None);

        started.ErrorCode.Should().Be(AudioEditErrorCodes.QuoteNotFound);
        started.Error.Should().Be(AudioEditJobService.QuoteExpiredText);
        engine.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Start_ForeignOrUsedQuote_Refused()
    {
        var engine = new FakeEngine("fal");
        var svc = Service(engine);
        var quoteId = await QuoteAsync(svc, Speak());

        (await svc.StartAsync("someone-else", Scope, new AudioJobInput(quoteId), CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.QuoteNotFound);
        var first = await svc.StartAsync(Owner, Scope, new AudioJobInput(quoteId), CancellationToken.None);
        first.Error.Should().BeNull();
        (await svc.StartAsync(Owner, Scope, new AudioJobInput(quoteId), CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.QuoteNotFound);
    }

    [Fact]
    public async Task Quote_ExplicitProviderDown_RefusedWithoutSubstitution()
    {
        var down = new FakeEngine("fal") { IsEnabled = false };
        var spare = new FakeEngine("higgsfield");
        var svc = Service(down, spare);

        var quote = await svc.QuoteAsync(Owner, Scope, Speak("fal"), CancellationToken.None);

        quote.ErrorCode.Should().Be(AudioEditErrorCodes.ProviderUnavailable);
        quote.Error.Should().Be("Поставщик «fal» сейчас недоступен");
        quote.Value.Should().BeNull();
    }

    [Fact]
    public async Task Quote_ResolvesChain_ThreadSettingsBeforePrefsAndDefault()
    {
        var fal = new FakeEngine("fal");
        var higgs = new FakeEngine("higgsfield");
        var thread = _store.Open(Owner, Session, "voice/a.wav", null, null,
            new AudioThreadSettings(AudioModes.Voice, "speak", "higgsfield", null, null, Count: 3)).Thread!;
        var svc = Service(fal, higgs);

        var quote = await svc.QuoteAsync(Owner, Scope,
            new AudioQuoteRequest(AudioModes.Voice, SessionId: Session, ThreadId: thread.Id), CancellationToken.None);

        quote.Value!.Provider.Should().Be("higgsfield");
        quote.Value.Count.Should().Be(3);
        quote.Value.Op.Should().Be(AudioOp.Speak);
        quote.Value.ExpiresAt.Should().Be(_time.GetUtcNow().UtcDateTime + AudioEditJobService.QuoteTtl);
    }

    // ── Потолки ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_ThirdJobOfOwner_RefusedWithText()
    {
        var engine = new FakeEngine("fal") { Hold = true };
        var svc = Service(engine);

        (await LaunchAsync(svc, Speak())).Error.Should().BeNull();
        (await LaunchAsync(svc, Speak())).Error.Should().BeNull();
        var third = await LaunchAsync(svc, Speak());

        third.ErrorCode.Should().Be(AudioEditErrorCodes.TooManyJobs);
        third.Error.Should().Be(AudioEditJobService.OwnerLimitText);
        engine.Release();
    }

    [Fact]
    public async Task Start_FifthJobOfInstance_RefusedWithText()
    {
        var engine = new FakeEngine("fal") { Hold = true };
        var svc = Service(engine);

        foreach (var owner in new[] { "a", "a", "b", "b" })
            (await LaunchAsync(svc, Speak(), owner)).Error.Should().BeNull();
        var fifth = await LaunchAsync(svc, Speak(), "c");

        fifth.ErrorCode.Should().Be(AudioEditErrorCodes.TooManyJobs);
        fifth.Error.Should().Be(AudioEditJobService.InstanceLimitText);
        engine.Release();
    }

    [Fact]
    public async Task Start_SecondLocalHeavy_Refused_LightStillRuns()
    {
        var local = new FakeEngine("local") { Hold = true };
        var svc = Service(local);
        var train = new AudioQuoteRequest(AudioModes.Voice, "trainVoice", "local");

        (await LaunchAsync(svc, train, "a")).Error.Should().BeNull();
        var second = await LaunchAsync(svc, train, "b");
        var light = await LaunchAsync(svc, Speak("local"), "b");

        second.ErrorCode.Should().Be(AudioEditErrorCodes.HeavyBusy);
        second.Error.Should().Be(AudioEditJobService.HeavyBusyText);
        light.Error.Should().BeNull();
        local.Release();
    }

    // ── Исполнение ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_Success_BecomesThreadVersionWithRolesAndLicense()
    {
        var engine = new FakeEngine("local");
        var svc = Service(engine);
        var thread = _store.Open(Owner, Session, "voice/a.wav", null, null).Thread!;

        var started = await LaunchAsync(svc, new AudioQuoteRequest(AudioModes.Process, "separate", "local", Count: 2),
            input: new AudioJobInput("", Session, thread.Id, Prompt: "раздели"));
        await svc.WhenDone(started.Value!.JobId);

        var after = _store.Get(Owner, Session).Threads.Single();
        var versions = after.Versions.Where(v => v.JobId == started.Value.JobId).ToList();
        versions.Should().HaveCount(2);
        versions.Should().AllSatisfy(v =>
        {
            v.License.Should().Be(AudioLicenses.CcByNc4.Label);
            v.BaseVersionId.Should().Be(AudioThreadVersion.OriginId);
            v.Files.Select(f => f.Role).Should().BeEquivalentTo([AudioFileRoles.Main, AudioFileRoles.Stem("vocals")]);
        });
        foreach (var file in versions[0].Files)
            File.Exists(Path.Combine(_workspace.JobDir(Owner, started.Value.JobId), file.Path)).Should().BeTrue();
        after.Launches.Single().Status.Should().Be(AudioThreadLaunchStatus.Done);
        after.Launches.Single().License.Should().Be(AudioLicenses.CcByNc4.Label);
        after.CurrentVersionId.Should().Be(versions[0].Id);
        svc.Get(Owner, Scope.Key, started.Value.JobId)!.Status.Should().Be(AudioEditJobStatus.Completed);

        _feed.Verify(f => f.AppendRecordAsync(Session,
            It.Is<StoredModuleRecord>(r => r.Module == "audioeditor" && r.RecordType == AudioJobThreads.RecordTypes.LaunchVersions),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_LocalSpend_RecordedOnAcceptWithZero()
    {
        var engine = new FakeEngine("local") { Hold = true };
        var svc = Service(engine);

        var started = await LaunchAsync(svc, Speak("local"), input: new AudioJobInput("", Text: "Привет"));
        await engine.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Задача ещё идёт, а трата уже лежит: пишется при принятии поставщиком
        SpendRecord record;
        lock (_spend) record = _spend.Should().ContainSingle().Subject;
        record.CostUsd.Should().Be(0);
        record.CostCredits.Should().BeNull();
        record.CostRub.Should().BeNull();
        record.Generations.Should().Be(1);
        record.OwnerId.Should().Be(Owner);
        record.ProjectId.Should().Be(Project.Id);
        record.Provider.Should().Be("local");
        record.Label.Should().Be($"{FakeEngine.SpeakModel} · бесплатно");

        engine.Release();
        await svc.WhenDone(started.Value!.JobId);
        lock (_spend) _spend.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_PerCharSpend_InUsdWithUnitInLabel()
    {
        var engine = new FakeEngine("higgsfield", AudioPriceUnits.Chars, 0.5);
        var svc = Service(engine);

        var started = await LaunchAsync(svc, Speak("higgsfield") with { Text = "Привет" },
            input: new AudioJobInput("", Text: "Привет"));
        await svc.WhenDone(started.Value!.JobId);

        lock (_spend)
        {
            var record = _spend.Should().ContainSingle().Subject;
            record.CostUsd.Should().Be(3);
            record.Label.Should().Be($"{FakeEngine.SpeakModel} · 6 симв.");
        }
    }

    // ── Сосед при отказе ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Failure_RetryQuoteIsNeighborWithSameOpAndVoiceKind_NotLaunched()
    {
        var failing = new FakeEngine("fal", cloneKind: AudioVoiceKind.Element) { Outcome = AudioOutcome.Failed };
        var otherKind = new FakeEngine("higgsfield", cloneKind: AudioVoiceKind.Clone);
        var sameKind = new FakeEngine("yandex", cloneKind: AudioVoiceKind.Element);
        var svc = Service(failing, otherKind, sameKind);
        var clone = new AudioQuoteRequest(AudioModes.Voice, "cloneVoice", "fal", VoiceKind: AudioVoiceKind.Element);

        var started = await LaunchAsync(svc, clone);
        await svc.WhenDone(started.Value!.JobId);

        var failed = _broadcaster.Of<AudioEditFailedMessage>().Should().ContainSingle().Subject;
        failed.RetryQuote.Should().NotBeNull();
        failed.RetryQuote!.Provider.Should().Be("yandex");
        failed.RetryQuote.Op.Should().Be(AudioOp.CloneVoice);
        failed.RetryQuote.VoiceKind.Should().Be(AudioVoiceKind.Element);
        otherKind.Runs.Should().Be(0);
        sameKind.Runs.Should().Be(0);
        svc.Get(Owner, Scope.Key, started.Value.JobId)!.Provider.Should().Be("fal");

        // Котировка соседа — только предложение: запускается отдельным вызовом человека
        (await svc.StartAsync(Owner, Scope, new AudioJobInput(failed.RetryQuote.QuoteId), CancellationToken.None))
            .Error.Should().BeNull();
    }

    [Fact]
    public async Task Failure_NoNeighborWithSameVoiceKind_NoRetry()
    {
        var failing = new FakeEngine("fal", cloneKind: AudioVoiceKind.Element) { Outcome = AudioOutcome.Failed };
        var otherKind = new FakeEngine("higgsfield", cloneKind: AudioVoiceKind.Clone);
        var svc = Service(failing, otherKind);
        var clone = new AudioQuoteRequest(AudioModes.Voice, "cloneVoice", "fal", VoiceKind: AudioVoiceKind.Element);

        var started = await LaunchAsync(svc, clone);
        await svc.WhenDone(started.Value!.JobId);

        _broadcaster.Of<AudioEditFailedMessage>().Single().RetryQuote.Should().BeNull();
        otherKind.Runs.Should().Be(0);
    }

    // ── События ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_SendsProgressThreadChangedAndCompletedToOwner()
    {
        var engine = new FakeEngine("local");
        var svc = Service(engine);
        var thread = _store.Open(Owner, Session, "voice/a.wav", null, null).Thread!;

        var started = await LaunchAsync(svc, Speak("local"), input: new AudioJobInput("", Session, thread.Id));
        await svc.WhenDone(started.Value!.JobId);

        _broadcaster.Owners.Should().OnlyContain(o => o == Owner);
        _broadcaster.Of<AudioEditProgressMessage>().Should().NotBeEmpty();
        _broadcaster.Of<AudioThreadChangedMessage>().Should().HaveCountGreaterThanOrEqualTo(2)
            .And.OnlyContain(m => m.SessionId == Session);
        var completed = _broadcaster.Of<AudioEditCompletedMessage>().Should().ContainSingle().Subject;
        completed.ThreadId.Should().Be(thread.Id);
        completed.Variants.Should().Equal(1);
        // Версии в нити раньше события completed
        _broadcaster.Messages.FindLastIndex(m => m is AudioThreadChangedMessage)
            .Should().BeLessThan(_broadcaster.Messages.FindIndex(m => m is AudioEditCompletedMessage));
    }

    [Fact]
    public async Task Cancel_StopsJobAndSendsCancelled()
    {
        var engine = new FakeEngine("fal") { Hold = true };
        var svc = Service(engine);
        var started = await LaunchAsync(svc, Speak());
        await engine.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var job = await svc.CancelAsync(Owner, Scope.Key, started.Value!.JobId, CancellationToken.None);

        job!.Status.Should().Be(AudioEditJobStatus.Cancelled);
        _broadcaster.Of<AudioEditFailedMessage>().Single().Outcome.Should().Be(AudioOutcome.Cancelled);
        (await svc.CancelAsync("someone-else", Scope.Key, started.Value.JobId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PrefsSave_SendsPrefsChanged()
    {
        var prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_root, "prefs")), _broadcaster);

        await prefs.SaveAsync(Owner, Scope, AudioModes.Music, new AudioModePrefs("song", "local", null, 2, null));

        var changed = _broadcaster.Of<AudioPrefsChangedMessage>().Should().ContainSingle().Subject;
        changed.Mode.Should().Be(AudioModes.Music);
        changed.Prefs.Count.Should().Be(2);
        _broadcaster.Owners.Should().Equal(Owner);
    }

    // ── Подставки ────────────────────────────────────────────────────────────────

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingBroadcaster : ISessionBroadcaster
    {
        private readonly ConcurrentQueue<(string Owner, ServerMessage Message)> _sent = new();

        public List<ServerMessage> Messages => [.. _sent.Select(s => s.Message)];
        public List<string> Owners => [.. _sent.Select(s => s.Owner)];
        public List<T> Of<T>() => [.. Messages.OfType<T>()];

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            _sent.Enqueue((ownerId, message));
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    // Поставщик-подставка: речь, клон заданного вида, стемы (CC BY-NC) и тяжёлое обучение голоса
    private sealed class FakeEngine(string key, string unit = AudioPriceUnits.Free, double price = 0,
        AudioVoiceKind cloneKind = AudioVoiceKind.Clone) : IAudioEngine
    {
        public const string SpeakModel = "fake-speech";

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public bool IsEnabled { get; init; } = true;
        public bool Hold { get; init; }
        public AudioOutcome Outcome { get; init; } = AudioOutcome.Ok;
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs => _runs;

        public void Release() => _release.TrySetResult();

        public string Key => key;
        public string Label => key;
        public string PriceUnit => unit;
        public bool Enabled => IsEnabled;
        // Заведён всегда: «лежит сейчас» — это Enabled=false
        public bool Registered => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new(SpeakModel, "Речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
                AudioLicenses.Apache2, unit), new AudioPriceHint(price, unit, "unit")),
            new("fake-clone", "Клон", new AudioCaps([AudioOp.CloneVoice], ["ru"], [cloneKind], [AudioOutputs.Audio],
                AudioLicenses.Mit, unit), new AudioPriceHint(price, unit, "unit")),
            new("fake-stems", "Стемы", new AudioCaps([AudioOp.Separate], [], [], [AudioOutputs.Stems],
                AudioLicenses.CcByNc4, unit, LanguageNeutral: true), new AudioPriceHint(price, unit, "run")),
            new("fake-rvc", "RVC", new AudioCaps([AudioOp.TrainVoice], [], [AudioVoiceKind.Rvc], [AudioOutputs.Model],
                AudioLicenses.Mit, unit, HeavyOps: [AudioOp.TrainVoice]), new AudioPriceHint(price, unit, "run")),
        ];

        public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            progress.Report(new AudioProgress(AudioStage.Queued, 1, 5));
            Accepted.TrySetResult();
            if (Hold) await _release.Task.WaitAsync(ct);
            if (Outcome != AudioOutcome.Ok) return AudioResult.Fail(Outcome, "поставщик не справился");
            progress.Report(new AudioProgress(AudioStage.Running));
            List<AudioFile> files = req.Op == AudioOp.Separate
                ? [new("main", [1], "audio/mpeg", ".mp3"), new("stem:vocals", [2], "audio/mpeg", ".mp3")]
                : [new("main", [1], "audio/wav", ".wav")];
            return new AudioResult(AudioOutcome.Ok, files, null, null, "remote-1", null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }
}
