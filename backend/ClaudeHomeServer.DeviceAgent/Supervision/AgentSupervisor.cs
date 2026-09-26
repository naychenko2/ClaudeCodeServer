using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>Дочерний <c>run</c> глазами супервизора (боевой — <see cref="ProcessChildLauncher"/>).</summary>
internal interface ISupervisedChild : IDisposable
{
    int Id { get; }

    /// <summary>Код выхода; завершается, когда процесс вышел.</summary>
    Task<int> Exited { get; }

    /// <summary>Вежливая остановка (Unix — SIGTERM), через <paramref name="grace"/> — жёсткая.</summary>
    Task StopAsync(TimeSpan grace);

    /// <summary>Убить сразу вместе с потомками.</summary>
    void Kill();
}

internal interface IChildLauncher
{
    /// <summary>Запустить <c>{exe} run</c> по контракту: маркер healthy — в <paramref name="healthyFile"/>.</summary>
    ISupervisedChild Start(string executable, string workingDirectory, string healthyFile);
}

/// <summary>Время и ожидания супервизора — отдельно, чтобы тесты шли без настоящих минут.</summary>
internal interface ISupervisorClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
    /// <summary>Дождаться задачи; отмена — <see cref="OperationCanceledException"/>.</summary>
    Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct);
}

internal sealed class SystemSupervisorClock : ISupervisorClock
{
    public static readonly SystemSupervisorClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
    public Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct) => task.WaitAsync(ct);
}

