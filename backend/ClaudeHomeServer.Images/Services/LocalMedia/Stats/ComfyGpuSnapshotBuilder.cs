using Microsoft.Extensions.Logging;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Общий билдер снимка очереди GPU (шаг 26б, ревью): единая точка правды для адаптеров
// видео/аудио/картинок. Принимает монитор + реестр + трекер и по тикету и позиции отдаёт
// LocalGpuQueueInfo?. null — монитора нет. Позиция: 0 = идёт, N = ждёт (N задач впереди).
// Адаптеры только вызывают билдер — логика изоляции/процента/ETA-цепочки живёт в одном месте.
public sealed class ComfyGpuSnapshotBuilder(
    ComfyQueueMonitor? monitor,
    ComfyPromptLedger? ledger,
    ComfyProgressTracker? steps,
    ILogger log)
{
    // Ревью Глеба (minor): билдер не должен ронять опрос. Он вызывается из catch (ComfyException)
    // адаптеров: если бросит, исключение выйдет наружу и обработчик не спасёт. Перехват любого
    // исключения (кроме отмены) → null, одно предупреждение с троттлингом (не на каждый опрос)
    internal TimeSpan WarnThrottle { get; init; } = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastWarn;

    // Позиция задачи в снимке монитора: 0 = идёт, N = ждёт (N задач впереди); null — не в очереди
    public static int? PositionInSnapshot(ComfyQueueMonitorSnapshot snap, string ticket)
    {
        if (snap.Running.Any(i => i.PromptId == ticket)) return 0;
        var i = snap.Pending.ToList().FindIndex(p => p.PromptId == ticket);
        return i < 0 ? null : snap.Running.Count + i;
    }

    // Снимок очереди GPU: Ahead — позиция, StartInSeconds — ComfyQueueEta, Steps/Percent — трекер
    // (точные шаги первого этапа) или оценка по ETA, Remaining — eta − elapsed, Busy — чужая/тяжёлая
    // задача впереди (только агрегат kind/heavy/остаток). null — монитора нет, снимок down или сбой сборки.
    public LocalGpuQueueInfo? Build(string ticket, int? position)
    {
        try
        {
            return BuildCore(ticket, position);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var now = TimeProvider.System.GetUtcNow();
            if (_lastWarn is { } last && now - last < WarnThrottle) return null;
            _lastWarn = now;
            log.LogWarning(ex, "Снимок очереди GPU: сбой сборки — {Error}", ex.Message);
            return null;
        }
    }

    private LocalGpuQueueInfo? BuildCore(string ticket, int? position)
    {
        if (monitor is null) return null;
        var snap = monitor.Snapshot;
        if (snap is null) return null;

        // M1: стенд недоступен — снимаем downSince (unix-мс), остальное null/Running=false;
        // фронт isStandDown(gpu) (gpu.downSince != null) видит «стенд недоступен»
        if (snap.Down)
            return new LocalGpuQueueInfo(null, null, null, null, false, null, null, snap.DownSince?.ToUnixTimeMilliseconds(), false);

        if (position is null) return null;

        // M2: идущая (позиция 0) — Running=true, Ahead=null (не «Следующая»); ждущая — Ahead=число впереди
        var isRunning = position == 0;
        var ahead = isRunning ? null : position;

        // StartInSeconds: ComfyQueueEta (цепочка с null-звеном → null)
        int? startIn = null;
        if (position is > 0)
        {
            var runningItems = snap.Running;
            var pendingItems = snap.Pending;
            ComfyQueueEta.Running? running = null;
            if (runningItems.Count > 0)
            {
                var first = runningItems[0];
                var (rEta, rStart) = ResolveEtaAndStart(first.PromptId);
                running = new ComfyQueueEta.Running(rEta, rStart);
            }
            var waiting = pendingItems
                .Where(p => p.PromptId != ticket)
                .Take(position.Value - 1)
                .Select(p => new ComfyQueueEta.Waiting(ResolveEta(p.PromptId)))
                .ToList();
            startIn = ComfyQueueEta.StartInSeconds(running, TimeProvider.System.GetUtcNow(), waiting, position.Value - 1);
        }

        // Шаги и процент: точные шаги первого этапа — PercentExact=true (потолок 99); этап > 1 и
        // отсутствие прогресса — оценка по ETA от StartedAt (потолок 95, PercentExact=false)
        LocalGpuSteps? gpuSteps = null;
        int? percent = null;
        bool percentExact = false;
        var progress = steps?.Get(ticket);
        if (progress is { } p && p.Stage == 1)
        {
            gpuSteps = new LocalGpuSteps(p.Step, p.Total, p.Stage);
            percent = p.Percent;
            percentExact = true;
        }
        else
        {
            var (tEta, tStart) = ResolveEtaAndStart(ticket);
            if (tEta is { } te && tStart is { } ts)
            {
                var elapsed = Math.Max(0, (TimeProvider.System.GetUtcNow() - ts).TotalSeconds);
                var rem = Math.Max(0, (int)(te - elapsed));
                percent = Math.Min(95, (int)(100.0 * (1.0 - (double)rem / te)));
                percentExact = false;
            }
        }

        // RemainingSeconds: eta − elapsed
        int? remaining = null;
        var (myEta, myStart) = ResolveEtaAndStart(ticket);
        if (myEta is { } me && myStart is { } ms)
        {
            var elapsed = Math.Max(0, (TimeProvider.System.GetUtcNow() - ms).TotalSeconds);
            remaining = Math.Max(0, (int)(me - elapsed));
        }

        // Busy: чужая или тяжёлая задача впереди — только агрегат (kind/heavy/остаток)
        LocalGpuBusy? busy = null;
        if (position is > 0 && snap.Running.Count > 0)
        {
            var blocker = snap.Running[0];
            var (kind, heavy, rem) = ResolveBusy(blocker.PromptId);
            if (heavy || IsForeign(blocker.PromptId))
                busy = new LocalGpuBusy(kind, heavy, rem);
        }

        // DownSince — unix-мс (long), как все времена фичи: внутренний снимок монитора держит
        // DateTimeOffset, конвертируем в точке заполнения DTO (единая точка, без двойной конверсии)
        return new LocalGpuQueueInfo(ahead, startIn, gpuSteps, percent, percentExact, remaining, busy, snap.DownSince?.ToUnixTimeMilliseconds(), isRunning);
    }

    // ETA и момент старта из реестра; null — не знаем
    private (int? Eta, DateTimeOffset? StartedAt) ResolveEtaAndStart(string promptId)
    {
        if (ledger?.TryGet(promptId, out var rec) is true && rec is not null)
            return (rec.EtaSeconds, rec.StartedAt);
        return (null, null);
    }

    private int? ResolveEta(string promptId) => ResolveEtaAndStart(promptId).Eta;

    // Агрегат «кто держит GPU»: kind, heavy, остаток. Без ownerId/sessionId/prompt/slug
    private (string Kind, bool Heavy, int? Remaining) ResolveBusy(string promptId)
    {
        if (ledger?.TryGet(promptId, out var rec) is true && rec is not null)
        {
            var origin = rec.Origin;
            var kind = origin?.Kind ?? "other";
            var heavy = origin?.Heavy ?? false;
            int? rem = null;
            if (rec.EtaSeconds is { } eta && rec.StartedAt is { } st)
                rem = Math.Max(0, (int)(eta - (TimeProvider.System.GetUtcNow() - st).TotalSeconds));
            return (kind, heavy, rem);
        }
        // Чужой (нет в реестре): kind из классификации монитора
        var snap = monitor?.Snapshot;
        if (snap is not null)
        {
            var foreign = snap.Foreign.FirstOrDefault(f => f.PromptId == promptId);
            if (foreign is not null)
            {
                var kind = foreign.Kind == ComfyForeignKind.Transcription ? "audio" : "other";
                return (kind, false, null);
            }
        }
        return ("other", false, null);
    }

    // Чужой ли прогон: есть в Foreign-списке монитора
    private bool IsForeign(string promptId) =>
        monitor?.Snapshot is { } snap && snap.Foreign.Any(f => f.PromptId == promptId);
}
