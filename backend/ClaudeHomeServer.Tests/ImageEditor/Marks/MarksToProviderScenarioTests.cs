using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Marks;

// Пометки холста до тела запроса поставщику целиком: признаки котировки, как их шлёт фронт →
// PickModel «Авто» → EditRequestComposer → драйвер на фейковом HTTP. Регрессия живого дефекта:
// одна кисть уводила в FLUX Fill без канала образцов, и стрелки с рамками выпадали из запроса.
public class MarksToProviderScenarioTests
{
    private const string Queue = "https://queue.test";

    private static readonly byte[] Source = TestImages.Png(20, 10, tail: 1);
    private static readonly byte[] Mask = TestImages.Png(20, 10, tail: 2);
    private static readonly byte[] Annotated = TestImages.Png(20, 10, tail: 3);

    private const string BrushJson = """{"type":"mask","points":[[0.4,0.4],[0.6,0.5]],"width":0.1}""";
    private const string RectJson = """{"type":"rect","x":0.62,"y":0.1,"w":0.26,"h":0.25}""";
    private const string ArrowJson = """{"type":"arrow","x1":0.1,"y1":0.9,"x2":0.3,"y2":0.7}""";
    private const string LabelJson = """{"type":"text","x":0.3,"y":0.65,"text":"сюда кота"}""";

    private sealed record Sent(string Model, JsonElement Body, string Prompt);

