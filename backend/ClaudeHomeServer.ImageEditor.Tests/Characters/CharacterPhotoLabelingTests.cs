using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Фоновая разметка фото персонажа (ADR-027 §9): флаг, повторы, human, отказы, последовательность, сжатие
public class CharacterPhotoLabelingTests : IDisposable
{
    private const string Owner = "u1";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ccs_label_" + Guid.NewGuid().ToString("N"));
    private readonly FakeLabeler _labeler = new();
    private readonly FakeMedia _media = new();
    private readonly FakeRaster _raster = new();
    private readonly FakeFlags _flags = new();
    private readonly CharacterLoraRuns _locks = new(NullLogger<CharacterLoraRuns>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private CharacterPhotoLabeling Service() =>
        new(_locks, NullLogger<CharacterPhotoLabeling>.Instance, _labeler, _flags, _media, _raster);

    private string Root(string name = "p")
    {
        var root = Path.Combine(_base, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static CharacterManifest MakeCharacter(string root, string name = "Anya")
    {
        var r = CharacterStore.Create(root,
            new CharacterDraft(name, null, [.. Enumerable.Range(1, 3).Select(i => new CharacterPhotoUpload(CharacterStoreTests.Jpeg((byte)i), null))]), DateTime.UtcNow);
        r.Error.Should().BeNull();
        return r.Value!.Manifest;
    }

    private static CharacterPhoto Photo(string root, string slug, int i) => CharacterStore.Get(root, slug)!.Photos[i];

    private static CharacterPhotoLabeling.Job JobOf(string root, string slug) => new(Owner, root, slug);

    [Fact]
    public async Task Флаг_выключен_ни_одного_вызова()
    {
        var root = Root();
        var m = MakeCharacter(root);
        await Service().ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(0);
        _media.Calls.Should().Be(0);
        _raster.Applied.Should().BeEmpty();
        Photo(root, m.Slug, 0).Angle.Should().BeNull();
    }

    [Fact]
    public void Флаг_выключен_повод_ничего_не_ставит()
    {
        var svc = Service();
        svc.Enqueue(Owner, Root(), "x");
        // Включаем флаг и запускаем: очередь должна быть пуста, значит вызовов нет
        _flags.On = true;
        svc.Invoking(s => s.ProcessAsync(JobOf(Root(), "нет"), default).GetAwaiter().GetResult()).Should().NotThrow();
        _labeler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ракурс_face_pose_пишется_а_эмоция_идёт_через_vision()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(3, 2, 0), new(60, 0, 0), null];
        _labeler.Answer = _ => ImageLabelResult.Ok(
            "```json\n[{\"emotion\":\"smile\"},{\"emotion\":\"laugh\"},{\"emotion\":\"бред\"}]\n```");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        var photos = CharacterStore.Get(root, m.Slug)!.Photos;
        photos.Select(p => p.Angle).Should().Equal("front", "profile", "unknown");
        photos.Select(p => p.Pitch).Should().Equal("level", "unknown", "unknown");
        photos.Select(p => p.Emotion).Should().Equal("smile", "laugh", "unknown");
        photos.Should().OnlyContain(p => p.LabelSource == "auto");
        _media.Calls.Should().Be(1);
        _labeler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Размеченное_и_unknown_повторно_не_уходят()
    {
        _flags.On = true;
        _media.Poses = [null, null, null];
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Refused);
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "unknown" && p.Emotion == "unknown");
        var calls = (_media.Calls, _labeler.Calls);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        (_media.Calls, _labeler.Calls).Should().Be(calls);
    }

    [Fact]
    public async Task Human_не_трогается_и_не_уходит_в_модель()
    {
        _flags.On = true;
        var root = Root();
        var m = MakeCharacter(root);
        CharacterStore.Update(root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels> { [m.Photos[0].File] = new("profile") })).Should().NotBeNull();
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(0, 0, 0), new(0, 0, 0)];
        _labeler.Answer = _ => ImageLabelResult.Ok("[{\"emotion\":\"sad\"},{\"emotion\":\"sad\"}]");

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _media.LastCount.Should().Be(2);
        var first = Photo(root, m.Slug, 0);
        first.LabelSource.Should().Be("human");
        first.Angle.Should().Be("profile");
        first.Emotion.Should().BeNull();
        _raster.Applied.Should().HaveCount(2);
    }

    [Fact]
    public async Task Отказ_модели_даёт_unknown_без_исключения()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.NoVision);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().Invoking(s => s.ProcessAsync(JobOf(root, m.Slug), default)).Should().NotThrowAsync();

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown" && p.LabelSource == "auto");
    }

    [Theory]
    [InlineData(ImageLabelFailure.Unavailable)]
    [InlineData(ImageLabelFailure.Timeout)]
    public async Task Временная_недоступность_ничего_не_пишет(ImageLabelFailure failure)
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(failure);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == null && p.Emotion == null && p.LabelSource == null);
    }

    [Fact]
    public async Task Failed_третий_раз_подряд_даёт_unknown_и_больше_не_уходит()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        for (var round = 1; round <= 2; round++)
        {
            await svc.ProcessAsync(JobOf(root, m.Slug), default);
            CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
                p => p.Angle == null && p.Emotion == null && p.LabelSource == null);
        }
        _labeler.Calls.Should().Be(4);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(
            p => p.Angle == "unknown" && p.Pitch == "unknown" && p.Emotion == "unknown" && p.LabelSource == "auto");
        _labeler.Calls.Should().Be(6);

        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Calls.Should().Be(6);
    }

    [Theory]
    [InlineData(ImageLabelFailure.Unavailable)]
    [InlineData(ImageLabelFailure.Timeout)]
    public async Task Временная_недоступность_не_увеличивает_счётчик_Failed(ImageLabelFailure temporary)
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        // Две Failed, затем пять временных отказов, затем снова Failed: всего три Failed, но unknown ещё не должно быть
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        _labeler.Answer = _ => ImageLabelResult.Fail(temporary);
        for (var i = 0; i < 5; i++) await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Emotion == null);

        // Третья Failed — счёт дошёл ровно до порога, временные его не сдвинули
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "unknown" && p.Emotion == "unknown");
    }

    [Fact]
    public async Task Успешный_ответ_сбрасывает_счётчик_Failed()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);
        var svc = Service();

        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        _labeler.Answer = _ => ImageLabelResult.Ok(
            "[{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"},{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"},{\"angle\":\"front\",\"pitch\":\"level\",\"emotion\":\"smile\"}]");
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "front" && p.Emotion == "smile");

        // Человек сбросил метки — фото снова в разметке; две новые неудачи — меньше порога, если счёт был сброшен успехом
        var reset = m.Photos.ToDictionary(p => p.File, _ => new CharacterPhotoLabels());
        CharacterStore.Update(root, Owner, m.Slug, new CharacterPatch(null, null, [], [], null, null, reset)).Should().NotBeNull();
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Failed);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);
        await svc.ProcessAsync(JobOf(root, m.Slug), default);

        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Emotion == null);
    }

    [Fact]
    public async Task Busy_face_pose_ничего_не_пишет()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Busy = true;
        _labeler.Answer = _ => ImageLabelResult.Fail(ImageLabelFailure.Timeout);
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _media.Calls.Should().Be(1);
        CharacterStore.Get(root, m.Slug)!.Photos.Should().OnlyContain(p => p.Angle == null && p.Pitch == null);
    }

    [Fact]
    public async Task Сжатие_до_512_перед_отправкой_в_облако()
    {
        _flags.On = true;
        _raster.Width = 3000;
        _raster.Height = 2000;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        _labeler.Answer = r => ImageLabelResult.Ok("[{\"angle\":\"front\",\"pitch\":\"level\"}]");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _raster.Applied.Should().NotBeEmpty();
        _raster.Applied.Select(o => o.Single()).Should().OnlyContain(o => IsWidth512(o));
        _labeler.Requests.Should().OnlyContain(r => r.Images.All(i => i.Bytes.SequenceEqual(FakeRaster.Output)));
        _labeler.Requests[0].Prompt.Should().Contain("Не опознавай");
    }

    [Fact]
    public async Task Мелкое_фото_не_увеличивается()
    {
        _flags.On = true;
        _raster.Width = 300;
        _raster.Height = 400;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.Vision, false, "m");
        var root = Root();
        var m = MakeCharacter(root);

        await Service().ProcessAsync(JobOf(root, m.Slug), default);

        _raster.Applied.Should().OnlyContain(o => o.Count == 0);
    }

    [Fact]
    public async Task Два_персонажа_строго_последовательно()
    {
        _flags.On = true;
        _labeler.Routes[LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null);
        _media.Poses = [new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)];
        _media.Delay = TimeSpan.FromMilliseconds(100);
        _labeler.Answer = _ => ImageLabelResult.Ok("[{\"emotion\":\"neutral\"},{\"emotion\":\"neutral\"},{\"emotion\":\"neutral\"}]");
        _labeler.Delay = TimeSpan.FromMilliseconds(50);
        var root = Root();
        var a = MakeCharacter(root, "Anya");
        var b = MakeCharacter(root, "Boris");
        var svc = Service();
        await svc.StartAsync(default);
        try
        {
            svc.Enqueue(Owner, root, a.Slug);
            svc.Enqueue(Owner, root, b.Slug);
            svc.Enqueue(Owner, root, b.Slug);
            await WaitAsync(() => CharacterStore.Get(root, b.Slug)!.Photos.All(p => p.Emotion != null));
        }
        finally { await svc.StopAsync(default); }

        Concurrency.Max.Should().Be(1);
        _media.Calls.Should().Be(2);
        CharacterStore.Get(root, a.Slug)!.Photos.Should().OnlyContain(p => p.Angle == "front" && p.Emotion == "neutral");
    }

    private static bool IsWidth512(ImageTransformOp op) => op is ResizeOp { Width: 512, Height: null };

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        done().Should().BeTrue();
    }

    private static class Concurrency
    {
        private static int _now;
        public static int Max { get; private set; }
        public static void Enter() { var n = Interlocked.Increment(ref _now); lock (typeof(Concurrency)) Max = Math.Max(Max, n); }
        public static void Exit() => Interlocked.Decrement(ref _now);
    }

    private sealed class FakeFlags : IFeatureFlagGate
    {
        public bool On { get; set; }
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.CharacterSmartPhotos;
    }

    private sealed class FakeLabeler : IImageLabeler
    {
        public Dictionary<string, ImageLabelRoute> Routes { get; } = new()
        {
            [LocalActionCatalog.CharacterPhotoAngle] = new(ImageLabelRouteKind.FacePose, false, null),
            [LocalActionCatalog.CharacterPhotoEmotion] = new(ImageLabelRouteKind.Vision, false, "m"),
        };
        public Func<ImageLabelRequest, ImageLabelResult> Answer { get; set; } = _ => ImageLabelResult.Ok("[]");
        public TimeSpan Delay { get; set; }
        public List<ImageLabelRequest> Requests { get; } = [];
        public int Calls => Requests.Count;

        public ImageLabelRoute RouteOf(string actionKey, string ownerId) => Routes[actionKey];

        public async Task<ImageLabelResult> LabelAsync(ImageLabelRequest request, CancellationToken ct)
        {
            Concurrency.Enter();
            try
            {
                Requests.Add(request);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                return Answer(request);
            }
            finally { Concurrency.Exit(); }
        }
    }

    private sealed class FakeMedia : ILocalImageMedia
    {
        public bool Available => true;
        public bool Configured => true;
        public bool Busy { get; set; }
        public TimeSpan Delay { get; set; }
        public IReadOnlyList<LocalFacePose?> Poses { get; set; } = [];
        public int Calls { get; private set; }
        public int LastCount { get; private set; }

        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult<int?>(0);
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => null;
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => Task.FromResult(false);

        public async Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct)
        {
            Concurrency.Enter();
            try
            {
                Calls++;
                LastCount = images.Count;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                if (Busy) return LocalFacePoseResult.Fail("занято", busy: true);
                return new LocalFacePoseResult([.. Poses.Take(images.Count)], null);
            }
            finally { Concurrency.Exit(); }
        }
    }

    private sealed class FakeRaster : IImageRaster
    {
        public static readonly byte[] Output = [1, 2, 3, 4];
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 600;
        public List<IReadOnlyList<ImageTransformOp>> Applied { get; } = [];

        public RasterProbe? Probe(byte[] data) => new(ImageEncodeFormat.Jpeg, "jpeg", Width, Height, 1, data.Length);

        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null)
        {
            Applied.Add(ops);
            return new RasterOutcome(new RasterImage(Output, ImageEncodeFormat.Jpeg, 1, 1), RasterError.None, null);
        }

        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => throw new NotSupportedException();
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => throw new NotSupportedException();
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => throw new NotSupportedException();
    }
}
