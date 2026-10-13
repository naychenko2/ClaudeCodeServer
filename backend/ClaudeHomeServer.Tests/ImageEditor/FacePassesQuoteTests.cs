using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Котировка хранит число проходов N и сверяет его на запуске (ADR-028, этап 3, шаг 7).
// (а) котировка: 1 лицо → facePasses=1; 2 лица → facePasses=2, ETA×2; обычная правка → null;
// (б) котировка на 2 лица + запуск на 1 → 404 quote_not_found, котировка НЕ расходуется (QuoteInfo жива);
// (в) котировка на 1 лицо + запуск на 1 → совпадение, запуск проходит;
// (г) котировка обычной правки + запуск с faceTarget → 404;
// (д) один персонаж на два лица → invalid_request на котировке.
public class FacePassesQuoteTests : IDisposable
{
    // Драйвер с IImageEditQuoter: ETA = 100 сек на правку, цена free.
    // RunAsync не бросает: запуск доходит до RunAsync, а N-сверка — до неё (в StartAsync)
    private sealed class EtaEditor : IImageEditor, IImageEditQuoter
    {
        public string Key => "local";
        public string Label => "LOCAL";
        public string PriceUnit => ImageEditPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models { get; } = [FakeImageEditor.Model("m")];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Models[0];
        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct) =>
            Task.FromResult(new ImageEditResult(EditOutcome.Ok, [], null, null, null, null));
        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

