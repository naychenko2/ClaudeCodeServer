using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Watchdog;

// Доставка будильника — узкий шов поверх SessionMessagingService: параметры отправки
// (preempt=false / wait=none / callerSessionId=null — план, «Будильник и доставка»)
// живут в одном месте и покрыты юнит-тестом, а цикл сервиса тестируется на fake-е.
public interface IWatchdogAlarm
{
    /// <summary>true — будильник принят (ход запущен/поставлен в очередь), false — не дошло.</summary>
    Task<bool> DeliverAsync(string ownerId, string sessionId, string text);
}

public sealed class WatchdogAlarm(SessionMessagingService messaging) : IWatchdogAlarm
{
    public async Task<bool> DeliverAsync(string ownerId, string sessionId, string text)
    {
        // callerSessionId: null — квота пробуждения штаба не тратится, глубина делегирования
        // берётся из agentDepthFallback (0): будильник — системный ход, не агентный.
        // preempt: false — живой ход получателя не рвём, сообщение встанет в очередь и
        // доставится по result. wait: none — сервис не ждёт завершения хода.
        var outcome = await messaging.SendAsync(ownerId, sessionId, text,
            callerSessionId: null, senderSessionId: null, agentDepthFallback: 0,
            wait: "none", timeoutSec: null, preempt: false);
        return outcome switch
        {
            SessionMessagingService.SendOutcome.Completed
                or SessionMessagingService.SendOutcome.Queued
                or SessionMessagingService.SendOutcome.Running => true,
            _ => false,
        };
    }
}

