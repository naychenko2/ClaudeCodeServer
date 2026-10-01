using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Marks;

// «Дорисовать за края»: фронт шлёт только пропорцию, поля в пикселях считает композер.
// Регрессия: Outpaint = null → Bria Expand отказывал «Не заданы поля дорисовки»,
// а FLUX.2 Outpaint у Higgsfield уходил без expand_*
public class OutpaintComposeTests
{
    private const string Queue = "https://queue.test";

    // Квадрат 10×10 под 16:9 → холст 18×10: по 4 пикселя слева и справа
    private static readonly byte[] Square = TestImages.Png(10, 10, tail: 1);

    private static ImageEditJobInput Input() =>
        new("q", "дорисуй пейзаж", null, new ImageBytes(Square, "image/png"), null, null, [], "images/hero.png");

    [Fact]
    public void Композер_ПропорцияБезПолей_СчитаетПоля()
    {
        var model = BriaExpand();

        var req = EditRequestComposer.Compose(Input(), ImageEditOp.Outpaint, model, 1, "16:9").Value!;

        req.Outpaint.Should().Be(new OutpaintSpec(4, 0, 4, 0));
    }

    [Fact]
    public void Композер_ПропорцияНеЧитается_ОтказДоПоставщика()
    {
        var model = BriaExpand();

        var result = EditRequestComposer.Compose(Input(), ImageEditOp.Outpaint, model, 1, "широко");

        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Fal_BriaExpand_ПоляПоНедостающейСтороне()
    {
        var http = new FakeHttp(c => c switch
        {
            _ when c.Method == HttpMethod.Post => FakeHttp.Json(
                $$"""{"request_id":"r1","status_url":"{{Queue}}/r1/status","response_url":"{{Queue}}/r1","cancel_url":"{{Queue}}/r1/cancel"}"""),
            _ when c.Url.EndsWith("/status") => FakeHttp.Json("""{"status":"COMPLETED"}"""),
            _ when c.Url == $"{Queue}/r1" => FakeHttp.Json("""{"image":{"url":"https://cdn.test/a.png"}}"""),
            _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(TestImages.Png(18, 10)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var editor = new FalImageEditor(http, TestImages.Config(("Fal:ApiKey", "k"), ("Fal:QueueBase", Queue)),
            NullLogger<FalImageEditor>.Instance) { PollInterval = TimeSpan.Zero, Downloader = http.Downloader() };
        var model = editor.PickModel(ImageEditOp.Outpaint, EditMode.Auto, new EditTraits(false, 0, false))!;
        model.Id.Should().Be(FalImageEditor.BriaExpand);

        var request = EditRequestComposer.Compose(Input(), ImageEditOp.Outpaint, model, 1, "16:9").Value!;
        var result = await editor.RunAsync(request, new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Ok, result.Error);
        var body = JsonDocument.Parse(http.Calls.First(c => c.Method == HttpMethod.Post).Body).RootElement;
        Ints(body, "canvas_size").Should().Equal(18, 10);
        Ints(body, "original_image_size").Should().Equal(10, 10);
        Ints(body, "original_image_location").Should().Equal(4, 0);
    }

    [Fact]
    public async Task Higgsfield_FluxOutpaint_ПоляПоНедостающейСтороне()
    {
        var (editor, http, _) = HiggsfieldImageEditorTests.Create();
        var model = editor.PickModel(ImageEditOp.Outpaint, EditMode.Auto, new EditTraits(false, 0, false))!;
        model.Id.Should().Be(HiggsfieldImageEditor.Outpaint);

        var request = EditRequestComposer.Compose(Input(), ImageEditOp.Outpaint, model, 1, "16:9").Value!;
        await editor.RunAsync(request, new SyncProgress(_ => { }), default);

        var args = HiggsfieldImageEditorTests.GenerateArgs(HiggsfieldImageEditorTests.Launch(http))!;
        ((int)args["expand_left"]!).Should().Be(4);
        ((int)args["expand_right"]!).Should().Be(4);
        ((int)args["expand_top"]!).Should().Be(0);
        ((int)args["expand_bottom"]!).Should().Be(0);
    }

    private static ImageEditModelInfo BriaExpand() =>
        new FalImageEditor(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
                TestImages.Config(("Fal:ApiKey", "k")), NullLogger<FalImageEditor>.Instance)
            .Models.Single(m => m.Id == FalImageEditor.BriaExpand);

    private static int[] Ints(JsonElement body, string name) =>
        body.GetProperty(name).EnumerateArray().Select(e => e.GetInt32()).ToArray();
}
