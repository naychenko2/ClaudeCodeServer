using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// Стенд фильма: проект во временной папке, нити сцен, рабочая папка клипов, хранилища фильма и службы поверх них.
// Чат s1 — чат проекта p1 владельца u1; чужие владелец и чат отделены Moq-справочником
internal sealed class FilmWorld : IDisposable
{
    public const string Owner = "u1";
    public const string ProjectId = "p1";
    public const string Session = "s1";

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "vfilm-" + Guid.NewGuid().ToString("N"));
    public readonly string ProjectRoot;
    public readonly Project Project;
    public readonly VideoEditScope Scope;
    public readonly VideoThreadStore Threads;
    public readonly VideoEditWorkspace Workspace;
    public readonly FilmStore Films = new();
    public readonly FilmSideStore Side;
    public readonly FilmBuildRegistry Registry = new();
    public readonly RecordingFeed Feed = new();
    public readonly RecordingBroadcaster Broadcaster = new();
    public readonly VideoJobThreads JobThreads;
    public readonly Mock<ISessionDirectory> Directory = new();
    public readonly FilmService Service;
    // Стор контекста чата и зеркало — только при withContext (стенд видит выбор человека через контекст)
    public readonly ChatContextStore? Context;
    public readonly ChatContextFocusMirror? Mirror;
    private int _jobs;

    public FilmWorld(IVideoDsp? dsp = null, bool withContext = false)
    {
        ProjectRoot = Path.Combine(Dir, "project");
        System.IO.Directory.CreateDirectory(ProjectRoot);
        Project = new Project { Id = ProjectId, OwnerId = Owner, RootPath = ProjectRoot };
        Scope = VideoEditScope.Of(Project);
        Threads = new VideoThreadStore(Path.Combine(Dir, "threads"));
        Workspace = new VideoEditWorkspace(Path.Combine(Dir, "work"));
        Side = new FilmSideStore(Path.Combine(Dir, "films"));
        var session = new Session { Id = Session, OwnerId = Owner, ProjectId = ProjectId };
        Directory.Setup(d => d.GetById(Session)).Returns(session);
        Directory.Setup(d => d.ResolveOwnerId(session)).Returns(Owner);
        if (withContext)
        {
            Context = new ChatContextStore(Path.Combine(Dir, "ctx"), new ContextKindRegistry([new VideoContextKind(Threads)]));
            Mirror = new ChatContextFocusMirror(Context, NullLogger<ChatContextFocusMirror>.Instance);
        }
        JobThreads = new VideoJobThreads(Threads, NullLogger<VideoJobThreads>.Instance, Directory.Object, Feed, Broadcaster, Mirror);
        Service = new FilmService(Films, Side, JobThreads, Registry, NullLogger<FilmService>.Instance, dsp);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    public string Full(string relative) => Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    public string WriteFile(string relative, byte[]? bytes = null)
    {
        var full = Full(relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes ?? [1, 2, 3, 4]);
        return full;
    }

    public static FilmItem Item(string file, double from = 0, double to = 5) =>
        new(file, [from, to], new FilmSceneSnapshot("т", null, null, "fal", "veo", 5));

    public static FilmDocument Doc(params FilmItem[] items) =>
        new(FilmDocument.CurrentSchema, "16:9", items,
            [.. Enumerable.Range(0, Math.Max(0, items.Length - 1)).Select(_ => new FilmCut(FilmCutTypes.Butt, 0))], null, []);

    // Фильм на диске проекта; возвращает ревизию
    public string WriteFilm(string path, FilmDocument doc)
    {
        var written = Films.WriteFile(Full(path), doc, null, create: true);
        if (written.Status != FilmStore.WriteStatus.Ok) throw new InvalidOperationException(written.Status.ToString());
        return written.Current.Revision;
    }

    // Сцена с готовой версией: клип лежит в рабочей папке задачи, версия — в нити чата
    public (VideoSceneDto Scene, VideoClipVersionDto Version) SceneWithClip(string folder = "video/утро",
        VideoSceneSettingsDto? settings = null, string provider = "fal", VideoCostDto? cost = null, string name = "Сцена 1")
    {
        settings ??= new VideoSceneSettingsDto(null, null, "Камера у окна", provider, "veo", 5, "16:9", false, 1);
        var added = Threads.AddScene(Owner, Session, folder, settings, null, name);
        var scene = added.Scene!;
        var job = "job-" + Interlocked.Increment(ref _jobs);
        Threads.AddLaunch(Owner, Session, scene.SceneId, new VideoLaunchDto(job, DateTime.UtcNow.AddSeconds(-30),
            VideoLaunchStatus.Running, false, VideoInitiators.Human, provider, "veo", 1, "т", null, null));
        var finished = Threads.FinishLaunch(Owner, Session, scene.SceneId, job, VideoLaunchStatus.Done,
            [new VideoVariantResult(1, 5, 4, false, cost ?? new VideoCostDto(provider == "local" ? "local" : "usd", provider == "local" ? 0 : 1.5))],
            VideoSignatures.Snapshot(settings));
        Workspace.SaveClip(Owner, job, 1, [9, 9, 9, 9], ".mp4");
        var version = finished.NewVersions.Single();
        return (Threads.Get(Owner, Session).Scenes.Single(s => s.SceneId == scene.SceneId), version);
    }

    public sealed class RecordingFeed : IChatFeed
    {
        public List<(string SessionId, StoredModuleRecord Record)> Records { get; } = [];

        public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default)
        {
            lock (Records) Records.Add((sessionId, record));
            return Task.FromResult(true);
        }
    }

    public sealed class RecordingBroadcaster : ISessionBroadcaster
    {
        public List<ServerMessage> Sent { get; } = [];

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
