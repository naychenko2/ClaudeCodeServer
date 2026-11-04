using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// S2d-2b: события задач редактора (прогресс, готово) приватного чата не уходят группе проекта
public sealed class JobEventAudienceTests : IDisposable
{
    private const string Owner = "owner";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-aud-" + Guid.NewGuid().ToString("N")[..8]);
    private ImageEditJobService? _service;

    public void Dispose()
    {
        _service?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private async Task<List<string>> RunAsync(Session? session)
    {
        var calls = new List<string>();
        var caps = new MediaFormatCaps("Фейк", ["1:1"],
            [new FormatTierSpec(FormatTier.Standard, "1 Мп", new Dictionary<string, FormatSize>())], null, "resolution");
        var model = new ImageEditModelInfo("fake-model", "Фейк",
            new ImageEditCaps([ImageEditOp.Generate], MaskSupport.None, 0, 4, false, Format: caps),
            new ImageEditPriceHint(0.01, ImageEditPriceUnits.Usd, "image"));
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("fake");
        editor.SetupGet(e => e.Label).Returns("Фейк");
        editor.SetupGet(e => e.PriceUnit).Returns(ImageEditPriceUnits.Usd);
        editor.SetupGet(e => e.Enabled).Returns(true);
        editor.SetupGet(e => e.Registered).Returns(true);
        editor.SetupGet(e => e.Models).Returns([model]);
        editor.Setup(e => e.PickModel(It.IsAny<ImageEditOp>(), It.IsAny<EditMode>(), It.IsAny<EditTraits>())).Returns(model);
        editor.Setup(e => e.RunAsync(It.IsAny<ImageEditRequest>(), It.IsAny<IProgress<EditProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                [new EditedImage(TestImages.Png(8, 6), "image/png")], new EditCost(0.01, ImageEditPriceUnits.Usd), true, null, null)));
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("c1")).Returns(session);
        var hub = new Mock<ISessionBroadcaster>();
        hub.Setup(h => h.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((u, m) => { lock (calls) calls.Add($"owner:{u}:{m.Type}"); }).Returns(Task.CompletedTask);
        hub.Setup(h => h.ToProject(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((p, m) => { lock (calls) calls.Add($"project:{p}:{m.Type}"); }).Returns(Task.CompletedTask);
        _service = new ImageEditJobService([editor.Object], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), hub.Object, null, null, dir.Object);

        var quote = await _service.QuoteAsync(Owner, Project,
            new ImageEditQuoteRequest("fake", "fake-model", EditMode.Auto, ImageEditOp.Generate, 1, false), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await _service.StartAsync(Owner, Project,
            new ImageEditJobInput(quote.Value!.QuoteId, "кот", null, null, null, null, [], null, ChatSessionId: "c1"), default);
        started.Value.Should().NotBeNull(started.Error);
        for (var i = 0; i < 500 && _service.Get(Owner, Project, started.Value!.JobId)!.IsActive(); i++) await Task.Delay(10);
        await Task.Delay(50);
        lock (calls) return [.. calls];
    }

    [Fact]
    public async Task Приватный_чат_события_задачи_только_автору()
    {
        var calls = await RunAsync(new Session { Id = "c1", OwnerId = Owner, ProjectId = Project, PrivateToUserId = "author" });

        calls.Should().NotBeEmpty();
        calls.Should().OnlyContain(c => c.StartsWith("owner:author:"));
    }

    [Fact]
    public async Task Общий_чат_события_задачи_группе_проекта()
    {
        var calls = await RunAsync(new Session { Id = "c1", OwnerId = Owner, ProjectId = Project });

        calls.Should().NotBeEmpty();
        calls.Should().OnlyContain(c => c.StartsWith("project:p1:"));
    }
}
