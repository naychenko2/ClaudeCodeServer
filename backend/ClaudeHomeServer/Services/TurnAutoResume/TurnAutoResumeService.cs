using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.TurnAutoResume;

/// <summary>
/// Фоновый оркестратор авто-возобновления ходов после рестарта сервера (флаг turn-auto-resume,
/// ADR-033). Ждёт ApplicationStarted, запись маркеров обрыва и паузу прогрева, затем
/// последовательно (пауза GapSeconds) возобновляет подходящих кандидатов, но не больше
/// MaxPerBoot за старт. Сбой одной сессии цикл не роняет.
/// </summary>
public class TurnAutoResumeService : BackgroundService
{
    private readonly ITurnAutoResumeHost _host;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TurnAutoResumeOptions _options;
    private readonly ILogger<TurnAutoResumeService> _log;
    // Шов времени: тесты подменяют паузы, чтобы не ждать реальных секунд (и не использовать Task.Delay)
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TurnAutoResumeService(ITurnAutoResumeHost host, IHostApplicationLifetime lifetime,
        IOptions<TurnAutoResumeOptions> options, ILogger<TurnAutoResumeService> log)
        : this(host, lifetime, options, log, static (d, ct) => Task.Delay(d, ct)) { }

    internal TurnAutoResumeService(ITurnAutoResumeHost host, IHostApplicationLifetime lifetime,
        IOptions<TurnAutoResumeOptions> options, ILogger<TurnAutoResumeService> log,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _host = host;
        _lifetime = lifetime;
        _options = options.Value;
        _log = log;
        _delay = delay;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // 1. ApplicationStarted: до него сервер ещё не принимает трафик
            await WaitForAsync(_lifetime.ApplicationStarted, ct);
            // 2. Маркеры обрыва — до первой отправки, иначе маркер ляжет позже «Продолжи»
            await _host.AbortMarkersWritten.WaitAsync(ct);
            // 3. Прогрев
            await _delay(TimeSpan.FromSeconds(Math.Max(0, _options.WarmupSeconds)), ct);

            await RunCycleAsync(ct);
            // Очереди отложенных сообщений (pending.json) разбираем и без кандидатов на resume
            await _host.DrainRestoredQueuesAsync();
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
        catch (Exception ex)
        {
            // Фоновый сервис не должен ронять хост (BackgroundServiceExceptionBehavior.StopHost)
            _log.LogError(ex, "Авто-возобновление ходов: цикл прерван ошибкой");
        }
    }

    private static async Task WaitForAsync(CancellationToken signal, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var regSignal = signal.Register(() => tcs.TrySetResult());
        using var regStop = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var candidates = _host.TakeCandidates();
        if (candidates.Count == 0) return;

        var max = Math.Max(0, _options.MaxPerBoot);
        var sent = 0;
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (sent >= max)
            {
                _log.LogWarning("Авто-возобновление: достигнут потолок MaxPerBoot={Max}, остальные кандидаты пропущены", max);
                break;
            }

            try
            {
                var session = _host.GetSession(id);
                if (session is null) { _log.LogInformation("Авто-возобновление {SessionId}: сессия не найдена", id); continue; }

                // Владелец резолвится через шов (ResolveOwnerId): у проектной сессии OwnerId
                // пустой — владелец берётся из проекта (ADR-033, дефект: проверка по OwnerId
                // отсеивала все проектные чаты).
                if (!_host.IsEnabledFor(session))
                {
                    _log.LogInformation("Авто-возобновление {SessionId}: флаг turn-auto-resume выключен у владельца", id);
                    continue;
                }

                var project = session.ProjectId is { } pid ? _host.GetProject(pid) : null;
                // Локальность и состояние устройства — из ProjectCapabilities (через хост), без инлайн-проверок
                var deviceOnline = project is null || _host.IsDeviceOnline(project);
                var deploy = _host.LastTurnWasDeployOrRollback(session, project);

                var verdict = TurnAutoResumeEligibility.Check(session, project, deviceOnline, deploy);
                if (!verdict.Eligible)
                {
                    _log.LogInformation("Авто-возобновление {SessionId} пропущено: {Reason}", id, verdict.Reason);
                    continue;
                }

                // Пауза между отправками (после предыдущей реальной отправки), чтобы не поднимать N CLI разом
                if (sent > 0)
                    await _delay(TimeSpan.FromSeconds(Math.Max(0, _options.GapSeconds)), ct);

                var result = await _host.ResumeAsync(id);
                if (result.Sent)
                {
                    sent++;
                    _log.LogInformation("Авто-возобновление {SessionId}: ход продолжен ({Sent}/{Max})", id, sent, max);
                }
                else
                    _log.LogInformation("Авто-возобновление {SessionId} не отправлено: {Reason}", id, result.Reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Сбой одной сессии не должен останавливать остальных
                _log.LogError(ex, "Авто-возобновление {SessionId}: ошибка, идём к следующему", id);
            }
        }
    }
}
