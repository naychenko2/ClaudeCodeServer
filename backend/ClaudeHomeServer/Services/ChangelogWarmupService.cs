using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Фоновый прогрев сводок «Что нового»: заранее дергает ChangelogService.GetDay
/// для холодных дней, чтобы пользователь при заходе получал кеш мгновенно, а не
/// ждал генерацию (~2 мин на жирный день). Живет внутри приложения — работает
/// у любого, кто запустил продукт, независимо от способа деплоя.
///
/// Правила прогрева:
/// - только последние Warmup:BackfillDays дней (не вся история окна);
/// - только дни без актуальной сводки (нет записи в кеше либо хеш sha-набора разошелся);
/// - «остыв»: день с недавним последним коммитом (моложе CooldownMinutes) пропускаем —
///   пока коммиты сыплются (типично сегодняшний), сводка тут же протухнет;
/// - последовательно, максимум MaxDaysPerPass дней за проход (параллель замерена
///   как проигрышная — см. комментарий у ChangelogService.SummarizeDay);
/// - degraded-результат (claude не ответил/не залогинен) — прерываем проход,
///   чтобы не молотить остальные дни впустую (backoff до следующего тика).
/// </summary>
public class ChangelogWarmupService(ChangelogService changelog, IConfiguration config,
    ILogger<ChangelogWarmupService> log) : BackgroundService
{
    private readonly bool _enabled = !bool.TryParse(config["Changelog:Warmup:Enabled"], out var e) || e;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        int.TryParse(config["Changelog:Warmup:IntervalMinutes"], out var i) && i > 0 ? i : 10);
    private readonly int _backfillDays =
        int.TryParse(config["Changelog:Warmup:BackfillDays"], out var d) && d > 0 ? d : 5;
    private readonly int _maxDaysPerPass =
        int.TryParse(config["Changelog:Warmup:MaxDaysPerPass"], out var m) && m > 0 ? m : 2;
    private readonly TimeSpan _cooldown = TimeSpan.FromMinutes(
        int.TryParse(config["Changelog:Warmup:CooldownMinutes"], out var c) && c >= 0 ? c : 15);

    // Стартовая задержка перед первым тиком — не драться за CPU с запуском приложения
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            log.LogInformation("Прогрев сводок «Что нового» выключен (Changelog:Warmup:Enabled=false)");
            return;
        }
        try
        {
            await Task.Delay(StartupDelay, ct);
            // Первый проход сразу после задержки, дальше — по таймеру
            await SafeTickAsync(ct);
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
                await SafeTickAsync(ct);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try { await TickAsync(DateTimeOffset.Now, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.LogError(ex, "Ошибка тика прогрева сводок «Что нового»"); }
    }

    /// <summary>Один проход прогрева. Публичный для юнит-тестов.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!changelog.GetStatus().Configured) return;

        var toWarm = changelog.GetWarmupCandidates(_backfillDays)
            .Where(c => ShouldWarm(c, now, _cooldown)) // уже отсортированы от новых к старым
            .Take(_maxDaysPerPass)
            .ToList();

        foreach (var candidate in toWarm)
        {
            ct.ThrowIfCancellationRequested();
            var day = await changelog.GetDay(candidate.Date);
            if (day.Degraded)
            {
                // claude не ответил (таймаут / не залогинен) — остальные дни ждут следующего тика
                log.LogWarning("Прогрев дня {Date} не удался ({Reason}) — проход прерван",
                    candidate.Date, day.DegradedReason);
                return;
            }
            log.LogInformation("Сводка дня {Date} прогрета фоном ({Count} пунктов)",
                candidate.Date, day.Items.Count);
        }
    }

    // Чистый предикат отбора — извлечен для юнит-тестов
    internal static bool ShouldWarm(WarmupCandidate c, DateTimeOffset now, TimeSpan cooldown) =>
        !c.Cached && now - c.LastCommitAt >= cooldown;
}
