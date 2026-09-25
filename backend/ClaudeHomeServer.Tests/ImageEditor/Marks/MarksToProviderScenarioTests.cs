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

    [Fact]
    public async Task КистьСтрелкаПодпись_ИсходникКопияМаска_ПоПорядку()
    {
        var sent = await RunFal("добавь кота", brush: true, ArrowJson, LabelJson);

        sent.Model.Should().Be(FalImageEditor.NanoBananaEdit);
        sent.Body.TryGetProperty("mask_url", out _).Should().BeFalse();
        ImageUrls(sent.Body).Should().Equal(Uri(Source), Uri(Annotated), Uri(Mask));
        sent.Prompt.Should().Contain("выделено кистью в центре (x 35–65 %, y 35–55 %)")
            .And.Contain("стрелка от (x 10 %, y 90 %)")
            .And.Contain("3) маска: белое — область, которую менять")
            .And.Contain("Менять только отмеченные места первой картинки");
    }

    [Fact]
    public async Task УдалениеСоСтрелкой_ИдётВМодельСОбразцами_ЗапросЧеловекаСохранён()
    {
        var sent = await RunFal("удали", brush: true, ArrowJson);

        sent.Model.Should().Be(FalImageEditor.NanoBananaEdit);
        ImageUrls(sent.Body).Should().Equal(Uri(Source), Uri(Annotated), Uri(Mask));
        sent.Prompt.Should().StartWith("удали").And.Contain("выделено кистью");
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
        var args = FakeHttp.Arguments(http.Calls.Single(c => FakeHttp.Tool(c) == "generate_image"))!;
        args["medias"]!.AsArray().Select(m => m!["role"]!.ToString())
            .Should().Equal("image_references", "image_references", "mask");
        args["is_inpaint"]!.GetValue<bool>().Should().BeTrue();
        args["prompt"]!.ToString().Should().Contain("выделено кистью").And.Contain("2) та же картинка с пометками");
        http.Calls.Count(c => c.Method == HttpMethod.Put).Should().Be(3);
    }
}
