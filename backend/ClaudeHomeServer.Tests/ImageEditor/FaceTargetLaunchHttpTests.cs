using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Запуск с FaceTarget: HTTP-статусы отказов (ADR-028, этап 2).
// faces_changed → 409, feature_disabled → 404, busy → 503, provider_unavailable → 409,
// version_not_found → 404, invalid_request → 400.
public class FaceTargetLaunchHttpTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;
    private readonly string _chatId;
    private readonly string _threadId;
    private readonly string _facesKey;
    private readonly string _charSlug;

    public FaceTargetLaunchHttpTests()
    {
        _factory = new TestWebApplicationFactory { UseFrontend = false };
        _factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new OkMedia());
            services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
        };

        _ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _projectId = CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _root = _factory.Services.GetRequiredService<ProjectManager>().GetById(_projectId)!.RootPath;
        _client = _factory.CreateAuthenticatedClient();

        // Персонаж: 3 фото (валидные JPEG)
        var photos = new List<CharacterPhotoUpload>();
        for (var i = 1; i <= 3; i++)
            photos.Add(new CharacterPhotoUpload(TestImages.Jpeg((byte)i)));
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Боря", null, photos), DateTime.UtcNow);
        created.Error.Should().BeNull();
        _charSlug = created.Value!.Manifest.Slug;

        // Чат + нить + версия
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var chat = sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат").GetAwaiter().GetResult();
        _chatId = chat.Id;
        File.WriteAllBytes(System.IO.Path.Combine(_root, "test.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var store = _factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(_ownerId, _chatId, "test.png", null, store.Get(_ownerId, _chatId).Revision);
        _threadId = thread.Thread!.Id;
        _facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(_root, "test.png")));
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string CreateProject(TestWebApplicationFactory factory, string username)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(username)!;
        var dir = System.IO.Path.Combine(factory.TempDir, "ft_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return factory.Services.GetRequiredService<ProjectManager>()
            .Create("ft-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, username).Id;
    }

    private void SetFlag(bool on) =>
        _factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(_ownerId, FeatureFlagKeys.CharacterFaceSwap, on);

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage resp) =>
        (await Json(resp)).GetProperty("code").GetString()!;

    private async Task<HttpResponseMessage> Start(string faceTargetJson, string? characterSlug = null,
        string? versionId = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        if (faceTargetJson is not null)
            form.Add(new StringContent(faceTargetJson), "faceTarget");
        if (characterSlug is not null)
            form.Add(new StringContent(characterSlug), "characterSlug");
        form.Add(new StringContent(_chatId), "sessionId");
        form.Add(new StringContent(_threadId), "threadId");
        if (versionId is not null)
            form.Add(new StringContent(versionId), "versionId");
        return await _client.PostAsync($"/api/projects/{_projectId}/image-editor/jobs", form);
    }

    private string FaceTargetJson(string facesKey, int face = 1) =>
        $"{{\"versionId\":\"origin\",\"face\":{face},\"facesKey\":\"{facesKey}\"}}";

    // ── faces_changed → 409 (MAJOR) ─────────────────────────────────────────────

    [Fact]
    public async Task FacesKey_не_совпал_409_faces_changed()
    {
        SetFlag(true);
        var resp = await Start(FaceTargetJson("stale:v1"), _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        // Тело 409 — свежий ImageThreadFacesResponse (facesKey + faces), не {error, code}
        var body = await Json(resp);
        body.GetProperty("facesKey").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("faces").EnumerateArray().Should().NotBeEmpty();
    }

    // ── feature_disabled → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Флаг_выключен_404_feature_disabled()
    {
        SetFlag(false);
        var resp = await Start(FaceTargetJson(_facesKey), _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.FeatureDisabled);
    }

    // ── version_not_found → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Версии_нет_404_version_not_found()
    {
        SetFlag(true);
        // FaceTarget с несуществующей версией
        var json = $"{{\"versionId\":\"нет-такой\",\"face\":1,\"facesKey\":\"{_facesKey}\"}}";
        var resp = await Start(json, _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.VersionNotFound);
    }

    // ── invalid_request → 400 (нет characterSlug) ─────────────────────────────────

    [Fact]
    public async Task Без_CharacterSlug_400_invalid_request()
    {
        SetFlag(true);
        var resp = await Start(FaceTargetJson(_facesKey), characterSlug: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── busy → 503 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Busy_503()
    {
        // Пересобираем фабрику с BusyMedia
        var factory = new TestWebApplicationFactory { UseFrontend = false };
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new BusyMedia());
            services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
        };
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var client = factory.CreateAuthenticatedClient();

        var photos = new List<CharacterPhotoUpload>();
        for (var i = 1; i <= 3; i++)
            photos.Add(new CharacterPhotoUpload(TestImages.Jpeg((byte)i)));
        var created = CharacterStore.Create(root,
            new CharacterDraft("Боря", null, photos), DateTime.UtcNow);
        var slug = created.Value!.Manifest.Slug;

        var sessions = factory.Services.GetRequiredService<SessionManager>();
        var chat = sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат").GetAwaiter().GetResult();
        File.WriteAllBytes(System.IO.Path.Combine(root, "test.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var store = factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(ownerId, chat.Id, "test.png", null, store.Get(ownerId, chat.Id).Revision);
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(root, "test.png")));

        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(ownerId, FeatureFlagKeys.CharacterFaceSwap, true);

        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        form.Add(new StringContent($"{{\"versionId\":\"origin\",\"face\":1,\"facesKey\":\"{facesKey}\"}}"), "faceTarget");
        form.Add(new StringContent(slug), "characterSlug");
        form.Add(new StringContent(chat.Id), "sessionId");
        form.Add(new StringContent(thread.Thread!.Id), "threadId");

        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.Busy);
        factory.Dispose();
    }

    // ── provider_unavailable → 409 ────────────────────────────────────────────────

    [Fact]
    public async Task Error_409_provider_unavailable()
    {
        var factory = new TestWebApplicationFactory { UseFrontend = false };
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new ErrorMedia());
            services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
        };
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var client = factory.CreateAuthenticatedClient();

        var photos = new List<CharacterPhotoUpload>();
        for (var i = 1; i <= 3; i++)
            photos.Add(new CharacterPhotoUpload(TestImages.Jpeg((byte)i)));
        var created = CharacterStore.Create(root,
            new CharacterDraft("Боря", null, photos), DateTime.UtcNow);
        var slug = created.Value!.Manifest.Slug;

        var sessions = factory.Services.GetRequiredService<SessionManager>();
        var chat = sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат").GetAwaiter().GetResult();
        File.WriteAllBytes(System.IO.Path.Combine(root, "test.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var store = factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(ownerId, chat.Id, "test.png", null, store.Get(ownerId, chat.Id).Revision);
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(root, "test.png")));

        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(ownerId, FeatureFlagKeys.CharacterFaceSwap, true);

        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        form.Add(new StringContent($"{{\"versionId\":\"origin\",\"face\":1,\"facesKey\":\"{facesKey}\"}}"), "faceTarget");
        form.Add(new StringContent(slug), "characterSlug");
        form.Add(new StringContent(chat.Id), "sessionId");
        form.Add(new StringContent(thread.Thread!.Id), "threadId");

        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        factory.Dispose();
    }

    // ── Фейки ILocalImageMedia ─────────────────────────────────────────────────────

    private sealed class OkMedia : ILocalImageMedia
    {
        public bool Configured => true;
        public bool Available => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct) =>
            Task.FromResult(new LocalFaceDetectResult(200, 200,
                [new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false)], null, false));
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class BusyMedia : ILocalImageMedia
    {
        public bool Configured => true;
        public bool Available => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct) =>
            Task.FromResult(new LocalFaceDetectResult(200, 200,
                [new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false)], null, true));
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ErrorMedia : ILocalImageMedia
    {
        public bool Configured => true;
        public bool Available => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct) =>
            Task.FromResult(new LocalFaceDetectResult(200, 200, [], "Сбой детекции", false));
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    // Котировка "q1" с провайдером "local" — для AssembleFaceTargetAsync
    private sealed class FakeQuoteJobs : IImageEditJobs
    {
        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct) =>
            throw new NotSupportedException();
        public ImageEditQuoteInfo? QuoteInfo(string ownerId, string projectId, string quoteId) =>
            quoteId == "q1" ? new ImageEditQuoteInfo(ImageEditOp.Edit, null, "local") : null;
        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;
        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);
        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => null;
    }
}
