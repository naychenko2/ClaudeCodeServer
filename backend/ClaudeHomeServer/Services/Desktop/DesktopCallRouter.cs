using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>Живое соединение устройства. Ready — устройство представилось (Hello).</summary>
public sealed record DeviceConnection(
    string ConnectionId,
    string OwnerId,
    string DeviceId,
    DateTimeOffset ConnectedAt)
{
    public int ProtocolVersion { get; init; }
    public IReadOnlyList<string> SupportedSteps { get; init; } = [];
    public string? ClientVersion { get; init; }
    public bool Ready { get; init; }
}

/// <summary>
/// Наблюдатель соединений устройств. Объявлен здесь, подписчика реализует сеанс рук:
/// разрыв соединения — одна из причин, по которым сеанс обязан погаснуть (ADR-008).
/// </summary>
public interface IDeviceConnectionObserver
{
    /// <summary>Устройство представилось и готово принимать команды.</summary>
    Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default);

    /// <summary>Соединение с устройством разорвано.</summary>
    Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default);
}

/// <summary>
/// Отправка команд в КОНКРЕТНОЕ соединение устройства. Отдельный интерфейс, чтобы
/// маршрутизатор жил без SignalR в тестах.
/// </summary>
public interface IDeviceCommandSender
{
    Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default);
    Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default);
    Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default);
}

/// <summary>Боевой отправитель — push в соединение через хаб устройств.</summary>
public sealed class DeviceHubCommandSender(IHubContext<DeviceHub, IDesktopDeviceClient> hub) : IDeviceCommandSender
{
    public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default)
        => hub.Clients.Client(connectionId).Call(command);

    public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default)
        => hub.Clients.Client(connectionId).Go(go);

    public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default)
        => hub.Clients.Client(connectionId).Cancel(cancel);
}

/// <summary>Заявка на вызов устройства. Владельца, устройство и чат резолвит гейт исполнения.</summary>
public sealed record DesktopCallRequest(
    string OwnerId,
    string DeviceId,
    string SessionId,
    string Kind,
    System.Text.Json.JsonElement? Args = null,
    bool RequiresConfirmation = true,
    int? ConfirmationWaitMinutes = null,
    string? DeviceName = null,
    string? ChatName = null);

/// <summary>Что случилось с присланным результатом. 409 — ТОЛЬКО на дубль.</summary>
public enum DesktopResultAcceptance
{
    Accepted,
    Duplicate,
    UnknownCall,
    Forbidden
}

/// <summary>Состояние вызова при попытке забрать результат по callId.</summary>
public enum DesktopResultLookup
{
    Found,
    Pending,
    UnknownCall,
    Forbidden
}

/// <summary>
/// Маршрутизатор вызовов десктопного агента (ADR-008, «Протокол канала»).
///
/// Жизнь вызова: push в конкретное соединение → Ack за 2 с (нет ack — честная ошибка, а не
/// висение до таймаута MCP) → ожидание человека В МИНУТАХ (исход awaiting_confirmation) →
/// встречный go → дедлайн исполнения по виду вызова → результат HTTP-POST'ом мимо 32-КБ
/// лимита сообщения хаба.
///
/// Инварианты: авто-ретраев нет нигде; в любом исходе возвращается индекс последнего
/// применённого шага; приём результата одноразовый, но поздний и частичный принимается.
/// </summary>
public sealed class DesktopCallRouter
{
    private enum ConfirmDecision { Confirmed, Declined }

    private enum WaitStatus { Signalled, TimedOut, Aborted }

    private sealed class CallRecord
    {
        public required string CallId { get; init; }
        public required string OwnerId { get; init; }
        public required string DeviceId { get; init; }
        public required string SessionId { get; init; }
        public required string Kind { get; init; }
        public required string ConnectionId { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public string? DeviceName { get; init; }

        public readonly TaskCompletionSource<bool> Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ConfirmDecision> Confirm = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<DesktopCallResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Внешняя остановка вызова (отмена, разрыв, погасший сеанс) — с готовым исходом.</summary>
        public readonly TaskCompletionSource<DesktopCallResult> Abort = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Последний применённый шаг по донесениям устройства (-1 — неизвестно).</summary>
        public int LastAppliedStep = -1;

        /// <summary>Сколько минут устройство просит на разговор с человеком.</summary>
        public int? AwaitMinutes;

        /// <summary>Что реально прислало устройство — приём одноразовый.</summary>
        public DesktopCallResult? Posted;

        /// <summary>Что ушло модели.</summary>
        public DesktopCallResult? Returned;

        public DateTimeOffset? FinishedAt;
    }

