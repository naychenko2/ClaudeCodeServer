using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Драйвер fal на фейковом HTTP (ADR-017, разделы 2 и 4): котировка «$ × варианты» по прайс
// API, очередь queue.fal.run со стадиями и отменой, неуспех не тарифицируется
public class FalImageEditorTests
{
    private const string Queue = "https://queue.test";
    private const string Api = "https://api.test/v1";

    private static FalImageEditor Editor(FakeHttp http, string? key = "fal-key") =>
        new(http, TestImages.Config(("Fal:ApiKey", key), ("Fal:QueueBase", Queue), ("Fal:ApiBase", Api)),
            NullLogger<FalImageEditor>.Instance) { PollInterval = TimeSpan.Zero, Downloader = http.Downloader() };

    private static ImageEditQuoteRequest QuoteRequest(int count, int? w = null, int? h = null) =>
        new("fal", "auto", EditMode.Fast, ImageEditOp.Edit, count, false, 0, false, w, h);

    private static ImageEditModelInfo ModelOf(FalImageEditor e, string id) => e.Models.Single(m => m.Id == id);

    [Fact]
    public async Task Котировка_ЦенаЗаКартинку_УмножаетсяНаЧислоВариантов()
    {
        var http = new FakeHttp(c => c.Url.Contains("/models/pricing")
            ? FakeHttp.Json("""{"prices":[{"endpoint_id":"fal-ai/nano-banana-2/edit","unit_price":0.08,"unit":"images","currency":"USD"}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var editor = Editor(http);

        var estimate = await editor.EstimateAsync(ModelOf(editor, FalImageEditor.NanoBananaEdit), QuoteRequest(3), default);

        estimate.Amount.Should().BeApproximately(0.24, 1e-9);
        estimate.Unit.Should().Be(ImageEditPriceUnits.Usd);
        estimate.Approx.Should().BeFalse();
        estimate.Source.Should().Be(ImageEditEstimateSources.Catalog);
        http.Calls.Single().Auth.Should().Be("Key fal-key");
    }

    [Fact]
    public async Task Котировка_Мегапиксели_СчитаютсяПоРазмеруИсходникаИВариантам()
    {
        var http = new FakeHttp(_ =>
            FakeHttp.Json("""{"prices":[{"endpoint_id":"fal-ai/flux-pro/v1/fill","unit_price":0.05,"unit":"megapixels"}]}"""));
        var editor = Editor(http);

        var estimate = await editor.EstimateAsync(ModelOf(editor, FalImageEditor.FluxFill), QuoteRequest(2, 2000, 1000), default);

        // 2 Мп × $0.05 × 2 варианта
        estimate.Amount.Should().BeApproximately(0.2, 1e-9);
        estimate.Approx.Should().BeTrue();
    }

    [Fact]
    public async Task Котировка_ПрайсНедоступен_ОриентирКаталогаТожеУмноженНаВарианты()
    {
        var editor = Editor(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var estimate = await editor.EstimateAsync(ModelOf(editor, FalImageEditor.NanoBananaProEdit), QuoteRequest(4), default);

        estimate.Amount.Should().BeApproximately(0.6, 1e-9);
        estimate.Approx.Should().BeTrue();
    }

    [Fact]
    public void БезКлюча_ПоставщикНедоступен()
    {
        Editor(new FakeHttp(_ => FakeHttp.Json("{}")), key: "").Enabled.Should().BeFalse();
    }

    // Ключ уходит на адреса из ответа очереди — чужой хост или http не опрашиваем вовсе
    [Theory]
    [InlineData("https://evil.test/r1/status", "https://queue.test/r1", "https://queue.test/r1/cancel")]
    [InlineData("https://queue.test/r1/status", "https://evil.test/r1", "https://queue.test/r1/cancel")]
    [InlineData("https://queue.test/r1/status", "https://queue.test/r1", "https://evil.test/r1/cancel")]
    [InlineData("http://queue.test/r1/status", "https://queue.test/r1", "https://queue.test/r1/cancel")]
    public async Task Запуск_АдресОчередиВнеХостовFal_ОтказЗначением_КлючНеУходит(string status, string response, string cancel)
    {
        var http = new FakeHttp(c => c.Method == HttpMethod.Post
            ? FakeHttp.Json($$"""{"request_id":"r1","status_url":"{{status}}","response_url":"{{response}}","cancel_url":"{{cancel}}"}""")
            : FakeHttp.Json("""{"status":"COMPLETED"}"""));
        var png = TestImages.Png(4, 4, tail: 7);
        var request = new ImageEditRequest(ImageEditOp.Edit, "убрать провод", new ImageBytes(png, "image/png"), null,
            [], 1, null, null, FalImageEditor.NanoBananaEdit, null);

        var result = await Editor(http).RunAsync(request, new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.Error.Should().Contain("вне своих хостов");
        http.Calls.Should().ContainSingle("после отправки ни опроса, ни отмены с ключом")
            .Which.Url.Should().Be($"{Queue}/{FalImageEditor.NanoBananaEdit}");
    }

    [Fact]
    public async Task Запуск_ОчередьСтатусРезультат_ВариантыСкачаныИСтадииОтчитаны()
    {
        var png = TestImages.Png(4, 4, tail: 7);
        var polls = 0;
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r1","status_url":"{{Queue}}/r1/status","response_url":"{{Queue}}/r1","cancel_url":"{{Queue}}/r1/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json(++polls == 1
                ? """{"status":"IN_QUEUE","queue_position":3}"""
                : polls == 2 ? """{"status":"IN_PROGRESS"}""" : """{"status":"COMPLETED"}"""),
            _ when c.Url == $"{Queue}/r1" => FakeHttp.Json(
                """{"images":[{"url":"https://cdn.test/a.png","content_type":"image/png"},{"url":"https://cdn.test/b.png"}]}"""),
            _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(png),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var stages = new List<EditProgress>();
        var request = new ImageEditRequest(ImageEditOp.Edit, "убрать провод", new ImageBytes(png, "image/png"), null,
            [], 2, null, null, FalImageEditor.NanoBananaEdit, null);

        var result = await Editor(http).RunAsync(request, new SyncProgress(stages.Add), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        result.Images.Should().HaveCount(2);
        result.Charged.Should().BeTrue();
        result.RemoteId.Should().Be("r1");
        stages.Should().ContainEquivalentOf(new EditProgress(EditStage.Queued, 3));
        stages.Select(s => s.Stage).Should().Contain([EditStage.Running, EditStage.Downloading]);

        var submit = http.Calls.First();
        submit.Url.Should().Be($"{Queue}/{FalImageEditor.NanoBananaEdit}");
        var body = JsonDocument.Parse(submit.Body).RootElement;
        body.GetProperty("num_images").GetInt32().Should().Be(2);
        body.GetProperty("image_urls")[0].GetString().Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task Запуск_ОшибкаРезультата_НеТарифицируется()
    {
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r2","status_url":"{{Queue}}/r2/status","response_url":"{{Queue}}/r2","cancel_url":"{{Queue}}/r2/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ => FakeHttp.Json("""{"detail":"Image failed content policy check"}""", HttpStatusCode.UnprocessableEntity),
        });

        var result = await Editor(http).RunAsync(EditRequest(), new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Rejected);
        result.Charged.Should().BeFalse();
        result.Error.Should().Contain("content policy");
    }

    [Fact]
    public async Task Запуск_ПустойОтветСТекстомМодели_ПричинаВОшибке()
    {
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r4","status_url":"{{Queue}}/r4/status","response_url":"{{Queue}}/r4","cancel_url":"{{Queue}}/r4/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ => FakeHttp.Json("""{"images":[],"description":"Уточните, кого нарисовать"}"""),
        });

        var result = await Editor(http).RunAsync(EditRequest(), new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.RemoteId.Should().Be("r4");
        result.Error.Should().Be("fal.ai не вернул картинок — модель ответила: «Уточните, кого нарисовать»");
    }

    [Fact]
    public async Task Запуск_КартинкаНеСкачалась_КодОтказаЗагрузчикаВОшибке()
    {
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r5","status_url":"{{Queue}}/r5/status","response_url":"{{Queue}}/r5","cancel_url":"{{Queue}}/r5/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ when c.Url == $"{Queue}/r5" => FakeHttp.Json("""{"images":[{"url":"https://cdn.test/a.png"}],"description":""}"""),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
        });

        var result = await Editor(http).RunAsync(EditRequest(), new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Failed);
        result.Charged.Should().BeTrue("fal картинку нарисовал");
        result.Error.Should().Be("fal.ai нарисовал картинку, но скачать её не удалось: http-403");
    }

    [Theory]
    [InlineData("""{"images":[],"description":"Blocked by safety filters"}""", EditOutcome.Rejected, "safety filters")]
    [InlineData("""{"images":[],"has_nsfw_concepts":[true]}""", EditOutcome.Rejected, "фильтром безопасности")]
    [InlineData("""{"images":[{"url":"https://cdn.test/a.png"}]}""", EditOutcome.Failed, "скачать её не удалось")]
    [InlineData("""{"images":[]}""", EditOutcome.Failed, "причину не назвал")]
    [InlineData("не json", EditOutcome.Failed, "не вернул картинок")]
    public void ПустойОтвет_ПричинаРазличается(string body, EditOutcome outcome, string error)
    {
        var (o, e) = FalImageEditor.EmptyResultReason(body);

        o.Should().Be(outcome);
        e.Should().Contain(error);
    }

    [Fact]
    public async Task Отмена_ОтзываетЗадачуУПоставщикаИПробрасываетОтмену()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r3","status_url":"{{Queue}}/r3/status","response_url":"{{Queue}}/r3","cancel_url":"{{Queue}}/r3/cancel"}"""),
            _ when c.Method == HttpMethod.Put => FakeHttp.Json("""{"status":"CANCELLATION_REQUESTED"}"""),
            _ => Cancel(cts, FakeHttp.Json("""{"status":"IN_QUEUE","queue_position":1}""")),
        });

