using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// S2d-2c: audio_thread_changed и события задач звука уходят по аудитории чата, а не всей группе проекта
public sealed class AudioEventAudienceTests : IDisposable
{
    private const string Owner = "owner";
    private const string Chat = "c1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-aud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Mock<ISessionDirectory> Directory_(Session? session)
    {
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById(Chat)).Returns(session);
        return dir;
    }

    private static (Mock<ISessionBroadcaster> Hub, List<string> Calls) Hub()
    {
        var calls = new List<string>();
        var hub = new Mock<ISessionBroadcaster>();
        hub.Setup(h => h.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((u, m) => { lock (calls) calls.Add($"owner:{u}:{m.Type}"); }).Returns(Task.CompletedTask);
        hub.Setup(h => h.ToProject(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((p, m) => { lock (calls) calls.Add($"project:{p}:{m.Type}"); }).Returns(Task.CompletedTask);
        return (hub, calls);
    }

    private async Task<List<string>> ThreadEventAsync(Session? session, Action<Session>? afterCreate = null)
    {
        var (hub, calls) = Hub();
        var threads = new AudioJobThreads(new AudioThreadStore(Path.Combine(_root, "threads")),
            NullLogger<AudioJobThreads>.Instance, Directory_(session).Object, null, hub.Object);
        // PrivateToUserId читается в момент отправки: меняем после создания сервиса
        if (session is not null) afterCreate?.Invoke(session);
        await threads.BroadcastAsync(Owner, "p1", Chat, AudioThreadsState.Empty);
        return calls;
    }

    [Fact]
    public async Task Нить_приватного_чата_только_автору() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1", PrivateToUserId = "author" }))
            .Should().Equal("owner:author:audio_thread_changed");

    [Fact]
    public async Task Нить_общего_чата_группе_проекта() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" }))
            .Should().Equal("project:p1:audio_thread_changed");

    [Fact]
    public async Task Приватность_нити_читается_в_момент_отправки() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" }, s => s.PrivateToUserId = "author"))
            .Should().Equal("owner:author:audio_thread_changed");

    [Fact]
    public async Task Нить_удалённого_чата_только_владельцу() =>
        (await ThreadEventAsync(null)).Should().Equal("owner:owner:audio_thread_changed");

    [Fact]
    public async Task Задача_приватного_чата_шлёт_прогресс_и_итог_только_автору()
    {
        var (hub, calls) = Hub();
        var session = new Session { Id = Chat, OwnerId = Owner, ProjectId = "p-1", PrivateToUserId = "author" };
        var engine = new QuickEngine();
        // ChatSessionId у задачи появляется только вместе с нитью чата
        var store = new AudioThreadStore(Path.Combine(_root, "jobthreads"));
        var threadId = store.Open(Owner, Chat, null, "", null).Thread!.Id;
        var svc = new AudioEditJobService([engine], new AudioEditWorkspace(Path.Combine(_root, "work")),
            NullLogger<AudioEditJobService>.Instance, new AudioJobThreads(store, NullLogger<AudioJobThreads>.Instance),
            broadcaster: hub.Object, sessions: Directory_(session).Object);
        var scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = _root, OwnerId = Owner });

        var quote = await svc.QuoteAsync(Owner, scope,
            new AudioQuoteRequest(AudioModes.Voice, "speak", "fal", QuickEngine.Model, SessionId: Chat, ThreadId: threadId, Text: "привет"), CancellationToken.None);
        quote.Error.Should().BeNull();
        var started = await svc.StartAsync(Owner, scope,
            new AudioJobInput(quote.Value!.QuoteId, SessionId: Chat, ThreadId: threadId, Text: "привет"), CancellationToken.None);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);

        calls.Should().Contain(c => c == "owner:author:audio_edit_completed");
        calls.Should().OnlyContain(c => c.StartsWith("owner:author:"), "ни одно событие задачи приватного чата не идёт группе проекта");
    }

    private sealed class QuickEngine : IAudioEngine
    {
        public const string Model = "quick-speech";

        public string Key => "fal";
        public string Label => "fal";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new(Model, "Речь", new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
                AudioLicenses.Apache2, AudioPriceUnits.Free), new AudioPriceHint(0, AudioPriceUnits.Free, "run")),
        ];

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            progress.Report(new AudioProgress(AudioStage.Running));
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/wav", ".wav")],
                null, false, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
