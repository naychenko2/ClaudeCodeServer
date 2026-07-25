using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Spend;

// Обслуживание хранилища расхода: разовый backfill истории при первом старте и периодический
// rollup дней, выпавших из детального окна (раз в час — дешёвая проверка, обычно no-op).
//
// Backfill: у StoredResultMessage нет отметки времени, поэтому ходы сессии распределяются
// равномерно между Session.CreatedAt и Session.UpdatedAt — по дням агрегатов это даёт
// правдоподобную картину, а точная минута для аналитики не важна. Ходы с расчётным временем
// после T0 (момент включения live-сбора) пропускаются — они уже записаны штатной точкой.
public sealed class SpendMaintenanceService(SpendStore store, SessionManager sessions,
    ChatHistoryService history, ILogger<SpendMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Не тормозим старт приложения: скан историй — в фоне после подъёма
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        if (!store.BackfillDone)
        {
            try
            {
                var imported = await BackfillAsync(DateTime.UtcNow, ct);
                store.MarkBackfillDone();
                log.LogInformation("spend: backfill истории завершён, импортировано записей: {Count}", imported);
            }
            catch (Exception ex)
            {
                // Маркер не ставим — попробуем на следующем старте
                log.LogError(ex, "spend: backfill истории не удался");
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try { store.RollupOlderThan(store.WindowStart); }
            catch (Exception ex) { log.LogError(ex, "spend: rollup не удался"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    // Разовый импорт расхода из историй чатов. t0 — граница дедупликации с live-сбором.
    internal async Task<int> BackfillAsync(DateTime t0, CancellationToken ct)
    {
        var imported = 0;
        foreach (var session in sessions.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            if (session.ClaudeSessionId is null) continue;

            List<StoredMessage> messages;
            try { messages = await history.LoadAsync(session.ClaudeSessionId); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "spend: backfill — история {Session} не прочиталась", session.Id);
                continue;
            }

            var results = messages.OfType<StoredResultMessage>().Where(m => m.Usage is not null).ToList();
            var falCosts = messages.OfType<StoredFalCostMessage>().ToList();
            if (results.Count == 0 && falCosts.Count == 0) continue;

            var ownerId = sessions.ResolveOwnerId(session) ?? "";
            var provider = SpendSources.NormalizeProvider(session.Provider);
            var total = results.Count + falCosts.Count;
            var index = 0;

            foreach (var m in results)
            {
                var ts = Spread(session.CreatedAt, session.UpdatedAt, index++, total);
                if (ts >= t0) continue;
                var u = m.Usage!;
                store.Record(new SpendRecord
                {
                    Timestamp = ts,
                    OwnerId = ownerId,
                    ProjectId = session.ProjectId,
                    SessionId = session.Id,
                    TaskId = session.TaskId,
                    PersonaId = session.PersonaId,
                    Provider = provider,
                    Model = session.Model,
                    Source = SpendSources.IsFree(provider, session.Model)
                        ? SpendSources.Free : SpendSources.ChatTurn,
                    InputTokens = u.InputTokens,
                    OutputTokens = u.OutputTokens,
                    CacheReadTokens = u.CacheReadTokens,
                    CacheCreationTokens = u.CacheCreationTokens,
                    CostUsd = m.TotalCostUsd,
                    DurationMs = m.DurationMs,
                });
                imported++;
            }

            foreach (var m in falCosts)
            {
                var ts = Spread(session.CreatedAt, session.UpdatedAt, index++, total);
                if (ts >= t0) continue;
                store.Record(new SpendRecord
                {
                    Timestamp = ts,
                    OwnerId = ownerId,
                    ProjectId = session.ProjectId,
                    SessionId = session.Id,
                    TaskId = session.TaskId,
                    PersonaId = session.PersonaId,
                    Provider = "fal",
                    Model = m.EndpointId,
                    Source = SpendSources.Fal,
                    CostUsd = m.CostUsd,
                    Generations = 1,
                    Label = m.EndpointId,
                });
                imported++;
            }
        }

        // Всё старше детального окна сразу сворачивается в дневные агрегаты
        store.RollupOlderThan(store.WindowStart);
        return imported;
    }

    // Равномерное распределение i-й записи из n по отрезку [from..to]
    internal static DateTime Spread(DateTime from, DateTime to, int i, int n)
    {
        if (to <= from || n <= 0) return from;
        return from + TimeSpan.FromTicks((to - from).Ticks * (i + 1) / (n + 1));
    }
}
