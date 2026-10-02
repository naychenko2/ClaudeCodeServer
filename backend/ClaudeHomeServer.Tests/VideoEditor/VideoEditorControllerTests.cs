using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.VideoEditor;

// Ручки модуля «Видео» на собранном приложении (ADR-022 §2, §6): флаг и изоляция владельцев (чужое
// неотличимо от несуществующего), ревизия сцен — 409, отдача клипа версии с Range, папки только внутри
// video/, личный чат без local и без проектных путей, локальный проект отказывает до диска.
// Поставщик — фейк в памяти поверх настоящего исполнителя съёмки.
public class VideoEditorControllerTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _projectRoot;
    private static readonly byte[] Clip = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

    public VideoEditorControllerTests()
    {
        _factory.ExtraServices = services =>
        {
            services.AddSingleton<IVideoEngine>(new FakeEngine());
            // Настоящий local-драйвер модуля с «живым» швом: в личном чате он обязан отказать
            services.AddSingleton<ILocalVideoMedia>(new FakeLocalMedia());
        };
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        EnableFlag(TestWebApplicationFactory.TestUsername);
        (_projectId, _projectRoot) = CreateProject(TestWebApplicationFactory.TestUsername);
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeLocalMedia : ILocalVideoMedia
    {
        public bool Available => true;
        public bool Configured => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult<int?>(0);
        public int? EtaSeconds(LocalVideoRequest request) => 60;
        public Task<LocalVideoSubmitted> SubmitAsync(LocalVideoRequest request, CancellationToken ct) =>
            Task.FromResult(LocalVideoSubmitted.Fail("не должен вызываться"));
        public Task<LocalVideoPoll> PollAsync(string ticket, CancellationToken ct) =>
            Task.FromResult(new LocalVideoPoll(LocalVideoState.Failed, null, null, "нет"));
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => Task.FromResult(false);
    }

    // Текстовая модель без кадров: сцене не нужны файлы проекта
    private sealed class FakeEngine : IVideoEngine
    {
        private static readonly VideoModelInfo Model = new("fake-model", "Фейк",
            new VideoCaps([5, 8], ["16:9"], false, false, VideoLicense.Commercial, VideoPriceUnits.Usd, FirstFrame: false),
            new VideoPriceHint(0.1, VideoPriceUnits.Usd, "sec"));

        public string Key => "fake";
        public string Label => "Фейк";
        public string PriceUnit => VideoPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<VideoModelInfo> Models => [Model];

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r", Accepted: true));
            return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile(Clip, "video/mp4", ".mp4", req.DurationSec, true),
                null, true, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private void EnableFlag(string username)
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(username)!.Id, FeatureFlagKeys.VideoEditor, true).Should().BeTrue();
    }

    private (string Id, string Root) CreateProject(string username)
    {
        var user = _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!;
        var dir = Path.Combine(_factory.TempDir, "vid_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var id = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("vid-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, username).Id;
        return (id, dir);
    }

    private string Root => $"/api/projects/{_projectId}/video-editor";

    private string Chat(string sessionId) => $"{Root}/sessions/{sessionId}";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> ProjectChat() => (await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Видео")).Id;

    private async Task<long> Revision(string chatUrl) =>
        (await Json(await _client.GetAsync($"{chatUrl}/scenes"))).GetProperty("revision").GetInt64();

    private async Task<string> AddScene(string chatUrl, string folder = "video/утро")
    {
        var resp = await _client.PostAsJsonAsync($"{chatUrl}/scenes",
            new { folder, settings = Settings("закат"), revision = await Revision(chatUrl) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("focus").GetProperty("sceneId").GetString()!;
    }

    private static object Settings(string text) =>
        new { text, provider = "fake", model = "fake-model", durationSec = 5, aspect = "16:9", sound = false, count = 1 };

    // Котировка → запуск → ожидание итога; возвращает id новой версии
    private async Task<string> Shoot(string scopeUrl, string chatId, string sceneId)
    {
        var quote = await _client.PostAsJsonAsync($"{scopeUrl}/quote",
            new { sessionId = chatId, sceneId, provider = "fake", model = "fake-model", count = 1, durationSec = 5 });
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        var quoteId = (await Json(quote)).GetProperty("quoteId").GetString()!;

        var started = await _client.PostAsJsonAsync($"{scopeUrl}/jobs", new { quoteId, sessionId = chatId, sceneId });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        await _factory.Services.GetRequiredService<VideoEditJobService>().WhenDone(jobId);

        var job = await Json(await _client.GetAsync($"{scopeUrl}/jobs/{jobId}"));
        job.GetProperty("status").GetString().Should().Be("completed", job.ToString());
        var store = _factory.Services.GetRequiredService<VideoJobThreads>().Store;
        return store.Get(_ownerId, chatId).Scenes.Single(s => s.SceneId == sceneId).Versions.Single(v => v.JobId == jobId).VersionId;
    }

    [Fact]
    public async Task Без_флага_и_без_входа_ручки_закрыты()
    {
        var chat = await ProjectChat();
        (await _factory.CreateClient().GetAsync($"{Root}/catalog")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(_ownerId, FeatureFlagKeys.VideoEditor, false);

        (await _client.GetAsync($"{Root}/catalog")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"{Chat(chat)}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Состояние_чата_несёт_нити_каталог_и_префы()
    {
        var chat = await ProjectChat();

        var state = await Json(await _client.GetAsync($"{Chat(chat)}/state"));

        state.GetProperty("threads").GetProperty("scenes").GetArrayLength().Should().Be(0);
        state.GetProperty("catalog").GetProperty("providers").EnumerateArray()
            .Select(p => p.GetProperty("key").GetString()).Should().Contain(["fake", "local"]);
        state.GetProperty("catalog").GetProperty("autoProviders").EnumerateArray().Select(e => e.GetString()).Should().Contain("fake");
        state.GetProperty("prefs").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Сцена_снимается_и_клип_отдаётся_с_Range()
    {
        var chat = await ProjectChat();
        var sceneId = await AddScene(Chat(chat));

        var versionId = await Shoot(Root, chat, sceneId);

        var url = $"{Chat(chat)}/scenes/{sceneId}/versions/{versionId}/file";
        var full = await _client.GetAsync(url);
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        full.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        (await full.Content.ReadAsByteArrayAsync()).Should().Equal(Clip);

        using var range = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(10, 19) } };
        var partial = await _client.SendAsync(range);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(Clip[10..20]);

        var state = await Json(await _client.GetAsync($"{Chat(chat)}/scenes"));
        var scene = state.GetProperty("scenes")[0];
        scene.GetProperty("versions")[0].GetProperty("cost").GetProperty("currency").GetString().Should().Be("usd");
        scene.GetProperty("launches")[0].GetProperty("status").GetString().Should().Be("done");
    }

    [Fact]
    public async Task Устаревшая_ревизия_409_со_свежим_состоянием()
    {
        var chat = await ProjectChat();
        await AddScene(Chat(chat));

        var stale = await _client.PostAsJsonAsync($"{Chat(chat)}/scenes", new { folder = "video", settings = Settings("т"), revision = 0 });

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(stale);
        body.GetProperty("code").GetString().Should().Be(VideoEditorErrors.RevisionConflict);
        body.GetProperty("state").GetProperty("scenes").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("kadry")]
    [InlineData("../video")]
    [InlineData("music/..")]
    public async Task Папка_сцены_вне_video_отказ_до_записи(string folder)
    {
        var chat = await ProjectChat();

        var resp = await _client.PostAsJsonAsync($"{Chat(chat)}/scenes",
            new { folder, settings = Settings("т"), revision = await Revision(Chat(chat)) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        (await Json(await _client.GetAsync($"{Chat(chat)}/scenes"))).GetProperty("scenes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Чужое_неотличимо_от_несуществующего()
    {
        var chat = await ProjectChat();
        var sceneId = await AddScene(Chat(chat));
        var versionId = await Shoot(Root, chat, sceneId);
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        EnableFlag(TestWebApplicationFactory.SecondUsername);

        (await second.GetAsync($"{Root}/catalog")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.GetAsync($"{Chat(chat)}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.GetAsync($"{Chat(chat)}/scenes/{sceneId}/versions/{versionId}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var (secondProject, _) = CreateProject(TestWebApplicationFactory.SecondUsername);
        var foreignChat = $"/api/projects/{secondProject}/video-editor/sessions/{chat}/state";
        (await second.GetAsync(foreignChat)).StatusCode.Should().Be(HttpStatusCode.NotFound, "чат другого проекта");
    }

    [Fact]
    public async Task Сцена_с_версиями_не_удаляется_а_пустая_удаляется()
    {
        var chat = await ProjectChat();
        var shot = await AddScene(Chat(chat));
        await Shoot(Root, chat, shot);
        var empty = await AddScene(Chat(chat));

        var refused = await _client.DeleteAsync($"{Chat(chat)}/scenes/{shot}?revision={await Revision(Chat(chat))}");
        var removed = await _client.DeleteAsync($"{Chat(chat)}/scenes/{empty}?revision={await Revision(Chat(chat))}");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Личный чат ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Личный_чат_снимает_но_local_закрыт_и_проектных_путей_нет()
    {
        var chat = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный");
        var personal = $"/api/video-editor/chats/{chat.Id}";

        var catalog = await Json(await _client.GetAsync($"{personal}/catalog"));
        var local = catalog.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "local");
        local.GetProperty("available").GetBoolean().Should().BeFalse();
        local.GetProperty("reason").GetString().Should().NotBeNullOrEmpty();

        (await _client.PostAsJsonAsync($"{personal}/scenes", new { folder = "video", revision = 0 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "папок проекта у личного чата нет");
        var opened = await _client.PostAsJsonAsync($"{personal}/scenes", new { folder = "", settings = Settings("т"), revision = 0 });
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var sceneId = (await Json(opened)).GetProperty("focus").GetProperty("sceneId").GetString()!;

        var asLocal = await _client.PostAsJsonAsync($"{personal}/quote", new { sessionId = chat.Id, sceneId, provider = "local" });
        asLocal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(asLocal)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.LocalUnavailablePersonal);

        var versionId = await Shoot(personal, chat.Id, sceneId);
        var download = await _client.GetAsync($"{personal}/scenes/{sceneId}/versions/{versionId}/file?download=true");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");

        // Кадр-файл и открытый фильм — файлы проекта, которых у личного чата нет
        var withFile = await _client.PutAsJsonAsync($"{personal}/scenes/{sceneId}/settings", new
        {
            settings = new { frameA = new { kind = "file", path = "a.png" }, text = "т" },
            revision = await Revision(personal),
        });
        withFile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var film = await _client.PutAsJsonAsync($"{personal}/scenes/focus", new
        {
            focus = new { sceneId, filmPath = "video/a/a.film" },
            revision = await Revision(personal),
        });
        film.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(film)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
    }

    [Fact]
    public async Task Личная_ручка_не_открывает_чат_проекта_и_чужой_личный_чат()
    {
        var projectChat = await ProjectChat();
        (await _client.GetAsync($"/api/video-editor/chats/{projectChat}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var secondId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var strangers = await Sessions.CreateChatAsync(secondId, ClaudeMode.AcceptEdits, name: "Чужой личный");
        (await _client.GetAsync($"/api/video-editor/chats/{strangers.Id}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Локальный_проект_отказ_до_диска()
    {
        var chat = await ProjectChat();
        var trap = Path.Combine(_factory.TempDir, "trap_" + Guid.NewGuid().ToString("N")[..8]);
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(_projectId)!;
        project.DeviceId = "dev";
        project.RootPath = trap;

        var responses = new[]
        {
            await _client.GetAsync($"{Root}/catalog"),
            await _client.GetAsync($"{Chat(chat)}/state"),
            await _client.PostAsJsonAsync($"{Chat(chat)}/scenes", new { folder = "video", revision = 0 }),
        };

        foreach (var r in responses)
        {
            r.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(r)).GetProperty("code").GetString().Should().Be(ProjectCapabilityGuard.Code);
        }
        Directory.Exists(trap).Should().BeFalse();
    }
}
