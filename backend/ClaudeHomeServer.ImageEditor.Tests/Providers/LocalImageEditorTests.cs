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
    private const string Owner = "0a1b2c3d-0000-0000-0000-000000000001";
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
        public bool Configured { get; set; } = true;
        public int? Queue { get; set; } = 3;
        public ConcurrentQueue<LocalImageRequest> Submitted { get; } = new();
        public List<string> Cancelled { get; } = [];
        public LocalImageSubmitted? Refuse { get; set; }
        // ETA прогона в ответе на постановку; RunningFirst — первый опрос билета отвечает Running
        public int? SubmitEta { get; set; } = 45;
        public bool RunningFirst { get; set; }
        // Постоянная часть времени генерации по тексту: с ней батч дешевле прогонов по одному
        public int GenerateBase { get; set; }
        // Билеты с этого номера висят в очереди вечно (чужие прогоны заняли видеокарту)
        public int? QueuedFrom { get; set; }
        // Сходство лица, которое адаптер возвращает в каждом файле результата
        public double? Similarity { get; set; }
        // Снимок очереди GPU, который адаптер возвращает в каждом опросе (шаг 28)
        public ClaudeHomeServer.Services.Media.LocalGpuQueueInfo? Gpu { get; set; }
        private readonly ConcurrentDictionary<string, bool> _seen = new();

        public IReadOnlyList<string>? Loras { get; set; } = [];

        public Task<int?> QueueLengthAsync(CancellationToken ct) => Task.FromResult(Queue);

        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult(Loras);

        public int? EtaSeconds(LocalImageOp op, int count, int images) => op switch
        {
            LocalImageOp.Generate when images == 0 => GenerateBase + 40 * count,
            LocalImageOp.FaceDetail => 25,
            _ => 60 + 10 * (images - 1),
        };

        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct)
        {
            if (Refuse is { } refuse) return Task.FromResult(refuse);
            Submitted.Enqueue(request);
            return Task.FromResult(new LocalImageSubmitted("t" + Submitted.Count, 0, SubmitEta, null));
        }

        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct)
        {
            if (RunningFirst && _seen.TryAdd(ticket, true))
                return Task.FromResult(new LocalImagePoll(LocalImageState.Running, null, [], null, Gpu: Gpu));
            if (QueuedFrom is { } from && int.Parse(ticket[1..]) >= from)
                return Task.FromResult(new LocalImagePoll(LocalImageState.Queued, 5, [], null, Gpu: Gpu));
            var request = Submitted.ElementAt(int.Parse(ticket[1..]) - 1);
            var files = Enumerable.Range(0, request.Count)
                .Select(i => new LocalImageFile(TestImages.Png(8, 8, (byte)i), "image/png", Similarity))
                .ToList();
            return Task.FromResult(new LocalImagePoll(LocalImageState.Completed, null, files, null, Gpu: Gpu));
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
        quote.Value.Model.Should().Be(LocalImageEditor.QwenImageTurbo, "авто-выбор — быстрая модель");
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
    [InlineData(true)]   // тумблер LocalMedia:Enabled выключен
    [InlineData(false)]  // нет шва: подсистема images выключена
    public void Каталог_НеЗаведён_ПоставщикСкрыт(bool withSeam)
    {
        // Лежащий ComfyUI при включённом тумблере не скрывает, а помечает — см. ImageEditCatalogTests
        IImageEditor editor = withSeam
            ? new LocalImageEditor(new FakeMedia { Configured = false, Available = false })
            : new LocalImageEditor(null);

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
        qwen.Mask.Should().Be(MaskSupport.Veil, "вуаль: место правки закрашивается серым, а не маской-образцом");
        qwen.MaxInputSide.Should().Be(1664);
        provider.Models.Single(m => m.Id == LocalImageEditor.FaceDetailer).Caps!.Ops
            .Should().Equal(ImageEditOp.EnhanceFaces);
    }

    // Вуаль (ADR-028, решение 12): Qwen-Image перерисовывает белую маску-образец, поэтому место
    // правки уходит серой вуалью (VeilMask) на копии холста, а не образцом; склейка «вне маски —
    // исходник» — по той же маске (KeepMask)
    [Fact]
    public async Task Правка_Кисть_МаскаВуалью_АНеОбразцом()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true), Input("перекрась в синий", mask));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Op.Should().Be(LocalImageOp.Edit);
        sent.EraseMask.Should().BeNull();
        sent.Images.Should().ContainSingle("вуаль: маска не образец — в запросе только холст");
        sent.VeilMask.Should().Equal(mask, "вуаль: место правки — серая заливка на копии холста");
        sent.KeepMask.Should().Equal(mask, "склейка «вне маски — исходник» — по той же маске");
        sent.Prompt.Should().Contain(EditRequestComposer.VeilHead, "текст человека оборачивается вуалью");
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
        sent.KeepMask.Should().BeNull("стирание и склейка «вне маски» несовместимы — адаптер бы отказал");
        sent.Images.Should().ContainSingle("размеченная копия и маска-образец стираемое только показали бы");
        sent.Prompt.Should().StartWith("удали").And.Contain(LocalImageEditor.EraseNote);
    }

    // (в) «Удали» у вуали: маска — каналом (req.Mask), а не образцом; мутация «взять только из
    // References» → EraseMask = null → кейс стирания не сработал бы → красный. Удаление не вуаль
    [Fact]
    public async Task Вуаль_Удали_МаскаСтирания_ИзКанала_АНеВуаль()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true), Input("удали", mask));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.EraseMask.Should().Equal(mask, "стирание берёт маску из канала, а не из References");
        sent.VeilMask.Should().BeNull("«удали» — стирание серым, вуаль не нужна");
    }

    // Вуаль: обычная правка кистью — маска каналом, и она же вуаль (VeilMask) и склейка (KeepMask)
    [Fact]
    public async Task Вуаль_Кисть_VeilMask_ИKeepMask_ИзКанала()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true), Input("перекрась в синий", mask));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.VeilMask.Should().Equal(mask, "вуаль: место правки закрашивается серым на отдельной копии холста");
        sent.KeepMask.Should().Equal(mask, "склейка «вне маски — исходник» берёт ту же маску из канала");
        sent.EraseMask.Should().BeNull("обычная правка — не стирание");
    }

    // Пункт 7 (ADR-028): путь с размеченной копией Артём с вуалью НЕ проверял — вуаль не включаем
    // (VeilMask = null, как раньше AsReference); склейки тоже нет (KeepMask = null)
    [Fact]
    public async Task Вуаль_СРазмеченнойКопией_ВуальНеВключается()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var mask = TestImages.Png(8, 8, 7);
        var annotated = TestImages.Png(8, 8, 8);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, mask: true),
            new ImageEditJobInput("", "перекрась", null, new ImageBytes(TestImages.Png(8, 8), "image/png"),
                new ImageBytes(mask, "image/png"), new ImageBytes(annotated, "image/png"), [], "images/hero.png"));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.VeilMask.Should().BeNull("путь с размеченной копией: вуаль не проверена — не включаем");
        sent.KeepMask.Should().BeNull("размеченная копия: склейки «вне маски» нет");
    }

    // Живой прогон 2026-09-28 (w15-*): серые поля Qwen-Image достраивает без швов — дорисовка в каталоге
    [Fact]
    public void Каталог_ДорисовкаЗаКраяУQwen()
    {
        var editor = new LocalImageEditor(new FakeMedia());

        editor.Models.Single(m => m.Id == LocalImageEditor.QwenImage).Caps.Ops.Should().Contain(ImageEditOp.Outpaint);
        editor.Models.Single(m => m.Id == LocalImageEditor.QwenImageTurbo).Caps.Ops.Should().Contain(ImageEditOp.Outpaint);
        editor.PickModel(ImageEditOp.Outpaint, EditMode.Auto, new EditTraits(false, 0, false))!.Id.Should().Be(LocalImageEditor.QwenImageTurbo);
    }

    [Fact]
    public void Каталог_ДвеМоделиQwen_ТурбоПервая_ПодписиСРежимом()
    {
        var editor = new LocalImageEditor(new FakeMedia());

        editor.Models.Select(m => m.Id).Should().Equal("qwen-image-2.1-turbo", "qwen-image-2.1", "face-detailer");
        editor.Models[0].Label.Should().Be("Qwen-Image 2.1 Turbo — быстро");
        editor.Models[1].Label.Should().Be("Qwen-Image 2.1 — качественно");
        foreach (var op in new[] { ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint })
            editor.PickModel(op, EditMode.Auto, new EditTraits(false, 0, false))!.Id.Should().Be("qwen-image-2.1-turbo");
        editor.PickModel(ImageEditOp.EnhanceFaces, EditMode.Auto, new EditTraits(false, 0, false))!.Id.Should().Be("face-detailer");
    }

    [Fact]
    public async Task Запуск_ВыбраннаяМодель_УходитВШов()
    {
        foreach (var model in new[] { LocalImageEditor.QwenImage, LocalImageEditor.QwenImageTurbo })
        {
            var media = new FakeMedia();
            var editor = new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) };
            var req = new ImageEditRequest(ImageEditOp.Generate, "котик", null, null, [], 1, "1:1", null, model, null);

            var result = await editor.RunAsync(req, new Progress<EditProgress>(), default);

            result.Outcome.Should().Be(EditOutcome.Ok);
            media.Submitted.Single().Model.Should().Be(model);
        }
    }

    [Fact]
    public async Task ДорисовкаЗаКрая_ПоляПодПропорцию_ОдинХолстБезОбразцов()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var input = Input("") with
        {
            AspectRatio = "16:9",
            References = [new ReferenceImage(TestImages.Png(4, 4), "image/png", ReferenceRole.Style, null)],
        };

        var job = await RunAsync(service, Quote(ImageEditOp.Outpaint, count: 2), input);

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        media.Submitted.Should().HaveCount(2, "вариант — прогон");
        var sent = media.Submitted.First();
        sent.Op.Should().Be(LocalImageOp.Edit);
        // 8×8 до 16:9 — ширина 14, по 3 px слева и справа
        sent.Pad.Should().Be(new LocalImagePad(3, 0, 3, 0));
        sent.KeepMask.Should().BeNull("дорисовка за края — холст расширяется, «вне маски» нечего склеивать");
        sent.Images.Should().ContainSingle("образец другой пропорции сбил бы модель с полей");
        sent.Prompt.Should().Be(LocalImageEditor.OutpaintNote);
    }

    [Theory]
    [InlineData(1024, 1024, "16:9", 398, 0, 398, 0)]
    [InlineData(1024, 1024, "9:16", 0, 398, 0, 398)]
    [InlineData(1000, 500, "1:1", 0, 250, 0, 250)]
    [InlineData(9, 8, "16:9", 2, 0, 3, 0)]       // нечётный пиксель — правой стороне
    [InlineData(800, 600, "4:3", 200, 150, 200, 150)] // пропорция уже та — холст в полтора раза
    [InlineData(800, 600, null, 200, 150, 200, 150)]
    public void Поля_ПодПропорцию(int w, int h, string? aspect, int left, int top, int right, int bottom) =>
        OutpaintSpec.ForAspect(w, h, aspect).Should().Be(new OutpaintSpec(left, top, right, bottom));

    [Theory]
    [InlineData("16x9")]
    [InlineData("0:1")]
    [InlineData("a:b")]
    public void Поля_НечитаемаяПропорция_Null(string aspect) =>
        OutpaintSpec.ForAspect(100, 100, aspect).Should().BeNull();

    [Fact]
    public async Task Правка_ВариантыИдутПрогонамиПоОчереди()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, count: 3), Input("сделай вечер"));

        job.Variants.Should().Equal(1, 2, 3);
        media.Submitted.Should().HaveCount(3).And.OnlyContain(r => r.Count == 1);
        media.Submitted.Should().OnlyContain(r => r.KeepMask == null, "без маски — «вне маски» склеивать нечего, запрос прежний");
    }

    // Полоса прогресса: каждая стадия несёт номер прогона, их число и ETA из ответа на постановку
    [Fact]
    public async Task Прогресс_НесётНомерПрогонаЧислоИEta()
    {
        var media = new FakeMedia { SubmitEta = 52, RunningFirst = true };
        var reports = new List<EditProgress>();
        var request = new ImageEditRequest(ImageEditOp.Edit, "сделай вечер", new ImageBytes(TestImages.Png(8, 8), "image/png"),
            null, [], 2, null, null, LocalImageEditor.QwenImage, null);

        var result = await Editor(media).RunAsync(request, new SyncProgress(reports.Add), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        reports.Select(r => (r.Stage, r.Run)).Should().Equal(
            (EditStage.Queued, 1), (EditStage.Running, 1), (EditStage.Downloading, 1),
            (EditStage.Queued, 2), (EditStage.Running, 2), (EditStage.Downloading, 2));
        reports.Should().OnlyContain(r => r.Runs == 2 && r.EtaSeconds == 52);
    }

    // Шаг 28: метка происхождения прогона (владелец, чат, вид=картинка, операция, тяжёлый ли)
    // заполняется при постановке; снимок очереди GPU из опроса прокидывается в отчёт прогресса
    [Fact]
    public async Task Прогон_НесётМеткуПроисхожденияИСнимокОчередиGpu()
    {
        var gpu = new ClaudeHomeServer.Services.Media.LocalGpuQueueInfo(
            Ahead: 1, StartInSeconds: 30, Steps: null, Percent: 40, PercentExact: false,
            RemainingSeconds: 20, Busy: null, DownSince: null);
        var media = new FakeMedia { RunningFirst = true, Gpu = gpu };
        var reports = new List<EditProgress>();
        var request = new ImageEditRequest(ImageEditOp.Generate, "кот в шляпе", null, null, [], 1, "1:1", null,
            LocalImageEditor.QwenImage, null, OwnerId: "user-a", SessionId: "chat-1");

        var result = await Editor(media).RunAsync(request, new SyncProgress(reports.Add), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        // Метка происхождения: владелец, чат, вид=картинка, операция (camelCase), тяжёлый ли
        var origin = media.Submitted.Should().ContainSingle().Subject.Origin;
        origin.Should().NotBeNull("запрос из задачи несёт владельца — метка заполняется");
        origin!.Should().Be(new ClaudeHomeServer.Services.Media.LocalJobOrigin(
            "user-a", "chat-1", "image", "generate", false));
        // Снимок очереди GPU из опроса прокинут в отчёт прогресса (не в статус/трату)
        reports.Should().Contain(p => p.Gpu != null && p.Gpu == gpu, "отчёт несёт живой снимок очереди GPU");
    }

    // Метка происхождения без задачи (OwnerId = null, как в тестах драйвера напрямую) — null,
    // как до шага 28: поведение прежних прогонов не меняется
    [Fact]
    public async Task ПрогонБезЗадачи_МеткаПроисхожденияNull()
    {
        var media = new FakeMedia();
        var request = new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, "1:1", null,
            LocalImageEditor.QwenImage, null);

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        media.Submitted.Should().ContainSingle().Which.Origin.Should().BeNull("без владельца — метка не заполняется");
    }

    // Варианты по готовности: генерация по тексту тоже идёт прогоном на вариант (граф ComfyWorkflows
    // не тронут — батч остаётся у MCP-инструмента), и каждый скачанный прогон сразу уходит отчётом Ready
    [Fact]
    public async Task ГенерацияПоТексту_ПрогонНаВариант_КаждыйОтдаётReady()
    {
        var media = new FakeMedia();
        var reports = new List<EditProgress>();
        var request = new ImageEditRequest(ImageEditOp.Generate, "кот в шляпе", null, null, [], 3, "1:1", null,
            LocalImageEditor.QwenImage, null);

        var result = await Editor(media).RunAsync(request, new SyncProgress(reports.Add), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        media.Submitted.Should().HaveCount(3).And.OnlyContain(r => r.Op == LocalImageOp.Generate && r.Count == 1);
        var ready = reports.Where(r => r.Ready is not null).ToList();
        ready.Select(r => (r.Stage, r.Run, r.Ready!.Count)).Should().Equal(
            (EditStage.Downloading, 1, 1), (EditStage.Downloading, 2, 1), (EditStage.Downloading, 3, 1));
        result.Images.Should().HaveCount(3, "итог несёт все варианты по порядку")
            .And.Equal(ready.SelectMany(r => r.Ready!));
    }

    [Fact]
    public async Task КотировкаГенерацииПоТексту_ВремяПрогонаНаЧислоВариантов()
    {
        var media = new FakeMedia { GenerateBase = 20 };
        var service = Service(Editor(media));

        var quote = await service.QuoteAsync("user-a", Project, Quote(ImageEditOp.Generate, count: 3), default);

        quote.Value!.Estimate.EtaSeconds.Should().Be(media.EtaSeconds(LocalImageOp.Generate, 1, 0) * 3,
            "три прогона по одному, а не один батч на три (он дал бы 140)");
    }

    // Потолок задачи истёк посреди пачки: готовое не теряется — результат Ok с тем, что успело
    [Fact]
    public async Task ПотолокИстёкПосредиПачки_ГотовыеВариантыОстаются()
    {
        var media = new FakeMedia { QueuedFrom = 2 };
        var editor = Editor(media);
        editor.Ceiling = TimeSpan.FromMilliseconds(300);
        var service = Service(editor);

        var job = await RunAsync(service, Quote(ImageEditOp.Edit, count: 3), Input("сделай вечер"));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        job.Variants.Should().Equal(1);
        service.OpenVariant("user-a", Project, job.JobId, 1).Should().NotBeNull();
        media.Cancelled.Should().Equal(["t2"], "повисший в очереди прогон снимается");
    }

    private sealed class SyncProgress(Action<EditProgress> report) : IProgress<EditProgress>
    {
        public void Report(EditProgress value) => report(value);
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

    // FaceDetailer образец не принимает: с персонажем в контексте лицо перерисуется «с нуля» и
    // сходство потеряется — отказ до постановки в очередь, не после
    [Fact]
    public async Task УлучшитьЛица_СПерсонажем_ОтказДоОчереди()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));

        var job = await RunAsync(service, CharacterQuote(ImageEditOp.EnhanceFaces), Input("") with { Character = Timur });

        job.Status.Should().Be(ImageEditJobStatus.Failed, job.Error);
        job.Error.Should().Contain("сходство с персонажем пропадёт");
        media.Submitted.Should().BeEmpty("перерисовка лица без образца не должна уходить в очередь");
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

    // ── Персонаж (живой прогон 2026-09-26: три фото — три одинаковых человека в кадре) ──

    private static readonly CharacterRef Timur = new("timur", "Тимур", null);

    private static List<ReferenceImage> Faces(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new ReferenceImage(TestImages.Png(8, 8, (byte)(40 + i)), "image/png",
            ReferenceRole.Character, "Тимур"))];

    private static ImageEditQuoteRequest CharacterQuote(ImageEditOp op, bool mask = false, bool annotations = false) =>
        new(LocalImageEditor.ProviderKey, "auto", EditMode.Auto, op, 1, mask, 0, true, null, null, annotations);

    [Fact]
    public async Task ПерсонажПоТексту_УходитОднимФото_ВПравкуНаПустомХолсте()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var faces = Faces(3);

        var job = await RunAsync(service, CharacterQuote(ImageEditOp.Generate),
            new ImageEditJobInput("", "Тимур сидит в кафе", null, null, null, null, faces, null, Timur));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Op.Should().Be(LocalImageOp.Generate, "с образцами адаптер строит граф правки на пустом холсте");
        sent.Images.Should().ContainSingle("Qwen-Image рисует по человеку на каждое фото")
            .Which.Should().Equal(faces[0].Bytes, "первым идёт primary-фото");
        sent.Prompt.Should().Contain("1) фото лица — персонаж «Тимур»").And.Contain("сохранить его лицо и черты");
    }

    [Fact]
    public async Task ПерсонажПоТексту_ВремяСчитаетФотоПерсонажа()
    {
        var service = Service(Editor(new FakeMedia()));

        var quote = await service.QuoteAsync("user-a", Project, CharacterQuote(ImageEditOp.Generate), default);

        quote.Value!.Estimate.EtaSeconds.Should().Be(60, "фото персонажа — прогон графа правки, а не генерации по тексту (40)");
    }

    [Fact]
    public async Task ПерсонажИПометки_ПорядокИсходникКопияМаскаФото_РолиПоНомерам()
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var source = TestImages.Png(8, 8, 1);
        var annotated = TestImages.Png(8, 8, 2);
        var mask = TestImages.Png(8, 8, 3);
        var faces = Faces(3);

        var job = await RunAsync(service, CharacterQuote(ImageEditOp.Edit, mask: true, annotations: true),
            new ImageEditJobInput("", "поставь сюда Тимура", null, new ImageBytes(source, "image/png"),
                new ImageBytes(mask, "image/png"), new ImageBytes(annotated, "image/png"), faces, "images/hero.png", Timur));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        // Путь с размеченной копией (ADR-028, п.7): вуаль не включается (VeilMask = null), маска
        // идёт образцом №3, как до вуали, — иначе до модели она не доходит (живой прогон 13.10)
        sent.Images.Should().HaveCount(4);
        sent.Images[0].Should().Equal(source, "холст — исходник, а не фото персонажа");
        sent.Images[1].Should().Equal(annotated);
        sent.Images[2].Should().Equal(mask);
        sent.Images[3].Should().Equal(faces[0].Bytes);
        sent.VeilMask.Should().BeNull("путь с размеченной копией: вуаль не проверена — не включаем");
        sent.KeepMask.Should().BeNull("размеченная копия может просить правку вне кисти — склейка выбросила бы её");
        sent.Prompt.Should().Contain("1) исходная картинка").And.Contain("2) та же картинка с пометками")
            .And.Contain("3) маска").And.Contain("4) фото лица — персонаж «Тимур»")
            .And.NotContain(EditRequestComposer.VeilNote);
    }

    // ── Режим B: LoRA персонажа плюс фото-образец ──

    private static readonly CharacterRef TimurLora = new("timur", "Тимур", null,
        new CharacterLora("timur.safetensors", "andreyk", 0.8));

    [Theory]
    [InlineData(ImageEditOp.Generate)]
    [InlineData(ImageEditOp.Edit)]
    public async Task ПерсонажСЛорой_УходитЛорой_ТриггеромВНачале_ИОдномФото(ImageEditOp op)
    {
        var media = new FakeMedia();
        var service = Service(Editor(media));
        var faces = Faces(3);
        var source = op == ImageEditOp.Edit ? new ImageBytes(TestImages.Png(8, 8, 1), "image/png") : null;

        var job = await RunAsync(service, CharacterQuote(op),
            new ImageEditJobInput("", "сидит в кафе", null, source, null, null, faces, op == ImageEditOp.Edit ? "images/hero.png" : null, TimurLora));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Lora.Should().Be(new LocalImageLora("timur.safetensors", 0.8));
        sent.Prompt.Should().Contain("photo of andreyk, ");
        sent.Images.Count(i => i.SequenceEqual(faces[0].Bytes)).Should().Be(1, "фото-образец остаётся: режим B = LoRA + образец");
        sent.Images.Should().HaveCount(op == ImageEditOp.Edit ? 2 : 1);
    }

    [Theory]
    [InlineData("photo of ANDREYK sitting in a cafe", "photo of ANDREYK sitting in a cafe")]
    [InlineData("сидит в кафе", "photo of andreyk, сидит в кафе")]
    [InlineData("andreykov в кафе", "photo of andreyk, andreykov в кафе")]
    public async Task ТриггерЛоры_НеДублируетсяЕслиЕстьСловом(string prompt, string expected)
    {
        var media = new FakeMedia();
        var job = await RunAsync(Service(Editor(media)), CharacterQuote(ImageEditOp.Generate),
            new ImageEditJobInput("", prompt, null, null, null, null, Faces(3), null, TimurLora));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        media.Submitted.Should().ContainSingle().Which.Prompt.Should().StartWith(expected);
    }

    [Fact]
    public async Task ПерсонажБезЛоры_ЗапросКакРаньше()
    {
        var media = new FakeMedia();
        var job = await RunAsync(Service(Editor(media)), CharacterQuote(ImageEditOp.Generate),
            new ImageEditJobInput("", "сидит в кафе", null, null, null, null, Faces(3), null, Timur));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Lora.Should().BeNull();
        sent.Prompt.Should().NotContain("photo of");
    }

    [Fact]
    public async Task ДорисовкаСПерсонажемСЛорой_ЛорыНет()
    {
        var media = new FakeMedia();
        var request = new ImageEditRequest(ImageEditOp.Outpaint, "дорисуй", new ImageBytes(TestImages.Png(16, 16), "image/png"),
            null, [], 1, "16:9", null, LocalImageEditor.QwenImage, TimurLora);

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        media.Submitted.Should().ContainSingle().Which.Lora.Should().BeNull();
    }

    [Fact]
    public async Task ТриФото_ОсновноеНеПервое_ВЗапросУходитОсновное()
    {
        var root = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(root);
        var created = CharacterStore.Create(root, new CharacterDraft("Тимур", null,
            [.. Enumerable.Range(1, 3).Select(i => new CharacterPhotoUpload(TestImages.Jpeg((byte)i), null))]), DateTime.UtcNow).Value!;
        CharacterStore.Update(root, Owner, created.Manifest.Slug,
            new CharacterPatch(null, null, [], [], created.Manifest.Photos[2].File));
        var character = CharacterStore.ForRequest(root, Owner, created.Manifest.Slug)!;
        var media = new FakeMedia();

        var job = await RunAsync(Service(Editor(media)), CharacterQuote(ImageEditOp.Generate),
            new ImageEditJobInput("", "сидит в кафе", null, null, null, null, character.Photos, null, character.Ref));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.Images.Should().ContainSingle().Which.Should().Equal(TestImages.Jpeg(3), "основное фото, а не первое в манифесте");
    }

    [Fact]
    public async Task БольшеШестнадцатиКартинок_ЧестныйОтказДоОчереди()
    {
        var media = new FakeMedia();
        var refs = Faces(16);
        var request = new ImageEditRequest(ImageEditOp.Edit, "поставь сюда Тимура",
            new ImageBytes(TestImages.Png(8, 8), "image/png"), null, refs, 1, null, null, LocalImageEditor.QwenImage, Timur);

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.Error.Should().Contain("не больше 16 картинок").And.Contain("в запросе 17");
        media.Submitted.Should().BeEmpty("молча обрезать нельзя — пропал бы персонаж или образец");
    }

    // У модели без потолка (nano-banana) фото персонажа едут все, одной ролью на диапазон
    [Fact]
    public void Композер_БезПотолкаФото_ВсеФотоОднойРолью()
    {
        var model = new ImageEditModelInfo("nano", "Nano",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.AsReference, 6, 4, true));
        var input = new ImageEditJobInput("", "поставь сюда Тимура", null, new ImageBytes(TestImages.Png(8, 8), "image/png"),
            null, null, Faces(3), null, Timur);

        var request = EditRequestComposer.Compose(input, ImageEditOp.Edit, model, 1).Value!;

        request.References.Should().HaveCount(3);
        request.Prompt.Should().Contain("2–4) 3 фото лица одного и того же человека").And.Contain("на результате он один");
    }

    // ── Сходство лица: фото эталона в запросе и Similarity в результате ──

    [Theory]
    [InlineData(ImageEditOp.Generate)]
    [InlineData(ImageEditOp.Edit)]
    public async Task ПерсонажСФото_ФотоЭталонаУходитВFaceReferences_ImagesПрежние(ImageEditOp op)
    {
        var media = new FakeMedia();
        var faces = Faces(3);
        byte[][] photos = [TestImages.Jpeg(1), TestImages.Jpeg(2)];
        var character = Timur with { FacePhotos = photos };
        var source = op == ImageEditOp.Edit ? new ImageBytes(TestImages.Png(8, 8, 1), "image/png") : null;
        var request = new ImageEditRequest(op, "сидит в кафе", source, null, faces, 1, null, null, LocalImageEditor.QwenImage, character);

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        sent.FaceReferences.Should().BeEquivalentTo(photos);
        sent.Images.Should().HaveCount(faces.Count + (source is null ? 0 : 1), "фото эталона в Images не входят");
    }

    // Кроп вокруг цели (ADR-028, решение 12): KeepMask — явный канал (не из References), Crop
    // прокидывается в LocalImageRequest; маска не среди образцов модели. Мутация «потерять канал»
    // (KeepMask не прокинуть) → красный; «вернуть маску в references» → красный
    [Fact]
    public async Task Подстановка_KeepMask_ИКроп_Доезжают_МаскаНеСредиОбразцов()
    {
        var media = new FakeMedia();
        var source = TestImages.Png(8, 8, 1);
        var mask = TestImages.Png(8, 8, 2);
        var face = TestImages.Png(8, 8, 3);
        var request = new ImageEditRequest(ImageEditOp.Edit, "заменить лицо",
            new ImageBytes(source, "image/png"), null,
            [new ReferenceImage(face, "image/png", ReferenceRole.Character, "Боря")],
            1, null, null, LocalImageEditor.QwenImage, null,
            FaceCrop: new LocalImageCrop(0, 0, 8, 8, 16, 16),
            KeepMask: new ImageBytes(mask, "image/png"));

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        var sent = media.Submitted.Should().ContainSingle().Subject;
        // Маска не среди образцов: только холст + фото персонажа
        sent.Images.Should().HaveCount(2, "холст + фото персонажа, маски нет");
        sent.Images[0].Should().Equal(source, "холст — первый");
        sent.Images[1].Should().Equal(face, "фото персонажа — второй");
        // KeepMask доехал явным каналом
        sent.KeepMask.Should().Equal(mask, "KeepMask — явный канал, не из References");
        // Кроп прокинут
        sent.Crop.Should().Be(new LocalImageCrop(0, 0, 8, 8, 16, 16));
    }

    [Fact]
    public async Task БезПерсонажа_FaceReferencesНет()
    {
        var media = new FakeMedia();
        var request = new ImageEditRequest(ImageEditOp.Generate, "сидит в кафе", null, null, [], 1, null, null,
            LocalImageEditor.QwenImage, null);

        var result = await Editor(media).RunAsync(request, new Progress<EditProgress>(), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        media.Submitted.Should().ContainSingle().Which.FaceReferences.Should().BeNull();
    }

    [Fact]
    public async Task Сходство_ПереноситсяВReadyИВИтог()
    {
        var media = new FakeMedia { Similarity = 0.77 };
        var ready = new List<EditedImage>();
        var request = new ImageEditRequest(ImageEditOp.Generate, "сидит в кафе", null, null, [], 1, null, null,
            LocalImageEditor.QwenImage, Timur);

        var result = await Editor(media).RunAsync(request,
            new SyncProgress(p => { if (p.Ready is { } r) ready.AddRange(r); }), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        result.Images.Should().ContainSingle().Which.Similarity.Should().Be(0.77);
        ready.Should().ContainSingle().Which.Similarity.Should().Be(0.77);
    }
}
