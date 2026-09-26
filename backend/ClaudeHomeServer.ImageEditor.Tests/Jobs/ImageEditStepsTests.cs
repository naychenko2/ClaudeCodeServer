using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// «Применить вариант» (ADR-018 §9): вариант задачи становится шагом ленты. С базовым шагом —
// продолжает его ленту, без него — своя лента, отдельно от папки задачи с вариантами v{n}
public class ImageEditStepsTests : IDisposable
{
    private const string Owner = "u";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-steps-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeJobs _jobs = new();
    private readonly ImageEditSteps _steps;

    public ImageEditStepsTests() =>
        _steps = new ImageEditSteps(new SkiaImageRaster(), new ImageEditWorkspace(_dir), _jobs);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Вариант_без_базового_шага_своя_лента_не_папка_задачи()
    {
        _jobs.BaseStepId = null;

        var step = Apply();

        step.Kind.Should().Be(ImageEditStepKinds.Variant);
        step.Parent.Should().BeNull();
        step.EditId.Should().NotBe(FakeJobs.JobId, "шаги не смешиваются с вариантами задачи");
        (step.JobId, step.Variant).Should().Be((FakeJobs.JobId, 1));
    }

    [Fact]
    public void Вариант_с_базовым_шагом_продолжает_его_ленту()
    {
        var first = _steps.Transform(Owner, Project, new ImageTransformRequest(new ImageTransformBase(Path: "hero.png"), []),
            new ProjectImage("hero.png", Png(20, 10)), dryRun: false).Value!.StepId!;
        var baseStep = _steps.Open(Owner, Project, first)!.Value.Step;
        _jobs.BaseStepId = first;

        var step = Apply();

        step.Parent.Should().Be(first);
        step.EditId.Should().Be(baseStep.EditId);
        step.SourcePath.Should().Be("hero.png");
    }

    private ImageEditStep Apply()
    {
        var result = _steps.Transform(Owner, Project,
            new ImageTransformRequest(new ImageTransformBase(JobId: FakeJobs.JobId, Variant: 1), []), null, dryRun: false);
        result.Value.Should().NotBeNull(result.Error);
        return _steps.Open(Owner, Project, result.Value!.StepId!)!.Value.Step;
    }

    private static byte[] Png(int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Coral);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FakeJobs : IImageEditJobs
    {
        public const string JobId = "0123456789abcdef0123456789abcdef";
        public string? BaseStepId;

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) =>
            ownerId == Owner && projectId == Project && jobId == JobId
                ? new ImageEditJobDto(JobId, Project, ImageEditJobStatus.Completed, "fal", "m", [1], null, EditOutcome.Ok,
                    true, null, null, DateTime.UtcNow, BaseStepId: BaseStepId)
                : null;

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) =>
            Get(ownerId, projectId, jobId) is null || variant != 1 ? null : new EditedImage(Png(16, 16), "image/png");

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(string o, string p, ImageEditQuoteRequest r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(string o, string p, ImageEditJobInput i, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ImageEditJobDto?> CancelAsync(string o, string p, string j, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
