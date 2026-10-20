using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private string FaceTargetJson(string facesKey, int face = 1, string characterSlug = "maria") =>
        $"{{\"versionId\":\"origin\",\"facesKey\":\"{facesKey}\",\"faces\":[{{\"face\":{face},\"characterSlug\":\"{characterSlug}\"}}]}}";

    // Котировка с faceTarget: тело — JSON-объект ImageEditQuoteRequest; faceTarget — вложенный объект
    private async Task<HttpResponseMessage> Quote(string faceTargetJson, string? characterSlug = null)
    {
        var body = new JsonObject
        {
            ["provider"] = "local",
            ["model"] = "auto",
            ["mode"] = "auto",
            ["op"] = "edit",
            ["count"] = 1,
            ["hasMask"] = true,
        };
        if (faceTargetJson is not null)
            body["faceTarget"] = JsonNode.Parse(faceTargetJson);
        if (characterSlug is not null)
            body["characterSlug"] = characterSlug;
        return await _client.PostAsync($"/api/projects/{_projectId}/image-editor/quote",
            new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
    }

    // ── faces_changed → 409 (MAJOR) ─────────────────────────────────────────────

    [Fact]
    public async Task FacesKey_не_совпал_409_faces_changed()
    {
        SetFlag(true);
        // Верхнего characterSlug нет: персонаж — в faceTarget.faces (иначе новый отказ «двусмысленный вход»)
        var resp = await Start(FaceTargetJson("stale:v1"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        // Тело 409 — свежий список лиц плюс явные code и error (ADR-028, этап 3)
        var body = await Json(resp);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.FacesChanged);
        body.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        // Свежая детекция: facesKey по байтам исходника, размер и лица — из OkMedia (200×200, одно лицо)
        body.GetProperty("facesKey").GetString().Should().Be(_facesKey);
        body.GetProperty("width").GetInt32().Should().Be(200);
        body.GetProperty("height").GetInt32().Should().Be(200);
        var faces = body.GetProperty("faces").EnumerateArray().ToList();
        faces.Should().NotBeEmpty();
        faces[0].GetProperty("n").GetInt32().Should().Be(1);
    }

    // Ручка faces отвечает прежним ImageThreadFacesResponse — без code и error (ADR-028, этап 3)
    [Fact]
    public async Task Faces_ручка_отвечает_без_code()
    {
        SetFlag(true);
        var resp = await _client.PostAsync(
            $"/api/projects/{_projectId}/image-editor/sessions/{_chatId}/threads/{_threadId}/versions/origin/faces",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("facesKey").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("width").GetInt32().Should().Be(200);
        body.GetProperty("height").GetInt32().Should().Be(200);
        body.GetProperty("faces").EnumerateArray().Should().NotBeEmpty();
        // Обёртка FacesChangedResponse — только для 409; у faces её нет
        body.TryGetProperty("code", out _).Should().BeFalse();
        body.TryGetProperty("error", out _).Should().BeFalse();
    }

    // ── feature_disabled → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Флаг_выключен_404_feature_disabled()
    {
        SetFlag(false);
        // Верхнего characterSlug нет: персонаж — в faceTarget.faces; флаг проверяется в сборщике
        var resp = await Start(FaceTargetJson(_facesKey));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.FeatureDisabled);
    }

    // ── version_not_found → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Версии_нет_404_version_not_found()
    {
        SetFlag(true);
        // FaceTarget с несуществующей версией; верхнего characterSlug нет (персонаж — в faces)
        var json = $"{{\"versionId\":\"нет-такой\",\"facesKey\":\"{_facesKey}\",\"faces\":[{{\"face\":1,\"characterSlug\":\"{_charSlug}\"}}]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.VersionNotFound);
    }

    // ── Запуск: faceTarget без верхнего characterSlug → 202 (персонаж в faces) ─────

    [Fact]
    public async Task Запуск_faceTarget_без_верхнего_слага_202()
    {
        SetFlag(true);
        var resp = await Start(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
    }

    // ── Запуск: faceTarget И верхний characterSlug → 400 (двусмысленный вход) ───────

    [Fact]
    public async Task Запуск_faceTarget_с_верхним_слагом_400()
    {
        SetFlag(true);
        var resp = await Start(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── Котировка: faceTarget без верхнего characterSlug → 200 ─────────────────────

    [Fact]
    public async Task Котировка_faceTarget_без_верхнего_слага_200()
    {
        SetFlag(true);
        var resp = await Quote(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    // ── Котировка: faceTarget И верхний characterSlug → 400 ─────────────────────────

    [Fact]
    public async Task Котировка_faceTarget_с_верхним_слагом_400()
    {
        SetFlag(true);
        var resp = await Quote(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── Парсер faceTarget: прислано, но не разбилось → 400, не 500 ─────────────────

    // face строкой вместо числа
    [Fact]
    public async Task Парсер_face_строкой_400()
    {
        SetFlag(true);
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[{{\"face\":\"abc\",\"characterSlug\":\"{_charSlug}\"}}]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // face = null
    [Fact]
    public async Task Парсер_face_null_400()
    {
        SetFlag(true);
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[{{\"face\":null,\"characterSlug\":\"{_charSlug}\"}}]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faces не массив
    [Fact]
    public async Task Парсер_faces_не_массив_400()
    {
        SetFlag(true);
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":\"x\"}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // элемент faces не объект
    [Fact]
    public async Task Парсер_элемент_не_объект_400()
    {
        SetFlag(true);
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[1]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "" (прислано, но пустое)
    [Fact]
    public async Task Парсер_пустая_строка_400()
    {
        SetFlag(true);
        var resp = await Start("");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "{}" (прислано, но пустой объект)
    [Fact]
    public async Task Парсер_пустой_объект_400()
    {
        SetFlag(true);
        var resp = await Start("{}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "null" (прислано, но JSON-null)
    [Fact]
    public async Task Парсер_null_400()
    {
        SetFlag(true);
        var resp = await Start("null");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── Номер лица в теле отказа по лицу (ADR-028, этап 3) ──────────────────────

    // Два лица, лицо 2 мелкое: тело 400 — прежние {error, code} плюс face = 2.
    // Мутация «не передавать номер» (отказ без Payload) — поля face нет, тест красный
    [Fact]
    public async Task Отказ_по_лицу_2_несёт_face_2()
    {
        var factory = new TestWebApplicationFactory { UseFrontend = false };
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new SmallSecondFaceMedia());
            services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
        };
        var client = factory.CreateAuthenticatedClient();
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var slug = CharacterStore.Create(root, new CharacterDraft("Боря", null,
            [.. Enumerable.Range(1, 3).Select(i => new CharacterPhotoUpload(TestImages.Jpeg((byte)i)))]),
            DateTime.UtcNow).Value!.Manifest.Slug;
        var chat = await factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат");
        File.WriteAllBytes(System.IO.Path.Combine(root, "test.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var store = factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(ownerId, chat.Id, "test.png", null, store.Get(ownerId, chat.Id).Revision);
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(root, "test.png")));
        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(ownerId, FeatureFlagKeys.CharacterFaceSwap, true);

        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
            { new StringContent(FaceTargetJson(facesKey, 2, slug)), "faceTarget" },
            { new StringContent(chat.Id), "sessionId" },
            { new StringContent(thread.Thread!.Id), "threadId" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("face").GetInt32().Should().Be(2);
        // Старый клиент: code и error на месте, лишних полей, кроме face, нет
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        body.GetProperty("error").GetString().Should().Contain("мелкое");
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["error", "code", "face"]);
        factory.Dispose();
    }

    // «Лицо 3 не найдено» (лицо 3 нет в списке): отказ привязан к запрошенному лицу — тело
    // несёт face = 3. Мутация «не передавать номер» (отказ без Payload) → поля face нет, тест красный
    [Fact]
    public async Task Лицо_не_найдено_несёт_face_3()
    {
        SetFlag(true);
        var resp = await Start(FaceTargetJson(_facesKey, 3, _charSlug));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        body.GetProperty("face").GetInt32().Should().Be(3);
        body.GetProperty("error").GetString().Should().Contain("3");
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["error", "code", "face"]);
    }

    // «Персонаж «{slug}» не найден»: HTTP-тело несёт face запрошенного лица (ADR-028, этап 3)
    [Fact]
    public async Task Персонаж_не_найден_несёт_face()
    {
        var factory = new TestWebApplicationFactory { UseFrontend = false };
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new OkMedia());
            services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
        };
        var client = factory.CreateAuthenticatedClient();
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var chat = await factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат");
        File.WriteAllBytes(System.IO.Path.Combine(root, "test.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var store = factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(ownerId, chat.Id, "test.png", null, store.Get(ownerId, chat.Id).Revision);
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(root, "test.png")));
        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(ownerId, FeatureFlagKeys.CharacterFaceSwap, true);

        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
            { new StringContent(FaceTargetJson(facesKey, 1, "no-such-char")), "faceTarget" },
            { new StringContent(chat.Id), "sessionId" },
            { new StringContent(thread.Thread!.Id), "threadId" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("face").GetInt32().Should().Be(1);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        body.GetProperty("error").GetString().Should().Be("Персонаж «no-such-char» не найден");
        factory.Dispose();
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
        var chat = await sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат");
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
        // Верхнего characterSlug нет: персонаж — в faceTarget.faces (иначе новый отказ «двусмысленный вход»)
        form.Add(new StringContent(FaceTargetJson(facesKey, 1, slug)), "faceTarget");
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
        var chat = await sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат");
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
        // Верхнего characterSlug нет: персонаж — в faceTarget.faces (иначе новый отказ «двусмысленный вход»)
        form.Add(new StringContent(FaceTargetJson(facesKey, 1, slug)), "faceTarget");
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

    // Два лица: первое годное, второе мелкое (Small) — отказ «слишком мелкое» по лицу 2
    private sealed class SmallSecondFaceMedia : ILocalImageMedia
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
                [new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false),
                 new LocalDetectedFace(2, 160, 160, 190, 190, 0.85, true)], null, false));
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

    // Котировка "q1" с провайдером "local" — для AssembleFaceTargetAsync; QuoteAsync отвечает
    // валидной котировкой, чтобы ручка quote с faceTarget прошла (200)
    private sealed class FakeQuoteJobs : IImageEditJobs
    {
        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            Task.FromResult(ImageEditCallResult<ImageEditQuoteDto>.Ok(new ImageEditQuoteDto(
                "q1", request.Provider, "m",
                new ImageEditEstimateDto(0.05, ImageEditPriceUnits.Usd, false, ImageEditEstimateSources.Catalog),
                DateTime.UtcNow.AddMinutes(10), 20)));
        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct) =>
            Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto("job-1")));
        public ImageEditQuoteInfo? QuoteInfo(string ownerId, string projectId, string quoteId) =>
            quoteId == "q1" ? new ImageEditQuoteInfo(ImageEditOp.Edit, null, "local", FacePasses: 1) : null;
        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;
        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);
        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => null;
    }
}