/// <summary>
/// Цикл серверных сторожей чатов (план «chat-watchdogs», шаг 2). Опрос ПОСЛЕДОВАТЕЛЬНЫЙ:
/// один зависший poll не тормозит остальных дольше собственного PollTimeoutSeconds
/// (per-poll таймаут с kill). Семантика исходов — по плану:
/// exit 0 → fired; exit != 0 → «ещё нет» (штатно); запуск не состоялся 3 подряд →
/// launch_failed; истёк потолок жизни → timed_out; poll-таймаут → kill и «ещё нет».
/// Терминал будит чат-владелец ОДНИМ системным ходом (3 попытки с шагом интервала;
/// недоставка — флагом DeliveredAt = null, исход не затирается).
/// Гашение: удаление чата (OnSessionDeleted — мгновенно) и архивация/удаление,
/// замеченные в тике (события архивации у SessionManager нет).
/// </summary>
public class WatchdogService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly WatchdogStore _store;
    private readonly IWatchdogEnvironment _env;
    private readonly IWatchdogCommandRunner _runner;
    private readonly IWatchdogAlarm _alarm;
    private readonly ILogger<WatchdogService>? _log;
    // Нотификатор присутствия сторожей (визуализация): терминалы ставятся этим сервисом
    // мимо методов стора, поэтому Changed стора терминалы не покрывает — дёргаем сами.
    // Optional: юнит-тесты цикла собирают сервис без него
    private readonly WatchdogNotifier? _notifier;

    // Токены идущих опросов per-сторож: снятие (watch_cancel/гашение чата) отменяет свой
    // токен, раннер по отмене Kill'ит процесс — тот же путь, что и остановка хоста.
    // Без этого отменённый poll жил бы сиротой до собственного per-poll таймаута
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollCts = new();

    public WatchdogService(WatchdogStore store, IWatchdogEnvironment env,
        IWatchdogCommandRunner runner, IWatchdogAlarm alarm,
        ILogger<WatchdogService>? log = null, WatchdogNotifier? notifier = null)
    {
        _store = store;
        _env = env;
        _runner = runner;
        _alarm = alarm;
        _log = log;
        _notifier = notifier;
        // Стор — единая точка всех снятий (Cancel и CancelBySession): слушаем его, а не
        // каждый вызыватель гасит токены сам. Подписка в конструкторе: TickAsync тестируется
        // без StartAsync, событие должно работать и без запущенного цикла
        store.ActiveCancelled += CancelRunningPoll;
    }

    public override void Dispose()
    {
        _store.ActiveCancelled -= CancelRunningPoll;
        base.Dispose();
    }

    private void CancelRunningPoll(string watchId) => _pollCts.GetValueOrDefault(watchId)?.Cancel();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Мгновенное гашение при удалении чата: событие уже существует (наблюдатель не
        // может уронить удаление — вызов обёрнут в try в SessionManager.DeleteAsync)
        _env.ChatDeleted += OnChatDeleted;
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log?.LogError(ex, "Ошибка тика цикла сторожей"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
        finally { _env.ChatDeleted -= OnChatDeleted; }
    }

    private void OnChatDeleted(Session session)
    {
        var n = _store.CancelBySession(session.Id);
        if (n > 0)
            _log?.LogInformation("Удаление чата {SessionId} погасило сторожей: {Count}", session.Id, n);
    }

    // Публичный для юнит-тестов: один проход по сторожам (fake-часы = передача nowUtc).
    public async Task TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        _store.PruneDelivered(nowUtc);

        // Сначала доставка долгов: терминал, случившийся до рестарта, не должен ждать
        // следующего опроса — своего у этого сторожа уже никогда не будет
        foreach (var w in _store.GetPendingDelivery())
        {
            if (w.DeliveryAttempts >= WatchdogLimits.DeliveryAttempts) continue;
            // Ретраи с шагом интервала сторожа от момента терминала: попытка k — в
            // FiredAt + k*интервал (k = число уже сделанных попыток)
            var nextAt = w.FiredAt ?? w.CreatedAt;
            if (nowUtc < nextAt + TimeSpan.FromSeconds(w.IntervalSeconds) * w.DeliveryAttempts) continue;
            await TryDeliverAsync(w, nowUtc);
        }

        foreach (var w in _store.GetActive())
        {
            ct.ThrowIfCancellationRequested();
            await PollOneAsync(w, nowUtc, ct);
        }
        _store.Save();
    }

    private async Task PollOneAsync(WatchdogRecord w, DateTime nowUtc, CancellationToken ct)
    {
        // Гашение по исчезновению/архивации чата: событие удаления ловится подпиской,
        // архивации события нет — проверяем в тике (архивный чат будильник оживать
        // не должен). FindChat изолирует по владельцу (GetOwned).
        var chat = _env.FindChat(w.SessionId, w.OwnerId);
        if (chat is null || chat.IsArchived)
        {
            w.Status = WatchdogStatus.Cancelled;
            // Гашение мимо Cancel стора: присутствие сторожа сообщаем нотификатору сами
            // (персист — общим Save тика ниже)
            _notifier?.NotifyChanged(w.OwnerId);
            _log?.LogInformation("Сторож «{Name}» погашен: чат {SessionId} недоступен (удалён или в архиве)",
                w.Name, w.SessionId);
            return;
        }

        // Потолок жизни: терминал timed_out, будильник «не дождались»
        if (nowUtc - w.CreatedAt >= TimeSpan.FromMinutes(w.TimeoutMinutes))
        {
            await TerminateAsync(w, WatchdogStatus.TimedOut, nowUtc);
            return;
        }

        if (!ShouldPoll(w, nowUtc)) return;

        var workDir = _env.ResolveWorkDir(w);
        if (workDir is null)
        {
            // Рабочего каталога нет (проект удалили / дом владельца не настроен) —
            // запуск не состоялся; та же ветка, что и отказ Start ниже
            await RegisterLaunchFailureAsync(w, nowUtc, WorkDirFailureText(w));
            return;
        }

        // Токен идущего опроса: связка внешнего ct (остановка хоста) и per-сторож отмены.
        // Регистрируем ДО проверки статуса: снятие, случившееся после регистрации, найдёт
        // токен событием стора; случившееся раньше — увидит не-Active статус ниже
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollCts[w.Id] = pollCts;
        try
        {
            // Сторож уже сняли, poll не стартуем вовсе (снимающий тик/событие опередили нас)
            if (w.Status != WatchdogStatus.Active) return;

            PollOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(w.OwnerId, workDir, w.PollCommand,
                    w.PollTimeoutSeconds, pollCts.Token);
            }
            catch (OperationCanceledException) when (pollCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Сторож сняли, пока команда работала: раннер уже Kill'ил процесс, отменённый
                // опрос не трактуем ни как «ещё нет», ни как сбой — терминал стоит у стора
                return;
            }
            catch (OperationCanceledException) { throw; }

            // Guard отмены: пока команда работала, сторож могли снять (watch_cancel из хода
            // или гашение чата) — исход опроса не имеет права перезаписать терминал ни на
            // fired, ни на счётчики (до сюда доходит только завершившийся до отмены poll)
            if (w.Status != WatchdogStatus.Active) return;

            LogPoll(w, outcome);

            switch (outcome.Kind)
            {
                case PollOutcomeKind.ExitCode when outcome.ExitCode == 0:
                    w.LastOutput = ClipOutput(outcome.Output);
                    await TerminateAsync(w, WatchdogStatus.Fired, nowUtc);
                    break;
                case PollOutcomeKind.ExitCode:
                    // exit != 0 — штатное «ещё нет»: упавшая один раз команда сторожа не убивает
                    w.ConsecutiveLaunchFailures = 0;
                    w.LastOutput = ClipOutput(outcome.Output);
                    w.LastPollAt = nowUtc;
                    break;
                case PollOutcomeKind.PollTimeout:
                    // Kill по таймауту = «ещё нет»: запуск СОСТОЯЛСЯ, счётчик сбоев запуска
                    // обнуляем (иначе зависающая команда за 3 круга гасила бы живой сторож)
                    w.ConsecutiveLaunchFailures = 0;
                    w.LastOutput = $"Запрос не уложился в {w.PollTimeoutSeconds} с и был снят";
                    w.LastPollAt = nowUtc;
                    break;
                default:
                    await RegisterLaunchFailureAsync(w, nowUtc, outcome.Failure ?? "запуск не состоялся");
                    break;
            }
        }
        finally
        {
            _pollCts.TryRemove(w.Id, out _);
        }
    }

    // Диагностика каждого опроса (инцидент 01.09 ловился вслепую — раннер был нем):
    // команда, исход и хвост вывода. INFO: один poll — одна строка, штучный объём;
    // Debug лишил бы прод-логи следственной картины
    private void LogPoll(WatchdogRecord w, PollOutcome outcome)
    {
        var head = outcome.Kind switch
        {
            PollOutcomeKind.ExitCode => $"exit {outcome.ExitCode}",
            PollOutcomeKind.PollTimeout => "таймаут poll",
            _ => $"запуск не состоялся: {outcome.Failure}",
        };
        var output = outcome.Output.Length > 200 ? outcome.Output[..200] + "…" : outcome.Output;
        _log?.LogInformation("Сторож «{Name}» ({Id}): poll «{Command}» → {Head}, вывод: {Output}",
            w.Name, w.Id, w.PollCommand, head, output);
    }

    // Причина «каталога нет» для будильника launch_failed (текст — по проекту сторожа)
    private static string WorkDirFailureText(WatchdogRecord w) =>
        w.ProjectId is { } pid
            ? $"проект {pid} не найден (удалён?)"
            : "домашняя папка владельца не настроена";

    // Запуск НЕ состоялся: копим подряд идущие сбои, 3 подряд → терминал launch_failed.
    // Попытка тоже съедает интервал (LastPollAt) — иначе три попытки пролетали бы за
    // три тика, не оставляя системе шанса поправиться.
    private async Task RegisterLaunchFailureAsync(WatchdogRecord w, DateTime nowUtc, string reason)
    {
        w.ConsecutiveLaunchFailures++;
        w.LastPollAt = nowUtc;
        w.LastOutput = ClipOutput(reason);
        if (w.ConsecutiveLaunchFailures >= WatchdogLimits.MaxConsecutiveLaunchFailures)
        {
            _log?.LogWarning("Сторож «{Name}»: запуск невозможен ({Reason}) — {Count} попыток подряд",
                w.Name, reason, w.ConsecutiveLaunchFailures);
            await TerminateAsync(w, WatchdogStatus.LaunchFailed, nowUtc);
        }
    }

    private async Task TerminateAsync(WatchdogRecord w, WatchdogStatus status, DateTime nowUtc)
    {
        w.Status = status;
        w.FiredAt = nowUtc;
        _store.Save();
        // Терминал мимо методов стора: Changed не стреляет — присутствие сторожа
        // (значки UI снимаются с чата/проекта) сообщаем нотификатору после Save
        _notifier?.NotifyChanged(w.OwnerId);
        _log?.LogInformation("Сторож «{Name}» чата {SessionId}: терминал {Status}", w.Name, w.SessionId, status);
        await TryDeliverAsync(w, nowUtc);
    }

    private async Task TryDeliverAsync(WatchdogRecord w, DateTime nowUtc)
    {
        var text = AlarmText(w);
        try
        {
            var delivered = await _alarm.DeliverAsync(w.OwnerId, w.SessionId, text);
            if (delivered) w.DeliveredAt = nowUtc;
            else w.DeliveryAttempts++;
        }
        catch (Exception ex)
        {
            // Сбой доставки — не сбой сторожа: попытка списана, ретрай подхватит в тике
            _log?.LogWarning(ex, "Будильник сторожа «{Name}» не доставлен (попытка {Attempt})",
                w.Name, w.DeliveryAttempts + 1);
            w.DeliveryAttempts++;
        }
        // Исчерпаны попытки — остаёмся с DeliveredAt = null: терминальный статус
        // сохраняется, недоставка видна в watch_list (отдельного статуса нет — план)
        _store.Save();
    }

    // --- Чистые функции — швы под юнит-тесты ---

    // Пора ли опрашивать: первый опрос — сразу, дальше период МЕЖДУ запусками
    internal static bool ShouldPoll(WatchdogRecord w, DateTime nowUtc) =>
        w.LastPollAt is not { } last || nowUtc - last >= TimeSpan.FromSeconds(w.IntervalSeconds);

    // Текст будильника: «⏰ Сторож «{name}»: {исход}» + обрезанный вывод последнего опроса
    internal static string AlarmText(WatchdogRecord w)
    {
        var head = w.Status switch
        {
            WatchdogStatus.Fired => "условие выполнено",
            WatchdogStatus.TimedOut => $"истёк потолок жизни ({w.TimeoutMinutes} мин), условие не выполнено",
            WatchdogStatus.LaunchFailed => $"запуск невозможен — {w.LastOutput}",
            _ => w.Status.ToString().ToLowerInvariant(),
        };
        var text = $"⏰ Сторож «{w.Name}»: {head}.";
        if (w.Status is WatchdogStatus.Fired or WatchdogStatus.TimedOut
            && !string.IsNullOrWhiteSpace(w.LastOutput))
            text += "\nВывод последнего опроса:\n" + w.LastOutput;
        return text;
    }

    // Обрезка вывода: ≤10 строк и ≤2000 символов (будильник — ход = токены)
    internal static string ClipOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return "";
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var clipped = lines.Length > 10 ? string.Join("\n", lines[..10]) + $"\n… ({lines.Length - 10} строк обрезано)" : string.Join("\n", lines);
        return clipped.Length > 2000 ? clipped[..2000] + "…" : clipped;
    }
}
