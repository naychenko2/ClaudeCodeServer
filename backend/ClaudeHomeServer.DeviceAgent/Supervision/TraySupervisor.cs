using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>Запуск трея глазами супервизора (боевой — <see cref="ProcessChildLauncher"/>).</summary>
internal interface ITrayLauncher
{
    ISupervisedChild Start(string executable, string workingDirectory);
}

internal sealed record TraySupervisorOptions
{
    /// <summary>Как часто смотреть, жив ли трей и не сменилась ли активная версия.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackoffMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Трей, проживший дольше, считается стабильным: бэкофф сбрасывается.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Трей агента (Ш7) под <c>ai-home-agent supervise</c> — рядом с <see cref="AgentSupervisor"/>,
/// в том же сеансе пользователя:
/// - трей берётся из каталога активной версии; нет его там (старая версия) — ждём, агент
///   работает и без трея;
/// - упал или вышел — перезапуск с бэкоффом 1→2→4…→60 с, сброс после 5 мин жизни;
/// - сменилась активная версия — старый трей гасится, поднимается трей новой версии: иначе
///   он держал бы файлы старого каталога, и уборка версий его не удалила бы;
/// - код <see cref="HandsTrayProcess.ExitAgentCode"/> — человек выбрал «Выйти из агента»:
///   <see cref="RunAsync"/> возвращает true, и супервизор гасит всё.
/// Контракт <see cref="SupervisorContract"/> не трогается: трей — не дочерний <c>run</c>.
/// </summary>
internal sealed class TraySupervisor(
    AgentLayout layout,
    ITrayLauncher launcher,
    ILogger log,
    TraySupervisorOptions? options = null,
    ISupervisorClock? clock = null)
{
    private readonly TraySupervisorOptions _options = options ?? new TraySupervisorOptions();
    private readonly ISupervisorClock _clock = clock ?? SystemSupervisorClock.Instance;

    /// <summary>true — человек попросил выйти из агента; false — супервизор останавливается сам.</summary>
    public async Task<bool> RunAsync(CancellationToken stop)
    {
        var backoff = _options.BackoffMin;
        string? missingLogged = null;
        while (!stop.IsCancellationRequested)
        {
            var active = layout.ReadActive();
            var exe = active is not null && layout.IsInstalled(active)
                ? Path.Combine(layout.VersionDir(active), HandsTrayProcess.ExecutableName)
                : null;
            if (exe is null || !File.Exists(exe))
            {
                if (missingLogged != active)
                {
                    log.LogInformation("Трея в версии «{Active}» нет — агент работает без значка", active);
                    missingLogged = active;
                }
                await Delay(_options.Poll, stop);
                continue;
            }
            missingLogged = null;

            var startedAt = _clock.Now;
            ISupervisedChild tray;
            try { tray = launcher.Start(exe, layout.VersionDir(active!)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
            {
                log.LogWarning("Трей {Version} не запустился: {Error} — повтор через {Delay} с", active, e.Message, backoff.TotalSeconds);
                await Delay(backoff, stop);
                backoff = Next(backoff);
                continue;
            }

            using (tray)
            {
                log.LogInformation("Трей {Version} pid={Pid}", active, tray.Id);
                var switched = false;
                while (!stop.IsCancellationRequested && !tray.Exited.IsCompleted)
                {
                    if (layout.ReadActive() != active)
                    {
                        switched = true;
                        break;
                    }
                    await Delay(_options.Poll, stop);
                }

                if (stop.IsCancellationRequested || switched)
                {
                    await tray.StopAsync(_options.StopGrace);
                    if (switched)
                    {
                        log.LogInformation("Активная версия сменилась — поднимаю трей версии {Next}", layout.ReadActive());
                        backoff = _options.BackoffMin;
                    }
                    continue;
                }

                var code = await tray.Exited;
                if (code == HandsTrayProcess.ExitAgentCode)
                {
                    log.LogInformation("Человек выбрал «Выйти из агента» в трее — останавливаю агента");
                    return true;
                }

                if (_clock.Now - startedAt >= _options.StableAfter) backoff = _options.BackoffMin;
                log.LogWarning("Трей {Version} вышел с кодом {Code} — перезапуск через {Delay} с", active, code, backoff.TotalSeconds);
            }
            await Delay(backoff, stop);
            backoff = Next(backoff);
        }
        return false;
    }

    private TimeSpan Next(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.BackoffMax.Ticks));

    private async Task Delay(TimeSpan delay, CancellationToken stop)
    {
        try { await _clock.Delay(delay, stop); }
        catch (OperationCanceledException) { }
    }
}
