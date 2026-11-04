using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// S2d-2c: video_thread_changed и события съёмки уходят по аудитории чата, а не всей группе проекта
public sealed class VideoEventAudienceTests : IDisposable
{
    private const string Owner = "owner";
    private const string Chat = "c1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-aud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Mock<ISessionDirectory> SessionDir(Session? session)
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
        var threads = new VideoJobThreads(new VideoThreadStore(Path.Combine(_root, "threads")),
            NullLogger<VideoJobThreads>.Instance, SessionDir(session).Object, null, hub.Object);
        // PrivateToUserId читается в момент отправки: меняем после создания сервиса
        if (session is not null) afterCreate?.Invoke(session);
        await threads.BroadcastAsync(Owner, "p1", Chat, VideoThreadsState.Empty);
        return calls;
    }

    [Fact]
    public async Task Нить_приватного_чата_только_автору() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1", PrivateToUserId = "author" }))
            .Should().Equal("owner:author:video_thread_changed");

    [Fact]
    public async Task Нить_общего_чата_группе_проекта() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" }))
            .Should().Equal("project:p1:video_thread_changed");

    [Fact]
    public async Task Приватность_нити_читается_в_момент_отправки() =>
        (await ThreadEventAsync(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" }, s => s.PrivateToUserId = "author"))
            .Should().Equal("owner:author:video_thread_changed");

    [Fact]
    public async Task Нить_удалённого_чата_только_владельцу() =>
        (await ThreadEventAsync(null)).Should().Equal("owner:owner:video_thread_changed");

    [Fact]
    public async Task Съёмка_приватного_чата_шлёт_прогресс_и_итог_только_автору()
    {
        var (hub, calls) = Hub();
        var session = new Session { Id = Chat, OwnerId = Owner, ProjectId = "proj1", PrivateToUserId = "author" };
        var store = new VideoThreadStore(Path.Combine(_root, "jobthreads"));
        var svc = new VideoEditJobService([new QuickEngine()], new VideoEditWorkspace(Path.Combine(_root, "ws")),
            new VideoJobThreads(store, NullLogger<VideoJobThreads>.Instance), NullLogger<VideoEditJobService>.Instance,
            broadcaster: hub.Object, sessions: SessionDir(session).Object);
        var scope = new VideoEditScope("proj1", null);
        var settings = new VideoSceneSettingsDto(null, null, "закат", null, null, null, null, null, null);
        var scene = store.AddScene(Owner, Chat, "", settings, null).Scene!.SceneId;

        var quote = (await svc.QuoteAsync(Owner, scope,
            new VideoQuoteRequest(Chat, scene, "fal", null, 1, 5, null, null), default)).Value!;
        var started = await svc.StartAsync(Owner, scope, new VideoLaunchRequest(quote.QuoteId, Chat, scene, null), default);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);

        calls.Should().Contain(c => c == "owner:author:video_edit_completed");
        calls.Should().OnlyContain(c => c.StartsWith("owner:author:"), "ни одно событие съёмки приватного чата не идёт группе проекта");
    }

    private sealed class QuickEngine : IVideoEngine
    {
        public string Key => "fal";
        public string Label => "fal";
        public string PriceUnit => "usd";
        public bool Enabled => true;

        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new VideoModelInfo("fal-model", "fal модель",
                new VideoCaps([5, 8], ["16:9"], false, false, VideoLicense.Commercial, "usd", FirstFrame: false),
                new VideoPriceHint(0.1, "usd", "sec")),
        ];

        public string? ScopeRefusal(VideoEditScope scope) => null;

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r1", Accepted: true));
            return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([1, 2, 3], "video/mp4", ".mp4", req.DurationSec, true),
                null, true, "r1", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }
}
