using System.Collections.Concurrent;
using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHomeDesktop.Core.Channel;

/// <summary>
/// Жизнь вызова на устройстве: Ack → разговор с человеком → встречный Go → исполнение →
/// результат POST'ом. Собирает вместе канал, журнал, тост подтверждения и грань исполнения;
/// сама ничего про WinAPI не знает и потому проверяется тестами целиком.
///
/// Три правила, которые здесь нельзя нарушать (ADR-008, «Протокол канала»):
/// 1. Ack уходит ПЕРВЫМ и до любого разговора с человеком — у сервера на него 2 секунды.
/// 2. Повторно пришедший вызов не исполняется, авто-ретраев исполнения нет нигде.
/// 3. Индекс последнего применённого шага возвращается в ЛЮБОМ исходе.
/// </summary>
public sealed class DesktopCallCoordinator(
    IDeviceChannel channel,
    DeviceApi api,
    CallJournal journal,
    IDesktopExecutor executor,
    IConfirmationUi confirmations,
    ICallFeed feed,
    ILogger<DesktopCallCoordinator>? log = null,
    TimeProvider? time = null) : IDeviceCallHandler
{
    private readonly ILogger _log = log ?? NullLogger<DesktopCallCoordinator>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    /// <summary>Сколько ждём встречного Go после подтверждения, прежде чем бросить вызов.</summary>
    private static readonly TimeSpan GoWait = TimeSpan.FromMinutes(1);

    /// <summary>Фоновая работа по вызовам — её ждут тесты, чтобы не гоняться за таймингами.</summary>
    public Task Idle => Task.WhenAll(_running.Values.ToArray());

    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);

    public async Task OnCallAsync(DesktopCallCommand command)
    {
        // Место для встречного Go заводим ДО ack: у вызова, которому подтверждение не нужно
        // (кадр внутри сеанса), go приходит сразу за ack и иначе потерялся бы.
        var pending = new PendingCall();
        _pending[command.CallId] = pending;

        // Ack — первым делом: сервер ждёт его 2 секунды, и никакой разговор с человеком не
        // имеет права встать раньше.
        try
        {
            await channel.AckAsync(command.CallId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ack по вызову {CallId} не ушёл", command.CallId);
        }

        if (!journal.TryBegin(command.CallId, command.Kind, out var known))
        {
            // Команда уже приходила. Исполнять её второй раз нельзя ни при каких условиях —
            // остаётся дослать результат, если он не доехал.
            _log.LogInformation("Вызов {CallId} уже известен журналу; повторно не исполняем", command.CallId);
            _pending.TryRemove(command.CallId, out _);
            pending.Cancellation.Dispose();
            if (known is { Result: not null, Delivered: false })
                Track(command.CallId, DeliverAsync(command.CallId, known.Result));
            return;
        }

        feed.Add(new DesktopFeedEntry(
            _time.GetUtcNow(), command.CallId, command.Kind, command.ChatName,
            ConfirmationText.For(command).Text));

        // Дальше — в фон: очередь сообщений канала не должна ждать человека и исполнения.
        Track(command.CallId, RunAsync(command, pending));
    }

    public void OnGo(DesktopGoCommand go)
    {
        if (_pending.TryGetValue(go.CallId, out var pending)) pending.Go.TrySetResult(go);
    }

    public void OnCancel(DesktopCancelCommand cancel)
    {
        if (!_pending.TryGetValue(cancel.CallId, out var pending)) return;
        _log.LogInformation("Вызов {CallId} отменён: {Reason}", cancel.CallId, cancel.Reason);
        pending.Go.TrySetResult(null);
        pending.Cancellation.Cancel();
    }

    /// <summary>Канал поднялся — досылаем всё, что не доехало до сервера.</summary>
    public async Task OnConnectedAsync()
    {
        foreach (var entry in journal.Undelivered())
        {
            if (entry.Result is null) continue;
            await DeliverAsync(entry.CallId, entry.Result);
        }
    }

    private async Task RunAsync(DesktopCallCommand command, PendingCall pending)
    {
        try
        {
            if (!DesktopCallKinds.IsKnown(command.Kind))
            {
                await FinishAsync(command, DeviceCallResultBody.Refused(
                    DesktopOutcomes.ProtocolError, DesktopClientOutcomeText.UnknownKind(command.Kind)));
                return;
            }

            if (!executor.Supports(command.Kind))
            {
                // Честный исход, а не молчание: состав tools/list от возможностей клиента не
                // зависит — он входит в сигнатуру запуска CLI. Версия клиента объявлена в Hello.
                await FinishAsync(command, DeviceCallResultBody.Refused(
                    DesktopOutcomes.ProtocolError, DesktopClientOutcomeText.NotSupported(command.Kind)));
                return;
            }

            if (command.RequiresConfirmation && !await AskHumanAsync(command, pending))
                return;

            // Встречный Go: до него исполнять нельзя даже то, что человек уже подтвердил.
            var go = await WaitForGoAsync(pending);
            if (go is null)
            {
                _log.LogInformation("По вызову {CallId} встречного go не пришло", command.CallId);
                journal.MarkDelivered(command.CallId);
                return;
            }

            journal.MarkExecuting(command.CallId);

            var deadline = go.DeadlineSeconds > 0
                ? TimeSpan.FromSeconds(go.DeadlineSeconds)
                : command.Deadline;

            using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(pending.Cancellation.Token);
            deadlineSource.CancelAfter(deadline);

            var progress = new Progress<int>(step =>
            {
                // Донесение о прогрессе: без него при обрыве и дедлайне индекс последнего
                // применённого шага взять было бы неоткуда.
                _ = channel.ProgressAsync(command.CallId, step);
            });

            DeviceCallResultBody result;
            try
            {
                result = await executor.ExecuteAsync(command, progress, deadlineSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Ни слова о повторе: неизвестный исход не означает «не применилось».
                result = new DeviceCallResultBody(
                    DesktopOutcomes.Cancelled, -1,
                    "Вызов прерван на устройстве: отмена или истёкший дедлайн исполнения.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Вызов {CallId} упал на устройстве", command.CallId);
                result = new DeviceCallResultBody(
                    DesktopOutcomes.Unknown, -1,
                    $"Клиент не смог довести вызов до конца: {ex.Message}");
            }

            await FinishAsync(command, result);
        }
        finally
        {
            _pending.TryRemove(command.CallId, out _);
            pending.Cancellation.Dispose();
        }
    }

    /// <summary>Разговор с человеком. false — дальше идти незачем: отказ или молчание.</summary>
    private async Task<bool> AskHumanAsync(DesktopCallCommand command, PendingCall pending)
    {
        var wait = command.ConfirmationWaitMinutes > 0
            ? TimeSpan.FromMinutes(command.ConfirmationWaitMinutes)
            : TimeSpan.FromMinutes(3);

        try
        {
            await channel.AwaitingAsync(command.CallId, (int)wait.TotalMinutes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Донесение об ожидании по вызову {CallId} не ушло", command.CallId);
        }

        // Текст тоста собирает КЛИЕНТ из фактических аргументов вызова: модельного резюме
        // человек не видит никогда.
        var prompt = ConfirmationText.For(command);
        ConfirmationDecision decision;
        try
        {
            decision = await confirmations.AskAsync(prompt, wait, pending.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            decision = ConfirmationDecision.NoAnswer;
        }

        switch (decision)
        {
            case ConfirmationDecision.Confirmed:
                await channel.ConfirmAsync(command.CallId);
                return true;

            case ConfirmationDecision.Declined:
                await channel.DeclineAsync(command.CallId);
                // Исход человека ставит сервер (denied), досылать нечего — но в журнале
                // отметка нужна: повторно пришедшая команда не должна снова будить человека.
                journal.RecordResult(command.CallId, DeviceCallResultBody.Refused(
                    DesktopOutcomes.Denied, "Человек отклонил действие на устройстве."));
                journal.MarkDelivered(command.CallId);
                feed.Add(Finished(command, DesktopOutcomes.Denied));
                return false;

            default:
                journal.MarkDelivered(command.CallId);
                feed.Add(Finished(command, DesktopOutcomes.AwaitingConfirmation));
                return false;
        }
    }

    private async Task<DesktopGoCommand?> WaitForGoAsync(PendingCall pending)
    {
        var timeout = Task.Delay(GoWait, pending.Cancellation.Token);
        var completed = await Task.WhenAny(pending.Go.Task, timeout);
        return completed == pending.Go.Task ? pending.Go.Task.Result : null;
    }

    private async Task FinishAsync(DesktopCallCommand command, DeviceCallResultBody result)
    {
        journal.RecordResult(command.CallId, result);
        feed.Add(Finished(command, result.Outcome, PayloadSize.Describe(result.Payload)));
        await DeliverAsync(command.CallId, result);
    }

    /// <summary>
    /// Доставка результата. Не доехало — запись остаётся в журнале и уедет на следующем
    /// подъёме канала; сервер, забывший вызов, и дубль закрывают запись насовсем.
    /// </summary>
    private async Task DeliverAsync(string callId, DeviceCallResultBody result)
    {
        var delivery = await api.PostResultAsync(callId, result);
        switch (delivery)
        {
            case ResultDelivery.Accepted:
            case ResultDelivery.Duplicate:
            case ResultDelivery.UnknownCall:
                journal.MarkDelivered(callId);
                break;
            default:
                _log.LogInformation("Результат вызова {CallId} не доехал ({Delivery}); дошлём после подъёма канала",
                    callId, delivery);
                break;
        }
    }

    private DesktopFeedEntry Finished(DesktopCallCommand command, string outcome, string? details = null) => new(
        _time.GetUtcNow(), command.CallId, command.Kind, command.ChatName,
        ConfirmationText.For(command).Text, outcome, details);

    private void Track(string callId, Task work)
    {
        var key = $"{callId}:{Guid.NewGuid():N}";
        _running[key] = work;
        _ = work.ContinueWith(_ => _running.TryRemove(key, out Task? _), TaskScheduler.Default);
    }

    private sealed class PendingCall
    {
        public readonly TaskCompletionSource<DesktopGoCommand?> Go =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly CancellationTokenSource Cancellation = new();
    }
}
