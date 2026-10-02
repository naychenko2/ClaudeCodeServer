using System.Net;
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

// Ручки фильма и сохранения сцены на собранном приложении (ADR-022 §2, блок 2): сохранение → фильм в списке и в
// состоянии, правка под ревизией (409 со свежим состоянием), границы записи (video/**, ссылки наружу), личный чат —
// personal_scope_no_films, чужое неотличимо от несуществующего. Съёмка — фейковый поставщик в памяти.
public class FilmControllerTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _projectRoot;
    private static readonly byte[] Clip = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

    public FilmControllerTests()
    {
        _factory.ExtraServices = services => services.AddSingleton<IVideoEngine>(new FakeEngine());
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.VideoEditor, true).Should().BeTrue();
        var dir = Path.Combine(_factory.TempDir, "film_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _projectRoot = dir;
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("film-" + Guid.NewGuid().ToString("N")[..6], dir, _ownerId, TestWebApplicationFactory.TestUsername).Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

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
    private string Root => $"/api/projects/{_projectId}/video-editor";
    private string Chat(string sessionId) => $"{Root}/sessions/{sessionId}";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> ProjectChat() => (await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Видео")).Id;

    // Сцена с готовой версией в чате: (sceneId, versionId)
    private async Task<(string SceneId, string VersionId)> ShotScene(string chat, string folder = "video/утро")
    {
        var revision = (await Json(await _client.GetAsync($"{Chat(chat)}/scenes"))).GetProperty("revision").GetInt64();
        var added = await _client.PostAsJsonAsync($"{Chat(chat)}/scenes", new
        {
            folder,
            settings = new { text = "закат", provider = "fake", model = "fake-model", durationSec = 5, aspect = "16:9", sound = false, count = 1 },
            revision,
        });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var sceneId = (await Json(added)).GetProperty("focus").GetProperty("sceneId").GetString()!;
        var quote = await _client.PostAsJsonAsync($"{Root}/quote",
            new { sessionId = chat, sceneId, provider = "fake", model = "fake-model", count = 1, durationSec = 5 });
        var quoteId = (await Json(quote)).GetProperty("quoteId").GetString()!;
        var started = await _client.PostAsJsonAsync($"{Root}/jobs", new { quoteId, sessionId = chat, sceneId });
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        await _factory.Services.GetRequiredService<VideoEditJobService>().WhenDone(jobId);
        var store = _factory.Services.GetRequiredService<VideoJobThreads>().Store;
        var versionId = store.Get(_ownerId, chat).Scenes.Single(s => s.SceneId == sceneId).Versions.Single().VersionId;
        return (sceneId, versionId);
    }

    private Task<HttpResponseMessage> Save(string chat, string sceneId, string versionId, string? folder = null, string? fileName = null) =>
        _client.PostAsJsonAsync($"{Chat(chat)}/scenes/{sceneId}/save", new { sessionId = chat, sceneId, versionId, folder, fileName });

    [Fact]
    public async Task Сохранение_сцены_кладёт_клип_в_проект_и_фильм_виден_в_списке_и_состоянии()
    {
        var chat = await ProjectChat();
        var (sceneId, versionId) = await ShotScene(chat);

        var saved = await Save(chat, sceneId, versionId);

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var body = await Json(saved);
        body.GetProperty("path").GetString().Should().Be("video/утро/scene-01.mp4");
        body.GetProperty("addedToFilm").GetBoolean().Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_projectRoot, "video", "утро", "scene-01.mp4")).Should().Equal(Clip);

        var list = await Json(await _client.GetAsync($"{Root}/films"));
        list.EnumerateArray().Single().GetProperty("path").GetString().Should().Be("video/утро/утро.film");
        var state = await Json(await _client.GetAsync($"{Root}/films/state?path=video/утро/утро.film"));
        state.GetProperty("document").GetProperty("items").GetArrayLength().Should().Be(1);
        state.GetProperty("marks")[0].GetProperty("updated").GetBoolean().Should().BeTrue();
        state.GetProperty("spent").GetProperty("usd").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Занятое_имя_409_name_taken_и_папка_вне_video_400()
    {
        var chat = await ProjectChat();
        var (sceneId, versionId) = await ShotScene(chat);
        (await Save(chat, sceneId, versionId, fileName: "final.mp4")).StatusCode.Should().Be(HttpStatusCode.OK);

        var taken = await Save(chat, sceneId, versionId, fileName: "final.mp4");
        var outside = await Save(chat, sceneId, versionId, folder: "docs/evil");

        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(taken)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.NameTaken);
        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(outside)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        Directory.Exists(Path.Combine(_projectRoot, "docs")).Should().BeFalse();
    }

    [Fact]
    public async Task Правка_под_ревизией_409_со_свежим_состоянием_и_200_с_верной()
    {
        var chat = await ProjectChat();
        var (sceneId, versionId) = await ShotScene(chat);
        await Save(chat, sceneId, versionId);
        var state = await Json(await _client.GetAsync($"{Root}/films/state?path=video/утро/утро.film"));
        var revision = state.GetProperty("revision").GetString()!;
        // Чужая правка в обход нас
        var filmPath = Path.Combine(_projectRoot, "video", "утро", "утро.film");
        var text = File.ReadAllText(filmPath).Replace("\"aspect\": \"16:9\"", "\"aspect\": \"1:1\"");
        File.WriteAllText(filmPath, text);

        var stale = await _client.PatchAsJsonAsync($"{Root}/films?path=video/утро/утро.film",
            new { expectedRevision = revision, ops = new[] { new { op = "trim", index = 0, trim = new[] { 0.0, 3.0 } } } });

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await Json(stale);
        conflict.GetProperty("code").GetString().Should().Be(VideoEditorErrors.RevisionConflict);
        conflict.GetProperty("state").GetProperty("document").GetProperty("aspect").GetString().Should().Be("1:1");

        var fresh = conflict.GetProperty("state").GetProperty("revision").GetString()!;
        var ok = await _client.PatchAsJsonAsync($"{Root}/films?path=video/утро/утро.film",
            new { expectedRevision = fresh, ops = new[] { new { op = "trim", index = 0, trim = new[] { 0.0, 3.0 } } } });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await Json(ok)).GetProperty("document").GetProperty("items")[0].GetProperty("trim")[1].GetDouble().Should().Be(3);
    }

    [Fact]
    public async Task Запись_вне_video_и_music_отказывает_и_неизвестная_схема_422()
    {
        var chat = await ProjectChat();
        var (sceneId, versionId) = await ShotScene(chat);
        await Save(chat, sceneId, versionId);
        var revision = (await Json(await _client.GetAsync($"{Root}/films/state?path=video/утро/утро.film"))).GetProperty("revision").GetString()!;

        var badPath = await _client.GetAsync($"{Root}/films/state?path=docs/x.film");
        var badClip = await _client.PatchAsJsonAsync($"{Root}/films?path=video/утро/утро.film",
            new { expectedRevision = revision, ops = new[] { new { op = "add", file = "docs/x.mp4", trim = new[] { 0.0, 3.0 } } } });
        Directory.CreateDirectory(Path.Combine(_projectRoot, "video", "new"));
        File.WriteAllText(Path.Combine(_projectRoot, "video", "new", "new.film"),
            """{ "schema": 9, "aspect": "16:9", "items": [], "cuts": [], "builds": [] }""");
        var future = await _client.GetAsync($"{Root}/films/state?path=video/new/new.film");
        var futurePatch = await _client.PatchAsJsonAsync($"{Root}/films?path=video/new/new.film",
            new { expectedRevision = (await Json(future)).GetProperty("revision").GetString(), ops = new[] { new { op = "music" } } });

        badPath.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(badClip)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.OutsideAllowedFolders);
        future.StatusCode.Should().Be(HttpStatusCode.OK, "неизвестная схема читается");
        futurePatch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(futurePatch)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.FilmSchemaUnsupported);
    }

    [Fact]
    public async Task Личный_чат_получает_personal_scope_no_films_а_чужие_ручки_404()
    {
        var personal = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный");
        var url = $"/api/video-editor/chats/{personal.Id}";

        foreach (var response in new[]
                 {
                     await _client.GetAsync($"{url}/films"),
                     await _client.GetAsync($"{url}/films/state?path=video/a/a.film"),
                     await _client.PostAsJsonAsync($"{url}/scenes/s1/save", new { sessionId = personal.Id, sceneId = "s1" }),
                 })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.PersonalScopeNoFilms);
        }

        var secondId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var strangers = await Sessions.CreateChatAsync(secondId, ClaudeMode.AcceptEdits, name: "Чужой личный");
        (await _client.GetAsync($"/api/video-editor/chats/{strangers.Id}/films")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var projectChat = await ProjectChat();
        (await _client.GetAsync($"/api/video-editor/chats/{projectChat}/films")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(_ownerId, FeatureFlagKeys.VideoEditor, false);
        (await _client.GetAsync($"{Root}/films")).StatusCode.Should().Be(HttpStatusCode.NotFound, "без флага ручки фильма закрыты");
    }

    [Fact]
    public async Task Локальный_проект_отказ_до_диска_и_для_ручек_фильма()
    {
        var trap = Path.Combine(_factory.TempDir, "trap_" + Guid.NewGuid().ToString("N")[..8]);
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(_projectId)!;
        project.DeviceId = "dev";
        project.RootPath = trap;

        var response = await _client.GetAsync($"{Root}/films");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(response)).GetProperty("code").GetString().Should().Be(ProjectCapabilityGuard.Code);
        Directory.Exists(trap).Should().BeFalse();
    }
}
