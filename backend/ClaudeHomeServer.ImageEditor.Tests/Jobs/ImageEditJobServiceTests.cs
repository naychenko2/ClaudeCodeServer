using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Исполнитель задач редактора (ADR-017, разделы 4, 7, 8): запуск только по котировке и
// только у её поставщика, трата — на запустившего, отмена останавливает задачу и шлёт
// событие, чужая задача неотличима от несуществующей
public class ImageEditJobServiceTests : IDisposable
{
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-jobs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly MemorySpendStore _spend = new();
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditJobService Service(params IImageEditor[] editors)
    {
        var service = new ImageEditJobService(editors, new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster);
        _services.Add(service);
        return service;
    }

    private static ImageEditQuoteRequest Quote(string provider, int count = 1) =>
        new(provider, "auto", EditMode.Fast, ImageEditOp.Edit, count, false, 0, false, null, null);

    private static ImageEditJobInput Input(string quoteId, byte[]? source = null, byte[]? annotated = null) =>
        new(quoteId, "убрать провод", null, new ImageBytes(source ?? TestImages.Png(4, 4), "image/png"), null,
            annotated is null ? null : new ImageBytes(annotated, "image/png"), [], "images/hero.png");

    private static async Task<string> StartAsync(ImageEditJobService service, string owner, string provider, int count = 1,
        byte[]? source = null, byte[]? annotated = null)
    {
        var quote = await service.QuoteAsync(owner, Project, Quote(provider, count), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await service.StartAsync(owner, Project, Input(quote.Value!.QuoteId, source, annotated), default);
        started.Value.Should().NotBeNull(started.Error);
        return started.Value!.JobId;
    }

    private static async Task<ImageEditJobDto> WaitDone(ImageEditJobService service, string owner, string jobId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = service.Get(owner, Project, jobId)!;
            if (job.Status is ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled)
                return job;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    [Fact]
    public async Task Higgsfield_ТратаПишетсяНаЗапустившего_ВКредитахПоКотировке()
    {
        var (higgsfield, _, _) = HiggsfieldImageEditorTests.Create();
        var service = Service(higgsfield);

        var jobId = await StartAsync(service, "user-b", "higgsfield", count: 2);
        var job = await WaitDone(service, "user-b", jobId);

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        job.Cost.Should().Be(new EditCost(3.0, ImageEditPriceUnits.Credits));
        var record = _spend.Records.Should().ContainSingle().Subject;
        record.OwnerId.Should().Be("user-b");
        record.ProjectId.Should().Be(Project);
        record.Provider.Should().Be("higgsfield");
        record.Source.Should().Be(SpendSources.Higgsfield);
        record.Label.Should().Be(ImageEditJobService.SpendLabel);
        record.CostCredits.Should().Be(3.0);
        record.CostUsd.Should().BeNull("кредиты с долларами не складываются");
        record.Generations.Should().Be(2);
        _broadcaster.ToOwnerCalls.Should().Contain(c => c.OwnerId == "user-b" && c.Message is ImageEditCompletedMessage);
        _broadcaster.ToOwnerCalls.Should().OnlyContain(c => c.OwnerId == "user-b");
    }

    [Fact]
    public async Task ДорисоватьЗаКрая_ПропорцииИзФормыДоходятДоДрайвера()
    {
        var (higgsfield, http, _) = HiggsfieldImageEditorTests.Create();
        var service = Service(higgsfield);
        var quote = await service.QuoteAsync("user-a", Project,
            new ImageEditQuoteRequest("higgsfield", "auto", EditMode.Fast, ImageEditOp.Outpaint, 1, false, 0, false, null, null),
            default);
        quote.Value.Should().NotBeNull(quote.Error);

        var started = await service.StartAsync("user-a", Project,
            Input(quote.Value!.QuoteId) with { AspectRatio = "16:9" }, default);
        started.Value.Should().NotBeNull(started.Error);
        var job = await WaitDone(service, "user-a", started.Value!.JobId);

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        var args = HiggsfieldImageEditorTests.GenerateArgs(HiggsfieldImageEditorTests.Launch(http))!;
        args["model"]!.GetValue<string>().Should().Be(HiggsfieldImageEditor.Outpaint);
        var ratio = args["aspect_ratio"]?.GetValue<string>();
        ratio.Should().Be("16:9", "иначе «Дорисовать 16:9» даёт квадрат");
    }

    [Fact]
    public async Task ОтказHiggsfield_FalНеВызывается_ПредложенаКотировкаСоседа()
    {
        var higgsfield = new ScriptedEditor("higgsfield", (_, _, _) =>
            Task.FromResult(new ImageEditResult(EditOutcome.InsufficientCredits, [], null, false, null, "нет кредитов")));
        var fal = new ScriptedEditor("fal", (_, _, _) => throw new InvalidOperationException("fal вызывать нельзя"));
        var service = Service(fal, higgsfield);

        var jobId = await StartAsync(service, "user-a", "higgsfield");
        var job = await WaitDone(service, "user-a", jobId);

        job.Status.Should().Be(ImageEditJobStatus.Failed);
        job.Provider.Should().Be("higgsfield");
        job.Outcome.Should().Be(EditOutcome.InsufficientCredits);
        job.Charged.Should().BeFalse();
        fal.Runs.Should().Be(0);
        higgsfield.Runs.Should().Be(1);
        _spend.Records.Should().BeEmpty();
        var failed = _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditFailedMessage>().Single();
        failed.RetryQuote!.Provider.Should().Be("fal");
    }

    [Fact]
    public async Task ОтменаDelete_ОстанавливаетЗадачуИШлётСобытиеОтмены()
    {
        var driverSawCancel = new TaskCompletionSource();
        var accepted = new TaskCompletionSource();
        var slow = new ScriptedEditor("higgsfield", async (_, progress, ct) =>
        {
            progress.Report(new EditProgress(EditStage.Queued));
            accepted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                driverSawCancel.TrySetResult();
                throw;
            }
            return null!;
        });
        var service = Service(slow);
        var jobId = await StartAsync(service, "user-a", "higgsfield", count: 2);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelled = await service.CancelAsync("user-a", Project, jobId, default);

        await driverSawCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled!.Status.Should().Be(ImageEditJobStatus.Cancelled);
        cancelled.Outcome.Should().Be(EditOutcome.Cancelled);
        // Higgsfield работу не останавливает: списание неизвестно, трата по котировке остаётся
        cancelled.Charged.Should().BeNull();
        _spend.Records.Should().ContainSingle(r => r.OwnerId == "user-a" && r.CostCredits == 3.0 && r.Generations == 2);
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditFailedMessage>()
            .Should().ContainSingle(m => m.JobId == jobId && m.Outcome == EditOutcome.Cancelled);
    }

    // C1 пересмотрен (хотфикс 2026-09-26): котировка без цены запуск не запрещает. Трата
    // ложится на запустившего при принятии с пометкой «сумма уточняется», фактическая сумма
    // из ответа поставщика догоняет её отдельной записью
    [Fact]
    public async Task Higgsfield_БезЦены_ЗапускИдёт_ТратаНаЗапустившегоСуммаДогоняется()
    {
        var higgsfield = new ScriptedEditor("higgsfield", (_, progress, _) =>
        {
            progress.Report(new EditProgress(EditStage.Queued));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [new EditedImage(TestImages.Png(2, 2), "image/png")],
                new EditCost(3.0, ImageEditPriceUnits.Credits), true, "r", null));
        }, price: null);
        var service = Service(higgsfield);

