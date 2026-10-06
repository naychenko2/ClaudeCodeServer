using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.VideoEditor;

// Загрузка кадра «С компьютера» в личном чате (ADR-022, «Контракты»): у личного чата нет проекта, кадр ложится в
// рабочую папку владельца, а FrameRef вида file годен для настроек сцены, котировки и запуска
public class VideoFrameUploadTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly FrameCapturingEngine _engine = new();

    public VideoFrameUploadTests()
    {
        _factory.ExtraServices = services => services.AddSingleton<IVideoEngine>(_engine);
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.VideoEditor, true).Should().BeTrue();
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Модель с первым кадром; запоминает байты кадра A, с которыми её запустили
    private sealed class FrameCapturingEngine : IVideoEngine
    {
        private static readonly VideoModelInfo Model = new("frame-model", "Кадровая",
            new VideoCaps([5], ["16:9"], false, false, VideoLicense.Commercial, VideoPriceUnits.Usd, FirstFrame: true),
            new VideoPriceHint(0.1, VideoPriceUnits.Usd, "sec"));

        public byte[]? SeenFrameA { get; private set; }
        public string Key => "framefake";
        public string Label => "Кадровый фейк";
        public string PriceUnit => VideoPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<VideoModelInfo> Models => [Model];

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            SeenFrameA = req.FrameA?.Bytes;
            progress.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r", Accepted: true));
            return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([1, 2, 3], "video/mp4", ".mp4", req.DurationSec, true),
                null, true, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> PersonalChat() =>
        (await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный")).Id;

    private static string Url(string chatId) => $"/api/video-editor/chats/{chatId}/frames/upload";

    private Task<HttpResponseMessage> Upload(string chatId, byte[] bytes, string fileName = "k.png")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);
        return _client.PostAsync(Url(chatId), content);
    }

    [Fact]
    public async Task Успешная_загрузка_даёт_кадр_для_настроек_котировки_и_запуска()
    {
        var chat = await PersonalChat();
        var uploaded = await Upload(chat, Png);
        uploaded.StatusCode.Should().Be(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        var frame = await Json(uploaded);
        frame.GetProperty("kind").GetString().Should().Be("file");
        var path = frame.GetProperty("path").GetString()!;
        VideoEditWorkspace.IsFrameRef(path).Should().BeTrue(path);

        var personal = $"/api/video-editor/chats/{chat}";
        var opened = await _client.PostAsJsonAsync($"{personal}/scenes", new
        {
            folder = "",
            settings = new { text = "т", provider = "framefake", model = "frame-model", durationSec = 5, aspect = "16:9", count = 1, frameA = new { kind = "file", path } },
            revision = 0,
        });
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var sceneId = (await Json(opened)).GetProperty("focus").GetProperty("sceneId").GetString()!;

        var quote = await _client.PostAsJsonAsync($"{personal}/quote",
            new { sessionId = chat, sceneId, provider = "framefake", model = "frame-model", count = 1, durationSec = 5 });
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        var quoteId = (await Json(quote)).GetProperty("quoteId").GetString()!;
        var started = await _client.PostAsJsonAsync($"{personal}/jobs", new { quoteId, sessionId = chat, sceneId });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        await _factory.Services.GetRequiredService<VideoEditJobService>().WhenDone((await Json(started)).GetProperty("jobId").GetString()!);

        _engine.SeenFrameA.Should().Equal(Png, "в драйвер ушли именно загруженные байты");
    }

    [Fact]
    public async Task Чужой_личный_чат_и_чат_проекта_404()
    {
        var secondId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var strangers = await Sessions.CreateChatAsync(secondId, ClaudeMode.AcceptEdits, name: "Чужой");
        (await Upload(strangers.Id, Png)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Upload("нет-такого", Png)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Не_картинка_400_даже_с_расширением_png()
    {
        var chat = await PersonalChat();
        var resp = await Upload(chat, "это не картинка"u8.ToArray(), "k.png");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(VideoEditorErrors.InvalidRequest);
    }

    [Fact]
    public async Task Больше_двадцати_мегабайт_413()
    {
        var chat = await PersonalChat();
        var big = new byte[(int)VideoFrameReader.MaxFrameBytes + 1];
        Png.CopyTo(big, 0);
        (await Upload(chat, big)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Загрузка_отдаёт_человеческое_имя_а_кадр_читается_превью_с_типом_по_сигнатуре()
    {
        var chat = await PersonalChat();
        var frame = await Json(await Upload(chat, Png, "кадр-а.png"));
        frame.GetProperty("fileName").GetString().Should().Be("кадр-а.png");
        var name = frame.GetProperty("path").GetString()!["frames/".Length..];

        var got = await _client.GetAsync($"/api/video-editor/chats/{chat}/frames/{name}");
        got.StatusCode.Should().Be(HttpStatusCode.OK);
        got.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await got.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("..%2F..%2Fsecret.png")]
    [InlineData("0123456789abcdef0123456789abcdef.png")]
    [InlineData("0123456789abcdef0123456789abcdef.gif")]
    public async Task Кадр_с_именем_вне_шаблона_или_несуществующий_404(string name)
    {
        var chat = await PersonalChat();
        (await _client.GetAsync($"/api/video-editor/chats/{chat}/frames/{name}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Кадр_чужого_владельца_и_чужого_чата_404()
    {
        var chat = await PersonalChat();
        var frame = await Json(await Upload(chat, Png));
        var name = frame.GetProperty("path").GetString()!["frames/".Length..];
        var secondId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var strangers = await Sessions.CreateChatAsync(secondId, ClaudeMode.AcceptEdits, name: "Чужой");

        (await _client.GetAsync($"/api/video-editor/chats/{strangers.Id}/frames/{name}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
