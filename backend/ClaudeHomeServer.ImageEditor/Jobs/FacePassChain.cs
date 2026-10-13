using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Цепочка проходов подстановки (ADR-028, этап 3): проход k вызывает editor.RunAsync со своим
// ImageEditRequest — исходник = выход прохода k−1 (проход 1 — исходник базовой версии),
// маска/кроп/персонаж/образцы — прохода k. Обёртка прогресса гасит Ready у непоследних проходов
// (промежуточные выходы версиями не становятся), стадия — «проход k из N» (Run/Runs).
// Проходы идут по возрастанию номера лица.
//
// Частичный результат (ADR-028, В1, шаг 10): выход прохода 1 удерживается, а не теряется.
// FailAsync задачи не сохраняет ImageEditResult.Images — версиями становятся только варианты,
// отданные по ходу через Ready, а обёртка шаг 9 гасит Ready у непоследних проходов. Поэтому
// обёртка запоминает погашенный Ready прохода k: если цепочка дошла до последнего прохода —
// отпускает только его; если проход k+1 упал или цепочку отменили — отпускает удержанный Ready
// предыдущего прохода как итоговую версию ДО финализации задачи, так чтобы в результате была
// ровно одна версия. Отмена/падение ВО ВРЕМЯ прохода 1 — версий нет (удержанного Ready ещё нет).
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
        var chain = new ChainProgress(progress, n);
        try
        {
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
                chain.Advance(k);
                last = await editor.RunAsync(request, chain, ct);
                // Выход прохода k — исходник прохода k+1 (промежуточный выход версиями не становится)
                if (last.Images is { Count: > 0 } imgs)
                    current = new ImageBytes(imgs[0].Bytes, imgs[0].ContentType);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена: если прошёл хотя бы один проход — его выход становится версией (В1)
            chain.ReleaseHeld();
            throw;
        }
        catch
        {
            // Падение прохода k: выход прохода k−1 (если был) становится версией (В1)
            chain.ReleaseHeld();
            throw;
        }
        return last;
    }

    // Обёртка прогресса: гасит Ready у непоследних проходов (промежуточные выходы версиями не
    // становятся), стадия — «проход k из N» (Run = k+1, Runs = N). Удержанный Ready последнего
    // завершённого прохода отпускается как итоговая версия, если цепочка оборвалась (сбой или
    // отмена) до последнего прохода. Advance переключает индекс до RunAsync прохода; Report
    // зовётся драйвером из RunAsync, поэтому индекс актуален.
    private sealed class ChainProgress(IProgress<EditProgress> outer, int total) : IProgress<EditProgress>
    {
        private readonly object _gate = new();
        private int _index;
        private IReadOnlyList<EditedImage>? _held;
        // Индекс прохода, выход которого удержан: для корректной стадии «проход k из N» при выпуске
        private int _heldIndex;

        public void Advance(int index)
        {
            lock (_gate) _index = index;
        }

        public void Report(EditProgress value)
        {
            bool releaseHeld;
            var ready = value.Ready;
            lock (_gate)
            {
                var last = _index == total - 1;
                // Гасим Ready непоследнего прохода, но держим его: если цепочка оборвётся,
                // выход этого прохода — итоговая версия. Последний проход отпускаем как есть
                if (!last)
                {
                    _held = value.Ready;
                    _heldIndex = _index;
                    ready = null;
                }
                releaseHeld = !last && value.Ready is null;
            }
            if (releaseHeld)
            {
                lock (_gate)
                {
                    var held = _held;
                    _held = null;
                    outer.Report(value with { Ready = held, Run = _heldIndex + 1, Runs = total });
                }
            }
            else
            {
                outer.Report(value with { Ready = ready, Run = _index + 1, Runs = total });
            }
        }

        // Отпускает удержанный Ready последнего завершённого прохода как итоговую версию;
        // вызывается при сбое или отмене цепочки. Idempotent: повторный вызов ничего не делает
        public void ReleaseHeld()
        {
            lock (_gate)
            {
                var held = _held;
                _held = null;
                if (held is not null)
                    outer.Report(new EditProgress(EditStage.Downloading, Run: _heldIndex + 1, Runs: total, Ready: held));
            }
        }
    }
}