        public Task<ImageEditEstimateDto> EstimateAsync(ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct) =>
            Task.FromResult(new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false, ImageEditEstimateSources.Provider, EtaSeconds: 100));
        public int? ExpectedSeconds(ImageEditModelInfo model) => 100;
    }

    private readonly ImageEditJobService _service;
    private const string Owner = "owner1";
    private const string Project = "project1";

    public FacePassesQuoteTests()
    {
        var workspace = new ImageEditWorkspace(Path.Combine(Path.GetTempPath(), "fp_" + Guid.NewGuid().ToString("N")));
        _service = new ImageEditJobService([new EtaEditor()], workspace, NullLogger<ImageEditJobService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FaceTarget FaceTarget(int faces, string slug = "maria") =>
        new("v1", "fk", Enumerable.Range(1, faces).Select(i =>
            new FaceAssignment(i, i == 1 ? slug : "ivan")).ToList());

    private static ImageEditQuoteRequest QuoteRequest(int count, FaceTarget? faceTarget) =>
        new("local", "auto", EditMode.Auto, ImageEditOp.Edit, count, HasMask: false,
            HasCharacter: faceTarget is not null, FaceTarget: faceTarget);

    // Вход запуска: FaceTarget — одна запись нити (0 или 1 лицо), как в реальном ImageEditJobInput.
    // Source — минимальный PNG: Compose требует исходник для non-generate
    private static ImageEditJobInput JobInput(string quoteId, int faces) =>
        new(quoteId, "prompt", null,
            new ImageBytes([0x89, 0x50, 0x4E, 0x47], "image/png"), null, null, [], null,
            FaceTarget: faces == 0 ? null : new ImageThreadFaceTarget(1, [10, 10, 100, 100], "maria"));

    // ── (а) Котировка: facePasses и ETA ───────────────────────────────────────────

    [Fact]
    public async Task Котировка_1_лицо_facePasses_1()
    {
        var result = await _service.QuoteAsync(Owner, Project, QuoteRequest(1, FaceTarget(1)), CancellationToken.None);
        result.Value.Should().NotBeNull();
        result.Value!.FacePasses.Should().Be(1);
        result.Value.Estimate.EtaSeconds.Should().Be(100);
    }

    [Fact]
    public async Task Котировка_2_лица_facePasses_2_ETA_x2()
    {
        var result = await _service.QuoteAsync(Owner, Project, QuoteRequest(1, FaceTarget(2)), CancellationToken.None);
        result.Value.Should().NotBeNull();
        result.Value!.FacePasses.Should().Be(2);
        // ETA ровно в 2 раза больше одной правки
        result.Value.Estimate.EtaSeconds.Should().Be(200);
    }

    [Fact]
    public async Task Котировка_обычной_правки_facePasses_null()
    {
        var result = await _service.QuoteAsync(Owner, Project, QuoteRequest(1, null), CancellationToken.None);
        result.Value.Should().NotBeNull();
        result.Value!.FacePasses.Should().BeNull();
    }

    // ── (б) Котировка на 2 лица + запуск на 1 → 404, котировка жива ───────────────

    [Fact]
    public async Task Запуск_1_лицо_по_котировке_2_лица_404_и_котировка_жива()
    {
        var quote = (await _service.QuoteAsync(Owner, Project, QuoteRequest(1, FaceTarget(2)), CancellationToken.None))
            .Value!;
        // Запуск на 1 лицо по котировке на 2: N не совпало → 404, котировка не расходуется
        var start = await _service.StartAsync(Owner, Project, JobInput(quote.QuoteId, 1), CancellationToken.None);
        start.ErrorCode.Should().Be(ImageEditErrorCodes.QuoteNotFound);
        start.Error!.Should().Contain("другое число лиц");

        // ТА ЖЕ котировка ещё жива: QuoteInfo по-прежнему возвращает её (не потрачена)
        _service.QuoteInfo(Owner, Project, quote.QuoteId).Should().NotBeNull();
    }

    // ── (в) Котировка на 1 лицо + запуск на 1 → совпадение, запуск проходит ───────

    [Fact]
    public async Task Запуск_1_лицо_по_котировке_1_лица_проходит()
    {
        var quote = (await _service.QuoteAsync(Owner, Project, QuoteRequest(1, FaceTarget(1)), CancellationToken.None))
            .Value!;
        var start = await _service.StartAsync(Owner, Project, JobInput(quote.QuoteId, 1), CancellationToken.None);
        start.Value.Should().NotBeNull();
    }

    // ── (г) Котировка обычной правки + запуск с faceTarget → 404 ──────────────────

    [Fact]
    public async Task Запуск_faceTarget_по_котировке_обычной_правки_404()
    {
        var quote = (await _service.QuoteAsync(Owner, Project, QuoteRequest(1, null), CancellationToken.None))
            .Value!;
        var start = await _service.StartAsync(Owner, Project, JobInput(quote.QuoteId, 1), CancellationToken.None);
        start.ErrorCode.Should().Be(ImageEditErrorCodes.QuoteNotFound);
        start.Error!.Should().Contain("другое число лиц");
    }

    // ── (д) Один персонаж на два лица → invalid_request на котировке ──────────────

    [Fact]
    public async Task Один_персонаж_на_два_лица_invalid_request_на_котировке()
    {
        // Оба лица — один и тот же персонаж (В3): котировка отказывает
        var faceTarget = new FaceTarget("v1", "fk",
            [new FaceAssignment(1, "maria"), new FaceAssignment(2, "maria")]);
        var result = await _service.QuoteAsync(Owner, Project, QuoteRequest(1, faceTarget), CancellationToken.None);
        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // ── (е) facePasses в JSON-ответе: null не пишется (шаг 7) ─────────────────────────

    private static string QuoteDtoJson(ImageEditQuoteDto dto) =>
        JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static ImageEditQuoteDto QuoteDto(int? facePasses) =>
        new("q1", "local", "auto",
            new ImageEditEstimateDto(0, ImageEditPriceUnits.Free, false, ImageEditEstimateSources.Provider),
            DateTime.UtcNow.AddMinutes(10), 60, FacePasses: facePasses);

    [Fact]
    public void facePasses_null_обычной_правки_не_пишется_в_JSON()
    {
        var json = QuoteDtoJson(QuoteDto(null));
        json.Should().NotContain("facePasses", "у обычной правки facePasses = null — ключ не пишется");
    }

    [Fact]
    public void facePasses_подстановки_пишется_в_JSON()
    {
        var json = QuoteDtoJson(QuoteDto(1));
        json.Should().Contain("\"facePasses\":1", "для подстановки facePasses = 1 — ключ пишется");
    }

    // ── (ж) Гонка: два параллельных запуска по одной котировке — ровно один проходит (шаг 7) ──

    [Fact]
    public async Task Два_параллельных_запуска_по_одной_котировке_ровно_один_проходит()
    {
        var quote = (await _service.QuoteAsync(Owner, Project, QuoteRequest(1, FaceTarget(1)), CancellationToken.None)).Value!;
        var results = await Task.WhenAll(
            _service.StartAsync(Owner, Project, JobInput(quote.QuoteId, 1), CancellationToken.None),
            _service.StartAsync(Owner, Project, JobInput(quote.QuoteId, 1), CancellationToken.None));

        results.Count(r => r.Value is not null).Should().Be(1, "ровно один запуск проходит");
        results.First(r => r.Value is null).ErrorCode.Should().Be(ImageEditErrorCodes.QuoteNotFound,
            "второй запуск: котировка уже расходуется");
    }
}
