using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Без флага «Формат» явные пропорции старого интерфейса (aspectRatio запуска) стоят выше формата из префов,
// который котировка подставила от ✦ агента: невидимый выбор не вправе перебивать видимый
public class ExplicitAspectPriorityTests : IDisposable
{
    private const string Owner = "user-p";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-aspect-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<ImageEditRequest> _requests = [];
    private readonly ImageEditJobService _service;

    public ExplicitAspectPriorityTests()
    {
        var caps = new MediaFormatCaps("Фейк", ["1:1", "16:9", "9:16"],
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
            .Returns((ImageEditRequest r, IProgress<EditProgress> _, CancellationToken _) =>
            {
                lock (_requests) _requests.Add(r);
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                    [new EditedImage(TestImages.Png(8, 6), "image/png")], new EditCost(0.01, ImageEditPriceUnits.Usd), true, null, null));
            });
        _service = new ImageEditJobService([editor.Object], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), new RecordingBroadcaster());
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    // Формат из префов, как его подставляет ручка котировки генерации: запись агента (✦) — 9:16
    private static MediaFormat StoredByAgent() =>
        new(FormatModes.Aspect, "9:16", FormatTier.Standard, null, null, ContextActor.Agent, DateTime.UtcNow);

    private async Task<ImageEditRequest> Run(string? explicitAspect)
    {
        var quote = await _service.QuoteAsync(Owner, Project,
            new ImageEditQuoteRequest("fake", "fake-model", EditMode.Auto, ImageEditOp.Generate, 1, false, Format: StoredByAgent()), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await _service.StartAsync(Owner, Project,
            new ImageEditJobInput(quote.Value!.QuoteId, "кот", null, null, null, null, [], null, AspectRatio: explicitAspect), default);
        started.Value.Should().NotBeNull(started.Error);
        for (var i = 0; i < 500 && _service.Get(Owner, Project, started.Value!.JobId)!.IsActive(); i++) await Task.Delay(10);
        lock (_requests) return _requests.Single();
    }

    [Fact]
    public async Task Явные_пропорции_запуска_выше_сохранённого_формата_агента()
    {
        var request = await Run("16:9");

        request.AspectRatio.Should().Be("16:9");
        request.Format!.Aspect.Should().Be("16:9");
    }

    [Fact]
    public async Task Без_явных_пропорций_сохранённый_формат_остаётся()
    {
        var request = await Run(null);

        request.AspectRatio.Should().Be("9:16");
        request.Format!.Aspect.Should().Be("9:16");
    }
}

internal static class JobDtoExtensions
{
    public static bool IsActive(this ImageEditJobDto job) =>
        job.Status is ImageEditJobStatus.Queued or ImageEditJobStatus.Running or ImageEditJobStatus.Downloading;
}
