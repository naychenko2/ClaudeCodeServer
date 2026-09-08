using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Power;

/// <summary>Запланированное действие: что и когда сработает.</summary>
public sealed record PowerPending(PowerAction Action, DateTimeOffset RunAt, string RequestedBy);

/// <summary>Итог попытки запланировать: причина отказа — текст для человека.</summary>
public sealed record PowerScheduleResult(bool Ok, PowerPending? Pending, string? Reason);

/// <summary>
/// Отсрочка и отмена команды питания. Саму команду отдаёт IPowerActions — здесь только время.
///
/// Отсчёт ведёт сервер, а не shutdown.exe своим ключом <c>/t</c>, и это осознанно. Так все три
/// действия отменяются одинаково (у сна встроенной отсрочки нет), отмена не гоняется с
/// <c>shutdown /a</c> за право первым дойти до системы, а любое окно продукта видит один и тот
/// же обратный отсчёт — включая то, из которого не нажимали. Цена — отсчёт не переживает
/// смерть процесса, но это ровно то поведение, которое нужно: упавший бэкенд не должен гасить
/// машину «по памяти».
/// </summary>
public sealed class PowerControlService(
    IOptions<PowerControlOptions> options, IPowerActions actions, ILogger<PowerControlService> log)
{
    private readonly Lock _gate = new();
    private PowerPending? _pending;
    private CancellationTokenSource? _cts;

    private PowerControlOptions Opt => options.Value;

    public bool Enabled => Opt.Enabled;
    public bool Available => actions.Available;
    public int DelaySeconds => Opt.EffectiveDelaySeconds;

    /// <summary>Что запланировано прямо сейчас; null — ничего.</summary>
    public PowerPending? Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>Планирует действие. Второй запрос поверх уже запланированного отклоняется.</summary>
    public PowerScheduleResult Schedule(PowerAction action, string requestedBy)
    {
        if (!Opt.Enabled) return new(false, null, "Управление питанием выключено в конфиге сервера.");
        if (!actions.Available) return new(false, null, "Машина сервера не умеет выполнять такие команды.");

        PowerPending pending;
        CancellationToken token;

        lock (_gate)
        {
            if (_pending is not null)
                return new(false, _pending, $"Уже запланировано: {Title(_pending.Action)}.");

            var delay = Opt.EffectiveDelaySeconds;
            pending = new PowerPending(action, DateTimeOffset.Now.AddSeconds(delay), requestedBy);
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _pending = pending;
        }

        log.LogWarning("{Action} машины запрошено пользователем {User}, сработает {RunAt:HH:mm:ss}.",
            Title(action), requestedBy, pending.RunAt);

        _ = RunAsync(pending, token);
        return new(true, pending, null);
    }

    /// <summary>Отменяет запланированное. false — отменять было нечего.</summary>
    public bool Cancel(string cancelledBy)
    {
        PowerPending? cancelled;

        lock (_gate)
        {
            if (_pending is null) return false;
            cancelled = _pending;
            _cts?.Cancel();
            Reset();
        }

        log.LogWarning("{Action} машины отменено пользователем {User}.", Title(cancelled.Action), cancelledBy);
        return true;
    }

    private async Task RunAsync(PowerPending pending, CancellationToken token)
    {
        try
        {
            var wait = pending.RunAt - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
        }
        catch (OperationCanceledException)
        {
            return; // отменили — состояние уже сброшено в Cancel
        }

        // Состояние снимаем ДО команды: выключение процесс не переживёт, а сон — переживёт, и
        // проснувшаяся машина не должна показывать «запланировано» от прошлой жизни.
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            Reset();
        }

        if (!actions.Execute(pending.Action))
            log.LogError("Команда {Action} не выполнена — машина осталась работать.", Title(pending.Action));
    }

    private void Reset()
    {
        _cts?.Dispose();
        _cts = null;
        _pending = null;
    }

    /// <summary>Название действия для логов и текстов отказа.</summary>
    public static string Title(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "Выключение",
        PowerAction.Restart => "Перезагрузка",
        PowerAction.Sleep => "Сон",
        _ => action.ToString(),
    };
}