    /// <summary>Потолок хранимых записей вызовов — защита от разрастания реестра.</summary>
    private const int MaxRetainedCalls = 500;

    private readonly IDeviceCommandSender _sender;
    private readonly IEnumerable<IDeviceConnectionObserver> _observers;
    private readonly ILogger<DesktopCallRouter> _log;
    private readonly TimeProvider _time;

    // connectionId → соединение; устройств у владельца может быть несколько, соединение одно
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<string, CallRecord> _calls = new();

    public DesktopCallRouter(
        IDeviceCommandSender sender,
        IEnumerable<IDeviceConnectionObserver> observers,
        ILogger<DesktopCallRouter> log,
        TimeProvider? timeProvider = null)
    {
        _sender = sender;
        _observers = observers;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ---------- соединения ----------

    /// <summary>Соединение установлено; команды не идут, пока устройство не представилось.</summary>
    public void RegisterConnection(string connectionId, string ownerId, string deviceId) =>
        _connections[connectionId] = new DeviceConnection(connectionId, ownerId, deviceId, _time.GetUtcNow());

    /// <summary>
    /// Устройство представилось: фиксируем версию протокола и поддерживаемые типы шагов
    /// (сервер их не додумывает), после чего устройство онлайн и о нём узнают наблюдатели.
    /// </summary>
    public async Task<DeviceHelloAck> HelloAsync(string connectionId, DeviceHello hello, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var conn))
            throw new InvalidOperationException("Соединение устройства не зарегистрировано");

        var wasReady = conn.Ready;
        // Одно устройство — одно соединение: прежнее (например, зависшее после спящего режима)
        // убираем из реестра, чтобы push не уходил в мёртвый канал.
        foreach (var (id, other) in _connections)
        {
            if (id == connectionId || other.OwnerId != conn.OwnerId || other.DeviceId != conn.DeviceId) continue;
            _connections.TryRemove(id, out _);
        }

        var ready = conn with
        {
            ProtocolVersion = hello.ProtocolVersion,
            SupportedSteps = hello.SupportedSteps ?? [],
            ClientVersion = hello.ClientVersion,
            Ready = true
        };
        _connections[connectionId] = ready;

        if (!wasReady) await NotifyAsync(o => o.OnDeviceOnlineAsync(ready, ct));

        return new DeviceHelloAck(
            DesktopProtocol.Version,
            (int)DesktopProtocol.AckTimeout.TotalSeconds,
            DesktopProtocol.MaxResultBytes,
            DesktopProtocol.MaxBatchSteps);
    }

