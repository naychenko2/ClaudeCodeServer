using System.Collections.Concurrent;
using AiHomeDesktop.App.Hands;

namespace AiHomeDesktop.App.Execution;

/// <summary>
/// Склейка вызова на устройстве (ADR-008, «Протокол канала»):
/// Call → Ack за 2 с → подтверждение человека, если оно требуется → встречный Go →
/// исполнение в пределах дедлайна вида вызова → POST результата.
///
/// Правила, которые этот класс держит и которые нельзя «улучшить» по дороге:
/// - двухфазность: до Go не исполняется ничего, даже подтверждённое;
/// - авто-ретраев нет нигде — повторно пришедший вызов не исполняется, а отвечает тем, что
///   уже записано в журнале: клик и ввод не идемпотентны;
/// - отказ человека результатом не оформляется — его оформляет сервер (исход denied) и
///   отдаёт модели текстом; наше дело донести Decline;
/// - Cancel гасит и ожидание человека, и невыполненные шаги; уже отправленное не откатывается;
/// - результат, который не доехал, остаётся в журнале и досылается после реконнекта.
///
/// Про WinAPI класс не знает ничего: вся машина за <see cref="IDesktopExecutor"/>.
/// </summary>
public sealed class CallPipeline(
    IDeviceChannelClient channel,
    IDeviceCallsApi api,
    IDesktopExecutor executor,
    ICallJournal journal,
    ICallConfirmation confirmation,
    IHandsActivityFeed feed,
    TimeProvider? timeProvider = null,
    Action<string, Exception?>? log = null)
{
    /// <summary>Раньше этого срока время у человека не просим — иначе просьба уходит на каждый тост.</summary>
    private static readonly TimeSpan MinNudgeDelay = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>Вызовы, которые сейчас ведутся (ожидание человека, Go, исполнение).</summary>
    public int ActiveCalls => _runs.Count;

    // ---------- вход канала ----------

    /// <summary>
    /// Пришла команда. Метод обязан вернуться быстро: он живёт на приёмнике SignalR, а Ack
    /// сервер ждёт две секунды. Всё, что длится, уходит в фоновую задачу.
    /// </summary>
    public async Task OnCallAsync(DesktopCall call, CancellationToken ct = default)
    {
        var run = new Run(call);
        // Регистрируем ДО Ack: встречный Go для вызова без подтверждения приходит сразу за ним.
        _runs[call.CallId] = run;

        try
        {
            await channel.AckAsync(call.CallId, ct).WaitAsync(ClientProtocol.AckTimeout, _time, ct);
        }
        catch (Exception ex)
        {
            // Ack не ушёл — сервер закроет вызов исходом no_ack. Своего результата не шлём:
            // ни один шаг не применён, и выдумывать исход не наше дело.
            Log($"Ack по вызову {call.CallId} не ушёл", ex);
            _runs.TryRemove(call.CallId, out _);
            return;
        }

        // Повторно пришедший вызов не исполняем — отвечаем тем, что уже знаем о нём.
        if (!await journal.TryBeginAsync(call.CallId, call.Kind, ct))
        {
            _runs.TryRemove(call.CallId, out _);
            await ResendKnownAsync(call, ct);
            return;
        }

        // Грань этой версии клиента — два инструмента; состав tools/list от этого не зависит.
        if (!ClientProtocol.Supports(call.Kind) || !executor.Supports(call.Kind))
        {
            await FinishAsync(run, new DesktopCallOutcome(
                DesktopOutcomes.Unknown, 0, ClientProtocol.UnsupportedMessage(call.Kind)), CancellationToken.None);
            run.Dispose();
            return;
        }

        _ = Task.Run(() => RunAsync(run), CancellationToken.None);
    }

    /// <summary>Встречный Go: с этого момента идут часы дедлайна исполнения.</summary>
    public void OnGo(string callId, int deadlineSeconds)
    {
        if (_runs.TryGetValue(callId, out var run)) run.Go.TrySetResult(deadlineSeconds);
        else Log($"Go по неизвестному вызову {callId}", null);
    }

    /// <summary>
    /// Отмена вызова сервером: истекло ожидание человека, погас сеанс, нажали «Стоп».
    /// Гасит и висящий тост, и незавершённое исполнение.
    /// </summary>
    public async Task OnCancelAsync(string callId, string reason, CancellationToken ct = default)
    {
        confirmation.Close(callId, reason);
        if (!_runs.TryRemove(callId, out var run)) return;

        run.Cancel();
        await journal.MarkDeliveredAsync(callId, ct);
        Feed(run.Call, HandsFeedKind.Cancelled, $"вызов отменён ({reason}); уже отправленное не откатывается");
    }

    /// <summary>
    /// Погасить все ведущиеся вызовы локально — «Стоп» человека и разрыв канала. Разрыв на
    /// стороне сервера делает сервер; здесь мы закрываем тосты, чтобы человек не подтверждал
    /// вызов, которого уже нет.
    /// </summary>
    public async Task CancelAllAsync(string reason, CancellationToken ct = default)
    {
        foreach (var callId in _runs.Keys.ToArray())
            await OnCancelAsync(callId, reason, ct);
    }

    /// <summary>
    /// Реконнект: досылаем результаты, которые не доехали. Сверяемся с сервером — вызов мог
    /// уже получить результат или вовсе быть вытеснен из реестра.
    /// </summary>
    public async Task FlushJournalAsync(CancellationToken ct = default)
    {
        foreach (var entry in await journal.UndeliveredAsync(ct))
        {
            if (entry.Outcome is null)
            {
                // Исполнение оборвалось, результата нет. Придумывать его нельзя, повторять
                // вызов — тем более: сервер сам закрыл его исходом unknown.
                await journal.MarkDeliveredAsync(entry.CallId, ct);
                continue;
            }

            switch (await api.GetResultStateAsync(entry.CallId, ct))
            {
                case ServerResultState.Found:
                case ServerResultState.UnknownCall:
                    await journal.MarkDeliveredAsync(entry.CallId, ct);
                    break;
                case ServerResultState.Pending:
                    await DeliverAsync(entry.CallId, entry.Outcome, ct);
                    break;
                case ServerResultState.Unreachable:
                    // Связи опять нет — журнал ждёт следующего реконнекта.
                    return;
            }
        }
    }

    // ---------- ведение вызова ----------

    private async Task RunAsync(Run run)
    {
        var call = run.Call;
        var ct = run.Token;

        try
        {
            if (call.RequiresConfirmation && !await AskAsync(call, ct))
            {
                // Отказ уходит модели текстом — исход denied ставит сервер по Decline.
                await SafeAsync(() => channel.DeclineAsync(call.CallId, CancellationToken.None), $"Decline {call.CallId}");
                await journal.MarkDeliveredAsync(call.CallId, CancellationToken.None);
                _runs.TryRemove(call.CallId, out _);
                Feed(call, HandsFeedKind.Declined, $"{Describe(call)} — отклонено на устройстве");
                return;
            }

            if (call.RequiresConfirmation)
            {
                await channel.ConfirmAsync(call.CallId, ct);
                Feed(call, HandsFeedKind.Confirmed, $"{Describe(call)} — подтверждено");
            }

            var deadlineSeconds = await WaitForGoAsync(run, ct);
            if (deadlineSeconds is null)
            {
                // Go не пришёл: сервер закрыл вызов сам (ожидание, дедлайн, погасший сеанс).
                await journal.MarkDeliveredAsync(call.CallId, CancellationToken.None);
                _runs.TryRemove(call.CallId, out _);
                Feed(call, HandsFeedKind.Cancelled, $"{Describe(call)} — сервер не дал ход исполнению");
                return;
            }

            await FinishAsync(run, await ExecuteAsync(run, deadlineSeconds.Value), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Отмену уже оформил OnCancelAsync — второй записи в ленте не нужно.
            _runs.TryRemove(call.CallId, out _);
        }
        catch (Exception ex)
        {
            Log($"Вызов {call.CallId} ({call.Kind}) сорвался", ex);
            await FinishAsync(run, new DesktopCallOutcome(
                DesktopOutcomes.Unknown, -1, $"Клиент устройства не довёл вызов до конца: {ex.Message}"), CancellationToken.None);
        }
        finally
        {
            confirmation.Close(call.CallId, "вызов завершён");
            run.Dispose();
        }
    }

    private async Task<DesktopCallOutcome> ExecuteAsync(Run run, int deadlineSeconds)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, deadlineSeconds)));

        try
        {
            return await executor.ExecuteAsync(run.Call, deadline.Token);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            throw; // отмена сервера — её оформляет OnCancelAsync
        }
        catch (OperationCanceledException)
        {
            // Свой дедлайн истёк раньше серверного. Индекс шага отдаёт исполнитель, у нас его
            // нет — -1 честнее нуля: «неизвестно» не то же самое, что «ни один шаг».
            return new DesktopCallOutcome(DesktopOutcomes.DeadlineExceeded, -1,
                $"Дедлайн исполнения {deadlineSeconds} с истёк на устройстве.");
        }
        catch (Exception ex)
        {
            Log($"Исполнение вызова {run.Call.CallId} упало", ex);
            return new DesktopCallOutcome(DesktopOutcomes.Unknown, -1,
                $"Исполнить вызов на устройстве не удалось: {ex.Message}");
        }
    }

    // Ждём Go столько же, сколько идёт само исполнение, но не меньше половины минуты:
    // после подтверждения сервер отвечает сразу, а вечного ожидания быть не должно.
    private async Task<int?> WaitForGoAsync(Run run, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Max(30, run.Call.DeadlineSeconds));
        try
        {
            return await run.Go.Task.WaitAsync(window, _time, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task<bool> AskAsync(DesktopCall call, CancellationToken ct)
    {
        var request = ConfirmationText.For(call);
        using var nudge = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asking = NudgeAsync(call, nudge.Token);

        try
        {
            return await confirmation.AskAsync(request, ct);
        }
        finally
        {
            await nudge.CancelAsync();
            await SafeAsync(() => asking, "просьба о времени");
        }
    }

    // Разговор с человеком затянулся — просим у сервера времени, пока он не закрыл ожидание.
    // Просим сразу потолок: второго продления протокол не даёт.
    private async Task NudgeAsync(DesktopCall call, CancellationToken ct)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, call.ConfirmationWaitMinutes));
        var delay = window / 2 < MinNudgeDelay ? MinNudgeDelay : window / 2;

        try
        {
            await Task.Delay(delay, _time, ct);
            await channel.AwaitingAsync(call.CallId, ClientProtocol.MaxConfirmationWaitMinutes, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Просьба о времени по вызову {call.CallId} не ушла", ex);
        }
    }

    // ---------- результат ----------

    private async Task FinishAsync(Run run, DesktopCallOutcome outcome, CancellationToken ct)
    {
        _runs.TryRemove(run.Call.CallId, out _);
        await journal.CompleteAsync(run.Call.CallId, outcome, ct);
        await DeliverAsync(run.Call.CallId, outcome, ct);

        // Каждый ушедший кадр обязан быть виден человеку — это цена того, что внутри сеанса
        // чтение идёт без отдельного нажатия.
        Feed(run.Call, outcome.Outcome == DesktopOutcomes.Ok ? HandsFeedKind.Sent : HandsFeedKind.Failed,
            HandsFeedText.ForResult(run.Call, outcome));
    }

    private async Task DeliverAsync(string callId, DesktopCallOutcome outcome, CancellationToken ct)
    {
        var delivery = await api.PostResultAsync(callId, outcome, ct);
        switch (delivery)
        {
            case CallResultDelivery.Accepted:
            case CallResultDelivery.Duplicate:
            case CallResultDelivery.UnknownCall:
                await journal.MarkDeliveredAsync(callId, ct);
                break;
            default:
                // Связи нет или отказ авторизации — запись остаётся в журнале до реконнекта.
                Log($"Результат вызова {callId} не доехал: {delivery}", null);
                break;
        }
    }

    // Команда пришла второй раз: исполнять её заново нельзя. Если результат по ней уже есть —
    // отдаём тот же; если исполнение не дошло до результата — говорим об этом прямо.
    private async Task ResendKnownAsync(DesktopCall call, CancellationToken ct)
    {
        var known = await journal.FindAsync(call.CallId, ct);
        var outcome = known?.Outcome ?? new DesktopCallOutcome(
            DesktopOutcomes.Unknown, -1,
            "Этот вызов устройство уже получало; повторно оно его не исполняет. Чем кончилась первая попытка — по журналу устройства неизвестно.");

        await DeliverAsync(call.CallId, outcome, ct);
        Feed(call, HandsFeedKind.Failed, $"{Describe(call)} — повторная команда, исполнение не запускалось");
    }

    // ---------- мелочи ----------

    private static string Describe(DesktopCall call) => call.Kind switch
    {
        DesktopCallKinds.Screen => $"кадр: {ConfirmationText.ScreenScope(call.Args)}",
        DesktopCallKinds.Open => "открытие приложения или ссылки",
        _ => $"вызов «{call.Kind}»"
    };

    private void Feed(DesktopCall call, HandsFeedKind kind, string text) =>
        feed.Add(new HandsFeedEntry(_time.GetUtcNow(), kind, call.ChatTitle, text));

    private void Log(string what, Exception? ex) => log?.Invoke(what, ex);

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try { await action(); }
        catch (Exception ex) { Log($"{what}: не удалось", ex); }
    }

    /// <summary>Ведущийся вызов: отмена и ожидание встречного Go.</summary>
    private sealed class Run(DesktopCall call) : IDisposable
    {
        public DesktopCall Call { get; } = call;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<int> Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => Cts.Token;

        public void Cancel()
        {
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            Go.TrySetCanceled();
        }

        public void Dispose() => Cts.Dispose();
    }
}
