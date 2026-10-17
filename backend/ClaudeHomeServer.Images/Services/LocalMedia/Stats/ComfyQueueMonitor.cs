using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Классификация чужого прогона (есть в /queue, нет в реестре): кто держит GPU
public enum ComfyForeignKind
{
    // Другой инстанс AI Home: client_id с префиксом ccs-local-media-
    Instance,
    // Распознавание: граф содержит узел TranscriptionGate
    Transcription,
    // Прочие чужие прогоны стенда
    External,
}

// Чужой прогон с классификацией: prompt_id и вид
public sealed record ComfyForeignRun(string PromptId, ComfyForeignKind Kind);

// Внутренний снимок монитора очереди: для IComfyStats (шаг 26).
// Только class_type узлов (текст промптов/входов не читается) — сериализация не утечает чужой граф.
// Ccs — кэш ответа маршрута /ccs/stats узла (шаг 31): «загружено сейчас» + карты; null —
// узел не сообщает (маршрут не установлен) или ещё не опрашивали.
public sealed record ComfyQueueMonitorSnapshot(
    bool Down,
    DateTimeOffset? DownSince,
    string? Version,
    IReadOnlyList<ComfyQueueItem> Running,
    IReadOnlyList<ComfyQueueItem> Pending,
    IReadOnlyList<ComfyForeignRun> Foreign,
    ComfyCcsStats? Ccs);

