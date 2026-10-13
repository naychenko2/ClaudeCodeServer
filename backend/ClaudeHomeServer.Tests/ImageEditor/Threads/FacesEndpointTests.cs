using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// «Найти лица на версии» (ADR-028, этап 2): POST .../threads/{threadId}/versions/{versionId}/faces.
// Флаг character-face-swap гейтит ручку (выключен — 404 feature_disabled), 200 — список лиц + facesKey,
// повторный запрос на те же байты берётся из кэша (детектор один раз), Busy — 503, Error — 409, оба не кэшируются,
// версии нет — 404 version_not_found, чужая нить — 404 thread_not_found, личная область — 404 (маршрута нет).
public class FacesEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly FakeLocalMedia _media;
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;

    public FacesEndpointTests()
    {
        _media = new FakeLocalMedia();
        _factory = new TestWebApplicationFactory();
        _factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(_media);
        };
        (_projectId, _root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Png(40, 20));
        File.WriteAllBytes(Path.Combine(_root, "images", "other.png"), Png(30, 30));
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Фейк ILocalImageMedia: DetectFacesAsync возвращает результат из делегата, считает вызовы
    private sealed class FakeLocalMedia : ILocalImageMedia
    {
        public int DetectCalls;
        public Func<byte[], LocalFaceDetectResult> Result = _ =>
            new(100, 100, [new LocalDetectedFace(1, 10, 10, 50, 50, 0.9, false)]);

        public bool Configured => true;
        public bool Available => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct)
        {
            Interlocked.Increment(ref DetectCalls);
            return Task.FromResult(Result(image));
        }
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ImageThreadStore Store => _factory.Services.GetRequiredService<ImageThreadStore>();

    private string Threads(string sessionId) => $"/api/projects/{_projectId}/image-editor/sessions/{sessionId}/threads";
    private long Revision(string chatId) => Store.Get(_ownerId, chatId).Revision;

    private void Flag(bool on) =>
        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(_ownerId, FeatureFlagKeys.CharacterFaceSwap, on);

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage resp) => (await Json(resp)).GetProperty("code").GetString()!;

    private async Task<Session> Chat() => await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат");

    // Нить на файле: возвращает (threadId, originVersionId)
    private async Task<(string ThreadId, string OriginVersionId)> OpenThread(string chatId, string file)
    {
        var resp = await _client.PostAsJsonAsync(Threads(chatId), new { file, revision = Revision(chatId) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var threadId = (await Json(resp)).GetProperty("focus").GetString()!;
        var origin = Store.Get(_ownerId, chatId).Threads.Single(t => t.Id == threadId).Version(ImageThreadVersion.OriginId)!;
        return (threadId, origin.Id);
    }

    private Task<HttpResponseMessage> Faces(string chatId, string threadId, string versionId) =>
        _client.PostAsync($"{Threads(chatId)}/{threadId}/versions/{versionId}/faces", null!);

    // Флаг выключен — 404 feature_disabled (ручки нет, как у DossiersController)
    [Fact]
    public async Task Флаг_выключен_feature_disabled()
    {
        var chat = await Chat();
        var (threadId, originId) = await OpenThread(chat.Id, "images/hero.png");
        Flag(false);

        var resp = await Faces(chat.Id, threadId, originId);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(resp)).Should().Be("feature_disabled");
        _media.DetectCalls.Should().Be(0, "флаг выключен — детектор не запускался");
    }

    // Флаг включён — 200 по контракту: facesKey, width, height, faces (n с 1, bbox, score, small)
    [Fact]
    public async Task Флаг_включен_200_по_контракту()
    {
        var chat = await Chat();
        var (threadId, originId) = await OpenThread(chat.Id, "images/hero.png");
        Flag(true);

        var resp = await Faces(chat.Id, threadId, originId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("facesKey").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("width").GetInt32().Should().Be(100);
        body.GetProperty("height").GetInt32().Should().Be(100);
        var face = body.GetProperty("faces").EnumerateArray().Should().ContainSingle().Subject;
        face.GetProperty("n").GetInt32().Should().Be(1);
        face.GetProperty("bbox").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(10, 10, 50, 50);
        face.GetProperty("score").GetDouble().Should().Be(0.9);
        face.GetProperty("small").GetBoolean().Should().BeFalse();
    }

    // Повторный запрос на те же байты — из кэша: детектор один раз; facesKey детерминирован
    [Fact]
    public async Task Повтор_на_те_же_байты_детектор_один_раз_facesKey_стабилен()
    {
        var chat = await Chat();
        var (threadId, originId) = await OpenThread(chat.Id, "images/hero.png");
        Flag(true);

        var first = await Faces(chat.Id, threadId, originId);
        var firstKey = (await Json(first)).GetProperty("facesKey").GetString()!;
        var second = await Faces(chat.Id, threadId, originId);
        var secondKey = (await Json(second)).GetProperty("facesKey").GetString()!;

        _media.DetectCalls.Should().Be(1, "повторный запрос на те же байты берётся из кэша");
        firstKey.Should().Be(secondKey, "facesKey детерминирован по байтам");
    }

    // Другие байты (другой файл) — другой facesKey и второй вызов детектора
    [Fact]
    public async Task Другие_байты_другой_facesKey_и_второй_детектор()
    {
        var chat = await Chat();
        var (t1, o1) = await OpenThread(chat.Id, "images/hero.png");
        var (t2, o2) = await OpenThread(chat.Id, "images/other.png");
        Flag(true);

        var k1 = (await Json(await Faces(chat.Id, t1, o1))).GetProperty("facesKey").GetString()!;
        var k2 = (await Json(await Faces(chat.Id, t2, o2))).GetProperty("facesKey").GetString()!;

        _media.DetectCalls.Should().Be(2, "разные байты — разные ключи, детектор дважды");
        k1.Should().NotBe(k2, "facesKey меняется при изменении байт");
    }

    // Busy — 503 busy; не кэшируется: повторный запрос запускает детектор снова
    [Fact]
    public async Task Busy_503_и_не_кэшируется()
    {
        var chat = await Chat();
        var (threadId, originId) = await OpenThread(chat.Id, "images/hero.png");
        Flag(true);
        _media.Result = _ => new LocalFaceDetectResult(0, 0, [], null, true);

        var resp = await Faces(chat.Id, threadId, originId);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Code(resp)).Should().Be("busy");

        await Faces(chat.Id, threadId, originId);
        _media.DetectCalls.Should().Be(2, "Busy не кэшируется — повторный запрос запускает детектор");
    }

    // Error — 409 provider_unavailable; не кэшируется
    [Fact]
    public async Task Error_409_provider_unavailable_и_не_кэшируется()
    {
        var chat = await Chat();
        var (threadId, originId) = await OpenThread(chat.Id, "images/hero.png");
        Flag(true);
        _media.Result = _ => new LocalFaceDetectResult(0, 0, [], "Сбой детекции");

        var resp = await Faces(chat.Id, threadId, originId);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(resp)).Should().Be("provider_unavailable");

        await Faces(chat.Id, threadId, originId);
        _media.DetectCalls.Should().Be(2, "Error не кэшируется — повторный запрос запускает детектор");
    }

    // Версии нет в нити — 404 version_not_found
    [Fact]
    public async Task Версии_нет_version_not_found()
    {
        var chat = await Chat();
        var (threadId, _) = await OpenThread(chat.Id, "images/hero.png");
        Flag(true);

        var resp = await Faces(chat.Id, threadId, "нет-такой");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(resp)).Should().Be(ImageEditErrorCodes.VersionNotFound);
    }

    // Чужая нить (из другого чата) — 404 thread_not_found, как у соседних ручек
    [Fact]
    public async Task Чужая_нить_thread_not_found()
    {
        var chat = await Chat();
        var other = await Chat();
        var (foreignThread, _) = await OpenThread(other.Id, "images/hero.png");
        Flag(true);

        var resp = await Faces(chat.Id, foreignThread, ImageThreadVersion.OriginId);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(resp)).Should().Be(ImageEditErrorCodes.ThreadNotFound);
    }

    // Личная область: маршрута faces нет (404) — персонажей у личного чата нет по построению
    [Fact]
    public async Task Личная_область_маршрута_нет()
    {
        var chat = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный чат");
        var resp = await _client.PostAsJsonAsync($"/api/image-editor/chats/{chat.Id}/threads",
            new { draftFolder = "", revision = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var threadId = (await Json(resp)).GetProperty("focus").GetString()!;
        Flag(true);

        var faces = await _client.PostAsync($"/api/image-editor/chats/{chat.Id}/threads/{threadId}/versions/{ImageThreadVersion.OriginId}/faces", null!);

        faces.StatusCode.Should().Be(HttpStatusCode.NotFound, "в личной области маршрута faces нет");
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
