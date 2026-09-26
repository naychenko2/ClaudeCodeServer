using System.Collections.Concurrent;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Поставщик «Локальные модели» (ADR-018, раздел «Локальные модели») на фейковом шве
// ILocalImageMedia: котировка без денег, маска образцом, скрытие без ComfyUI, нулевая трата
public class LocalImageEditorTests : IDisposable
{
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-local-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly MemorySpendStore _spend = new();
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    internal sealed class FakeMedia : ILocalImageMedia
    {
        public bool Available { get; set; } = true;
        public int? Queue { get; set; } = 3;
        public ConcurrentQueue<LocalImageRequest> Submitted { get; } = new();
        public List<string> Cancelled { get; } = [];
        public LocalImageSubmitted? Refuse { get; set; }

        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult(Queue);

        public int? EtaSeconds(LocalImageOp op, int count, int images) => op switch
        {
            LocalImageOp.Generate when images == 0 => 40 * count,
            LocalImageOp.FaceDetail => 25,
            _ => 60 + 10 * (images - 1),
        };

        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct)
        {
            if (Refuse is { } refuse) return Task.FromResult(refuse);
            Submitted.Enqueue(request);
            return Task.FromResult(new LocalImageSubmitted("t" + Submitted.Count, 0, 45, null));
        }

        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct)
        {
            var request = Submitted.ElementAt(int.Parse(ticket[1..]) - 1);
            var files = Enumerable.Range(0, request.Count)
                .Select(i => new LocalImageFile(TestImages.Png(8, 8, (byte)i), "image/png"))
                .ToList();
            return Task.FromResult(new LocalImagePoll(LocalImageState.Completed, null, files, null));
        }

        public Task<bool> CancelAsync(string ticket, CancellationToken ct)
        {
            Cancelled.Add(ticket);
            return Task.FromResult(true);
        }
    }

    private ImageEditJobService Service(params IImageEditor[] editors)
    {
        var service = new ImageEditJobService(editors, new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, _spend);
        _services.Add(service);
        return service;
    }

    private static LocalImageEditor Editor(FakeMedia media) => new(media) { PollInterval = TimeSpan.FromMilliseconds(1) };

    private static ImageEditQuoteRequest Quote(ImageEditOp op, int count = 1, bool mask = false) =>
        new(LocalImageEditor.ProviderKey, "auto", EditMode.Auto, op, count, mask, 0, false, null, null);

    private static async Task<ImageEditJobDto> WaitDone(ImageEditJobService service, string jobId)
    {
        for (var i = 0; i < 500; i++)
        {
            var job = service.Get("user-a", Project, jobId)!;
            if (job.Status is ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled)
                return job;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    private async Task<ImageEditJobDto> RunAsync(ImageEditJobService service, ImageEditQuoteRequest quoteRequest,
        ImageEditJobInput input)
    {
        var quote = await service.QuoteAsync("user-a", Project, quoteRequest, default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await service.StartAsync("user-a", Project, input with { QuoteId = quote.Value!.QuoteId }, default);
        started.Value.Should().NotBeNull(started.Error);
        return await WaitDone(service, started.Value!.JobId);
    }

    private static ImageEditJobInput Input(string prompt, byte[]? mask = null) =>
        new("", prompt, null, new ImageBytes(TestImages.Png(8, 8), "image/png"),
            mask is null ? null : new ImageBytes(mask, "image/png"), null, [], "images/hero.png");

    [Fact]
    public async Task Котировка_Бесплатно_СВременемИОчередью()
    {
        var media = new FakeMedia { Queue = 3 };
        var service = Service(Editor(media));

        var quote = await service.QuoteAsync("user-a", Project, Quote(ImageEditOp.Generate, count: 2), default);

        quote.Value.Should().NotBeNull(quote.Error);
        quote.Value!.Provider.Should().Be("local");
        quote.Value.Model.Should().Be(LocalImageEditor.QwenImage);
        quote.Value.Estimate.Should().Be(new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false,
            ImageEditEstimateSources.Provider, EtaSeconds: 80, QueueLength: 3));
        quote.Value.ExpectedSeconds.Should().Be(80, "время для процентов — то же, что в котировке");
    }

    [Fact]
    public async Task Котировка_ComfyUiНеОтвечает_ПоставщикНедоступен()
    {
        var service = Service(Editor(new FakeMedia { Queue = null }));

        var quote = await service.QuoteAsync("user-a", Project, Quote(ImageEditOp.Edit), default);

        quote.ErrorCode.Should().Be(ImageEditErrorCodes.ProviderUnavailable);
    }

    [Theory]
    [InlineData(true)]   // тумблер выключен или ComfyUI не отвечает
    [InlineData(false)]  // нет шва: подсистема images выключена
    public void Каталог_БезComfyUi_ПоставщикСкрыт(bool withSeam)
    {
        IImageEditor editor = withSeam ? new LocalImageEditor(new FakeMedia { Available = false }) : new LocalImageEditor(null);

        var catalog = ImageEditCatalog.Build([editor], null, null);

        catalog.Providers.Should().BeEmpty();
        catalog.Reason.Should().Be(ImageEditCatalogReasons.NoProviderConfigured);
    }

    [Fact]
    public void Каталог_ЧестныеВозможности_МаскаТолькоОбразцом()
    {
        var catalog = ImageEditCatalog.Build([new LocalImageEditor(new FakeMedia())], "local", null);

        var provider = catalog.Providers.Should().ContainSingle().Subject;
        provider.Label.Should().Be("Локальные модели");
        provider.PriceUnit.Should().Be(ImageEditPriceUnits.Free);
        var qwen = provider.Models.Single(m => m.Id == LocalImageEditor.QwenImage).Caps!;
        qwen.Mask.Should().Be(MaskSupport.AsReference, "отдельного канала маски у Qwen-Image нет");
        qwen.MaxInputSide.Should().Be(1664);
        provider.Models.Single(m => m.Id == LocalImageEditor.FaceDetailer).Caps!.Ops
            .Should().Equal(ImageEditOp.EnhanceFaces);
    }

    [Fact]
    public async Task Правка_МаскаУходитОбразцом_АНеКаналом()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true), Input("перекрась в синий", mask));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Op.Should().Be(LocalImageOp.Edit);
        sent.EraseMask.Should().BeNull();
        sent.Images.Should().HaveCount(2);
        sent.Images[1].Should().Equal(mask, "маска — вторая картинка после холста");
        sent.Prompt.Should().Contain("маска: белое — область, которую менять");
    }

    // Живой прогон 2026-09-26: маску-образец Qwen-Image прочла как «убери фон». Стирание уходит
    // маской стирания (серая заливка на холсте), одним холстом — без маски среди образцов
    [Fact]
    public async Task УдалиКистью_МаскаСтирания_ОдинХолст()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true), Input("удали", mask));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.EraseMask.Should().Equal(mask);
        sent.Images.Should().ContainSingle("размеченная копия и маска-образец стираемое только показали бы");
        sent.Prompt.Should().StartWith("удали").And.Contain(LocalImageEditor.EraseNote);
    }

    [Fact]
    public async Task Правка_ВариантыИдутПрогонамиПоОчереди()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, count: 3), Input("сделай вечер"));

        job.Variants.Should().Equal(1, 2, 3);
        media.Submitted.Should().HaveCount(3).And.OnlyContain(r => r.Count == 1);
    }

    [Fact]
    public async Task УлучшитьЛица_ОдинПрогонFaceDetailer()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));

        var job = await RunAsync(service, Quote(ImageEditOp.EnhanceFaces), Input(""));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        job.Model.Should().Be(LocalImageEditor.FaceDetailer);
        media.Submitted.Should().ContainSingle().Which.Op.Should().Be(LocalImageOp.FaceDetail);
    }

    [Fact]
    public async Task Трата_ЗаписьСНулевойСуммой_НаЗапустившего()
    {
        var service = Service(Editor(new FakeMedia()));

        var job = await RunAsync(service, Quote(ImageEditOp.Generate, count: 2), Input("кот"));

        job.Cost.Should().Be(new EditCost(0, ImageEditPriceUnits.Free));
        job.Charged.Should().BeFalse();
        var record = _spend.Records.Should().ContainSingle().Subject;
        record.OwnerId.Should().Be("user-a");
        record.Provider.Should().Be("local");
        record.CostUsd.Should().Be(0);
        record.CostCredits.Should().BeNull();
        record.Generations.Should().Be(2);
    }

    [Fact]
    public async Task ОчередьЗанята_ОтказБезТраты()
    {
        var media = new FakeMedia { Refuse = LocalImageSubmitted.Fail("Очередь занята", busy: true) };
        var service = Service(Editor(media));

        var job = await RunAsync(service, Quote(ImageEditOp.Generate), Input("кот"));

        job.Status.Should().Be(ImageEditJobStatus.Failed);
        job.Outcome.Should().Be(EditOutcome.Unavailable);
        job.Charged.Should().BeFalse();
        _spend.Records.Should().BeEmpty("задача не принята — учитывать нечего");
    }
}
