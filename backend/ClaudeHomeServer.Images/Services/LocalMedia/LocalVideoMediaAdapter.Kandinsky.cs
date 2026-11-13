using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Kandinsky 6 Lite в шве ILocalVideoMedia (Ш5): тот же ComfyClient, очередь и реестр прогонов, что у H3, свой граф
// (ComfyWorkflows.Kandinsky6Video). Без апскейла: у съёмки нет латентов, SubmitUpscaleAsync честно откажет.
public sealed partial class LocalVideoMediaAdapter
{
    // Замер ночи 13.11 на 3090: ≈160 с на прогретую модель, после H3 холодная загрузка добавляет ≈10 с и больше.
    // В котировку идёт холодная цифра: Kandinsky берут по ручному выбору, обычно после H3
    internal const int KandinskyEtaSeconds = 170;

    private static int? KandinskyEta(LocalVideoRequest request) =>
        request.Seconds == ComfyWorkflows.Kandinsky6Seconds ? KandinskyEtaSeconds : null;

    private async Task<LocalVideoSubmitted> SubmitKandinskyAsync(LocalVideoRequest request, LocalMediaOptions options, CancellationToken ct)
    {
        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length == 0) return LocalVideoSubmitted.Fail("Нужен запрос — что происходит в кадре.");
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            return LocalVideoSubmitted.Fail($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");
        if (request.Seconds != ComfyWorkflows.Kandinsky6Seconds)
            return LocalVideoSubmitted.Fail("Kandinsky 6 Lite снимает ролик только в 5 секунд.");
        if (request.LastFrame is { Length: > 0 })
            return LocalVideoSubmitted.Fail("Kandinsky 6 Lite не берёт последний кадр — только текст или первый кадр.");

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

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже или выберите другого поставщика.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        try
        {
            var firstName = first is null ? null : await comfy.UploadImageAsync(first.Bytes, $"{id}-in1{first.Extension}", ct);
            var graph = ComfyWorkflows.Kandinsky6Video(prompt, firstName, seed, $"{ComfyWorkflows.OutputFolder}/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            var size = ComfyWorkflows.Kandinsky6Size;
            var details = new LocalRunDetails(LocalVideoModels.Kandinsky6Lite, prompt, new LocalRunParams(
                Width: size.Width, Height: size.Height, DurationSec: ComfyWorkflows.Kandinsky6Seconds,
                Frames: ComfyWorkflows.Kandinsky6Frames, Steps: 10, Seed: seed));
            ledger?.Register(queued.PromptId, request.Origin, KandinskyEtaSeconds, details);
            Sweep();
            _shots[queued.PromptId] = new Shot(id, prompt, firstName ?? "", null, size, ComfyWorkflows.Kandinsky6Seconds, seed, Now, request.Origin);
            return new LocalVideoSubmitted(queued.PromptId, queue, KandinskyEtaSeconds, null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял задачу Kandinsky 6: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
    }
}
