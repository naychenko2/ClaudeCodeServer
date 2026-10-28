using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Запись FaceTargets[] в нить, сходство и статус на каждое лицо, чтение старых данных (ADR-028, §9–11)
public class ImageThreadFaceTargetsTests : IDisposable
{
    private const string Owner = "owner-ft";
    private const string Chat = "chat-ft";
    private const string Project = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_thread_ft_" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;
    private readonly List<ImageEditJobService> _services = [];

    public ImageThreadFaceTargetsTests()
    {
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ImageThread Thread(string id) => _store.Get(Owner, Chat).Threads.Single(t => t.Id == id);

    private string Opened() => _store.Open(Owner, Chat, "images/hero.png", null, _store.Get(Owner, Chat).Revision).Thread!.Id;

    private static ImageThreadLaunch Launch(string job, params ImageThreadFaceTarget[] targets) =>
        new(job, ImageThreadVersion.OriginId, null, DateTime.UtcNow, ImageThreadLaunchStatus.Running, "human", "подстановка",
            FaceTargets: targets);

    // (1) Сверка старых данных: JSON нити этапа 2 с одиночным faceTarget читается как массив из одного элемента
    [Fact]
    public void Старое_одиночное_faceTarget_читается_как_массив_из_одного_элемента()
    {
        var path = _store.StatePath(Owner, Chat);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"focus":"t1","revision":3,"threads":[{"id":"t1","file":"images/hero.png","lineage":[],"draftFolder":null,
             "stacks":[],"currentStackId":null,"settings":null,"pendingJobId":null,"createdAt":"2026-10-01T10:00:00Z",
             "versions":[{"id":"origin","number":0,"jobId":null,"variant":null,"baseVersionId":null,"baseStepId":null,
               "steps":[],"currentStepId":null,"createdAt":"2026-10-01T10:00:00Z"},
              {"id":"v1","number":1,"jobId":"job-old","variant":1,"baseVersionId":"origin","baseStepId":null,
               "steps":["s1"],"currentStepId":"s1","createdAt":"2026-10-01T10:05:00Z","similarity":0.71}],
             "currentVersionId":"v1",
             "launches":[{"jobId":"job-old","baseVersionId":"origin","baseStepId":null,"at":"2026-10-01T10:01:00Z",
               "status":"done","initiator":"human","prompt":"Мария на лицо 2",
               "faceTarget":{"n":2,"bBox":[10,20,110,140],"characterSlug":"maria"}}]}],
             "events":[]}
            """);

        var launch = _store.Get(Owner, Chat).Threads.Single().Launches.Single();

        launch.FaceTargets.Should().ContainSingle();
        var t = launch.FaceTargets![0];
        t.N.Should().Be(2);
        t.BBox.Should().Equal(10, 20, 110, 140);
        t.CharacterSlug.Should().Be("maria");
        t.Status.Should().Be("done", "статус — по статусу запуска");
        t.Similarity.Should().Be(0.71, "сходство — из старого поля версии запуска");
        launch.FaceTarget.Should().BeNull("пишется только новое поле");
    }

    // (2) Круговой тест записи и чтения на N = 2: статусы и сходство переживают перечитывание
    [Fact]
    public void Запись_и_чтение_двух_лиц_со_статусами_и_сходством()
    {
        var id = Opened();
        _store.AddLaunch(Owner, Chat, id, Launch("job-1",
            new ImageThreadFaceTarget(1, [0, 0, 8, 8], "anya"), new ImageThreadFaceTarget(2, [8, 0, 16, 8], "borya")));

        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "v1", null)], null,
            [(1, "done", 0.81), (2, "done", 0.77)]);

        var reread = new ImageThreadStore(_store.Root).Get(Owner, Chat).Threads.Single().Launches.Single();
        reread.FaceTargets!.Select(f => (f.N, f.CharacterSlug, f.Status, f.Similarity))
            .Should().Equal((1, "anya", "done", 0.81), (2, "borya", "done", 0.77));
        reread.FaceTargets![1].BBox.Should().Equal(8, 0, 16, 8);
        var json = File.ReadAllText(_store.StatePath(Owner, Chat));
        json.Should().Contain("faceTargets").And.NotContain("\"faceTarget\"", "старое поле не пишется");
    }

    // Сквозной стенд: настоящая задача + цепочка + нить; второй проход ждёт, пока запуск ляжет в нить
    private sealed class GatedEditor : IImageEditor
    {
        public const string ProviderKey = "gated";
        private static readonly ImageEditModelInfo Model = new("m", "M",
            new ImageEditCaps([ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, false));
        public bool FailPass2 { get; init; }
        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key => ProviderKey;
        public string Label => "Gated";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            var first = req.Character!.Slug == "anya";
            await Go.Task.WaitAsync(ct);
            if (!first && FailPass2) throw new InvalidOperationException("сбой прохода 2");
            var img = new EditedImage(Png(first ? SKColors.Blue : SKColors.Green), "image/png", first ? 0.81 : 0.77);
            progress.Report(new EditProgress(EditStage.Downloading, Ready: [img]));
            return new ImageEditResult(EditOutcome.Ok, [img], null, true, null, null);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static FacePass Pass(int face, string slug, int x) => new(
        face, new ImageBytes(Png(SKColors.White), "image/png"), new LocalImageCrop(x, 0, 8, 8, 16, 16),
        new ImageBytes(Png(SKColors.White), "image/png"), new CharacterRef(slug, slug, null, null, null),
        [new ReferenceImage(Png(SKColors.Red), "image/png", ReferenceRole.Character, slug + ".png")],
        new ImageThreadFaceTarget(face, [x, 0, x + 8, 8], slug), "Заменить на " + slug);

    // Запуск двух лиц от входа через цепочку и задачу до записи в нить; возвращает нить после финала
    private Task<ImageThreadLaunch> RunTwoFacesAsync(bool failPass2) => RunFacesAsync(2, failPass2, launchAfterFinish: false);

    // faces — сколько лиц (1 или 2). launchAfterFinish: запуск ложится в нить уже после конца задачи
    // (быстрый поставщик, ImageEditLaunchAssembler.LaunchAsync) — версия рождается в финале, а не по Ready
    private async Task<ImageThreadLaunch> RunFacesAsync(int faces, bool failPass2, bool launchAfterFinish)
    {
        var editor = new GatedEditor { FailPass2 = failPass2 };
        var workspace = new ImageEditWorkspace(Path.Combine(_dir, "image-editor"));
        var jobs = new ImageEditJobService([editor], workspace, NullLogger<ImageEditJobService>.Instance);
        _services.Add(jobs);
        var steps = new ImageEditSteps(new SkiaImageRaster(), workspace, jobs);
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = Project });
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, null,
            new RecordingBroadcaster(), steps);
        threads.Watch(jobs);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Finished += (_, _) => { finished.TrySetResult(); return Task.CompletedTask; };

        var id = Opened();
        var quote = await jobs.QuoteAsync(Owner, Project, new ImageEditQuoteRequest(GatedEditor.ProviderKey, "m",
            EditMode.Auto, ImageEditOp.Inpaint, 1, true, 0, false, null, null,
            FaceTarget: new FaceTarget("origin", "key", [.. new[] { new FaceAssignment(1, "anya"), new FaceAssignment(2, "borya") }.Take(faces)])), default);
        quote.Value.Should().NotBeNull(quote.Error);
        FacePass[] passes = [.. new[] { Pass(1, "anya", 0), Pass(2, "borya", 8) }.Take(faces)];
        var input = new ImageEditJobInput(quote.Value!.QuoteId, "подстановка", null, new ImageBytes(Png(SKColors.Black), "image/png"),
            passes[0].Mask, null, passes[0].References, null, passes[0].Character, ChatSessionId: Chat, ThreadId: id,
            BaseVersionId: ImageThreadVersion.OriginId, FaceTarget: passes[0].ThreadFaceTarget, FaceCrop: passes[0].Crop,
            KeepMask: passes[0].KeepMask, FacePasses: passes);
        var started = await jobs.StartAsync(Owner, Project, input, default);
        started.Value.Should().NotBeNull(started.Error);

        var jobId = started.Value!.JobId;
        if (launchAfterFinish)
        {
            editor.Go.SetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        await threads.OnLaunchedAsync(Owner, Project, Chat, id, jobs.Get(Owner, Project, jobId),
            jobId, "подстановка", ImageEditInitiator.Human, ImageThreadVersion.OriginId, null, default,
            passes.Select(p => p.ThreadFaceTarget).ToList());
        Thread(id).Launches.Single().FaceTargets.Should().HaveCount(faces, "запуск лёг в нить со всеми лицами");
        if (launchAfterFinish)
        {
            Thread(id).Versions.Where(v => !v.IsOrigin).Should().BeEmpty("по Ready запуска в нити ещё не было");
            await threads.OnJobFinishedAsync(Owner, jobs.Get(Owner, Project, jobId)!);
        }
        else
        {
            editor.Go.SetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return Thread(id).Launches.Single();
    }

    // (3a) Оба прохода прошли: обе версии-цели done, сходство каждого — своё, одна версия
    [Fact]
    public async Task Сквозной_успех_пишет_done_и_сходство_каждого_лица()
    {
        var launch = await RunTwoFacesAsync(failPass2: false);

        launch.Status.Should().Be(ImageThreadLaunchStatus.Done);
        launch.FaceTargets!.Select(f => (f.N, f.CharacterSlug, f.Status, f.Similarity))
            .Should().Equal((1, "anya", "done", 0.81), (2, "borya", "done", 0.77));
        Thread(Thread0()).Versions.Where(v => !v.IsOrigin).Should().ContainSingle();
    }

    // (3b) Частичный результат: проход 2 упал — лицо 1 done со своим сходством, лицо 2 failed без сходства
    [Fact]
    public async Task Сквозной_частичный_результат_пишет_done_и_failed_и_сходство_только_прошедшего()
    {
        var launch = await RunTwoFacesAsync(failPass2: true);

        launch.Status.Should().Be(ImageThreadLaunchStatus.Done, "версия есть — запуск done");
        launch.FaceTargets!.Select(f => (f.N, f.Status, f.Similarity))
            .Should().Equal((1, "done", 0.81), (2, "failed", (double?)null));
        var versions = Thread(Thread0()).Versions.Where(v => !v.IsOrigin).ToList();
        versions.Should().ContainSingle("ровно одна версия — выход прохода 1");
    }

    // Д4 (ADR-028, §11): у версии на два лица общего сходства нет ни на одном из путей записи —
    // ни по Ready последнего прохода (OnVariantReadyAsync), ни в финале (OnJobFinishedAsync);
    // сходство каждого лица остаётся в FaceTargets[]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Версия_на_два_лица_без_общего_сходства_а_сходство_лиц_на_месте(bool launchAfterFinish)
    {
        var launch = await RunFacesAsync(2, failPass2: false, launchAfterFinish);

        var version = Thread(Thread0()).Versions.Where(v => !v.IsOrigin).Should().ContainSingle().Subject;
        version.Similarity.Should().BeNull("общего сходства версии при N > 1 нет");
        version.MostSimilar.Should().BeFalse();
        launch.FaceTargets!.Select(f => (f.N, f.Status, f.Similarity))
            .Should().Equal((1, "done", 0.81), (2, "done", 0.77));
    }

    // N = 1 — как раньше: у версии сходство её варианта на обоих путях записи
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Slow")]
    public async Task Версия_на_одно_лицо_хранит_сходство(bool launchAfterFinish)
    {
        var launch = await RunFacesAsync(1, failPass2: false, launchAfterFinish);

        var version = Thread(Thread0()).Versions.Where(v => !v.IsOrigin).Should().ContainSingle().Subject;
        version.Similarity.Should().Be(0.81);
        launch.FaceTargets!.Select(f => (f.N, f.Status, f.Similarity)).Should().Equal((1, "done", 0.81));
    }

    private string Thread0() => _store.Get(Owner, Chat).Threads.Single().Id;
}