    /// <summary>
    /// Соединение разорвано: устройство уходит в офлайн, вызовы в полёте закрываются исходом
    /// unknown — что с ними стало на той стороне, сервер не знает (результат из локального
    /// журнала устройство отдаст при реконнекте по callId).
    /// </summary>
    public async Task RemoveConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_connections.TryRemove(connectionId, out var conn)) return;

        foreach (var call in _calls.Values.Where(c => c.ConnectionId == connectionId && c.Returned is null).ToList())
            call.Abort.TrySetResult(DesktopCallResult.Server(
                call.CallId, DesktopOutcomes.Unknown, call.LastAppliedStep, call.DeviceName));

        if (conn.Ready) await NotifyAsync(o => o.OnDeviceOfflineAsync(conn, ct));
    }

    /// <summary>Готовое к работе соединение устройства владельца, либо null.</summary>
    public DeviceConnection? Find(string ownerId, string deviceId) =>
        _connections.Values.FirstOrDefault(c =>
            c.Ready && c.OwnerId == ownerId && c.DeviceId == deviceId);

    public bool IsOnline(string ownerId, string deviceId) => Find(ownerId, deviceId) is not null;

    /// <summary>Онлайн-устройства владельца — для desktop_devices и бейджа «руки на home».</summary>
    public IReadOnlyList<DeviceConnection> Online(string ownerId) =>
        _connections.Values.Where(c => c.Ready && c.OwnerId == ownerId).ToList();

    // ---------- вызов ----------

    /// <summary>Провести вызов через все фазы и вернуть исход. Исключений наружу не бросает.</summary>
    public async Task<DesktopCallResult> InvokeAsync(DesktopCallRequest request, CancellationToken ct = default)
    {
        Prune();

        var callId = DesktopProtocol.NewCallId();

        if (!DesktopCallKinds.IsKnown(request.Kind))
            return DesktopCallResult.Server(callId, DesktopOutcomes.ProtocolError, 0, request.DeviceName);

        var conn = Find(request.OwnerId, request.DeviceId);
        if (conn is null)
            return DesktopCallResult.Server(callId, DesktopOutcomes.DeviceOffline, 0, request.DeviceName);

        var deadline = DesktopProtocol.DeadlineFor(request.Kind);
        var confirmWait = ClampConfirmationWait(request.ConfirmationWaitMinutes);

        var call = new CallRecord
        {
            CallId = callId,
            OwnerId = request.OwnerId,
            DeviceId = request.DeviceId,
            SessionId = request.SessionId,
            Kind = request.Kind,
            ConnectionId = conn.ConnectionId,
            DeviceName = request.DeviceName,
            CreatedAt = _time.GetUtcNow()
        };
        _calls[callId] = call;

        var command = new DesktopCallCommand(
            DesktopProtocol.Version, callId, request.Kind, request.Args,
            (int)deadline.TotalSeconds, request.RequiresConfirmation,
            (int)confirmWait.TotalMinutes, request.SessionId, request.ChatName,
            _time.GetUtcNow().ToUnixTimeMilliseconds());

        try
        {
            await _sender.SendCallAsync(conn.ConnectionId, command, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Команда {CallId} не ушла в соединение устройства {DeviceId}", callId, request.DeviceId);
            return Finish(call, DesktopCallResult.Server(callId, DesktopOutcomes.ProtocolError, 0, request.DeviceName));
        }

        try
        {
            // Фаза 1. Ack за 2 с — иначе честная ошибка, а не висение до таймаута MCP.
            switch (await WaitAsync(call, call.Ack.Task, DesktopProtocol.AckTimeout, ct))
            {
                case WaitStatus.TimedOut:
                    return Finish(call, DesktopCallResult.Server(callId, DesktopOutcomes.NoAck, 0, request.DeviceName));
                case WaitStatus.Aborted:
                    return Finish(call, call.Abort.Task.Result);
            }

            // Фаза 2. Разговор с человеком. Часы исполнения здесь не идут: ожидание меряется
            // минутами, и его истечение — самостоятельный исход, а не «дедлайн истёк».
            if (request.RequiresConfirmation)
            {
                var (status, decision) = await WaitForConfirmationAsync(call, confirmWait, ct);
                if (status == WaitStatus.Aborted) return Finish(call, call.Abort.Task.Result);
                if (status == WaitStatus.TimedOut)
                {
                    await SendCancelAsync(call, "ожидание подтверждения истекло");
                    return Finish(call, DesktopCallResult.Server(
                        callId, DesktopOutcomes.AwaitingConfirmation, Math.Max(call.LastAppliedStep, 0),
                        request.DeviceName, (int)confirmWait.TotalMinutes));
                }
                if (decision == ConfirmDecision.Declined)
                    return Finish(call, DesktopCallResult.Server(callId, DesktopOutcomes.Denied, 0, request.DeviceName));
            }

            // Фаза 3. Встречный go — только теперь устройство вправе исполнять.
            await _sender.SendGoAsync(conn.ConnectionId, new DesktopGoCommand(callId, (int)deadline.TotalSeconds), ct);

            // Фаза 4. Дедлайн исполнения по виду вызова.
            switch (await WaitAsync(call, call.Result.Task, deadline, ct))
            {
                case WaitStatus.TimedOut:
                    await SendCancelAsync(call, "дедлайн исполнения истёк");
                    return Finish(call, DesktopCallResult.Server(
                        callId, DesktopOutcomes.DeadlineExceeded, call.LastAppliedStep, request.DeviceName));
                case WaitStatus.Aborted:
                    return Finish(call, call.Abort.Task.Result);
            }

            return Finish(call, call.Result.Task.Result);
        }
        catch (OperationCanceledException)
        {
            // Interrupt пользователя: гасим ожидание и невыполненные шаги на устройстве.
            await SendCancelAsync(call, "вызов отменён");
            return Finish(call, DesktopCallResult.Server(
                callId, DesktopOutcomes.Cancelled, call.LastAppliedStep, request.DeviceName));
        }
    }

    // ---------- донесения устройства (хаб) ----------

    /// <summary>Подтверждение приёма команды. Чужое соединение донесение не проводит.</summary>
    public bool Ack(string callId, string connectionId) =>
        TryGetOwnCall(callId, connectionId, out var call) && call.Ack.TrySetResult(true);

    /// <summary>Устройство просит времени на разговор с человеком (минуты).</summary>
    public bool Awaiting(string callId, string connectionId, int minutes)
    {
        if (!TryGetOwnCall(callId, connectionId, out var call)) return false;
        call.AwaitMinutes = Math.Max(1, minutes);
        return true;
    }

    /// <summary>Человек подтвердил — можно слать go.</summary>
    public bool Confirm(string callId, string connectionId) =>
        TryGetOwnCall(callId, connectionId, out var call) && call.Confirm.TrySetResult(ConfirmDecision.Confirmed);

    /// <summary>Человек отказал — отказ уходит модели текстом.</summary>
    public bool Decline(string callId, string connectionId) =>
        TryGetOwnCall(callId, connectionId, out var call) && call.Confirm.TrySetResult(ConfirmDecision.Declined);

    /// <summary>
    /// Донесение о прогрессе батча. Нужно ради инварианта «в любом исходе возвращается индекс
    /// последнего применённого шага»: при обрыве и дедлайне результата от устройства нет.
    /// </summary>
    public bool Progress(string callId, string connectionId, int lastAppliedStep)
    {
        if (!TryGetOwnCall(callId, connectionId, out var call)) return false;
        if (lastAppliedStep > call.LastAppliedStep) call.LastAppliedStep = lastAppliedStep;
        return true;
    }

    // ---------- результат (HTTP) ----------

    /// <summary>
    /// Принять результат вызова. Приём одноразовый: повтор — 409, всё остальное принимается,
    /// включая поздний (ожидающий уже ушёл по дедлайну) и частичный результат.
    /// </summary>
    public DesktopResultAcceptance TryAcceptResult(string callId, string ownerId, string deviceId, DesktopCallResult result)
    {
        if (!_calls.TryGetValue(callId, out var call)) return DesktopResultAcceptance.UnknownCall;
        if (call.OwnerId != ownerId || call.DeviceId != deviceId) return DesktopResultAcceptance.Forbidden;

        lock (call)
        {
            if (call.Posted is not null) return DesktopResultAcceptance.Duplicate;
            var step = result.LastAppliedStep >= 0 ? result.LastAppliedStep : call.LastAppliedStep;
            // Исход, которого нет в протоколе, — это unknown, а не «поверим устройству».
            var outcome = DesktopOutcomes.FromDevice.Contains(result.Outcome)
                ? result.Outcome
                : DesktopOutcomes.Unknown;
            call.Posted = result with { CallId = callId, Outcome = outcome, LastAppliedStep = step };
        }

        if (call.Posted!.LastAppliedStep > call.LastAppliedStep) call.LastAppliedStep = call.Posted.LastAppliedStep;
        // Ожидающий мог уже уйти по дедлайну или обрыву — тогда результат просто ложится в
        // реестр и достаётся эндпоинтом «забрать результат по callId».
        call.Result.TrySetResult(call.Posted);
        return DesktopResultAcceptance.Accepted;
    }

    /// <summary>Забрать присланный результат по callId (реконнект устройства).</summary>
    public DesktopResultLookup TryGetPostedResult(string callId, string ownerId, string deviceId, out DesktopCallResult? result)
    {
        result = null;
        if (!_calls.TryGetValue(callId, out var call)) return DesktopResultLookup.UnknownCall;
        if (call.OwnerId != ownerId || call.DeviceId != deviceId) return DesktopResultLookup.Forbidden;
        if (call.Posted is null) return DesktopResultLookup.Pending;
        result = call.Posted;
        return DesktopResultLookup.Found;
    }

    // ---------- отмена ----------

    /// <summary>Отменить конкретный вызов: ожидание и невыполненные шаги гаснут.</summary>
    public async Task<bool> CancelAsync(string callId, string reason, CancellationToken ct = default)
    {
        if (!_calls.TryGetValue(callId, out var call) || call.Returned is not null) return false;
        await SendCancelAsync(call, reason, ct);
        return call.Abort.TrySetResult(DesktopCallResult.Server(
            callId, DesktopOutcomes.Cancelled, call.LastAppliedStep, call.DeviceName));
    }

    /// <summary>
    /// Отменить все вызовы чата — сеанс погас или грань выключили в проекте: живой ход CLI
    /// иначе доработает со старым составом.
    /// </summary>
    public Task CancelSessionAsync(string sessionId, string reason, CancellationToken ct = default) =>
        CancelWhereAsync(c => c.SessionId == sessionId, reason, ct);

    /// <summary>Отменить все вызовы устройства.</summary>
    public Task CancelDeviceAsync(string ownerId, string deviceId, string reason, CancellationToken ct = default) =>
        CancelWhereAsync(c => c.OwnerId == ownerId && c.DeviceId == deviceId, reason, ct);

    private async Task CancelWhereAsync(Func<CallRecord, bool> predicate, string reason, CancellationToken ct)
    {
        foreach (var call in _calls.Values.Where(c => c.Returned is null && predicate(c)).ToList())
            await CancelAsync(call.CallId, reason, ct);
    }

    // ---------- внутреннее ----------

    private async Task<(WaitStatus Status, ConfirmDecision? Decision)> WaitForConfirmationAsync(
        CallRecord call, TimeSpan window, CancellationToken ct)
    {
        var status = await WaitAsync(call, call.Confirm.Task, window, ct);
        if (status == WaitStatus.Signalled) return (status, call.Confirm.Task.Result);
        if (status == WaitStatus.Aborted) return (status, null);

        // Устройство могло попросить больше минут, чем окно по умолчанию, — уважаем просьбу
        // в пределах потолка. Второго продления нет: ожидание не должно быть бесконечным.
        var asked = call.AwaitMinutes is int m
            ? Min(TimeSpan.FromMinutes(m), DesktopProtocol.MaxConfirmationWait)
            : TimeSpan.Zero;
        var extra = asked > window ? asked - window : TimeSpan.Zero;
        if (extra <= TimeSpan.Zero) return (WaitStatus.TimedOut, null);

        status = await WaitAsync(call, call.Confirm.Task, extra, ct);
        return status == WaitStatus.Signalled ? (status, call.Confirm.Task.Result) : (status, null);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan ClampConfirmationWait(int? minutes)
    {
        if (minutes is not int m || m <= 0) return DesktopProtocol.DefaultConfirmationWait;
        var wait = TimeSpan.FromMinutes(m);
        return wait > DesktopProtocol.MaxConfirmationWait ? DesktopProtocol.MaxConfirmationWait : wait;
    }

    // Ожидание фазы: сигнал устройства / истечение окна / внешняя остановка вызова.
    // Время — через TimeProvider, чтобы тесты не спали.
    private async Task<WaitStatus> WaitAsync(CallRecord call, Task task, TimeSpan timeout, CancellationToken ct)
    {
        if (task.IsCompleted) return WaitStatus.Signalled;
        if (call.Abort.Task.IsCompleted) return WaitStatus.Aborted;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, _time, cts.Token);
        var done = await Task.WhenAny(task, delay, call.Abort.Task);
        await cts.CancelAsync();

        if (done == task) return WaitStatus.Signalled;
        if (done == call.Abort.Task) return WaitStatus.Aborted;
        ct.ThrowIfCancellationRequested();
        return WaitStatus.TimedOut;
    }

    private async Task SendCancelAsync(CallRecord call, string reason, CancellationToken ct = default)
    {
        try
        {
            await _sender.SendCancelAsync(call.ConnectionId, new DesktopCancelCommand(call.CallId, reason), ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Отмена вызова {CallId} не доехала до устройства", call.CallId);
        }
    }

    private DesktopCallResult Finish(CallRecord call, DesktopCallResult result)
    {
        lock (call)
        {
            call.Returned ??= result;
            call.FinishedAt = _time.GetUtcNow();
            return call.Returned;
        }
    }

    private bool TryGetOwnCall(string callId, string connectionId, out CallRecord call)
    {
        call = null!;
        if (!_calls.TryGetValue(callId, out var found)) return false;
        // Донесение принимается только от того соединения, которому команда и уходила.
        if (found.ConnectionId != connectionId) return false;
        call = found;
        return true;
    }

    /// <summary>Чистка реестра: завершённые вызовы живут ResultRetention, потом уходят.</summary>
    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, call) in _calls)
        {
            if (call.FinishedAt is DateTimeOffset finished && now - finished > DesktopProtocol.ResultRetention)
                _calls.TryRemove(id, out _);
        }

        if (_calls.Count <= MaxRetainedCalls) return;
        foreach (var call in _calls.Values
                     .Where(c => c.FinishedAt is not null)
                     .OrderBy(c => c.FinishedAt)
                     .Take(_calls.Count - MaxRetainedCalls))
            _calls.TryRemove(call.CallId, out _);
    }

    private async Task NotifyAsync(Func<IDeviceConnectionObserver, Task> action)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await action(observer);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Наблюдатель соединений устройств упал");
            }
        }
    }
}