// Фоновый монитор очереди ComfyUI (шаг 24): опрашивает /queue и /system_stats, ведёт внутренний
// снимок (running/queue/lastHour/slots/status) для будущей IComfyStats (шаг 26). Период: 2с, пока
// есть наши активные прогоны в реестре или панель запрашивала снимок в последние 10с (Touch),
// иначе 15с (живость). При уходе prompt_id из очереди — один GET /history/{id} для исхода и
// фактической длительности, потом ledger.Complete. ComfyUI недоступен → статус down с DownSince.
public sealed class ComfyQueueMonitor(
    ComfyClient comfy,
    ComfyPromptLedger ledger,
    IConfiguration config,
    ILogger<ComfyQueueMonitor> log,
    TimeProvider? time = null) : BackgroundService
{
    // Периоды: 2с — активные/панель, 15с — только живость
    internal TimeSpan ActivePoll { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan TouchWindow { get; init; } = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _lastTouch;
    // Wake-токен: Touch() будит монитор, не досиживая текущую паузу (иначе снимок устаревает)
    private CancellationTokenSource? _wakeCts;
    private readonly object _touchLock = new();

    // Последний вычисленный период: тесты проверяют переключение 2с ↔ 15с
    internal TimeSpan LastDelay { get; private set; }

    // Состояние: down — ComfyUI недоступен, DownSince — первый сбой
    internal bool Down { get; private set; }
    internal DateTimeOffset? DownSince { get; private set; }

    // Внутренний снимок: текущее состояние очереди
    internal ComfyQueueMonitorSnapshot? Snapshot { get; private set; }

    // Кэш ответа маршрута /ccs/stats узла (шаг 31): «загружено сейчас» + карты. TTL ~5 с при
    // активной панели/работе, ~30 с в простое; при недоступном ComfyUI не опрашиваем (кэш держим).
    // CcsSupported — видели ли хоть один валидный ответ (version 1): true — маршрут установлен,
    // false — не сообщает (404/сбой) → ComfyStats кладёт пометку в unsupported, gpus null.
    internal ComfyCcsStats? Ccs { get; private set; }
    internal bool CcsSupported { get; private set; }
    internal TimeSpan CcsTtl { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CcsIdleTtl { get; init; } = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastCcsPoll;

    // Сколько тиков подряд история не публиковалась: после потолка — Complete(failed), иначе
    // запись зависнет в ledger._active навсегда (ledger.Active() возвращает только незавершённые,
    // поэтому дубли финализации невозможны — _finalized не нужен)
    private const int MaxNullHistoryTicks = 20;
    private readonly Dictionary<string, int> _nullTicks = new(StringComparer.Ordinal);

    // Панель запросила снимок: следующий опрос в 2с, а не 15; и будит монитор, если он
    // досиживает текущую паузу (Touch после простоя не должен ждать 15с, иначе снимок устаревает)
    public void Touch()
    {
        lock (_touchLock)
        {
            _lastTouch = _time.GetUtcNow();
            _wakeCts?.Cancel();
        }
    }

    private bool RecentlyTouched()
    {
        lock (_touchLock)
            return _lastTouch is { } t && _time.GetUtcNow() - t <= TouchWindow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);
            if (!await DelayAsync(LastDelay, stoppingToken)) return;
        }
    }

    // Один тик: вычислить период, опросить /queue + /system_stats, обновить снимок, финализировать
    // ушедшие. Тесты зовут его напрямую (без Task.Delay), проверяя LastDelay и снимок
    internal async Task TickAsync(CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled)
        {
            LastDelay = IdlePoll;
            return;
        }

        var active = ledger.HasActive() || RecentlyTouched();
        LastDelay = active ? ActivePoll : IdlePoll;

        try
        {
            var queue = await comfy.GetQueueAsync(ct);
            var stats = await comfy.GetSystemStatsAsync(ct);
            SetDown(false);
            UpdateSnapshot(queue, stats, await PollCcsAsync(active, ct));
        }
        catch (ComfyException ex)
        {
            log.LogDebug("Монитор очереди ComfyUI: {Error}", ex.Message);
            SetDown(true);
            // ComfyUI недоступен — ccs не опрашиваем, кэш (Ccs) держим: данные устарели, но
            // «не сообщает» это не значит «маршрут исчез»
            Snapshot = new ComfyQueueMonitorSnapshot(true, DownSince, null, [], [], [], Ccs);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Страховка в корне (C1): любое исключение опроса (не только ComfyException —
            // например, мусорный тип в чужом extra_data) не роняет цикл монитора, иначе
            // умолчание .NET BackgroundServiceExceptionBehavior.StopHost остановило бы весь сервер.
            // Лог — с троттлингом (не на каждый тик).
            WarnPollThrottled(ex);
        }

        await FinalizeLeftAsync(ct);
    }

    // Опрос /ccs/stats с кэшем (TTL): активная панель/работа — ~5 с, простое — ~30 с. null —
    // не опрашивали (кэш ещё пуст) или TTL не истёк (возвращаем кэш). Валидный ответ (version 1)
    // ставит CcsSupported=true — маршрут установлен, даже если данных в нём нет.
    private async Task<ComfyCcsStats?> PollCcsAsync(bool active, CancellationToken ct)
    {
        var ttl = active ? CcsTtl : CcsIdleTtl;
        var now = _time.GetUtcNow();
        if (_lastCcsPoll is { } last && now - last < ttl)
            return Ccs;   // TTL не истёк — кэш
        _lastCcsPoll = now;
        ComfyCcsStats? result;
        try
        {
            result = await comfy.GetCcsStatsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Страховка в корне: любое исключение опроса узла (не только ComfyException) не
            // роняет цикл монитора. Лог — с троттлингом (не на каждый тик); кэш (Ccs) держим.
            WarnCcsThrottled(ex);
            return Ccs;
        }
        Ccs = result;
        if (result is { HasData: true }) CcsSupported = true;
        return result;
    }

    // Предупреждение о сбое опроса узла — не чаще раза в 30 с (не на каждый тик)
    private void WarnCcsThrottled(Exception ex)
    {
        var now = _time.GetUtcNow();
        if (_lastCcsWarn is { } last && now - last < CcsWarnThrottle) return;
        _lastCcsWarn = now;
        log.LogWarning(ex, "Монитор очереди ComfyUI: сбой опроса /ccs/stats — {Error}", ex.Message);
    }

    // Период троттлинга предупреждений о сбое опроса узла
    internal TimeSpan CcsWarnThrottle { get; init; } = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastCcsWarn;

    // Предупреждение о сбое опроса очереди/статистики (не только ComfyException) — не чаще
    // раза в 30 с (не на каждый тик); цикл продолжает работу
    private void WarnPollThrottled(Exception ex)
    {
        var now = _time.GetUtcNow();
        if (_lastPollWarn is { } last && now - last < PollWarnThrottle) return;
        _lastPollWarn = now;
        log.LogWarning(ex, "Монитор очереди ComfyUI: сбой опроса — {Error}", ex.Message);
    }

    // Период троттлинга предупреждений о сбое опроса очереди/статистики
    internal TimeSpan PollWarnThrottle { get; init; } = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastPollWarn;

    // ComfyUI недоступен → down с DownSince первого сбоя; доступен → up
    private void SetDown(bool down)
    {
        if (down && !Down) DownSince = _time.GetUtcNow();
        Down = down;
        if (!down) DownSince = null;
    }

    // Обновить снимок: running/pending из очереди, version из system_stats, чужие — классификация,
    // ccs — кэш «загружено сейчас» + карты узла (шаг 31)
    private void UpdateSnapshot(ComfyQueueState queue, ComfySystemStats? stats, ComfyCcsStats? ccs)
    {
        var foreign = new List<ComfyForeignRun>();
        foreach (var item in queue.Running.Concat(queue.Pending))
            if (!ledger.Contains(item.PromptId))
                foreign.Add(new ComfyForeignRun(item.PromptId, ClassifyForeign(item)));

        Snapshot = new ComfyQueueMonitorSnapshot(
            Down: false,
            DownSince: null,
            Version: stats?.Version,
            Running: queue.Running,
            Pending: queue.Pending,
            Foreign: foreign,
            Ccs: ccs);
    }

    // Классификация чужого прогона: client_id с префиксом ccs-local-media- → instance,
    // узел TranscriptionGate в графе → transcription, иначе → external.
    // Инстанс AI Home с TranscriptionGate в графе остаётся Instance (первая проверка раньше)
    internal static ComfyForeignKind ClassifyForeign(ComfyQueueItem item)
    {
        if (item.ClientId is { } id && id.StartsWith(ComfyClient.ClientPrefix, StringComparison.Ordinal))
            return ComfyForeignKind.Instance;
        if (item.ClassTypes.Contains(TranscriptionGateNode, StringComparer.Ordinal))
            return ComfyForeignKind.Transcription;
        return ComfyForeignKind.External;
    }

    // Узел распознавания: его наличие в class_type графа маркирует прогон как transcription
    internal const string TranscriptionGateNode = "TranscriptionGate";

    // Финализация: prompt_id из реестра, который ушёл из очереди → GET /history/{id} → ledger.Complete.
    // ledger.Active() возвращает только незавершённые, поэтому дубли финализации невозможны.
    // История ещё не опубликована (гонка) — считаем тики; после MaxNullHistoryTicks → Complete(failed)
    private async Task FinalizeLeftAsync(CancellationToken ct)
    {
        var queue = Snapshot;
        if (queue is null || Down) return; // ComfyUI недоступен — истории не получить, попробуем в следующий тик
        var inQueue = new HashSet<string>(
            queue.Running.Concat(queue.Pending).Select(i => i.PromptId), StringComparer.Ordinal);

        foreach (var record in ledger.Active())
        {
            var id = record.PromptId;
            if (inQueue.Contains(id)) continue;

            try
            {
                var history = await comfy.GetHistoryAsync(id, ct);
                if (history is null)
                {
                    var ticks = _nullTicks.TryGetValue(id, out var prev) ? prev + 1 : 1;
                    if (ticks >= MaxNullHistoryTicks)
                    {
                        // prompt_id пропал из истории ComfyUI (рестарт стенда или бэкенда): не сбой
                        // графа, а прерванный прогон — исход interrupted, не failed
                        ledger.Complete(id, "interrupted", null, error: null);
                        _nullTicks.Remove(id);
                    }
                    else
                    {
                        _nullTicks[id] = ticks;
                    }
                    continue;
                }
                _nullTicks.Remove(id);
                int? runSeconds = history.RunSeconds is { } s ? (int)s : null;
                // Ошибка графа (history.Error) — в журнал; completed — null
                ledger.Complete(id, history.Failed ? "failed" : (history.Completed ? "completed" : "failed"), runSeconds,
                    error: history.Error);
            }
            catch (ComfyException)
            {
                // ComfyUI недоступен: не финализируем, попробуем в следующий тик
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Страховка (C1): любое исключение чтения истории (не только ComfyException)
                // не роняет цикл монитора; запись останется активной и дофинализируется в следующий тик
                WarnPollThrottled(ex);
            }
        }
    }

    // Пауза между тиками: Task.Delay(delay) с объединением stoppingToken и wake-токена.
    // Touch() будит монитор, не досиживая текущую паузу (иначе снимок простоял бы до 15с);
    // stop (stoppingToken) — выход из цикла.
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        CancellationTokenSource wake;
        lock (_touchLock)
        {
            wake = _wakeCts = new CancellationTokenSource();
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
        try
        {
            await Task.Delay(delay, linked.Token);
            return true;   // проспали полностью — цикл продолжается
        }
        catch (OperationCanceledException)
        {
            // ct (stop) — выход из цикла; wake (Touch) — цикл продолжается
            return !ct.IsCancellationRequested;
        }
    }
}
