using System.Net;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Драйвер Higgsfield на фейковом MCP (ADR-017, раздел 2а). Фикстуры — живые ответы
// mcp.higgsfield.ai от 2026-09-25 (get_cost, media_upload) и боевые образцы jobs_wait /
// generate_image из mediaExtract.test.ts.
public class HiggsfieldImageEditorTests
{
    internal const string CostFixture = "Cost preflight for nano_banana_2: 1.5 credits (1.5 exact). No job submitted.";

    internal const string UploadFixture =
        "Generated 1 upload URL. Run the curl command, then call media_confirm with the media_id.\n" +
        "- a9ee96f2-1edc-47af-809a-395fdfd3f400: Upload the file using: curl -X PUT -H \"Content-Type: image/png\" " +
        "--data-binary @probe.png 'https://s3.test/user/a9ee96f2-1edc-47af-809a-395fdfd3f400.png?X-Amz-Signature=abc'. " +
        "After upload, call the media_confirm tool with type \"image\" and media_id \"a9ee96f2-1edc-47af-809a-395fdfd3f400\".";

    internal const string GenerateFixture =
        """{"results":[{"id":"8c516bb9-f774-4523-9777-d9f5cb2ca3bf","type":"image","status":"pending","model":"nano_banana_2"}]}""";

    internal const string JobsWaitFixture =
        """{"jobs":[{"index":0,"job_id":"8c516bb9-f774-4523-9777-d9f5cb2ca3bf","status":"completed","type":"image","model":"nano_banana_2","result_url":"https://cdn.test/hf.png"}],"summary":{"total":1,"completed":1,"failed":0,"active":0,"errors":0},"all_terminal":true}""";

    internal static (HiggsfieldImageEditor Editor, FakeHttp Http, FakeHiggsfieldAccess Access) Create(
        Func<FakeHttp.Call, HttpResponseMessage>? route = null, string? token = "admin-token")
    {
        var access = new FakeHiggsfieldAccess(token);
        var http = new FakeHttp(route ?? HappyRoute);
        var client = new HiggsfieldMcpClient(http, TestImages.Config(("Higgsfield:McpUrl", "https://mcp.test/mcp")), access);
        return (new HiggsfieldImageEditor(client) { PollInterval = TimeSpan.Zero }, http, access);
    }

