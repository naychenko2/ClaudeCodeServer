using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Подстановка персонажа на лицо (ADR-028, этап 2): каждый отказ из п.2 a–f,
// тест шва (образцы только персонаж B, не A из контекста), запись FaceTarget в нить
public class FaceTargetLaunchTests : IDisposable
{
    private const string Owner = "user-ft";
    private const string Chat = "chat-ft";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_face_target_" + Guid.NewGuid().ToString("N"));
    private readonly ImageEditScope _scope;
    private readonly string _charB;
    private readonly string _charA;
    private readonly ImageThreadStore _store;
    private readonly ImageThreadService _threads;
    private string _threadId = "";
    private string _facesKey = "";

    public FaceTargetLaunchTests()
    {
        Directory.CreateDirectory(_root);
        var createdA = CharacterStore.Create(_root,
            new CharacterDraft("Аня", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        createdA.Error.Should().BeNull();
        _charA = createdA.Value!.Manifest.Slug;
        var createdB = CharacterStore.Create(_root,
            new CharacterDraft("Боря", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        createdB.Error.Should().BeNull();
        _charB = createdB.Value!.Manifest.Slug;
        _scope = ImageEditScope.Of(new Project { Id = "p1", OwnerId = Owner, RootPath = _root });

        // Нить с версией: файл проекта + нить
        File.WriteAllBytes(Path.Combine(_root, "test.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        _store = new ImageThreadStore(Path.Combine(_root, "threads"));
        var log = new Mock<ILogger<ImageThreadService>>();
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" });
        _threads = new ImageThreadService(_store, log.Object, directory: directory.Object);
        var open = _threads.OpenAsync(Owner, "p1", Chat, "test.png", null, 0, default).GetAwaiter().GetResult();
        _threadId = _store.Get(Owner, Chat).Threads.First().Id;
        _facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(Path.Combine(_root, "test.png")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Хелперы ─────────────────────────────────────────────────────────────────

    private static ILocalImageMedia FakeMedia(IReadOnlyList<LocalDetectedFace> faces,
        bool busy = false, string? error = null)
    {
        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new LocalFaceDetectResult(200, 200, faces, error, busy)));
        return media.Object;
    }

    private static IFeatureFlagGate Flags(bool faceSwapOn)
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.CharacterFaceSwap)).Returns(faceSwapOn);
        return flags.Object;
    }

    private static IImageEditJobs QuoteJobs(ImageEditOp op = ImageEditOp.Edit, string? provider = "local")
    {
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Owner, "p1", "q1"))
            .Returns(new ImageEditQuoteInfo(op, null, provider));
        return jobs.Object;
    }

    private ImageEditLaunchAssembler Assembler(ILocalImageMedia? media = null,
        IFeatureFlagGate? flags = null, IImageEditJobs? jobs = null, ImageContextLaunch? context = null)
    {
        return new ImageEditLaunchAssembler(
            [HiggsfieldImageEditorTests.Create().Editor],
            jobs ?? QuoteJobs(),
            new SkiaImageRaster(),
            threads: _threads,
            flags: flags ?? Flags(true),
            faces: new FaceDetectionCache(),
            localMedia: media,
            context: context);
    }

    private string FaceTargetJson(string facesKey, int face = 1, string? characterSlug = null) =>
        $"{{\"versionId\":\"origin\",\"facesKey\":\"{facesKey}\",\"faces\":[{{\"face\":{face},\"characterSlug\":\"{characterSlug ?? _charB}\"}}]}}";

    private ImageEditLaunchRequest Request(string? slug, string? faceTargetJson,
        string? prompt = null, long? contextRevision = null)
    {
        FaceTarget? faceTarget = null;
        if (faceTargetJson is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(faceTargetJson);
            var r = doc.RootElement;
            var faces = new List<FaceAssignment>();
            foreach (var el in r.GetProperty("faces").EnumerateArray())
                faces.Add(new FaceAssignment(el.GetProperty("face").GetInt32(), el.GetProperty("characterSlug").GetString()!));
            faceTarget = new FaceTarget(r.GetProperty("versionId").GetString()!,
                r.GetProperty("facesKey").GetString()!, faces);
        }
        return new ImageEditLaunchRequest(
            "q1", prompt, null, null, null, null, null, [], [], slug,
            ThreadSessionId: Chat, ThreadId: _threadId,
            ContextRevision: contextRevision,
            FaceTarget: faceTarget);
    }

    // ── a. Флаг выключен ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Флаг_выключен_даёт_feature_disabled()
    {
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(flags: Flags(false)).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be("feature_disabled");
    }

    // ── b. Личная область ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Личная_область_даёт_invalid_request()
    {
        var personalScope = ImageEditScope.Of(new Session { Id = Chat, ProjectId = null, OwnerId = Owner });
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler().AssembleAsync(Owner, personalScope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── b. Нет CharacterSlug ──────────────────────────────────────────────────────

    [Fact]
    public async Task Без_CharacterSlug_даёт_invalid_request()
    {
        var req = Request(null, FaceTargetJson(_facesKey, 1, ""));
        var result = await Assembler().AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("персонажа");
    }

    // ── b. Mask в теле ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mask_в_теле_даёт_invalid_request()
    {
        var req = Request(_charB, FaceTargetJson(_facesKey));
        req = req with { Mask = new ImageBytes([1, 2, 3], "image/png") };
        var result = await Assembler().AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── b. Провайдер не local ─────────────────────────────────────────────────────

    [Fact]
    public async Task Провайдер_не_local_даёт_invalid_request()
    {
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(jobs: QuoteJobs(provider: "fal")).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("локальными");
    }

    // ── b. ContextRevision ────────────────────────────────────────────────────────

    [Fact]
    public async Task ContextRevision_даёт_invalid_request()
    {
        var req = Request(_charB, FaceTargetJson(_facesKey), contextRevision: 5);
        var result = await Assembler().AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("контекстом");
    }

    // ── b. ReferencePaths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReferencePaths_даёт_invalid_request()
    {
        var req = Request(_charB, FaceTargetJson(_facesKey));
        req = req with { ReferencePaths = [("ref.png", ReferenceRole.Object)] };
        var result = await Assembler().AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── d. Busy ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Busy_даёт_busy()
    {
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var media = FakeMedia(faces, busy: true);
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be("busy");
    }

    // ── d. Error ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Error_даёт_provider_unavailable()
    {
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var media = FakeMedia(faces, error: "ComfyUI не ответил");
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.ProviderUnavailable);
    }

    // ── d. facesKey не совпал → 409 faces_changed ─────────────────────────────────

    [Fact]
    public async Task FacesKey_не_совпал_даёт_faces_changed()
    {
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var media = FakeMedia(faces);
        var req = Request(_charB, FaceTargetJson("stale:v1", 1, _charB));
        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be("faces_changed");
        result.Payload.Should().NotBeNull();
    }

    // ── e. Лицо вне диапазона ─────────────────────────────────────────────────────

    [Fact]
    public async Task Лицо_вне_диапазона_даёт_invalid_request()
    {
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var media = FakeMedia(faces);
        var req = Request(_charB, FaceTargetJson(_facesKey, 99, _charB));
        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("99");
    }

    // ── e. Лицо small ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Лицо_small_даёт_invalid_request()
    {
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 30, 30, 0.9, true) };
        var media = FakeMedia(faces);
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("мелкое");
    }

    // ── ТЕСТ ШВА (ключевой) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Шов_образцы_только_персонаж_B_без_A_из_контекста()
    {
        var faces = new[] {
            new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false),
            new LocalDetectedFace(2, 120, 10, 200, 100, 0.85, false)
        };
        var media = FakeMedia(faces);
        var req = Request(_charB, FaceTargetJson(_facesKey, 2, _charB),
            prompt: "дополнительно в профиль");

        var result = await Assembler(media: media).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().NotBeNull(result.Error);
        var input = result.Value!;

        // Маска построена
        input.Mask.Should().NotBeNull();
        input.Mask!.Bytes.Should().NotBeEmpty();

        // Образцы — только персонаж B
        input.References.Should().NotBeEmpty();
        foreach (var r in input.References)
            r.Role.Should().Be(ReferenceRole.Character);

        // Персонаж — B
        input.Character.Should().NotBeNull();
        input.Character!.Slug.Should().Be(_charB);

        // Промпт содержит имя B
        input.Prompt.Should().Contain("Боря");

        // FaceTarget записан
        input.FaceTarget.Should().NotBeNull();
        input.FaceTarget!.N.Should().Be(2);
        input.FaceTarget!.CharacterSlug.Should().Be(_charB);
    }

    // ── Без FaceTarget — как раньше (регресс) ─────────────────────────────────────

    [Fact]
    public async Task Без_FaceTarget_запуск_как_раньше()
    {
        var req = Request(_charB, null);
        var result = await Assembler().AssembleAsync(Owner, _scope, req, default);

        // Без FaceTarget — обычный запуск (может быть Ok или Invalid по другим причинам)
        result.ErrorCode.Should().NotBe("feature_disabled");
        result.ErrorCode.Should().NotBe("faces_changed");
    }

    // ── Запись FaceTarget в нить: сериализация WhenWritingDefault ─────────────────

    [Fact]
    public void Запись_FaceTarget_в_нити_сериализуется()
    {
        var launch = new ImageThreadLaunch("job-1", "origin", null, DateTime.UtcNow,
            ImageThreadLaunchStatus.Running, "human", "тест",
            FaceTarget: new ImageThreadFaceTarget(2, [10, 10, 100, 100], "boria"));

        var json = System.Text.Json.JsonSerializer.Serialize(launch);
        json.Should().Contain("FaceTarget");
        json.Should().Contain("boria");

        // Старая нить без FaceTarget читается
        var oldLaunch = new ImageThreadLaunch("job-2", "origin", null, DateTime.UtcNow,
            ImageThreadLaunchStatus.Running, "human", "тест");
        var oldJson = System.Text.Json.JsonSerializer.Serialize(oldLaunch);
        oldJson.Should().NotContain("FaceTarget");

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ImageThreadLaunch>(oldJson);
        deserialized!.FaceTarget.Should().BeNull();
    }

    // ── LoraNamespace.VisibleTo: чужая LoRA отбрасывается ──────────────────────────

    [Fact]
    public async Task Чужая_LoRA_отбрасывается_персонаж_находится()
    {
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Чужой", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        var slug = created.Value!.Manifest.Slug;
        CharacterStore.Update(_root, Owner, slug,
            new CharacterPatch(null, null, [], [], null,
                CharacterLoraPatch.Set("ccs/otherowner/lora", null, null, null)));

        var req = Request(slug, FaceTargetJson(_facesKey));
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var result = await Assembler(media: FakeMedia(faces)).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.Character.Should().NotBeNull();
        result.Value!.Character!.Lora.Should().BeNull();
    }

    // ── Гейт контекста чата: FaceTarget не читает IChatContextStore ───────────────

    [Fact]
    public async Task Гейт_контекста_чата_FaceTarget_не_читает_стор()
    {
        // Фейк IChatContextStore: считает обращения Get; в сторе — активная ревизия с персонажем «A»
        var getCallCount = 0;
        var storeMock = new Mock<IChatContextStore>();
        var itemA = new ContextItem("i-a", ImageContextKind.CharacterKind,
            new JsonObject { ["slug"] = _charA }, "character", ContextActor.Human, DateTime.UtcNow);
        var stateA = new ChatContextState(1, null, [itemA]);
        storeMock.Setup(s => s.Get(Owner, Chat))
            .Callback(() => Interlocked.Increment(ref getCallCount))
            .Returns(stateA);

        var registry = new ContextKindRegistry([
            new ImageContextKind(_store),
            new ProjectFileContextKind()]);
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p1" });
        directory.Setup(d => d.ResolveOwnerId(It.IsAny<Session>())).Returns((Session s) => s.OwnerId ?? Owner);
        var context = new ImageContextLaunch(storeMock.Object, registry, directory.Object, _store);

        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var req = Request(_charB, FaceTargetJson(_facesKey));
        var result = await Assembler(media: FakeMedia(faces), context: context)
            .AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().NotBeNull(result.Error);
        // Персонаж — B, не A из контекста
        result.Value!.Character!.Slug.Should().Be(_charB);
        // Стор контекста не читался вовсе
        getCallCount.Should().Be(0, "FaceTarget не должен читать IChatContextStore");
    }

    // ── Отмена запуска не убивает single-flight-детекцию ───────────────────────────

    [Fact]
    public async Task Отмена_запуска_не_убивает_детекцию_для_остальных()
    {
        // Общий кэш: запуск и read-only ждут одну и ту же single-flight-задачу
        var cache = new FaceDetectionCache();

        // Управляемый детектор: записывает полученный токен, возвращает задачу,
        // которую завершаем мы (без Task.Delay — TCS)
        var tcs = new TaskCompletionSource<LocalFaceDetectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenTcs = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectCount = 0;

        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((_, ct) =>
            {
                Interlocked.Increment(ref detectCount);
                tokenTcs.TrySetResult(ct);
            })
            .Returns(() => tcs.Task);

        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var req = Request(_charB, FaceTargetJson(_facesKey));

        // Ассемблер с общим кэшем
        var assembler = new ImageEditLaunchAssembler(
            [HiggsfieldImageEditorTests.Create().Editor],
            QuoteJobs(),
            new SkiaImageRaster(),
            threads: _threads,
            flags: Flags(true),
            faces: cache,
            localMedia: media.Object);

        // Токен запуска — отменяем во время детекции
        var launchCts = new CancellationTokenSource();

        // (а) Запуск: ассемблер с FaceTarget
        var launchTask = assembler.AssembleAsync(Owner, _scope, req, launchCts.Token);

        // Ждём, пока детектор будет вызван (запись поставлена в кэш)
        var sourceBytes = File.ReadAllBytes(Path.Combine(_root, "test.png"));
        while (Volatile.Read(ref detectCount) == 0)
            await Task.Delay(10);

        // (б) Параллельный read-only: прямой GetOrDetectAsync с другим токеном
        var readOnlyCts = new CancellationTokenSource();
        var readOnlyTask = cache.GetOrDetectAsync(Owner, sourceBytes,
            () => media.Object.DetectFacesAsync(sourceBytes, CancellationToken.None), readOnlyCts.Token);

        // Отменяем токен запуска
        launchCts.Cancel();

        // Запуск получает OCE
        var launchEx = await Record.ExceptionAsync(() => launchTask);
        launchEx.Should().BeAssignableTo<OperationCanceledException>();

        // Завершаем детекцию
        tcs.SetResult(new LocalFaceDetectResult(200, 200, faces));

        // Read-only получает результат (верхний таймаут 2 с: поломка краснит, а не вешает)
        var readOnlyResult = await readOnlyTask.WaitAsync(TimeSpan.FromSeconds(2));
        readOnlyResult.Faces.Should().HaveCount(1);

        // Детектор не получил отменённый токен: детекция на CancellationToken.None
        (await tokenTcs.Task).CanBeCanceled.Should().BeFalse("детекция должна быть на CancellationToken.None");

        // Детектор вызван один раз (single-flight)
        Volatile.Read(ref detectCount).Should().Be(1);
    }

    // ── Нормализация EXIF: source в ImageEditJobInput — в системе координат экрана ──────

    // JPEG с EXIF Orientation 6 (сырые 1312×640, показ 640×1312): после нормализации
    // source = 640×1312 без EXIF, маска по bbox детекции в той же системе. facesKey — по сырым
    // байтам (совпадает с faces-эндпоинтом), поэтому faces_changed не срабатывает
    [Fact]
    public async Task EXIF6_source_нормализован_и_facesKey_стабилен()
    {
        // Создаём JPEG 1312×640 с EXIF Orientation 6
        var jpeg = JpegWithExif6();
        var jpegPath = Path.Combine(_root, "exif6.jpg");
        File.WriteAllBytes(jpegPath, jpeg);

        // Открываем нить на JPEG (текущий revision, не 0 — иначе Conflict); facesKey — из файловых байтов
        var revision = _store.Get(Owner, Chat).Revision;
        _threads.OpenAsync(Owner, "p1", Chat, "exif6.jpg", null, revision, default).GetAwaiter().GetResult();
        var threadId = _store.Get(Owner, Chat).Threads.Last().Id;
        var facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(jpegPath));

        // Воркер возвращает нормализованные размеры 640×1312
        var faces = new[] { new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false) };
        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new LocalFaceDetectResult(640, 1312, faces)));

        var req = new ImageEditLaunchRequest(
            "q1", null, null, null, null, null, null, [], [], _charB,
            ThreadSessionId: Chat, ThreadId: threadId,
            FaceTarget: new FaceTarget("origin", facesKey, [new FaceAssignment(1, _charB)]));

        var result = await Assembler(media: media.Object).AssembleAsync(Owner, _scope, req, default);

        result.Value.Should().NotBeNull(result.Error);
        var input = result.Value!;
        // Source нормализован: 640×1312 (не 1312×640)
        input.Source.Should().NotBeNull();
        var sourceSize = ImageDimensions.Read(input.Source!.Bytes);
        sourceSize.Should().Be((640, 1312));
        // Маска построена в той же системе
        input.Mask.Should().NotBeNull();
    }

    // JPEG 1312×640 с EXIF Orientation 6 (тот же формат, что в LocalImageMediaAdapterTests)
    private static byte[] JpegWithExif6()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1312, 640, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Gray);
        canvas.Flush();
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        var jpeg = data!.ToArray();

        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
        void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void Le32(List<byte> b, uint v) { for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
        Le16(tiff, 1);
        Le16(tiff, 0x0112);
        Le16(tiff, 3);
        Le32(tiff, 1);
        Le32(tiff, 6);
        Le32(tiff, 0);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }
}
