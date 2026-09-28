using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
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

    private ImageEditJobService Service(params IImageEditor[] editors) => Service(null, editors);

    private ImageEditJobService Service(TimeProvider? time, params IImageEditor[] editors)
    {
        var service = new ImageEditJobService(editors, new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster, time);
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

    // ADR-018 §2: чат картинки и инициатор идут из входа в задачу, её события и запись траты —
    // «Модели и расход» различает, сколько потратил агент
    [Theory]
    [InlineData(ImageEditInitiator.Human, SpendInitiators.Human)]
    [InlineData(ImageEditInitiator.Agent, SpendInitiators.Agent)]
    public async Task ЧатИИнициатор_ДоходятДоТратыDtoИСобытий(ImageEditInitiator initiator, string spendInitiator)
    {
        var (higgsfield, _, _) = HiggsfieldImageEditorTests.Create();
        var service = Service(higgsfield);
        var quote = await service.QuoteAsync("user-b", Project, Quote("higgsfield", 2), default);
        var started = await service.StartAsync("user-b", Project,
            Input(quote.Value!.QuoteId) with { ChatSessionId = "chat-1", Initiator = initiator }, default);

        var job = await WaitDone(service, "user-b", started.Value!.JobId);

        job.ChatSessionId.Should().Be("chat-1");
        job.Initiator.Should().Be(initiator);
        job.Count.Should().Be(2);
        job.Estimate.Should().NotBeNull();
        var record = _spend.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("chat-1");
        record.Initiator.Should().Be(spendInitiator);
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditCompletedMessage>().Should()
            .ContainSingle().Which.Should().Match<ImageEditCompletedMessage>(m => m.ChatSessionId == "chat-1" && m.Initiator == initiator);
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

    // Полоса прогресса: «прошло» считается часами бэкенда от перехода в Running, наружу — уже
    // разницей, а не меткой; вне Running его нет
    [Fact]
    public async Task Прогресс_ПрошлоСПереходаВRunning_ПоЧасамБэкенда()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var running = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var editor = new ScriptedEditor("fal", async (_, progress, ct) =>
        {
            progress.Report(new EditProgress(EditStage.Queued, 2, Run: 2, Runs: 3, EtaSeconds: 40));
            time.Advance(TimeSpan.FromSeconds(30));
            progress.Report(new EditProgress(EditStage.Running, null, Run: 2, Runs: 3, EtaSeconds: 40));
            running.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return new ImageEditResult(EditOutcome.Ok, [new EditedImage(TestImages.Png(2, 2), "image/png")], null, true, "r", null);
        });
        var service = Service(time, editor);
        var jobId = await StartAsync(service, "owner", "fal");
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(7));
        var job = service.Get("owner", Project, jobId)!;

        job.Status.Should().Be(ImageEditJobStatus.Running);
        job.RunElapsedSeconds.Should().Be(7, "отсчёт — с перехода в Running, а не с постановки в очередь");
        (job.Run, job.Runs, job.EtaSeconds).Should().Be((2, 3, 40));
        var progress = _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditProgressMessage>().ToList();
        progress.First().RunElapsedSeconds.Should().BeNull("в очереди прогон ещё не идёт");
        progress.Last().Should().Match<ImageEditProgressMessage>(m =>
            m.Run == 2 && m.Runs == 3 && m.EtaSeconds == 40 && m.RunElapsedSeconds == 0);

        gate.SetResult();
        (await WaitDone(service, "owner", jobId)).RunElapsedSeconds.Should().BeNull();
    }

    // ── Варианты по готовности (EditProgress.Ready) ──────────────────────────

    private static EditedImage Variant(byte shade) => new(TestImages.Png(4, 4, shade), "image/png");

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(what);
    }

    [Fact]
    public async Task ПоГотовности_ПервыйВариантВиденПокаЗадачаИдёт_ФиналБезДублей()
    {
        var gate = new TaskCompletionSource();
        var ready = new List<ImageEditJobDto>();
        EditedImage[] images = [Variant(1), Variant(2), Variant(3)];
        var editor = new ScriptedEditor("fal", async (_, progress, ct) =>
        {
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 3, Ready: [images[0]]));
            await gate.Task.WaitAsync(ct);
            progress.Report(new EditProgress(EditStage.Downloading, Run: 2, Runs: 3, Ready: [images[1]]));
            progress.Report(new EditProgress(EditStage.Downloading, Run: 3, Runs: 3, Ready: [images[2]]));
            return new ImageEditResult(EditOutcome.Ok, images, null, true, "r", null);
        });
        var service = Service(editor);
        service.VariantReady += (_, dto) => { lock (ready) ready.Add(dto); return Task.CompletedTask; };
        var jobId = await StartAsync(service, "owner", "fal", count: 3);

        await WaitUntil(() => service.Get("owner", Project, jobId)!.Variants.Count == 1, "первый вариант не отдан");
        var running = service.Get("owner", Project, jobId)!;
        running.Status.Should().NotBe(ImageEditJobStatus.Completed, "задача ещё идёт");
        running.Variants.Should().Equal(1);
        service.OpenVariant("owner", Project, jobId, 1)!.Bytes.Should().Equal(images[0].Bytes);
        service.OpenVariant("owner", Project, jobId, 2).Should().BeNull("второго ещё нет");

        gate.SetResult();
        var done = await WaitDone(service, "owner", jobId);

        done.Status.Should().Be(ImageEditJobStatus.Completed);
        done.Variants.Should().Equal(1, 2, 3);
        service.OpenVariant("owner", Project, jobId, 3)!.Bytes.Should().Equal(images[2].Bytes);
        lock (ready) ready.Select(d => d.Variants.Count).Should().Equal(1, 2, 3);
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageEditCompletedMessage>()
            .Should().ContainSingle().Which.Variants.Should().Equal(1, 2, 3);
    }

    // Разбор готового варианта медленный (приведение размера), а драйвер уже вернул итог: Finished
    // обязан дождаться очереди — иначе нить закрыла бы запуск раньше, чем узнала о последнем варианте
    [Fact]
    public async Task ПоГотовности_ПоследнийVariantReadyРаньшеFinished()
    {
        var events = new List<string>();
        EditedImage[] images = [Variant(1), Variant(2)];
        var editor = new ScriptedEditor("fal", (_, progress, _) =>
        {
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 2, Ready: [images[0]]));
            progress.Report(new EditProgress(EditStage.Downloading, Run: 2, Runs: 2, Ready: [images[1]]));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, images, null, true, "r", null));
        });
        var service = new ImageEditJobService([editor], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster, raster: new SlowRaster());
        _services.Add(service);
        service.VariantReady += (_, dto) => { lock (events) events.Add($"ready {dto.Variants.Count}"); return Task.CompletedTask; };
        service.Finished += (_, dto) => { lock (events) events.Add($"finished {dto.Variants.Count}"); return Task.CompletedTask; };

        var jobId = await StartAsync(service, "owner", "fal", count: 2);
        var done = await WaitDone(service, "owner", jobId);

        done.Variants.Should().Equal(1, 2);
        await WaitUntil(() => { lock (events) return events.Count == 3; }, "события не пришли");
        lock (events) events.Should().Equal("ready 1", "ready 2", "finished 2");
    }

    [Fact]
    public async Task ПоГотовности_ОтменаПосерединеОставляетГотовыйВариант()
    {
        var editor = new ScriptedEditor("fal", async (_, progress, ct) =>
        {
            progress.Report(new EditProgress(EditStage.Downloading, Run: 1, Runs: 3, Ready: [Variant(1)]));
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        });
        var service = Service(editor);
        var jobId = await StartAsync(service, "owner", "fal", count: 3);
        await WaitUntil(() => service.Get("owner", Project, jobId)!.Variants.Count == 1, "первый вариант не отдан");

        var cancelled = await service.CancelAsync("owner", Project, jobId, default);

        cancelled!.Status.Should().Be(ImageEditJobStatus.Cancelled);
        cancelled.Variants.Should().Equal(1);
        service.OpenVariant("owner", Project, jobId, 1).Should().NotBeNull("отмена не стирает готовое");
    }

    // Растр, у которого чтение заголовка тянется: разбор варианта заметно дольше, чем драйвер
    private sealed class SlowRaster : IImageRaster
    {
        private readonly SkiaImageRaster _inner = new();

        public RasterProbe? Probe(byte[] data)
        {
            Thread.Sleep(100);
            return _inner.Probe(data);
        }

        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null) =>
            _inner.Apply(data, ops, encode);
        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => _inner.Encode(data, encode);
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => _inner.ResizeMask(mask, width, height);
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => _inner.EraseMasked(image, mask);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
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
