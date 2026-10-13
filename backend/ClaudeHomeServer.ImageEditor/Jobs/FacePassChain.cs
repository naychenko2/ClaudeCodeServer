using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Цепочка проходов подстановки (ADR-028, этап 3): проход k вызывает editor.RunAsync со своим
// ImageEditRequest — исходник = выход прохода k−1 (проход 1 — исходник базовой версии),
// маска/кроп/персонаж/образцы — прохода k. Обёртка прогресса гасит Ready у непоследних проходов
// (промежуточные выходы версиями не становятся), стадия — «проход k из N» (Run/Runs).
// Проходы идут по возрастанию номера лица. Отмена/частичный результат — шаг 10: результат каждого
// прохода и его статус доступны после цикла (PassOutcome), без переписывания.
public sealed class FacePassChain
{
    // Итог одного прохода: результат и признак отмены (для шага 10)
    public sealed record PassOutcome(ImageEditResult Result, bool Cancelled);

    public static async Task<ImageEditResult> RunAsync(
        IImageEditor editor,
        IReadOnlyList<FacePass> passes,
        ImageBytes source,
        string modelId,
        IProgress<EditProgress> progress,
        CancellationToken ct)
    {
        var n = passes.Count;
        var current = source;
        var last = default(ImageEditResult);
        for (var k = 0; k < n; k++)
        {
            var pass = passes[k];
            var request = new ImageEditRequest(
                ImageEditOp.Inpaint,
                pass.Prompt,
                current,
                pass.Mask,
                pass.References,
                1,
                null,
                null,
                modelId,
                pass.Character,
                null,
                Instruction: pass.Prompt,
                FaceCrop: pass.Crop,
                KeepMask: pass.KeepMask);
            last = await editor.RunAsync(request, new ProgressWrapper(progress, k, n), ct);
            // Выход прохода k — исходник прохода k+1 (промежуточный выход версиями не становится)
            if (last.Images is { Count: > 0 } imgs)
                current = new ImageBytes(imgs[0].Bytes, imgs[0].ContentType);
        }
        return last;
    }

    // Обёртка прогресса: гасит Ready у непоследних проходов (промежуточные выходы версиями не
    // становятся), стадия — «проход k из N» (Run = k+1, Runs = N)
    private sealed class ProgressWrapper(IProgress<EditProgress> outer, int index, int total) : IProgress<EditProgress>
    {
        public void Report(EditProgress value)
        {
            var ready = index < total - 1 ? null : value.Ready;
            outer.Report(value with { Ready = ready, Run = index + 1, Runs = total });
        }
    }
}
