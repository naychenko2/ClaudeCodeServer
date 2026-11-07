using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Фактическая цена fal в редакторе картинок (дефект 22068e9d, шаг S-SPEND-7): асинхронный путь
// (queue.fal.run) суммы не называет — FalImageEditor.cs:307 отдаёт ActualCost = null, а id запроса
// из тела ответа очереди уже доходит до задачи (ImageEditResult.RemoteId). Сумму догоняет отдельная
// запись по Core-шову IFalRequestCost. Опрос — только fal, только доллары, только пока своей суммы
// нет; «цены нет» и отказ опроса — штатный исход: первая запись остаётся, хоста ничего не роняет.
// Упавшая задача тарифицирована фалом так же: отказ на скачивании — Fail(charged=true, id)
// (FalImageEditor.cs:298-305), а суммы в отказном ответе нет нисколько. Догон и здесь тот же
// guard и тот же путь (шаг S-SPEND-7b); не тарифицировано или id нет — опроса нет.
public class ImageEditSpendTopUpTests : IDisposable
{
    private const string Project = "p1";
    private const string Owner = "user-a";

    // Догоняющая запись при исправном опросе появляется сразу: окно — страховка от зависшего
    // опроса, а не ожидание реального бэкоффа FalCostService (тот же приём, что ImageGenerationSpendTests)
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    // Окно для проверки «ничего не добавилось»: к моменту терминального события решения о
    // догоне уже принято (TopUpAmountAsync стартует до NotifyFinishedAsync), это страховка
    // от случая, который мог бы дописаться чуть позже
    private static readonly TimeSpan NoTopUp = TimeSpan.FromMilliseconds(300);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-topup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditJobService Service(CapturingSpend spend, IFalRequestCost? cost, ILogger<ImageEditJobService> log,
        params IImageEditor[] editors)
    {
        var service = new ImageEditJobService(editors, new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            log, spend, _broadcaster, null, null, null, cost);
        _services.Add(service);
        return service;
    }

    private static ImageEditQuoteRequest Quote(string provider, int count) =>
        new(provider, "auto", EditMode.Fast, ImageEditOp.Edit, count, false, 0, false, null, null);

    private static async Task<string> StartAsync(ImageEditJobService service, string owner, string provider, int count = 1)
    {
        var quote = await service.QuoteAsync(owner, Project, Quote(provider, count), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await service.StartAsync(owner, Project,
            new ImageEditJobInput(quote.Value!.QuoteId, "убрать провод", null,
                new ImageBytes(TestImages.Png(4, 4), "image/png"), null, null, [], "images/hero.png"), default);
        started.Value.Should().NotBeNull(started.Error);
        return started.Value!.JobId;
    }

    // Терминальное состояние задачи — по её же завершающему событию (образец ImageEditJobServiceTests)
    private async Task<ImageEditJobDto> WaitDone(ImageEditJobService service, string owner, string jobId)
    {
        var finished = new TaskCompletionSource();
        void OnMessage(ServerMessage message) { if (IsCompletionFor(jobId, message)) finished.TrySetResult(); }
        _broadcaster.MessageSent += OnMessage;
        if (!_broadcaster.AllMessages.Any(m => IsCompletionFor(jobId, m)))
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _broadcaster.MessageSent -= OnMessage;
        return service.Get(owner, Project, jobId)!;
    }

    private static bool IsCompletionFor(string jobId, ServerMessage message) =>
        message is ImageEditCompletedMessage completed && completed.JobId == jobId
        || message is ImageEditFailedMessage failed && failed.JobId == jobId;

    // ── Догон суммы по request_id ────────────────────────────────────────────────

    [Fact]
    public async Task Фал_БезСуммыВКотировке_ЦенуДогоняютОтдельнойЗаписьюПоRequestId()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        await spend.WaitForSecond(Settle);

        spend.Records.Should().HaveCount(2);
        var first = spend.Records.First();
        first.OwnerId.Should().Be(Owner);
        first.Provider.Should().Be(SpendSources.Fal);
        first.Generations.Should().Be(2, "число вариантов — с первой записи");
        first.CostUsd.Should().BeNull("ни котировка, ни ответ поставщика суммы не назвали");
        first.Label.Should().Be(ImageEditJobService.SpendLabelPendingAmount);

        var topUp = spend.Records.Last();
        topUp.OwnerId.Should().Be(Owner);
        topUp.Provider.Should().Be(SpendSources.Fal);
        topUp.Generations.Should().Be(0, "догоняющая запись не повторяет генерации");
        topUp.CostUsd.Should().Be(0.04);
        topUp.Label.Should().Be(ImageEditJobService.SpendLabel);

        cost.Calls.Should().Be(1);
        cost.RequestIds.Should().ContainSingle().Which.Should().Be("req-1",
            "цена спрашивается по request_id тела ответа очереди");
    }

