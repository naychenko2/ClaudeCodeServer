using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// LTX-2.5 в шве ILocalVideoMedia: тот же ComfyClient, очередь и реестр прогонов, что у H3, свой граф
// (ComfyWorkflows.Ltx25Video). Без апскейла «Макс»: у съёмки нет латентов, SubmitUpscaleAsync честно откажет.
public sealed partial class LocalVideoMediaAdapter
{
    // Замер 14.11 на 3090, 1280×704, 5 с: холодный старт 79 с, прогретая модель 57–71 с. В котировку — холодная
    // цифра, короче 5 с — пропорционально числу кадров
    internal const int LtxEtaSecondsAt5 = 80;

    private static int? LtxEta(LocalVideoRequest request) =>
        request.Seconds is >= ComfyWorkflows.Ltx25MinSeconds and <= ComfyWorkflows.Ltx25MaxSeconds
            ? Math.Max(30, LtxEtaSecondsAt5 * ComfyWorkflows.Ltx25Frames(request.Seconds) / ComfyWorkflows.Ltx25Frames(5))
            : null;

    private async Task<LocalVideoSubmitted> SubmitLtxAsync(LocalVideoRequest request, LocalMediaOptions options, CancellationToken ct)
    {
        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length == 0) return LocalVideoSubmitted.Fail("Нужен запрос — что происходит в кадре.");
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            return LocalVideoSubmitted.Fail($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");
        if (request.Seconds is < ComfyWorkflows.Ltx25MinSeconds or > ComfyWorkflows.Ltx25MaxSeconds)
            return LocalVideoSubmitted.Fail(
                $"LTX-2.5 снимает ролик от {ComfyWorkflows.Ltx25MinSeconds} до {ComfyWorkflows.Ltx25MaxSeconds} секунд.");
        var portrait = string.Equals(request.Size, "portrait", StringComparison.OrdinalIgnoreCase);

        LocalMediaService.InputFile? first = null;
        if (request.FirstFrame is { Length: > 0 })
        {
            try
            {
                first = LocalMediaService.CheckInput(request.FirstFrame, FirstSlot, FirstSlot, LocalMediaService.MediaKind.Image);
            }
            catch (LocalMediaInputException ex)
            {
                return LocalVideoSubmitted.Fail(ex.Message);
            }
        }

        LocalMediaService.InputFile? last = null;
        if (request.LastFrame is { Length: > 0 })
        {
            try
            {
                last = LocalMediaService.CheckInput(request.LastFrame, FirstSlot, FirstSlot, LocalMediaService.MediaKind.Image);
            }
            catch (LocalMediaInputException ex)
            {
                return LocalVideoSubmitted.Fail(ex.Message);
            }
        }

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже или выберите другого поставщика.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        var eta = LtxEta(request) ?? LtxEtaSecondsAt5;
        try
        {
            var firstName = first is null ? null : await comfy.UploadImageAsync(first.Bytes, $"{id}-in1{first.Extension}", ct);
            var lastName = last is null ? null : await comfy.UploadImageAsync(last.Bytes, $"{id}-in2{last.Extension}", ct);
            var graph = ComfyWorkflows.Ltx25Video(prompt, firstName, lastName, request.Seconds, portrait, seed, $"{ComfyWorkflows.OutputFolder}/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            var size = ComfyWorkflows.Ltx25Size(portrait);
            var details = new LocalRunDetails(LocalVideoModels.Ltx25, prompt, new LocalRunParams(
                Width: size.Width, Height: size.Height, DurationSec: request.Seconds,
                Frames: ComfyWorkflows.Ltx25Frames(request.Seconds), Steps: 11, Seed: seed));
            ledger?.Register(queued.PromptId, request.Origin, eta, details);
            Sweep();
            _shots[queued.PromptId] = new Shot(id, prompt, firstName ?? "", lastName, size, request.Seconds, seed, Now, request.Origin);
            return new LocalVideoSubmitted(queued.PromptId, queue, eta, null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял задачу LTX-2.5: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
    }
}
