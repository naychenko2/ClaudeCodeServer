using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Цепочка проходов подстановки (ADR-028, этап 3): проход k вызывает editor.RunAsync с готовым
// запросом requests[k] (собран задачей тем же путём, что одиночный запуск: ужатие + Compose),
// подменяется только исходник — выход прохода k−1 (проход 1 — исходник своего запроса).
// Сама цепочка запрос не собирает: иначе проход разошёлся бы с одиночным запуском по составу.
// Обёртка прогресса гасит Ready у непоследних проходов (промежуточные выходы версиями не
// становятся), стадия — «проход k из N» (Run/Runs).
// Проходы идут по возрастанию номера лица.
// Выход прохода k — полноразмерный файл модели, он минует ужатие PrepareAsync; перед проходом k+1
// его прогоняет prepareSource (ужатие по весу до потолка холста MaxCanvasMb ?? MaxInputMb, размер
// тот же): lossless-выход прохода k сам становится img1, и склейка в графе остаётся единственной
// точкой шва. Отказ подготовки — сбой прохода k+1.
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
    // Итог одного прохода (ADR-028, §9): N — номер лица, Status — done | failed | cancelled,
    // Similarity — сходство лица, измеренное внутри маски этого прохода (null — не считалось)
    public sealed record FacePassResult(int N, string Status, double? Similarity);

    public static async Task<ImageEditResult> RunAsync(
        IImageEditor editor,
        IReadOnlyList<FacePass> passes,
        IReadOnlyList<ImageEditRequest> requests,
        IProgress<EditProgress> progress,
        CancellationToken ct,
        Action<FacePassResult>? onResult = null,
        Func<ImageBytes, CancellationToken, Task<ImageEditCallResult<ImageBytes>>>? prepareSource = null)
    {
        var n = passes.Count;
        var k = 0;
        // Исходник прохода 1 — тот, что в его запросе (базовая версия, ужатая под модель)
        var current = requests[0].Source!;
        var last = default(ImageEditResult);
        var chain = new ChainProgress(progress, n);
        // Сбой, о котором драйвер сообщил результатом (Local, fal, Higgsfield не бросают)
        ImageEditResult? failure = null;
        try
        {
            for (; k < n; k++)
            {
                var pass = passes[k];
                chain.Advance(k);
                // Исходник прохода k+1 — выход прохода k, ужатый по весу под лимит модели. Отказ —
                // сбой именно этого прохода (k уже указывает на него): общая ветка «проход упал»
                if (k > 0 && prepareSource is not null)
                {
                    var prepared = await prepareSource(current, ct);
                    if (prepared.Value is not { } fitted)
                    {
                        failure = new ImageEditResult(EditOutcome.Failed, [], null, null, null,
                            prepared.Error ?? "Картинку не удалось подготовить");
                        break;
                    }
                    current = fitted;
                }
                var request = requests[k] with { Source = current };
                last = await editor.RunAsync(request, chain, ct);
                // Отмена задачи, о которой драйвер сообщил результатом, — та же ветка, что исключение отмены
                if (last.Outcome == EditOutcome.Cancelled) ct.ThrowIfCancellationRequested();
                failure = PassFailure(last);
                // k не двигаем: ветка «проход k упал» ниже пишет failed именно этому лицу
                if (failure is not null) break;
                // Выход прохода k — исходник прохода k+1 (промежуточный выход версиями не становится)
                var img = last.Images[0];
                current = new ImageBytes(img.Bytes, img.ContentType);
                onResult?.Invoke(new FacePassResult(pass.Face, "done", img.Similarity));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Проход k отменён на ходу, последующие не начинались — тоже cancelled
            for (var i = k; i < n; i++) onResult?.Invoke(new FacePassResult(passes[i].Face, "cancelled", null));
            // Отмена: если прошёл хотя бы один проход — его выход становится версией (В1)
            chain.ReleaseHeld();
            throw;
        }
        catch
        {
            // Исключение прохода k (кроме отмены по ct) — проход упал; текст сбоя соберёт задача
            PassFailed(k);
            throw;
        }
        if (failure is not null)
        {
            PassFailed(k);
            return failure;
        }
        // N ≥ 1 гарантирует FaceTargetRules: цикл прошёл хотя бы раз, last присвоен
        return last!;

        // Ветка «проход k упал» — одна для исключения и для сбоя результатом (ADR-028, §8–9):
        // лицу k — failed, последующие не начинались — cancelled; выход прохода k−1 (если был)
        // становится версией (В1)
        void PassFailed(int failed)
        {
            onResult?.Invoke(new FacePassResult(passes[failed].Face, "failed", null));
            for (var i = failed + 1; i < n; i++) onResult?.Invoke(new FacePassResult(passes[i].Face, "cancelled", null));
            chain.ReleaseHeld();
        }
    }

    // Сбой прохода результатом (ADR-028, §8): Outcome ≠ Ok либо Ok без картинок. null — проход удался.
    // Cancelled сюда доходит только без отмены задачи (прервали снаружи, например на узле) — это
    // сбой: человек ничего не отменял, поэтому в задачу уходит Failed, а не Cancelled
    private static ImageEditResult? PassFailure(ImageEditResult result) => result switch
    {
        // Images non-nullable по типу, но драйвер может вернуть null: Ok без списка — тоже сбой прохода
        { Outcome: EditOutcome.Ok, Images: { Count: > 0 } } => null,
        { Outcome: EditOutcome.Ok } => result with { Outcome = EditOutcome.Failed, Error = result.Error ?? "Проход не вернул картинку" },
        { Outcome: EditOutcome.Cancelled } => result with { Outcome = EditOutcome.Failed, Error = result.Error ?? "Проход прерван вне задачи" },
        _ => result,
    };

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
                    // Удержанное не стираем промежуточным отчётом без Ready (Queued/Running следующего
                    // прохода или поздний отчёт этого же): иначе падение дальше теряет версию (В1)
                    if (value.Ready is not null)
                    {
                        _held = value.Ready;
                        _heldIndex = _index;
                    }
                    ready = null;
                }
                // Авто-выпуск — только на стадии завершения без картинок.
                // На текущих драйверах ветка — no-op: Local шлёт Downloading вместе с Ready, fal/Higgsfield
                // Ready не шлют вовсе. Она для будущих драйверов, что пришлют Downloading без картинок ПОСЛЕ
                // Ready непоследнего прохода, — чтобы выход прохода k не потерялся (ADR-028, В1).
                // Падение и отмену цепочки закрывает ReleaseHeld() в RunAsync
                releaseHeld = !last && value.Stage == EditStage.Downloading && value.Ready is null;
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