    // ── Отрицания: опрос не по чему и не о чём ───────────────────────────────────

    [Fact]
    public async Task Фал_СуммаКотировкиУжеЗаписана_ОценкуФактомНеЗаменяют()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: 1.5, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        await WaitDone(service, Owner, jobId);

        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().Be(3.0);
        cost.Calls.Should().Be(0, "цена каталога записана — догонять нечего");
    }

    [Fact]
    public async Task ФалВКредитах_ЦенуUsdНеОпрашивают()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Credits, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 1);
        await WaitDone(service, Owner, jobId);

        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
        cost.Calls.Should().Be(0, "кредиты долларовым опросом не спрашиваются");
    }

    [Fact]
    public async Task НеФал_ЦенуПоRequestIdНеОпрашивают()
    {
        // Единица — доллары, id есть: единственное, что не даёт опроса, — провайдер не fal
        var editor = new ScriptedEditor("higgsfield", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "higgsfield", count: 1);
        await WaitDone(service, Owner, jobId);

        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.Provider.Should().Be(SpendSources.Higgsfield);
        cost.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ФалБезRequestId_ОпросаНет()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: null);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 1);
        await WaitDone(service, Owner, jobId);

        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
        cost.Calls.Should().Be(0, "спрашивать не по чему: ответа с id не было");
    }

    // ── Отказ опроса ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Фал_ЦеныНет_ЗадачаГотова_ОстаётсяОднаЗаписьБезСуммы()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Completed);
        cost.Calls.Should().Be(1, "опрос был — просто цены не оказалось");
        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
    }

    // «Опрос не удался» видно только по журналу: исключение фонового метода не меняет ни статуса
    // задачи, ни её записей — и не должно доходить до необработанного
    [Fact]
    public async Task Фал_ОпросЦеныНеУдался_ЗадачаГотова_ОднаЗаписьИПредупреждениеВЛог()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null, throws: true);
        var log = new RecordingLog();
        var service = Service(spend, cost, log, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Completed, "отказ опроса не роняет задачу");
        cost.Calls.Should().Be(1);
        log.Errors.Should().Contain(m => m.Contains("req-1"),
            "отказ — предупреждение в лог внутри фонового метода, не необработанное исключение");
        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
    }

    // Две цены на одну задачу продукт не порождает (один request_id на задачу), поэтому guard
    // дописывания проверяем напрямую: повторный RecordSpend с той же суммой на том же job
    // (тот же job.Gate, что у догоняющей записи) не добавляет записей.
    [Fact]
    public async Task Фал_ТаЖеСуммаПовторно_ЗаписейНеПрибавляется()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        await WaitDone(service, Owner, jobId);
        await spend.WaitForSecond(Settle);
        spend.Records.Should().HaveCount(2);

        RecordSpendAgain(service, jobId, 0.04);
        RecordSpendAgain(service, jobId, 0.04);

        spend.Records.Should().HaveCount(2, "RecordSpend решает по RecordedAmount: та же сумма повторно не пишется");
    }

    // ── Упавшая задача: fal берёт деньги и за отказ (шаг S-SPEND-7b) ─────────────

    [Fact]
    public async Task Фал_УпавшаяЗадачаТарифицирована_ЦенуДогоняютОтдельнойЗаписью()
    {
        // Отказ на скачивании у fal: charged=true с request_id (FalImageEditor.cs:298-305)
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1",
            failWith: "сбой скачивания");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Failed);
        await spend.WaitForSecond(Settle);

        spend.Records.Should().HaveCount(2);
        var first = spend.Records.First();
        first.OwnerId.Should().Be(Owner);
        first.Provider.Should().Be(SpendSources.Fal);
        first.Generations.Should().Be(2, "число вариантов — с первой записи");
        first.CostUsd.Should().BeNull("ни котировки, ни суммы в отказном ответе нет");
        first.Label.Should().Be(ImageEditJobService.SpendLabelPendingAmount);

        var topUp = spend.Records.Last();
        topUp.OwnerId.Should().Be(Owner);
        topUp.Provider.Should().Be(SpendSources.Fal);
        topUp.Generations.Should().Be(0, "догоняющая запись не повторяет генерации");
        topUp.CostUsd.Should().Be(0.04);
        topUp.Label.Should().Be(ImageEditJobService.SpendLabel);

        cost.Calls.Should().Be(1);
        cost.RequestIds.Should().ContainSingle().Which.Should().Be("req-1",
            "цена спрашивается по request_id отказа: деньги по нему списаны");
    }

    [Fact]
    public async Task Фал_УпавшаяЗадачаБезRequestId_ОпросаНет()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: null,
            failWith: "сбой скачивания");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 1);
        await WaitDone(service, Owner, jobId);

        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
        cost.Calls.Should().Be(0, "спрашивать не по чему: отказа с id не было");
    }

    [Fact]
    public async Task Фал_УпавшаяЗадачаНеТарифицирована_ОпросаНет()
    {
        // Поставщик честно сказал «не списано» — опроса фактической цены нет даже с id в ответе
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1",
            failWith: "отказано", charged: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = Service(spend, cost, NullLogger<ImageEditJobService>.Instance, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 1);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Failed);
        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().BeEmpty("не списано и не принято — записывать нечего");
        cost.Calls.Should().Be(0, "не тарифицировано — о фактической цене не спрашивают");
    }

    [Fact]
    public async Task Фал_УпавшаяЗадача_ОпросЦеныНеУдался_ЗадачаFailed_ОднаЗаписьИОшибкаВЛог()
    {
        var editor = new ScriptedEditor("fal", ImageEditPriceUnits.Usd, price: null, remoteId: "req-1",
            failWith: "сбой скачивания");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null, throws: true);
        var log = new RecordingLog();
        var service = Service(spend, cost, log, editor);

        var jobId = await StartAsync(service, Owner, "fal", count: 2);
        var job = await WaitDone(service, Owner, jobId);

        job.Status.Should().Be(ImageEditJobStatus.Failed, "отказ опроса не меняет исход задачи");
        cost.Calls.Should().Be(1);
        log.Errors.Should().Contain(m => m.Contains("req-1"),
            "отказ — ошибка в лог внутри фонового метода, не необработанное исключение");
        await spend.WaitForSecond(NoTopUp);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
    }

    // ── Фейки ────────────────────────────────────────────────────────────────────

    // Драйвер асинхронного пути: прайса модели нет (price: null — как у fal-модели вне каталога),
    // ActualCost — никогда (FalImageEditor.cs:307), request_id — есть (тот же путь ParseTicket).
    // failWith — тот же путь отказа: Fail(charged, id) без суммы (FalImageEditor.cs:298-305)
    private sealed class ScriptedEditor(string key, string unit, double? price, string? remoteId,
        string? failWith = null, bool charged = true) : IImageEditor
    {
        private readonly ImageEditModelInfo _model = new("m-topup", "Topup",
            new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true),
            price is { } p ? new ImageEditPriceHint(p, unit, "image") : null);

        private int _runs;
        public int Runs => _runs;

        public string Key => key;
        public string Label => key;
        public string PriceUnit => unit;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [_model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => _model;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            if (failWith is not null)
                return Task.FromResult(new ImageEditResult(EditOutcome.Failed, [], null, charged, remoteId, failWith));
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                [new EditedImage(TestImages.Png(2, 2), "image/png"), new EditedImage(TestImages.Png(2, 2), "image/png")],
                null, true, remoteId, null));
        }

        public Task<bool> CancelRemoteAsync(string id, CancellationToken ct) => Task.FromResult(false);
    }

    // Учёт расхода с сигналом второй (догоняющей) записи: не дождался — значит её никто и не писал
    private sealed class CapturingSpend : ISpendCollector
    {
        private readonly List<SpendRecord> _records = [];
        private readonly TaskCompletionSource _second = new();

        public IReadOnlyList<SpendRecord> Records
        {
            get { lock (_records) return [.. _records]; }
        }

        public void Record(SpendRecord record)
        {
            lock (_records)
            {
                _records.Add(record);
                if (_records.Count >= 2) _second.TrySetResult();
            }
        }

        public async Task WaitForSecond(TimeSpan timeout) => await Task.WhenAny(_second.Task, Task.Delay(timeout));
    }

    // Цена по request_id: известная / её нет / опрос не удался. Считает обращения и сами id
    private sealed class FakeCost(double? usd, bool throws = false) : IFalRequestCost
    {
        private readonly List<string> _ids = [];

        public int Calls { get; private set; }
        public IReadOnlyList<string> RequestIds
        {
            get { lock (_ids) return [.. _ids]; }
        }

        public Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
        {
            lock (_ids) _ids.Add(requestId);
            Calls++;
            return throws
                ? Task.FromException<double?>(new InvalidOperationException("опрос цены не удался"))
                : Task.FromResult(usd);
        }
    }

    // Журнал сервиса: «опрос цены не удался» обязан остаться записью журнала внутри фонового метода
    private sealed class RecordingLog : ILogger<ImageEditJobService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Errors
        {
            get { lock (_entries) return [.. _entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NoScope();
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NoScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    // Приватный путь дописывания на том же job: публичного способа вызвать его дважды у одной
    // задачи нет (один request_id на задачу), а guard именно от этого и защищает
    private static void RecordSpendAgain(ImageEditJobService service, string jobId, double amount)
    {
        var type = typeof(ImageEditJobService);
        var jobs = type.GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        var args = new object?[] { jobId, null };
        jobs.GetType().GetMethod("TryGetValue")!.Invoke(jobs, args);
        type.GetMethod("RecordSpend", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { args[1], amount });
    }
}
