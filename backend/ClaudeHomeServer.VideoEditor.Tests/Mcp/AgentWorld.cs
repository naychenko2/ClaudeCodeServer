using System.Security.Claims;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using ClaudeHomeServer.Services.VideoEditor.Tests.Films;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// Стенд агента «Видео»: тот же мир, что у фильма (FilmWorld), плюс исполнитель съёмки на подставном поставщике,
// сервисы сцен, сохранения и сборки, тулсет — и КОНТРОЛЛЕРЫ ручек человека поверх тех же сервисов. Матричный тест
// гоняет одно действие и ручкой, и инструментом и сравнивает записи ленты.
internal sealed class AgentWorld : IDisposable
{
    public const string PersonalChat = "chat-personal";

    public readonly FilmWorld F;
    public readonly FakeEngine Engine;
    public readonly SpendLog Spend = new();
    public readonly VideoPrefsService Prefs;
    public readonly VideoEditJobService Jobs;
    public readonly VideoSceneService Scenes;
    public readonly FilmSceneSaver Saver;
    public readonly FilmAssembler Assembler;
    public readonly Mock<IDelegatedTurnGate> Gate = new();
    public readonly VideoEditorToolset Toolset;
    public readonly VideoEditorController Rest;
    public readonly FilmController RestFilms;
    public readonly Dictionary<string, Session> Sessions = new();

    public string Owner => FilmWorld.Owner;
    public string ProjectId => FilmWorld.ProjectId;
    public string Chat => FilmWorld.Session;

    public AgentWorld(bool blocking = false, bool agentLaunch = true, bool delegatedDenied = false, bool withGate = true,
        bool flagOn = true, bool withContext = false)
    {
        F = new FilmWorld(new ToolDsp(), withContext);
        Engine = new FakeEngine("fal", blocking);
        Prefs = new VideoPrefsService(new VideoPrefsStore(Path.Combine(F.Dir, "prefs")));
        Jobs = new VideoEditJobService([Engine], F.Workspace, F.JobThreads, NullLogger<VideoEditJobService>.Instance,
            Prefs, Spend, F.Broadcaster);
        Scenes = new VideoSceneService(F.JobThreads, Prefs);
        Saver = new FilmSceneSaver(F.Service, F.Workspace, NullLogger<FilmSceneSaver>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VideoEditorToolset.AgentLaunchKey] = agentLaunch ? "true" : "false",
        }).Build();
        Assembler = new FilmAssembler(F.Service, F.Registry, config, NullLogger<FilmAssembler>.Instance, new ToolDsp());

        Sessions[Chat] = new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId };
        Sessions[PersonalChat] = new Session { Id = PersonalChat, OwnerId = Owner };
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => Sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(It.IsAny<string>(), FeatureFlagKeys.VideoEditor)).Returns(flagOn);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(F.Project);
        Gate.Setup(g => g.Deny(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string _, string action) => delegatedDenied ? $"{action}: делегированный ход — отказ" : null);

        Toolset = new VideoEditorToolset(accessor.Object, flags.Object, projects.Object, [Engine], F.JobThreads, Jobs, Prefs,
            Scenes, F.Service, Saver, Assembler, withGate ? Gate.Object : null, config);

        // Ручки человека поверх тех же сервисов: гейт — настоящий, поверх подставных справочников
        var scopeGate = new VideoEditScopeGate(flags.Object, projects.Object, F.Directory.Object);
        Rest = new VideoEditorController(scopeGate, [Engine], Jobs, F.JobThreads, Prefs, F.Workspace, Scenes)
            { ControllerContext = UserContext() };
        var music = new FilmMusicComposer(F.Service, F.Side, F.JobThreads, projects.Object, NullLogger<FilmMusicComposer>.Instance);
        RestFilms = new FilmController(scopeGate, F.Service, Saver, Assembler, music) { ControllerContext = UserContext() };
    }

    private ControllerContext UserContext() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner)], "test")),
        },
    };

    public void Dispose()
    {
        Jobs.Dispose();
        F.Dispose();
    }

    public McpToolCallContext Ctx(string? chat = null, string? owner = null) =>
        new(owner ?? Owner, chat ?? Chat, chat ?? Chat);

    public Task<McpToolCallResult> Call(string tool, JsonObject? args = null, string? chat = null) =>
        Toolset.CallAsync(tool, args ?? new JsonObject(), Ctx(chat), default);

    public static JsonObject Parse(McpToolCallResult result) => JsonNode.Parse(result.Text)!.AsObject();

    // Сцена без версий, в фокусе; запись ленты о ней в тестах сбрасывается вызывающим
    public VideoSceneDto NewScene(string text = "закат над морем", string folder = "video/утро", string name = "Сцена 1") =>
        F.Threads.AddScene(Owner, Chat, folder,
            new VideoSceneSettingsDto(null, null, text, null, null, 5, "16:9", false, 1), null, name).Scene!;

    // Дождаться конца всех съёмок чата
    public async Task WaitJobsAsync()
    {
        foreach (var launch in F.Threads.Get(Owner, Chat).Scenes.SelectMany(s => s.Launches))
            await Jobs.WhenDone(launch.JobId);
    }

    public sealed class SpendLog : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    internal sealed class ToolDsp : IVideoDsp
    {
        public bool Available => true;

        public Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct) =>
            Task.FromResult<VideoDspInfo?>(new VideoDspInfo(10, 640, 360, 24, true));

        public Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);

        public Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct) =>
            Task.FromResult(VideoDspResult.Success);

        public async Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress,
            CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outPath, [1, 2, 3], ct);
            return VideoDspResult.Success;
        }
    }

    // Подставной поставщик: сразу отдаёт клип (или, blocking, висит до отмены — для video_cancel)
    internal sealed class FakeEngine(string key, bool blocking) : IVideoEngine
    {
        public string Key => key;
        public string Label => key;
        public string PriceUnit => VideoPriceUnits.Usd;
        public bool Enabled => true;
        public int Runs;

        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new VideoModelInfo("fal-model", "fal модель",
                new VideoCaps([5, 8], ["16:9"], false, false, VideoLicense.Commercial, VideoPriceUnits.Usd, FirstFrame: false),
                new VideoPriceHint(0.1, VideoPriceUnits.Usd, "sec")),
        ];

        public async Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r1", Accepted: true));
            if (blocking) await Task.Delay(Timeout.Infinite, ct);
            return new VideoResult(VideoOutcome.Ok, new VideoFile([1, 2, 3], "video/mp4", ".mp4", req.DurationSec, true),
                null, true, "r1", null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }
}