    internal static HttpResponseMessage HappyRoute(FakeHttp.Call c) => c switch
    {
        _ when c.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.OK),
        _ when c.Url.StartsWith("https://cdn.test/") => FakeHttp.Bytes(TestImages.Png(4, 4)),
        _ => FakeHttp.Tool(c) switch
        {
            "media_upload" => FakeHttp.McpText(UploadFixture),
            "media_confirm" => FakeHttp.McpText("Media confirmed."),
            "generate_image" when FakeHttp.Arguments(c)?["get_cost"] is not null => FakeHttp.McpText(CostFixture),
            "generate_image" => FakeHttp.McpText(GenerateFixture),
            "jobs_wait" => FakeHttp.McpText(JobsWaitFixture),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        },
    };

    private static ImageEditQuoteRequest QuoteRequest(int count) =>
        new("higgsfield", "auto", EditMode.Fast, ImageEditOp.Edit, count, false, 0, false, null, null);

    [Fact]
    public async Task Котировка_КредитыЗаЗапуск_УмножаютсяНаЧислоВариантов()
    {
        var (editor, http, _) = Create();
        var model = editor.Models.Single(m => m.Id == HiggsfieldImageEditor.NanoBanana2);

        var estimate = await editor.EstimateAsync(model, QuoteRequest(3), default);

        estimate.Amount.Should().BeApproximately(4.5, 1e-9);
        estimate.Unit.Should().Be(ImageEditPriceUnits.Credits);
        estimate.Approx.Should().BeFalse();
        estimate.Source.Should().Be(ImageEditEstimateSources.Provider);
        var args = FakeHttp.Arguments(http.Calls.Single())!;
        args["get_cost"]!.GetValue<bool>().Should().BeTrue();
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        http.Calls.Single().Auth.Should().Be("Bearer admin-token");
    }

    [Fact]
    public async Task Котировка_НеразобранныйОтвет_ЦенаНеизвестна_АНеИсключение()
    {
        var (editor, _, _) = Create(_ => FakeHttp.McpText("Something new from Higgsfield"));

        var estimate = await editor.EstimateAsync(editor.Models[0], QuoteRequest(2), default);

        estimate.Amount.Should().BeNull();
        estimate.Source.Should().Be(ImageEditEstimateSources.Unknown);
    }

    [Fact]
    public async Task БезТокена_НедоступенИКотировкаОтказывает()
    {
        var (editor, http, _) = Create(token: null);

        editor.Enabled.Should().BeFalse();
        var act = () => editor.EstimateAsync(editor.Models[0], QuoteRequest(1), default);
        await act.Should().ThrowAsync<ImageEditProviderUnavailableException>();
        http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Запуск_ЗагрузкаГенерацияОжидание_ВариантСкачан()
    {
        var (editor, http, access) = Create();
        var png = TestImages.Png(8, 8);
        var stages = new List<EditProgress>();
        var request = new ImageEditRequest(ImageEditOp.Inpaint, "лампа", new ImageBytes(png, "image/png"),
            new ImageBytes(png, "image/png"), [], 1, null, null, HiggsfieldImageEditor.NanoBanana2, null);

        var result = await editor.RunAsync(request, new SyncProgress(stages.Add), default);

        result.Outcome.Should().Be(EditOutcome.Ok);
        result.Images.Should().ContainSingle();
        stages.Select(s => s.Stage).Should().Contain([EditStage.Queued, EditStage.Downloading]);

        var generate = http.Calls.Single(c => FakeHttp.Tool(c) == "generate_image");
        var args = FakeHttp.Arguments(generate)!;
        args["use_unlim"]!.GetValue<bool>().Should().BeFalse();
        args["is_inpaint"]!.GetValue<bool>().Should().BeTrue();
        args["medias"]!.AsArray().Select(m => m!["role"]!.ToString())
            .Should().BeEquivalentTo(["image_references", "mask"]);
        http.Calls.Count(c => c.Method == HttpMethod.Put).Should().Be(2);
        // Токен берётся на каждый вызов, а не раз на задачу
        access.Calls.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task Запуск_ОтказИнструмента_НехваткаКредитов()
    {
        var (editor, _, _) = Create(c => FakeHttp.Tool(c) == "generate_image"
            ? FakeHttp.McpText("Insufficient credits: your balance is 0.5, required 1.5", isError: true)
            : HappyRoute(c));

        var result = await editor.RunAsync(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, null, null,
            HiggsfieldImageEditor.NanoBanana2, null), new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.InsufficientCredits);
        result.Charged.Should().BeFalse();
    }

    [Fact]
    public async Task Запуск_ВопросUnlimChoice_ОтказАНеЗависание()
    {
        var (editor, _, _) = Create(c => FakeHttp.Tool(c) == "generate_image"
            ? FakeHttp.McpText("""{"unlim_choice":{"question":"Use free generations?"}}""")
            : HappyRoute(c));

        var result = await editor.RunAsync(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, null, null,
            HiggsfieldImageEditor.NanoBanana2, null), new SyncProgress(_ => { }), default);

        result.Outcome.Should().Be(EditOutcome.Failed);
    }

    [Fact]
    public void ФикстурыОтветов_Разбираются()
    {
        HiggsfieldImageEditor.ParseUploadSlot(new HiggsfieldCall(true, false, UploadFixture, null))
            .Should().Be(("a9ee96f2-1edc-47af-809a-395fdfd3f400",
                "https://s3.test/user/a9ee96f2-1edc-47af-809a-395fdfd3f400.png?X-Amz-Signature=abc"));
        HiggsfieldImageEditor.ParseCredits(new HiggsfieldCall(true, false, CostFixture, null)).Should().Be(1.5);
        HiggsfieldImageEditor.JobIds(new HiggsfieldCall(true, false, GenerateFixture, null).Json())
            .Should().Equal("8c516bb9-f774-4523-9777-d9f5cb2ca3bf");
    }

    [Theory]
    [InlineData("Insufficient credits to run this generation", EditOutcome.InsufficientCredits)]
    [InlineData("Not enough credits on balance", EditOutcome.InsufficientCredits)]
    [InlineData("Prompt rejected by content moderation", EditOutcome.Rejected)]
    [InlineData("NSFW content detected", EditOutcome.Rejected)]
    [InlineData("Unknown model: nano_banana_9", EditOutcome.Failed)]
    public void Классификатор_ОбразцыОтветов(string text, EditOutcome expected)
    {
        HiggsfieldImageEditor.Classify(text).Should().Be(expected);
    }
}