internal sealed record SupervisorOptions
{
    /// <summary>Сколько новая версия может не писать healthy, прежде чем её откатят (Р7).</summary>
    public TimeSpan HealthyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthyPoll { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan BackoffMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Дочерний, проживший дольше, считается стабильным: бэкофф сбрасывается.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Нет активной версии — через сколько посмотреть снова.</summary>
    public TimeSpan NoVersionRetry { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// <c>ai-home-agent supervise</c> (Р7, пока без самообновления):
/// - поднимает дочерний <c>run</c> из <c>versions/{active}</c>;
/// - код 75 — перечитать <c>active</c> и поднять новую версию без паузы;
/// - любой другой код — перезапуск с бэкоффом 1→2→4…→60 с, сброс после 5 мин жизни;
/// - версия без маркера healthy за 60 с (или упавшая до него) — убить дочерний, пометить
///   версию плохой, вернуть <c>active</c> на <c>previous</c> и переписать автозапуск;
/// - версия, помеченная плохой меньше суток назад, не пробуется: сразу откат.
/// Откатываться некуда — версия продолжает работать: лучше агент, который ещё не
/// дозвонился до сервера, чем никакого.
/// </summary>
internal sealed class AgentSupervisor(
    AgentLayout layout,
    IChildLauncher launcher,
    Action<string> repointAutostart,
    ILogger log,
    SupervisorOptions? options = null,
    ISupervisorClock? clock = null)
{
    private readonly SupervisorOptions _options = options ?? new SupervisorOptions();
    private readonly ISupervisorClock _clock = clock ?? SystemSupervisorClock.Instance;

    public async Task<int> RunAsync(CancellationToken stop)
    {
        var backoff = _options.BackoffMin;
        while (!stop.IsCancellationRequested)
        {
            var active = layout.ReadActive();
            if (active is null || !layout.IsInstalled(active))
            {
                log.LogWarning("Активной версии нет (active = «{Active}») — жду {Delay} с", active, _options.NoVersionRetry.TotalSeconds);
                await Delay(_options.NoVersionRetry, stop);
                continue;
            }

            if (layout.IsBad(active, _clock.Now) && RollbackTarget(active) is { } fallback)
            {
                log.LogWarning("Версия {Active} помечена плохой — не пробую, возвращаюсь на {Previous}", active, fallback);
                Rollback(active, fallback, markBad: false);
                continue;
            }

            var mustProve = !layout.IsHealthy(active);
            var startedAt = _clock.Now;
            using var child = launcher.Start(layout.ExeOf(active), layout.VersionDir(active), layout.HealthyOf(active));
            log.LogInformation("Дочерний run {Version} pid={Pid}{Prove}", active, child.Id,
                mustProve ? $", жду healthy до {_options.HealthyTimeout.TotalSeconds:0} с" : "");

            if (mustProve)
            {
                var proven = await WaitHealthyAsync(active, child, stop);
                if (stop.IsCancellationRequested) { await child.StopAsync(_options.StopGrace); break; }
                if (proven)
                {
                    log.LogInformation("Версия {Version} здорова через {Seconds:0.0} с", active, (_clock.Now - startedAt).TotalSeconds);
                }
                else if (RollbackTarget(active) is { } previous)
                {
                    log.LogWarning(child.Exited.IsCompleted
                            ? "Версия {Version} упала, не став здоровой — откат на {Previous}"
                            : "Версия {Version} не записала healthy за отведённое время — откат на {Previous}",
                        active, previous);
                    await KillAsync(child);
                    Rollback(active, previous, markBad: true);
                    backoff = _options.BackoffMin;
                    continue;
                }
                else
                {
                    log.LogWarning("Версия {Version} не стала здоровой, но откатываться некуда — работаю на ней", active);
                }
            }

            int code;
            try { code = await _clock.WaitAsync(child.Exited, stop); }
            catch (OperationCanceledException)
            {
                log.LogInformation("Остановка супервизора — гашу дочерний");
                await child.StopAsync(_options.StopGrace);
                break;
            }

            if (code == SupervisorContract.SwitchExitCode)
            {
                log.LogInformation("Дочерний {Version} вышел с 75 — переключаюсь на active = {Next}", active, layout.ReadActive());
                backoff = _options.BackoffMin;
                continue;
            }

            if (_clock.Now - startedAt >= _options.StableAfter) backoff = _options.BackoffMin;
            log.LogWarning("Дочерний {Version} вышел с кодом {Code} — перезапуск через {Delay} с", active, code, backoff.TotalSeconds);
            await Delay(backoff, stop);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.BackoffMax.Ticks));
        }

        log.LogInformation("Супервизор остановлен");
        return 0;
    }

    /// <summary>Куда откатиться с версии: прошлая, установленная и не помеченная плохой.</summary>
    private string? RollbackTarget(string failed)
    {
        var previous = layout.ReadPrevious();
        return previous is not null && previous != failed && layout.IsInstalled(previous) && !layout.IsBad(previous, _clock.Now)
            ? previous
            : null;
    }

    private void Rollback(string failed, string previous, bool markBad)
    {
        if (markBad) layout.MarkBad(failed, _clock.Now);
        layout.RollbackTo(previous);
        try { repointAutostart(previous); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogWarning("Автозапуск не переписан на {Version}: {Error}", previous, e.Message);
        }
        log.LogWarning("active = {Previous}; {Failed} {Mark}", previous, failed, markBad ? "помечена плохой" : "пропущена");
    }

    private async Task<bool> WaitHealthyAsync(string version, ISupervisedChild child, CancellationToken stop)
    {
        var deadline = _clock.Now + _options.HealthyTimeout;
        while (!stop.IsCancellationRequested)
        {
            if (layout.IsHealthy(version)) return true;
            if (child.Exited.IsCompleted || _clock.Now >= deadline) return false;
            await Delay(_options.HealthyPoll, stop);
        }
        return false;
    }

    private async Task KillAsync(ISupervisedChild child)
    {
        child.Kill();
        try { await child.Exited.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { log.LogWarning("Дочерний pid={Pid} не вышел за 10 с после kill", child.Id); }
    }

    private async Task Delay(TimeSpan delay, CancellationToken stop)
    {
        try { await _clock.Delay(delay, stop); }
        catch (OperationCanceledException) { }
    }
}
