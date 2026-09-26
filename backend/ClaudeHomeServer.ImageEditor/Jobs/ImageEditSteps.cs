using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;

namespace ClaudeHomeServer.Services.Images.Editing;

// Правки без ИИ и шаги истории (ADR-018 §9): POST …/transform. Бесплатно — без поставщика,
// котировки и записи трат. Правки без ИИ и применённые варианты — шаги ОДНОЙ ленты с Parent.
// В проект не пишет никогда: файл проекта приходит байтами, результат ложится в рабочую папку.
// dryRun кодирует в память и ничего не пишет — только вес для «2,4 МБ → 310 КБ».
public sealed class ImageEditSteps(IImageRaster raster, ImageEditWorkspace workspace, IImageEditJobs jobs)
{
    public ImageEditCallResult<ImageTransformResponse> Transform(
        string ownerId, string projectId, ImageTransformRequest request, ProjectImage? file, bool dryRun)
    {
        var b = request.Base;
        var ops = request.Ops ?? [];
        var kinds = (file is not null ? 1 : 0)
                    + (string.IsNullOrWhiteSpace(b?.StepId) ? 0 : 1)
                    + (string.IsNullOrWhiteSpace(b?.JobId) ? 0 : 1);
        if (b is null || kinds != 1)
            return Invalid("База правки — ровно одно: путь файла, шаг истории или вариант задачи");

        byte[] bytes;
        string editId;
        string? parent = null, sourcePath = null, jobId = null;
        int? variant = null;
        var kind = ImageEditStepKinds.Transform;

        if (file is not null)
        {
            bytes = file.Bytes;
            editId = ImageEditWorkspace.NewStepId();
            sourcePath = file.RelativePath;
        }
        else if (!string.IsNullOrWhiteSpace(b.StepId))
        {
            if (Open(ownerId, projectId, b.StepId.Trim()) is not { } found) return StepNotFound();
            bytes = found.Image.Bytes;
            editId = found.Step.EditId;
            parent = found.Step.StepId;
            sourcePath = found.Step.SourcePath;
        }
        else
        {
            jobId = b.JobId!.Trim();
            if (b.Variant is not { } n) return Invalid("Не указан номер варианта");
            var job = jobs.Get(ownerId, projectId, jobId);
            var image = job is null ? null : jobs.OpenVariant(ownerId, projectId, jobId, n);
            if (job is null || image is null)
                return ImageEditCallResult<ImageTransformResponse>.Fail(ImageEditErrorCodes.JobNotFound, "Задача не найдена");
            bytes = image.Bytes;
            variant = n;
            kind = ImageEditStepKinds.Variant;
            // Вариант продолжает ленту шага, с которого запускали правку; без него — своя лента,
            // а не папка задачи: шаги не смешиваются с вариантами v{n}
            var baseStep = job.BaseStepId is { } s ? Open(ownerId, projectId, s) : null;
            parent = baseStep?.Step.StepId;
            editId = baseStep?.Step.EditId ?? ImageEditWorkspace.NewStepId();
            sourcePath = baseStep?.Step.SourcePath;
        }

        var outcome = raster.Apply(bytes, ops, request.Encode);
        if (!outcome.Ok)
            return ImageEditCallResult<ImageTransformResponse>.Fail(
                outcome.Error == RasterError.Busy ? ImageEditErrorCodes.TooManyJobs : ImageEditErrorCodes.InvalidRequest,
                outcome.Message ?? "Правка не выполнена");
        var result = outcome.Image!;
        if (dryRun)
            return ImageEditCallResult<ImageTransformResponse>.Ok(
                new ImageTransformResponse(null, result.Width, result.Height, result.Bytes.LongLength));

        var step = new ImageEditStep(ImageEditWorkspace.NewStepId(), editId, projectId, parent, kind,
            result.Width, result.Height, result.Bytes.LongLength, DateTime.UtcNow, sourcePath, jobId, variant);
        workspace.SaveStep(ownerId, step, result.Bytes);
        return ImageEditCallResult<ImageTransformResponse>.Ok(
            new ImageTransformResponse(step.StepId, step.Width, step.Height, step.Bytes));
    }

    // Шаг своего проекта; чужой владелец или проект неотличимы от отсутствующего
    public (ImageEditStep Step, EditedImage Image)? Open(string ownerId, string projectId, string stepId) =>
        workspace.OpenStep(ownerId, stepId) is { } found && found.Step.ProjectId == projectId ? found : null;

    private static ImageEditCallResult<ImageTransformResponse> StepNotFound() =>
        ImageEditCallResult<ImageTransformResponse>.Fail(ImageEditErrorCodes.StepNotFound,
            "Шаг истории не найден — возможно, он устарел");

    private static ImageEditCallResult<ImageTransformResponse> Invalid(string error) =>
        ImageEditCallResult<ImageTransformResponse>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}

// Файл проекта, уже прочитанный контроллером из-под ProjectLinkGuard; RelativePath — от корня
public sealed record ProjectImage(string RelativePath, byte[] Bytes);