        var quote = await service.QuoteAsync("user-a", Project, Quote("higgsfield", count: 2), default);
        quote.Value!.Estimate.Amount.Should().BeNull();
        var job = await WaitDone(service, "user-a", await StartAsync(service, "user-a", "higgsfield", count: 2));

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        higgsfield.Runs.Should().Be(1);
        job.Cost.Should().Be(new EditCost(3.0, ImageEditPriceUnits.Credits));
        _spend.Records.Should().HaveCount(2);
        var accepted = _spend.Records.First();
        accepted.OwnerId.Should().Be("user-a");
        accepted.Provider.Should().Be("higgsfield");
        accepted.Generations.Should().Be(2);
        accepted.CostCredits.Should().BeNull();
        accepted.Label.Should().Be(ImageEditJobService.SpendLabelPendingAmount);
        var topUp = _spend.Records.Last();
        topUp.OwnerId.Should().Be("user-a");
        topUp.Generations.Should().Be(0);
        topUp.CostCredits.Should().Be(3.0);
        topUp.Label.Should().Be(ImageEditJobService.SpendLabel);
    }

    // Сосед-Higgsfield без цены теперь предлагается повтором: по такой котировке можно запуститься
    [Fact]
    public async Task ПовторЧерезHiggsfieldБезЦены_Предлагается()
    {
        var fal = new ScriptedEditor("fal", (_, _, _) =>
            Task.FromResult(new ImageEditResult(EditOutcome.Failed, [], null, false, null, "сбой")));
        var higgsfield = new ScriptedEditor("higgsfield", (_, _, _) => throw new InvalidOperationException(), price: null);
        var service = Service(fal, higgsfield);

        var job = await WaitDone(service, "user-a", await StartAsync(service, "user-a", "fal"));

        job.Status.Should().Be(ImageEditJobStatus.Failed);
        var retry = _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditFailedMessage>().Single().RetryQuote;
        retry!.Provider.Should().Be("higgsfield");
        retry.Estimate.Amount.Should().BeNull();
        higgsfield.Runs.Should().Be(0);
    }

    // Потолки одновременных задач на Higgsfield не действуют: работа идёт у поставщика
    [Fact]
    public async Task ПотолокЗадач_НаHiggsfieldНеДействует()
    {
        var gate = new TaskCompletionSource();
        var editor = new ScriptedEditor("higgsfield", async (_, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new ImageEditResult(EditOutcome.Failed, [], null, false, null, "x");
        });
        var service = Service(editor);

        for (var i = 0; i < ImageEditJobService.MaxJobsPerInstance + 1; i++)
            await StartAsync(service, "owner", "higgsfield");

        gate.SetResult();
    }

    // Сумма неизвестна (fal без прайса) — запись всё равно ложится при принятии, на
    // запустившего и с числом вариантов; фактическая цена догоняет её отдельной записью
    [Fact]
    public async Task НеизвестнаяСумма_ТратаВсёРавноЗаписанаИСуммаДогоняется()
    {
        var editor = new ScriptedEditor("fal", (_, progress, _) =>
        {
            progress.Report(new EditProgress(EditStage.Running));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                [new EditedImage(TestImages.Png(2, 2), "image/png"), new EditedImage(TestImages.Png(2, 2), "image/png")],
                new EditCost(0.16, ImageEditPriceUnits.Usd), true, "r", null));
        }, price: null);
        var service = Service(editor);

        var job = await WaitDone(service, "user-a", await StartAsync(service, "user-a", "fal", count: 2));

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        _spend.Records.Should().HaveCount(2);
        var accepted = _spend.Records.First();
        accepted.OwnerId.Should().Be("user-a");
        accepted.Generations.Should().Be(2);
        accepted.CostUsd.Should().BeNull();
        var topUp = _spend.Records.Last();
        topUp.OwnerId.Should().Be("user-a");
        topUp.Generations.Should().Be(0);
        topUp.CostUsd.Should().Be(0.16);
    }

    [Fact]
    public async Task ЧужойПользователь_НеЧитаетНеОтменяетНеСкачивает()
    {
        var gate = new TaskCompletionSource();
        var editor = new ScriptedEditor("fal", async (_, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new ImageEditResult(EditOutcome.Ok, [new EditedImage(TestImages.Png(2, 2), "image/png")], null, true, "r", null);
        });
        var service = Service(editor);
        var jobId = await StartAsync(service, "owner", "fal");

        service.Get("stranger", Project, jobId).Should().BeNull();
        (await service.CancelAsync("stranger", Project, jobId, default)).Should().BeNull();
        service.Get("owner", "other-project", jobId).Should().BeNull();

        gate.SetResult();
        var job = await WaitDone(service, "owner", jobId);
        job.Status.Should().Be(ImageEditJobStatus.Completed);
        editor.SawCancel.Should().BeFalse();
        service.OpenVariant("stranger", Project, jobId, 1).Should().BeNull();
        service.OpenVariant("owner", Project, jobId, 1).Should().NotBeNull();
    }

    [Fact]
    public async Task ЧужаяКотировка_НеЗапускается()
    {
        var service = Service(new ScriptedEditor("fal", (_, _, _) => throw new InvalidOperationException()));
        var quote = await service.QuoteAsync("owner", Project, Quote("fal"), default);

        var started = await service.StartAsync("stranger", Project, Input(quote.Value!.QuoteId), default);

        started.ErrorCode.Should().Be(ImageEditErrorCodes.QuoteNotFound);
    }

    [Fact]
    public async Task ЗапечённыеПометки_ФайлОригиналаНеМеняется_ВариантНовымФайлом()
    {
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(root, "images"));
        var originalPath = Path.Combine(root, "images", "hero.png");
        var original = TestImages.Png(6, 6, tail: 1);
        await File.WriteAllBytesAsync(originalPath, original);

        ImageEditRequest? seen = null;
        var result = TestImages.Png(6, 6, tail: 9);
        var editor = new ScriptedEditor("fal", (req, _, _) =>
        {
            seen = req;
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [new EditedImage(result, "image/png")], null, true, "r", null));
        });
        var service = Service(editor);

        var jobId = await StartAsync(service, "owner", "fal",
            source: await File.ReadAllBytesAsync(originalPath), annotated: TestImages.Png(6, 6, tail: 5));
        await WaitDone(service, "owner", jobId);
        var saved = new ImageEditSaver(new VersionedImageStore()).Save(root,
            new ImageEditSaveRequest(jobId, 1, "images/hero.png", null, null),
            service.OpenVariant("owner", Project, jobId, 1)!);

        seen!.Source!.Bytes.Should().Equal(original);
        seen.References.Should().ContainSingle(r => r.Label == EditRequestComposer.AnnotatedLabel);
        (await File.ReadAllBytesAsync(originalPath)).Should().Equal(original);
        saved.Value!.Path.Should().Be("images/hero.v2.png");
        (await File.ReadAllBytesAsync(Path.Combine(root, "images", "hero.v2.png"))).Should().Equal(result);
    }

    [Fact]
    public async Task ПотолокЗадачВладельца_429()
    {
        var gate = new TaskCompletionSource();
        var editor = new ScriptedEditor("fal", async (_, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new ImageEditResult(EditOutcome.Failed, [], null, false, null, "x");
        });
        var service = Service(editor);
        await StartAsync(service, "owner", "fal");
        await StartAsync(service, "owner", "fal");

        var quote = await service.QuoteAsync("owner", Project, Quote("fal"), default);
        var third = await service.StartAsync("owner", Project, Input(quote.Value!.QuoteId), default);

        third.ErrorCode.Should().Be(ImageEditErrorCodes.TooManyJobs);
        gate.SetResult();
    }

    // Драйвер со сценарием: цена по ориентиру (по умолчанию 1.5 за вариант, null — прайса
    // нет), счётчик запусков
    private sealed class ScriptedEditor(
        string key, Func<ImageEditRequest, IProgress<EditProgress>, CancellationToken, Task<ImageEditResult>> run,
        double? price = 1.5) : IImageEditor
    {
        private readonly ImageEditModelInfo _model = new("m-" + Guid.NewGuid().ToString("N")[..4], "M",
            new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true),
            price is { } p ? new ImageEditPriceHint(p, ImageEditPriceUnits.Credits, "image") : null);

        private int _runs;
        public int Runs => _runs;
        public bool SawCancel { get; private set; }

        public string Key => key;
        public string Label => key;
        public string PriceUnit => key == "higgsfield" ? ImageEditPriceUnits.Credits : ImageEditPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [_model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => _model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            using var _ = ct.Register(() => SawCancel = true);
            return await run(req, progress, ct);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
