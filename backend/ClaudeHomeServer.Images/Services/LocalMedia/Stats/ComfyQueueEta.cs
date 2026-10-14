namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Расчёт «когда стартует i-я ждущая задача» по очереди ComfyUI. Чистая функция: без IO,
// часов и состояния — now/startedAt/eta приходят аргументом, поэтому тесты детерминированы.
//
// Правило: старт i-й = остаток идущей + Σ eta ждущих впереди. Остаток идущей =
// eta − (now − startedAt), не меньше 0 (перерасход — 0, «скоро»). ЕСЛИ у любого звена
// цепочки (идущая или ждущая впереди) eta = null — для этой и всех последующих null:
// время не обещаем. Без идущей — только Σ впереди.
public static class ComfyQueueEta
{
    // Идущая задача: оценка полной длительности (eta, сек) и момент старта; null — не знаем
    public sealed record Running(int? EtaSeconds, DateTimeOffset? StartedAt);

    // Ждущая: оценка полной длительности (eta, сек); null — не знаем
    public sealed record Waiting(int? EtaSeconds);

    // Старт i-й ждущей в секундах от now; null — время не обещаем (см. правило выше)
    public static int? StartInSeconds(Running? running, DateTimeOffset now, IReadOnlyList<Waiting> queue, int index)
    {
        // Остаток идущей: eta − (now − startedAt), не меньше 0 (перерасход — 0, «скоро»).
        // Идущая есть, но её eta = null → время не обещаем (null)
        int? runningRemaining = null;
        if (running is { } r)
        {
            if (r.EtaSeconds is not { } eta) return null;
            // startedAt позже now (гонка часов) → elapsed отрицательный, eta + |elapsed| завышал бы остаток
            var elapsed = r.StartedAt is { } started ? Math.Max(0, (now - started).TotalSeconds) : 0;
            runningRemaining = Math.Max(0, (int)(eta - elapsed));
        }

        // Σ eta ждущих впереди; null, если у любого впереди eta = null
        int? ahead = 0;
        for (var i = 0; i < index; i++)
        {
            if (queue[i].EtaSeconds is not { } aheadEta)
            {
                ahead = null;
                break;
            }
            ahead = (ahead ?? 0) + aheadEta;
        }

        // Без идущей — только Σ впереди; с идущей — остаток + Σ, null если хоть одно звено null
        if (running is null) return ahead;
        if (ahead is null) return null;
        return runningRemaining + ahead.Value;
    }
}
