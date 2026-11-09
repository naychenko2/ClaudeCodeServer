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
// faces_changed → 409, busy → 503, provider_unavailable → 409,
// version_not_found → 404, invalid_request → 400.
[Trait("Category", "Host")]
public class FaceTargetLaunchHttpTests : IClassFixture<FaceTargetLaunchHttpTests.Hosts>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly Hosts _hosts;

    // Сцена базовых тестов на хосте Ok готовится при первом обращении: тесты на SmallSecond/Busy/Error
    // и «без персонажа» заводят свои сцены сами, лишний проект/персонаж/чат им не нужен
    private Scene? _scene;
    private HttpClient? _clientLazy;
    private Scene Base => _scene ??= Prepare(_factory);
    private HttpClient _client => _clientLazy ??= _factory.CreateAuthenticatedClient();
    private string _ownerId => Base.OwnerId;
    private string _projectId => Base.ProjectId;
    private string _chatId => Base.ChatId;
    private string _threadId => Base.ThreadId;
    private string _facesKey => Base.FacesKey;
    private string _charSlug => Base.CharSlug!;

    // Хост на КОНФИГУРАЦИЮ (шов детекции лиц), а не на тест: объекты фабрик создаются сразу, ASP.NET-хост
    // внутри каждой стартует лениво; гасятся вместе с фикстурой. Состояние (проект, персонаж, чат, нить, файл) каждый тест заводит
    // сам в своём каталоге через Prepare
    public sealed class Hosts : IDisposable
    {
        // Одно годное лицо
        public MediaHost Ok { get; } = new(new OkMedia()) { UseFrontend = false };
        // Два лица, второе мелкое
        public MediaHost SmallSecond { get; } = new(new SmallSecondFaceMedia()) { UseFrontend = false };
        // Детекция занята
        public MediaHost Busy { get; } = new(new BusyMedia()) { UseFrontend = false };
        // Детекция вернула ошибку
        public MediaHost Error { get; } = new(new ErrorMedia()) { UseFrontend = false };

        public void Dispose()
        {
            Ok.Dispose();
            SmallSecond.Dispose();
            Busy.Dispose();
            Error.Dispose();
        }
    }

    public sealed class MediaHost : TestWebApplicationFactory
    {
        public MediaHost(ILocalImageMedia media)
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IImageEditor>(new FakeImageEditor("local", models: FakeImageEditor.Model("m")));
                services.RemoveAll<ILocalImageMedia>();
                services.AddSingleton(media);
                services.AddSingleton<IImageEditJobs>(new FakeQuoteJobs());
            };
        }
    }

    // Чат + нить + файл исходника в новом проекте хоста; персонаж «Боря» — по запросу
    private sealed record Scene(string OwnerId, string ProjectId, string Root, string ChatId, string ThreadId,
        string FacesKey, string? CharSlug);

    private static Scene Prepare(TestWebApplicationFactory factory, bool withCharacter = true)
    {
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;

        string? slug = null;
        if (withCharacter)
        {
            // Персонаж: 3 фото (валидные JPEG)
            var photos = new List<CharacterPhotoUpload>();
            for (var i = 1; i <= 3; i++)
                photos.Add(new CharacterPhotoUpload(TestImages.Jpeg((byte)i)));
            var created = CharacterStore.Create(root,
                new CharacterDraft("Боря", null, photos), DateTime.UtcNow);
            created.Error.Should().BeNull();
            slug = created.Value!.Manifest.Slug;
        }

        var sessions = factory.Services.GetRequiredService<SessionManager>();
        var chat = sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат").GetAwaiter().GetResult();
        // Байты уникальны на сцену: кэш лиц хоста (FaceDetectionCache) — синглтон с ключом (владелец, sha256),
        // при одинаковых байтах детектор звался бы один раз на класс, а остальные тесты брали бы кэш
        File.WriteAllBytes(System.IO.Path.Combine(root, "test.png"),
            [0x89, 0x50, 0x4E, 0x47, .. Guid.NewGuid().ToByteArray()]);
        var store = factory.Services.GetRequiredService<ImageThreadStore>();
        var thread = store.Open(ownerId, chat.Id, "test.png", null, store.Get(ownerId, chat.Id).Revision);
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(System.IO.Path.Combine(root, "test.png")));
        return new Scene(ownerId, projectId, root, chat.Id, thread.Thread!.Id, facesKey, slug);
    }

    public FaceTargetLaunchHttpTests(Hosts hosts)
    {
        _hosts = hosts;
        _factory = hosts.Ok;
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

    // Форма запуска с faceTarget в нити сцены
    private static MultipartFormDataContent JobForm(Scene scene, string faceTargetJson)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("q1"), "quoteId" },
            { new StringContent("тест"), "prompt" },
            { new StringContent(faceTargetJson), "faceTarget" },
            { new StringContent(scene.ChatId), "sessionId" },
            { new StringContent(scene.ThreadId), "threadId" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "test.png");
        return form;
    }

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

    // ── version_not_found → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Версии_нет_404_version_not_found()
    {
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
        var resp = await Start(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
    }

    // ── Запуск: faceTarget И верхний characterSlug → 400 (двусмысленный вход) ───────

    [Fact]
    public async Task Запуск_faceTarget_с_верхним_слагом_400()
    {
        var resp = await Start(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── Котировка: faceTarget без верхнего characterSlug → 200 ─────────────────────

    [Fact]
    public async Task Котировка_faceTarget_без_верхнего_слага_200()
    {
        var resp = await Quote(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    // ── Котировка: faceTarget И верхний characterSlug → 400 ─────────────────────────

    [Fact]
    public async Task Котировка_faceTarget_с_верхним_слагом_400()
    {
        var resp = await Quote(FaceTargetJson(_facesKey, 1, _charSlug), characterSlug: _charSlug);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── Парсер faceTarget: прислано, но не разбилось → 400, не 500 ─────────────────

    // face строкой вместо числа
    [Fact]
    public async Task Парсер_face_строкой_400()
    {
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[{{\"face\":\"abc\",\"characterSlug\":\"{_charSlug}\"}}]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // face = null
    [Fact]
    public async Task Парсер_face_null_400()
    {
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[{{\"face\":null,\"characterSlug\":\"{_charSlug}\"}}]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faces не массив
    [Fact]
    public async Task Парсер_faces_не_массив_400()
    {
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":\"x\"}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // элемент faces не объект
    [Fact]
    public async Task Парсер_элемент_не_объект_400()
    {
        var json = $"{{\"versionId\":\"origin\",\"facesKey\":\"{_facesKey}\",\"faces\":[1]}}";
        var resp = await Start(json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "" (прислано, но пустое)
    [Fact]
    public async Task Парсер_пустая_строка_400()
    {
        var resp = await Start("");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "{}" (прислано, но пустой объект)
    [Fact]
    public async Task Парсер_пустой_объект_400()
    {
        var resp = await Start("{}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // faceTarget = "null" (прислано, но JSON-null)
    [Fact]
    public async Task Парсер_null_400()
    {
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
        var factory = _hosts.SmallSecond;
        var client = factory.CreateAuthenticatedClient();
        var scene = Prepare(factory);

        var resp = await client.PostAsync($"/api/projects/{scene.ProjectId}/image-editor/jobs",
            JobForm(scene, FaceTargetJson(scene.FacesKey, 2, scene.CharSlug!)));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("face").GetInt32().Should().Be(2);
        // Старый клиент: code и error на месте, лишних полей, кроме face, нет
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        body.GetProperty("error").GetString().Should().Contain("мелкое");
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["error", "code", "face"]);
    }

    // «Лицо 3 не найдено» (лицо 3 нет в списке): отказ привязан к запрошенному лицу — тело
    // несёт face = 3. Мутация «не передавать номер» (отказ без Payload) → поля face нет, тест красный
    [Fact]
    public async Task Лицо_не_найдено_несёт_face_3()
    {
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
        var factory = _hosts.Ok;
        var client = factory.CreateAuthenticatedClient();
        var scene = Prepare(factory, withCharacter: false);

        var resp = await client.PostAsync($"/api/projects/{scene.ProjectId}/image-editor/jobs",
            JobForm(scene, FaceTargetJson(scene.FacesKey, 1, "no-such-char")));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("face").GetInt32().Should().Be(1);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        body.GetProperty("error").GetString().Should().Be("Персонаж «no-such-char» не найден");
    }

    // ── busy → 503 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Busy_503()
    {
        var factory = _hosts.Busy;
        var client = factory.CreateAuthenticatedClient();
        var scene = Prepare(factory);

        // Верхнего characterSlug нет: персонаж — в faceTarget.faces (иначе новый отказ «двусмысленный вход»)
        var resp = await client.PostAsync($"/api/projects/{scene.ProjectId}/image-editor/jobs",
            JobForm(scene, FaceTargetJson(scene.FacesKey, 1, scene.CharSlug!)));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.Busy);
    }

    // ── provider_unavailable → 409 ────────────────────────────────────────────────

    [Fact]
    public async Task Error_409_provider_unavailable()
    {
        var factory = _hosts.Error;
        var client = factory.CreateAuthenticatedClient();
        var scene = Prepare(factory);

        // Верхнего characterSlug нет: персонаж — в faceTarget.faces (иначе новый отказ «двусмысленный вход»)
        var resp = await client.PostAsync($"/api/projects/{scene.ProjectId}/image-editor/jobs",
            JobForm(scene, FaceTargetJson(scene.FacesKey, 1, scene.CharSlug!)));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await Code(resp)).Should().Be(ImageEditErrorCodes.ProviderUnavailable);
    }

    // ── Фейки ILocalImageMedia ─────────────────────────────────────────────────────

    public sealed class OkMedia : ILocalImageMedia
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
    public sealed class SmallSecondFaceMedia : ILocalImageMedia
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

    public sealed class BusyMedia : ILocalImageMedia
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

    public sealed class ErrorMedia : ILocalImageMedia
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
    public sealed class FakeQuoteJobs : IImageEditJobs
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
