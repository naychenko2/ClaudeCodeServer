namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Сеанс рук со стороны устройства (ADR-008, «Сеанс рук и согласие»).
///
/// Сеанс стартует ТОЛЬКО отсюда: у веб-морды и у модели кнопки «начать» нет — веб-морда
/// может лишь попросить, и просьба приходит сюда заявкой с именем чата, проекта и персоны.
/// Безымянной кнопки «Начать сеанс» не существует: человек всегда выбирает конкретный чат.
///
/// «Стоп» — вне канала агента: кнопка в окне и пункт трея зовут <see cref="StopAsync"/>,
/// разрыв сеанса делает сервер. Закрытие окна оболочки — это тоже стоп, но со своим поводом
/// (<see cref="HandsStopReasons.ClientClosed"/>); жизнь в трее закрытием НЕ считается.
/// </summary>
public sealed class HandsSessionManager(
    IHandsApi api,
    IHandsIndicator indicator,
    IHandsActivityFeed feed,
    TimeProvider? timeProvider = null,
    Action<string, Exception?>? log = null) : IAsyncDisposable
{
    /// <summary>Как часто клиент спрашивает очередь заявок и статус сеанса.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    private IReadOnlyList<HandsRequest> _requests = [];
    private HandsSessionInfo? _session;
    private Task? _polling;

    /// <summary>Состояние изменилось — окно перерисовывает очередь, отсчёт и кнопки.</summary>
    public event Action? Changed;

    /// <summary>Очередь заявок: свежие сверху. Пустая — обычное состояние, а не ошибка.</summary>
    public IReadOnlyList<HandsRequest> Requests
    {
        get { lock (_lock) return _requests; }
    }

    /// <summary>Текущий сеанс этого устройства либо null.</summary>
    public HandsSessionInfo? Session
    {
        get { lock (_lock) return _session; }
    }

    /// <summary>Идёт ли сеанс — по нему трей меняет иконку, а окно показывает «Стоп».</summary>
    public bool Active => Session is not null;

    // ---------- опрос ----------

    /// <summary>Начать опрос сервера. Повторный вызов ничего не ломает.</summary>
    public void StartPolling()
    {
        lock (_lock) _polling ??= Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        while (!_stopping.IsCancellationRequested)
        {
            await SafeRefreshAsync();
            try
            {
                if (!await timer.WaitForNextTickAsync(_stopping.Token)) return;
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SafeRefreshAsync()
    {
        // Разрыв связи — штатное состояние, а не авария: молча ждём следующего тика.
        try { await RefreshAsync(_stopping.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Invoke("Опрос сеанса рук не удался", ex); }
    }

    /// <summary>Перечитать очередь заявок и статус сеанса.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var requests = await api.RequestsAsync(ct);
        var session = await api.CurrentAsync(ct);
        Apply(requests, session);
    }

    // ---------- старт и стоп ----------

    /// <summary>
    /// Начать сеанс для выбранного чата. Отказ сервера (409) возвращается текстом — его
    /// показывают человеку как есть: причин ровно столько, сколько назвал сервер.
    /// </summary>
    public async Task<HandsStartOutcome> StartAsync(string chatSessionId, CancellationToken ct = default)
    {
        HandsStartOutcome outcome;
        try
        {
            outcome = await api.StartAsync(chatSessionId, ct);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Старт сеанса для чата {chatSessionId} не удался", ex);
            return new HandsStartOutcome(false, null, $"Сервер не ответил на старт сеанса: {ex.Message}");
        }

        if (outcome is { Started: true, Session: not null })
        {
            Apply(Requests.Where(r => r.ChatSessionId != chatSessionId).ToList(), outcome.Session);
            Feed(HandsFeedKind.Session, outcome.Session.ChatTitle,
                $"сеанс начат на устройстве {outcome.Session.Device ?? "этом"}");
        }
        else
        {
            // Заявка могла протухнуть, а чат — уехать на другое устройство: перечитываем.
            await SafeRefreshAsync();
        }

        return outcome;
    }

    /// <summary>«Стоп» человека: кнопка в окне и пункт трея. Повод по умолчанию — «человек остановил».</summary>
    public async Task<bool> StopAsync(string reason = HandsStopReasons.Stopped, CancellationToken ct = default)
    {
        var stopping = Session;
        try
        {
            var stopped = await api.StopAsync(reason, ct);
            Apply(Requests, null);
            if (stopping is not null)
                Feed(HandsFeedKind.Session, stopping.ChatTitle, StopText(reason));
            return stopped;
        }
        catch (Exception ex)
        {
            log?.Invoke("Остановить сеанс не удалось", ex);
            return false;
        }
    }

    /// <summary>
    /// Окно оболочки закрыли — сеанс гаснет с этим поводом. Сворачивание в трей сюда НЕ
    /// относится: оно не закрытие, и сеанс продолжается.
    /// </summary>
    public Task<bool> StopOnWindowClosedAsync(CancellationToken ct = default) =>
        StopAsync(HandsStopReasons.ClientClosed, ct);

    // ---------- отсчёт ----------

    /// <summary>
    /// Сколько сеансу осталось: минимум из 15 минут простоя и потолка в 2 часа. Считает
    /// сервер, клиент только показывает — иначе два отсчёта разъедутся.
    /// </summary>
    public string CountdownText()
    {
        var session = Session;
        if (session is null) return "Сеанса нет";

        var left = session.TimeLeft(_time.GetUtcNow());
        var clock = left.TotalHours >= 1
            ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
            : $"{left.Minutes}:{left.Seconds:00}";

        return session.EndsByIdle
            ? $"Погаснет через {clock} без вызовов"
            : $"Погаснет через {clock} — потолок сеанса";
    }

    // ---------- внутреннее ----------

    private void Apply(IReadOnlyList<HandsRequest> requests, HandsSessionInfo? session)
    {
        HandsSessionInfo? was;
        lock (_lock)
        {
            was = _session;
            _requests = requests;
            _session = session;
        }

        // Сеанс мог погаснуть на сервере (простой, потолок, разрыв, снятый тумблер грани):
        // человек узнаёт об этом из ленты, а не по молча пропавшей кнопке.
        if (was is not null && session is null)
            Feed(HandsFeedKind.Session, was.ChatTitle, "сеанс погас на сервере");

        indicator.Update(
            session is not null ? HandsIndicatorState.Active
            : requests.Count > 0 ? HandsIndicatorState.Requested
            : HandsIndicatorState.Idle,
            session?.ChatTitle ?? (requests.Count > 0 ? requests[0].ChatTitle : null));

        Changed?.Invoke();
    }

    private static string StopText(string reason) => reason == HandsStopReasons.ClientClosed
        ? "сеанс остановлен: окно клиента закрыто"
        : "сеанс остановлен человеком";

    private void Feed(HandsFeedKind kind, string chatTitle, string text) =>
        feed.Add(new HandsFeedEntry(_time.GetUtcNow(), kind, chatTitle, text));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_polling is { } polling)
        {
            try { await polling; } catch (Exception ex) { log?.Invoke("Опрос сеанса рук остановлен с ошибкой", ex); }
        }
        _stopping.Dispose();
    }
}
