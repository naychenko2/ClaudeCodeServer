using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Подстановка на два лица по HTTP (ADR-028, этап 3, шаг 12): настоящие котировка, сборщик,
// задача с цепочкой проходов и нить; подменные только драйвер «local» и детектор лиц.
// Котировка на 2 лица → запуск → одна версия, в нити FaceTargets лицо → персонаж со статусами done.
// Отказы: котировка на 1 лицо + запуск на 2, один персонаж на два лица, три лица.
[Trait("Category", "Host")]
public class FaceTargetLaunchHttpTwoFacesTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ChainEditor _editor = new();
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _chatId;
    private readonly string _threadId;
    private readonly string _facesKey;
    private readonly string _slugA;
    private readonly string _slugB;
    private readonly string _slugC;

    public FaceTargetLaunchHttpTwoFacesTests()
    {
        _factory = new TestWebApplicationFactory { UseFrontend = false };
        _factory.ExtraServices = services =>
        {
            // Боевой LocalImageEditor тоже «local» и доступен при настроенном детекторе — снимаем,
            // чтобы котировка и задача шли в подменный драйвер
            services.RemoveAll<IImageEditor>();
            services.AddSingleton<IImageEditor>(_editor);
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton<ILocalImageMedia>(new ThreeFacesMedia());
        };

        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var dir = Path.Combine(_factory.TempDir, "ft2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("ft2-" + Guid.NewGuid().ToString("N")[..6], dir, _ownerId, TestWebApplicationFactory.TestUsername).Id;
        var root = _factory.Services.GetRequiredService<ProjectManager>().GetById(_projectId)!.RootPath;
        _client = _factory.CreateAuthenticatedClient();
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.CharacterFaceSwap, true);

        _slugA = Character(root, "Аня", 1);
        _slugB = Character(root, "Боря", 4);
        _slugC = Character(root, "Вера", 7);

        // Исходник — настоящий PNG 200×200: маски и кропы строит растр по bbox детекции
        var source = Png(200, 200, SKColors.Gray);
        File.WriteAllBytes(Path.Combine(root, "test.png"), source);
        var chat = _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат").GetAwaiter().GetResult();
        _chatId = chat.Id;
        var store = _factory.Services.GetRequiredService<ImageThreadStore>();
        _threadId = store.Open(_ownerId, _chatId, "test.png", null, store.Get(_ownerId, _chatId).Revision).Thread!.Id;
        _facesKey = FaceDetectionCache.FacesKey(source);
    }

    public void Dispose()
    {
        _editor.Go.TrySetResult();
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Character(string root, string name, int tag)
    {
        var created = CharacterStore.Create(root, new CharacterDraft(name, null,
            [.. Enumerable.Range(tag, 3).Select(i => new CharacterPhotoUpload(Fakes.TestImages.Jpeg((byte)i)))]),
            DateTime.UtcNow);
        created.Error.Should().BeNull();
        return created.Value!.Manifest.Slug;
    }

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string FaceTargetJson(params (int Face, string Slug)[] faces) =>
        new JsonObject
        {
            ["versionId"] = "origin",
            ["facesKey"] = _facesKey,
            ["faces"] = new JsonArray([.. faces.Select(f => (JsonNode)new JsonObject
            {
                ["face"] = f.Face,
                ["characterSlug"] = f.Slug,
            })]),
        }.ToJsonString();

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<HttpResponseMessage> Quote(string faceTargetJson)
    {
        var body = new JsonObject
        {
            ["provider"] = "local",
            ["model"] = "auto",
            ["mode"] = "auto",
            ["op"] = "edit",
            ["count"] = 1,
            ["hasMask"] = true,
            ["faceTarget"] = JsonNode.Parse(faceTargetJson),
        };
        return await _client.PostAsync($"/api/projects/{_projectId}/image-editor/quote",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
    }

    private async Task<string> QuoteId(string faceTargetJson)
    {
        var resp = await Quote(faceTargetJson);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("quoteId").GetString()!;
    }

    private async Task<HttpResponseMessage> Start(string quoteId, string faceTargetJson)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(quoteId), "quoteId" },
            { new StringContent("тест"), "prompt" },
            { new StringContent(faceTargetJson), "faceTarget" },
            { new StringContent(_chatId), "sessionId" },
            { new StringContent(_threadId), "threadId" },
        };
        return await _client.PostAsync($"/api/projects/{_projectId}/image-editor/jobs", form);
    }

    private ImageThread Thread() =>
        _factory.Services.GetRequiredService<ImageThreadStore>().Get(_ownerId, _chatId)
            .Threads.Single(t => t.Id == _threadId);

    // ── Сквозной успех: 2 лица → одна версия, лицо → свой персонаж, done/done ─────────

    [Fact]
    public async Task Два_лица_котировка_запуск_одна_версия_и_FaceTargets_в_нити()
    {
        // Котировка: facePasses = 2, ETA удвоена (драйвер: 100 сек на правку)
        var twoFaces = FaceTargetJson((1, _slugA), (2, _slugB));
        var quoteResp = await Quote(twoFaces);
        quoteResp.StatusCode.Should().Be(HttpStatusCode.OK, await quoteResp.Content.ReadAsStringAsync());
        var quote = await Json(quoteResp);
        quote.GetProperty("facePasses").GetInt32().Should().Be(2);
        quote.GetProperty("estimate").GetProperty("etaSeconds").GetInt32().Should().Be(200);

        // Финал задачи: нить подписана на Finished раньше (сервис нити создаётся до подписки теста),
        // слушатели зовутся по очереди — к срабатыванию TCS запись в нить уже сделана
        _factory.Services.GetRequiredService<ImageThreadService>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Services.GetRequiredService<ImageEditJobService>().Finished += (_, _) =>
        {
            finished.TrySetResult();
            return Task.CompletedTask;
        };

        var start = await Start(quote.GetProperty("quoteId").GetString()!, twoFaces);
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());
        // Проходы ждут, пока запуск ляжет в нить
        Thread().Launches.Should().ContainSingle().Which.FaceTargets.Should().HaveCount(2);
        _editor.Go.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Драйвер: проход 1 — лицо 1 персонажем A по исходнику, проход 2 — лицо 2 персонажем B по выходу прохода 1
        _editor.Requests.Select(r => r.Character!.Slug).Should().Equal(_slugA, _slugB);
        _editor.Requests[1].Source!.Bytes.Should().Equal(_editor.Outputs[0], "проход 2 правит выход прохода 1");

        // Нить — реальным чтением стора: одна версия от запуска, в запуске лицо → персонаж, done/done
        var thread = Thread();
        var launch = thread.Launches.Single();
        launch.Status.Should().Be(ImageThreadLaunchStatus.Done);
        launch.FaceTargets!.Select(f => (f.N, f.CharacterSlug, f.Status))
            .Should().Equal((1, _slugA, "done"), (2, _slugB, "done"));
        var versions = thread.Versions.Where(v => !v.IsOrigin).ToList();
        versions.Should().ContainSingle("промежуточный выход прохода 1 версией не становится");
        versions[0].JobId.Should().Be(launch.JobId);
    }

    // ── Отказы при открытом N = 2 ───────────────────────────────────────────────────

    // Котировка на 1 лицо + запуск на 2 → 404 quote_not_found (сверка N), драйвер не звали
    [Fact]
    public async Task Котировка_на_1_лицо_запуск_на_2_404_quote_not_found()
    {
        var quoteId = await QuoteId(FaceTargetJson((1, _slugA)));

        var resp = await Start(quoteId, FaceTargetJson((1, _slugA), (2, _slugB)));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.QuoteNotFound);
        Thread().Launches.Should().BeEmpty();
    }

    // Один персонаж на два лица → 400 invalid_request и на котировке, и на запуске
    [Fact]
    public async Task Один_персонаж_на_два_лица_400()
    {
        var same = FaceTargetJson((1, _slugA), (2, _slugA));
        var quote = await Quote(same);
        quote.StatusCode.Should().Be(HttpStatusCode.BadRequest, await quote.Content.ReadAsStringAsync());

        // Запуск по честной котировке на 2 лица, но с одним персонажем дважды
        var quoteId = await QuoteId(FaceTargetJson((1, _slugA), (2, _slugB)));
        var resp = await Start(quoteId, same);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        Thread().Launches.Should().BeEmpty();
    }

    // Три лица → 400 invalid_request (лимит N ≤ 2) и на котировке, и на запуске
    [Fact]
    public async Task Три_лица_400()
    {
        var three = FaceTargetJson((1, _slugA), (2, _slugB), (3, _slugC));
        var quote = await Quote(three);
        quote.StatusCode.Should().Be(HttpStatusCode.BadRequest, await quote.Content.ReadAsStringAsync());

        var quoteId = await QuoteId(FaceTargetJson((1, _slugA), (2, _slugB)));
        var resp = await Start(quoteId, three);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        Thread().Launches.Should().BeEmpty();
    }

    // ── Подменные ──────────────────────────────────────────────────────────────────

    // Драйвер «local»: ETA 100 сек на правку; проход ждёт Go, отдаёт свой PNG через Ready и запоминает запрос
    private sealed class ChainEditor : IImageEditor, IImageEditQuoter
    {
        private static readonly ImageEditModelInfo Model = new("m", "m",
            new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true));
        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ImageEditRequest> Requests { get; } = [];
        public List<byte[]> Outputs { get; } = [];
        public string Key => "local";
        public string Label => "LOCAL";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            await Go.Task.WaitAsync(ct);
            byte[] bytes;
            lock (Requests)
            {
                Requests.Add(req);
                bytes = Png(200, 200, Requests.Count == 1 ? SKColors.Blue : SKColors.Green);
                Outputs.Add(bytes);
            }
            var img = new EditedImage(bytes, "image/png");
            progress.Report(new EditProgress(EditStage.Downloading, Ready: [img]));
            return new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

        public Task<ImageEditEstimateDto> EstimateAsync(ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct) =>
            Task.FromResult(new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false, ImageEditEstimateSources.Provider, EtaSeconds: 100));
        public int? ExpectedSeconds(ImageEditModelInfo model) => 100;
    }

    // Детектор: три годных лица на 200×200 — третье нужно отказу «три лица» (до лимита детекция не доходит)
    private sealed class ThreeFacesMedia : ILocalImageMedia
    {
        public bool Configured => true;
        public bool Available => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult<int?>(0);
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => null;
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct) =>
            Task.FromResult(new LocalFaceDetectResult(200, 200,
                [new LocalDetectedFace(1, 10, 10, 60, 60, 0.9, false),
                 new LocalDetectedFace(2, 130, 10, 180, 60, 0.85, false),
                 new LocalDetectedFace(3, 70, 130, 120, 180, 0.8, false)], null, false));
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }
}