    private static async Task<Sent> RunFal(string prompt, bool brush, params string[] drawn)
    {
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r1","status_url":"{{Queue}}/r1/status","response_url":"{{Queue}}/r1","cancel_url":"{{Queue}}/r1/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ when c.Url == $"{Queue}/r1" => FakeHttp.Json("""{"images":[{"url":"https://cdn.test/a.png"}]}"""),
            _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(TestImages.Png(4, 4)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var editor = new FalImageEditor(http, TestImages.Config(("Fal:ApiKey", "k"), ("Fal:QueueBase", Queue)),
            NullLogger<FalImageEditor>.Instance) { PollInterval = TimeSpan.Zero };

        // Ровно то, что шлёт фронт в котировку
        var hasAnnotations = drawn.Length > 0;
        var removal = brush && EditIntent.IsRemoval(prompt);
        var op = brush ? ImageEditOp.Inpaint : ImageEditOp.Edit;
        var model = editor.PickModel(op, EditMode.Auto, new EditTraits(brush, 0, false, hasAnnotations, removal))!;

        var marks = "[" + string.Join(",", (brush ? new[] { BrushJson } : []).Concat(drawn)) + "]";
        var input = new ImageEditJobInput("q", prompt, marks, new ImageBytes(Source, "image/png"),
            brush ? new ImageBytes(Mask, "image/png") : null,
            hasAnnotations ? new ImageBytes(Annotated, "image/png") : null, [], "images/hero.png");
        var request = EditRequestComposer.Compose(input, op, model, 1).Value!;

        var result = await editor.RunAsync(request, new SyncProgress(_ => { }), default);
        result.Outcome.Should().Be(EditOutcome.Ok);

        var submit = http.Calls.First(c => c.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(submit.Body).RootElement;
        return new Sent(submit.Url[(Queue.Length + 1)..], body, body.GetProperty("prompt").GetString()!);
    }

    private static string Uri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static string[] ImageUrls(JsonElement body) =>
        body.GetProperty("image_urls").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Кисть_ЧистыйИнпейнт_МаскаОтдельнымКаналом()
    {
        var sent = await RunFal("сделай синим", brush: true);

        sent.Model.Should().Be(FalImageEditor.FluxFill);
        sent.Body.GetProperty("image_url").GetString().Should().Be(Uri(Source));
        sent.Body.GetProperty("mask_url").GetString().Should().Be(Uri(Mask));
        sent.Body.TryGetProperty("image_urls", out _).Should().BeFalse();
        // Место задаёт маска попиксельно; запрос FLUX Fill — только что рисовать
        sent.Prompt.Should().Be("сделай синим");
    }

    // FLUX Fill дорисовывает в маске новый предмет вместо фона (живой прогон 2026-09-26),
    // поэтому стирание идёт в модель с образцами: маска образцом, место — ещё и координатами
    [Theory]
    [InlineData("удали")]
    [InlineData("Убрать отмеченное")]
    public async Task КистьИУдаление_МодельСОбразцами_ИсходникИМаска_КоординатыКисти(string prompt)
    {
        var sent = await RunFal(prompt, brush: true);

        sent.Model.Should().Be(FalImageEditor.NanoBananaEdit);
        sent.Body.TryGetProperty("mask_url", out _).Should().BeFalse();
        ImageUrls(sent.Body).Should().Equal(Uri(Source), Uri(Mask));
        sent.Prompt.Should().StartWith(prompt)
            .And.Contain("выделено кистью в центре (x 35–65 %, y 35–55 %)")
            .And.Contain("2) маска: белое — область, которую менять");
    }

    [Fact]
    public void FluxFillВыбранРуками_ПросьбаСтереть_ОтказДоЗапуска()
    {
        var fill = new FalImageEditor(new FakeHttp(_ => FakeHttp.Json("{}")), TestImages.Config(("Fal:ApiKey", "k")),
            NullLogger<FalImageEditor>.Instance).Models.Single(m => m.Id == FalImageEditor.FluxFill);
        var input = new ImageEditJobInput("q", "убери кружку", $"[{BrushJson}]", new ImageBytes(Source, "image/png"),
            new ImageBytes(Mask, "image/png"), null, [], "images/hero.png");

        var result = EditRequestComposer.Compose(input, ImageEditOp.Inpaint, fill, 1);

        result.Value.Should().BeNull();
        result.Error.Should().Contain("не стирает").And.Contain("«Авто»");
    }

    [Theory]
    [InlineData("удали", true)]
    [InlineData("Убрать отмеченное", true)]
    [InlineData("сотри провод", true)]
    [InlineData("remove the cup", true)]
    [InlineData("убери лампу и поставь вазу", false)]
    [InlineData("замени на кота", false)]
    [InlineData("сделай синим", false)]
    [InlineData("мастер-класс", false)]
    [InlineData("", false)]
    public void НамерениеУдалить_ПоТекстуЗапроса(string prompt, bool removal)
    {
        EditIntent.IsRemoval(prompt).Should().Be(removal);
    }

    [Fact]
    public async Task Рамка_МодельСОбразцами_ИсходникИКопия_КоординатыВЗапросе()
    {
        var sent = await RunFal("замени на лампу", brush: false, RectJson);

        sent.Model.Should().Be(FalImageEditor.NanoBananaEdit);
        ImageUrls(sent.Body).Should().Equal(Uri(Source), Uri(Annotated));
        sent.Prompt.Should().StartWith("замени на лампу")
            .And.Contain("рамка вверху справа (x 62–88 %, y 10–35 %)")
            .And.Contain("1) исходная картинка")
            .And.Contain("2) та же картинка с пометками поверх");
    }

    [Fact]
    public async Task СтрелкаСПодписью_МодельСОбразцами_КоординатыОбеихПометок()
    {
        var sent = await RunFal("добавь кота", brush: false, ArrowJson, LabelJson);

        sent.Model.Should().Be(FalImageEditor.NanoBananaEdit);
        ImageUrls(sent.Body).Should().Equal(Uri(Source), Uri(Annotated));
        sent.Prompt.Should().Contain("стрелка от (x 10 %, y 90 %) к (x 30 %, y 70 %)")
            .And.Contain("подпись посередине слева (x 30 %, y 65 %): «сюда кота»");
    }

    // ── Кисть вместе с пометками: два запроса ───────────────────────────────────
    // Дефект B (живой прогон 2026-09-26): в одном запросе nano-banana рисовал кота по стрелке,
    // а закрашенную кружку оставлял, 2 из 2. Теперь сначала правка по маске, затем по пометкам
    // поверх её результата

    private static readonly byte[] Cleaned = TestImages.Png(20, 10, tail: 4);
    private static readonly byte[] Final = TestImages.Png(20, 10, tail: 5);

    private sealed record TwoPass(FakeHttp Http, ImageEditResult Result, JsonElement First, JsonElement Second);

    private static async Task<TwoPass> RunFalTwoPass(string prompt, int count, Func<int, HttpResponseMessage>? result = null)
    {
        var posts = 0;
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => Ticket(++posts),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ when c.Url == $"{Queue}/r1" => result?.Invoke(1) ?? FakeHttp.Json("""{"images":[{"url":"https://cdn.test/cleaned.png"}]}"""),
            _ when c.Url == $"{Queue}/r2" => result?.Invoke(2) ?? FakeHttp.Json(
                """{"images":[{"url":"https://cdn.test/final.png"},{"url":"https://cdn.test/final.png"}]}"""),
            _ when c.Url == "https://cdn.test/cleaned.png" => FakeHttp.Bytes(Cleaned),
            _ when c.Url == "https://cdn.test/final.png" => FakeHttp.Bytes(Final),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var editor = new FalImageEditor(http, TestImages.Config(("Fal:ApiKey", "k"), ("Fal:QueueBase", Queue)),
            NullLogger<FalImageEditor>.Instance) { PollInterval = TimeSpan.Zero };

        var removal = EditIntent.IsRemoval(prompt);
        var model = editor.PickModel(ImageEditOp.Inpaint, EditMode.Auto, new EditTraits(true, 0, false, true, removal))!;
        var input = new ImageEditJobInput("q", prompt, $"[{BrushJson},{ArrowJson},{LabelJson}]",
            new ImageBytes(Source, "image/png"), new ImageBytes(Mask, "image/png"),
            new ImageBytes(Annotated, "image/png"), [], "images/hero.png");
        var request = EditRequestComposer.Compose(input, ImageEditOp.Inpaint, model, count).Value!;

        var run = await editor.RunAsync(request, new SyncProgress(_ => { }), default);

        var bodies = http.Calls.Where(c => c.Method == HttpMethod.Post)
            .Select(c => JsonDocument.Parse(c.Body).RootElement).ToList();
        return new TwoPass(http, run, bodies[0], bodies.ElementAtOrDefault(1));
    }

    private static HttpResponseMessage Ticket(int n) => FakeHttp.Json(
        $$"""{"request_id":"r{{n}}","status_url":"{{Queue}}/r{{n}}/status","response_url":"{{Queue}}/r{{n}}","cancel_url":"{{Queue}}/r{{n}}/cancel"}""");

    [Theory]
    [InlineData("удали закрашенное, а по стрелке посади кота")]
    [InlineData("добавь кота")]
    public async Task КистьСтрелкаПодпись_СначалаПравкаПоМаске_ПотомПометкиПоверхЕёРезультата(string prompt)
    {
        var run = await RunFalTwoPass(prompt, count: 2);

        run.Result.Outcome.Should().Be(EditOutcome.Ok);
        run.Result.Images.Select(i => i.Bytes).Should().AllBeEquivalentTo(Final);
        var calls = run.Http.Calls.ToList();
        calls.Where(c => c.Method == HttpMethod.Post).Select(c => c.Url)
            .Should().Equal($"{Queue}/{FalImageEditor.NanoBananaEdit}", $"{Queue}/{FalImageEditor.NanoBananaEdit}");
        // Второй запрос уходит только после того, как скачан результат первого
        calls.FindIndex(c => c.Url == "https://cdn.test/cleaned.png")
            .Should().BeLessThan(calls.FindLastIndex(c => c.Method == HttpMethod.Post));

        // Первый: исходник и маска, один вариант, только кисть
        ImageUrls(run.First).Should().Equal(Uri(Source), Uri(Mask));
        run.First.GetProperty("num_images").GetInt32().Should().Be(1);
        run.First.TryGetProperty("mask_url", out _).Should().BeFalse();
        run.First.GetProperty("prompt").GetString().Should().StartWith(prompt)
            .And.Contain("выделено кистью в центре (x 35–65 %, y 35–55 %)")
            .And.Contain("2) маска: белое — область, которую менять")
            .And.Contain("первый шаг правки")
            .And.NotContain("стрелка от").And.NotContain("сюда кота");

        // Второй: результат первого вместо исходника, размеченная копия, без маски, все варианты
        ImageUrls(run.Second).Should().Equal(Uri(Cleaned), Uri(Annotated));
        run.Second.GetProperty("num_images").GetInt32().Should().Be(2);
        run.Second.GetProperty("prompt").GetString().Should().StartWith(prompt)
            .And.Contain("стрелка от (x 10 %, y 90 %) к (x 30 %, y 70 %)")
            .And.Contain("подпись посередине слева (x 30 %, y 65 %): «сюда кота»")
            .And.Contain("2) картинка с пометками поверх")
            .And.Contain("второй шаг правки")
            .And.NotContain("выделено кистью").And.NotContain("маска");
    }

    [Fact]
    public async Task ДваЗапроса_ПервыйНеУдался_ВторойНеЗапускается()
    {
        var run = await RunFalTwoPass("удали", count: 1,
            n => FakeHttp.Json("""{"detail":"Image failed content policy check"}""", HttpStatusCode.UnprocessableEntity));

        run.Result.Outcome.Should().Be(EditOutcome.Rejected);
        run.Result.Charged.Should().BeFalse();
        run.Http.Calls.Count(c => c.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task ДваЗапроса_ВторойНеУдался_ПервыйВсёРавноТарифицирован()
    {
        var run = await RunFalTwoPass("удали", count: 1, n => n == 1
            ? FakeHttp.Json("""{"images":[{"url":"https://cdn.test/cleaned.png"}]}""")
            : FakeHttp.Json("""{"detail":"boom"}""", HttpStatusCode.InternalServerError));

        run.Result.Outcome.Should().Be(EditOutcome.Failed);
        run.Result.Charged.Should().BeTrue();
    }

    [Fact]
    public async Task ДваЗапроса_КотировкаСчитаетЛишнююКартинкуПервогоПрохода()
    {
        var editor = new FalImageEditor(new FakeHttp(_ => FakeHttp.Json(
                """{"prices":[{"endpoint_id":"fal-ai/nano-banana-2/edit","unit_price":0.08,"unit":"images"}]}""")),
            TestImages.Config(("Fal:ApiKey", "k")), NullLogger<FalImageEditor>.Instance);
        var model = editor.Models.Single(m => m.Id == FalImageEditor.NanoBananaEdit);
        var both = new ImageEditQuoteRequest("fal", model.Id, EditMode.Auto, ImageEditOp.Inpaint, 2, true, 0, false,
            null, null, HasAnnotations: true);

        (await editor.EstimateAsync(model, both, default)).Amount.Should().BeApproximately(0.24, 1e-9);
        (await editor.EstimateAsync(model, both with { HasAnnotations = false }, default)).Amount
            .Should().BeApproximately(0.16, 1e-9);
    }

    [Fact]
    public void КистьБезPngМаски_ПонятныйОтказ_АНеПадениеДрайвера()
    {
        var model = new FalImageEditor(new FakeHttp(_ => FakeHttp.Json("{}")), TestImages.Config(("Fal:ApiKey", "k")),
            NullLogger<FalImageEditor>.Instance).PickModel(ImageEditOp.Inpaint, EditMode.Auto, new EditTraits(true, 0, false))!;
        var input = new ImageEditJobInput("q", "удали", $"[{BrushJson}]", new ImageBytes(Source, "image/png"),
            null, null, [], "images/hero.png");

        var result = EditRequestComposer.Compose(input, ImageEditOp.Inpaint, model, 1);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("Маска не дошла").And.NotContain("Инпейнт без маски");
    }

    [Fact]
    public async Task Higgsfield_КистьИПометки_КопияОбразцом_МаскаСвоимКаналом()
    {
        var (editor, http, _) = HiggsfieldImageEditorTests.Create();
        var model = editor.PickModel(ImageEditOp.Inpaint, EditMode.Auto, new EditTraits(true, 0, false, true))!;
        var input = new ImageEditJobInput("q", "добавь кота", $"[{BrushJson},{ArrowJson}]",
            new ImageBytes(Source, "image/png"), new ImageBytes(Mask, "image/png"),
            new ImageBytes(Annotated, "image/png"), [], "images/hero.png");
        var request = EditRequestComposer.Compose(input, ImageEditOp.Inpaint, model, 1).Value!;

        var result = await editor.RunAsync(request, new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        model.Id.Should().Be(HiggsfieldImageEditor.NanoBanana2);
        var args = HiggsfieldImageEditorTests.GenerateArgs(HiggsfieldImageEditorTests.Launch(http))!;
        args["medias"]!.AsArray().Select(m => m!["role"]!.ToString())
            .Should().Equal("image_references", "image_references", "mask");
        args["is_inpaint"]!.GetValue<bool>().Should().BeTrue();
        args["prompt"]!.ToString().Should().Contain("выделено кистью").And.Contain("2) та же картинка с пометками");
        http.Calls.Count(c => c.Method == HttpMethod.Put).Should().Be(3);
    }
}