        var act = () => Editor(http).RunAsync(EditRequest(), new SyncProgress(_ => { }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        http.Calls.Should().Contain(c => c.Method == HttpMethod.Put && c.Url == $"{Queue}/r3/cancel");
    }

    [Fact]
    public void ТелоИнпейнта_МаскаОтдельнымПолем()
    {
        var png = TestImages.Png(8, 8);
        var body = FalImageEditor.BuildBody(new ImageEditRequest(ImageEditOp.Inpaint, "лампа",
            new ImageBytes(png, "image/png"), new ImageBytes(png, "image/png"), [], 1, null, null, FalImageEditor.FluxFill, null));

        body.Should().ContainKey("mask_url");
        body["image_url"].Should().NotBe(null);
        body.Should().NotContainKey("image_urls");
    }

    private static ImageEditRequest EditRequest() =>
        new(ImageEditOp.Edit, "x", new ImageBytes(TestImages.Png(4, 4), "image/png"), null, [], 1, null, null,
            FalImageEditor.NanoBananaEdit, null);

    private static HttpResponseMessage Cancel(CancellationTokenSource cts, HttpResponseMessage response)
    {
        cts.Cancel();
        return response;
    }
}

internal sealed class SyncProgress(Action<EditProgress> onReport) : IProgress<EditProgress>
{
    public void Report(EditProgress value) => onReport(value);
}
