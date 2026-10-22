using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Core.Telemetry;

namespace ClaudeHomeServer.Services;

// Элемент очереди сессии. Work — рассылка (SignalR), Msg+Info — сообщение для наблюдателей
// OnSessionMessage; Barrier — маркер «дождаться всего, что легло раньше» (DrainAsync).
// Droppable — кадр-дельта, который при переполнении сливается с хвостом очереди (см. класс).
// WorkFor — как пересобрать Work для слитого кадра; null у кадра без рассылки (или без дельты).
internal readonly record struct PipelineItem(
    Func<Task>? Work,
    ServerMessage? Msg,
    Session? Info,
    bool Droppable,
    long EnqueuedAt,
    TaskCompletionSource? Barrier = null,
    Func<ServerMessage, Func<Task>>? WorkFor = null);

// Очередь одной сессии между чтением stdout CLI и рассылкой/наблюдателями.
// Раньше read-loop адаптера ждал всю цепочку (SignalR Group.SendAsync медленного клиента,
// затем каждый наблюдатель по очереди), пока CLI стоял на записи в полный пайп — MCP-вызовы
// агента ждали десятки секунд (замер 21.10). Теперь обработчик сообщения кладёт кадр сюда и
// сразу возвращается; ОДИН потребитель на сессию разбирает очередь строго по порядку — порядок
// событий в ленте и у наблюдателей сохраняется.
//
// Очередь неограниченная намеренно: наблюдатель на Result (TaskExecutionService) может из потока
// потребителя синхронно положить кадр в очередь ТОЙ ЖЕ сессии (ошибка старта хода,
// ReplyRejected, локальный ход). При ограниченной очереди с ожиданием места это тупик:
// запись ждёт потребителя, а потребитель ждёт запись — сессия встаёт навсегда.
//
// Память ограничена по числу кадров СЛИЯНИЕМ, а не потерей: при длине очереди >= ёмкости
// очередная дельта (TextDelta, ThinkingDelta, ToolInputDelta) сливается с ПОСЛЕДНИМ ещё не
// разобранным кадром, если тот — дельта того же вида. Текст дельт склеивается (фронт копит
// их в пузырь — потерянная дельта рвала бы живую ленту до перезагрузки), ToolInputDelta
// накопительный (ClaudeSession шлёт весь JSON инструмента на текущий момент), поэтому
// побеждает последний. Слияние идёт только с хвостом: через несливаемый кадр (ToolUse,
// ToolResult, Result, Error, Exited, статусы и пр.) оно не переходит — порядок не нарушается.
// Несливаемые кадры кладутся всегда; их рост по размеру не режется, а наблюдается: сумма текста
// выше порога — предупреждение в лог и метрика ccs.session.queue_bytes (ограничитель — отдельное
// решение после данных).
//
// Закрытие: на ExitedMessage очередь НЕ закрывается (сессия живёт между ходами); её закрывает
// CompleteAsync при удалении записи сессии — с дренажем до конца, чтобы ResultMessage дошёл
// до всех наблюдателей.
internal sealed class SessionMessagePipeline
{
    // Ёмкость очереди. Один ход со стримингом даёт сотни дельт в секунду, а медленный клиент
    // или тяжёлый наблюдатель отстаёт секундами; 2000 кадров покрывают такой отрыв, не раздувая
    // память на сессию (кадры — ссылки на уже созданные record'ы).
    internal const int DefaultCapacity = 2000;

    // Порог суммарного текста несливаемых кадров в очереди (символов UTF-16, ~16 МБ при 2 байтах).
    // Только сигнал наблюдаемости: кадры не выбрасываются
    internal const long DefaultQueueBytesLimit = 16L * 1024 * 1024;

    private sealed class Entry(PipelineItem item, long bytes)
    {
        public PipelineItem Item = item;
        public readonly long Bytes = bytes;
    }

    private readonly object _gate = new();
    private readonly Queue<Entry> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Func<PipelineItem, Task> _process;
    private readonly Task _consumer;
    private readonly int _capacity;
    private readonly long _queueBytesLimit;
    private readonly string _sessionId;
    private readonly Func<string> _sessionKind;
    private readonly ILogger? _log;
    // Последний положенный и ещё не взятый потребителем кадр — единственный кандидат на слияние
    private Entry? _tail;
    private bool _completed;
    private long _queuedBytes;
    private bool _overBytes;
    private bool _coalesceLogged;
    private int _coalesced;
    private int _bytesWarnings;

    public SessionMessagePipeline(Func<PipelineItem, Task> process, int capacity = DefaultCapacity,
        long queueBytesLimit = DefaultQueueBytesLimit, string sessionId = "",
        Func<string>? sessionKind = null, ILogger? log = null)
    {
        _process = process;
        _capacity = capacity;
        _queueBytesLimit = queueBytesLimit;
        _sessionId = sessionId;
        _sessionKind = sessionKind ?? (() => "human");
        _log = log;
        _consumer = Task.Run(ConsumeAsync);
    }

    // Сколько кадров-дельт слито с предыдущими за время жизни очереди (диагностика и тесты)
    public int Coalesced => Volatile.Read(ref _coalesced);

    // Сколько раз суммарный текст несливаемых кадров пересёк порог (диагностика и тесты)
    public int BytesWarnings => Volatile.Read(ref _bytesWarnings);

    // Кадры, которые можно слить при переполнении: чистый поток текста/мыслей/частичного JSON
    // инструмента.
    internal static bool IsDroppable(ServerMessage msg) =>
        msg is TextDeltaMessage or ThinkingDeltaMessage or ToolInputDeltaMessage;

    // Результат слияния двух соседних дельт; null — слить нельзя (разный вид или другой инструмент)
    internal static ServerMessage? TryMerge(ServerMessage tail, ServerMessage next) => (tail, next) switch
    {
        (TextDeltaMessage a, TextDeltaMessage b) => new TextDeltaMessage(a.Text + b.Text),
        (ThinkingDeltaMessage a, ThinkingDeltaMessage b) => new ThinkingDeltaMessage(a.Text + b.Text),
        (ToolInputDeltaMessage a, ToolInputDeltaMessage b) when a.ToolUseId == b.ToolUseId => b,
        _ => null,
    };

    // Размер текста, который несливаемый кадр держит в памяти очереди (символов)
    private static long FrameSize(ServerMessage? msg) => msg switch
    {
        ToolResultMessage m => m.Content?.Length ?? 0,
        AgentTextMessage m => m.Text?.Length ?? 0,
        AgentThinkingMessage m => m.Text?.Length ?? 0,
        _ => 0,
    };

    public ValueTask EnqueueAsync(PipelineItem item)
    {
        var bytes = item.Droppable ? 0 : FrameSize(item.Msg);
        long? bytesToReport = null;
        bool warn = false, logCoalesce = false;
        bool accepted = true;
        lock (_gate)
        {
            if (_completed)
                accepted = false;
            else if (item.Droppable && _queue.Count >= _capacity
                && _tail is { } tail && TryMergeInto(tail, item))
            {
                _coalesced++;
                if (!_coalesceLogged) { _coalesceLogged = true; logCoalesce = true; }
            }
            else
            {
                var entry = new Entry(item, bytes);
                _queue.Enqueue(entry);
                _tail = entry;
                if (item.Msg is ResultMessage) _coalesceLogged = false;
                if (bytes > 0)
                {
                    _queuedBytes += bytes;
                    bytesToReport = _queuedBytes;
                    if (_queuedBytes > _queueBytesLimit && !_overBytes)
                    {
                        _overBytes = true;
                        _bytesWarnings++;
                        warn = true;
                    }
                }
                _signal.Release();
            }
        }

        if (!accepted)
        {
            // Кадру умирающего процесса некому служить (очередь закрыта при удалении сессии)
            item.Barrier?.TrySetResult();
            return ValueTask.CompletedTask;
        }
        if (logCoalesce)
            _log?.LogInformation(
                "Очередь сессии {SessionId} переполнена: дельты сливаются в один кадр (текст не теряется)", _sessionId);
        if (bytesToReport is { } reported)
            ServerMetrics.RecordSessionQueueBytes(reported, _sessionKind());
        if (warn)
            _log?.LogWarning(
                "Очередь сессии {SessionId}: суммарный текст несливаемых кадров превысил {Limit} симв. — клиент или наблюдатель отстаёт",
                _sessionId, _queueBytesLimit);
        return ValueTask.CompletedTask;
    }

    // Сливает кадр в хвост очереди; вызывается под _gate
    private bool TryMergeInto(Entry tail, PipelineItem next)
    {
        var current = tail.Item;
        if (current.Msg is null || next.Msg is null) return false;
        // Рассылку кадров с WorkFor и без него не смешиваем: у кадра без рассылки (пустой показ)
        // слитый текст иначе ушёл бы клиенту или, наоборот, пропал
        if ((current.WorkFor is null) != (next.WorkFor is null)) return false;
        if (TryMerge(current.Msg, next.Msg) is not { } merged) return false;
        tail.Item = current with
        {
            Msg = merged,
            Work = current.WorkFor is { } rebuild ? rebuild(merged) : current.Work,
        };
        ServerMetrics.CoalescedFrames.Add(1, new KeyValuePair<string, object?>("session_kind", _sessionKind()));
        return true;
    }

    // Дожидается, пока будет разобрано всё, что легло в очередь до вызова
    public async Task DrainAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new PipelineItem(null, null, null, false, 0, barrier));
        await barrier.Task;
    }

    // Закрывает очередь и дожидается дренажа. Потолок ожидания защищает удаление чата от
    // зависшего наблюдателя; по истечении недоразобранное остаётся на совести потребителя.
    public async Task CompleteAsync(TimeSpan timeout)
    {
        lock (_gate) { _completed = true; }
        _signal.Release();
        try { await _consumer.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[SessionMessagePipeline] Дренаж очереди не уложился в потолок, продолжаем без ожидания");
        }
    }

    private async Task ConsumeAsync()
    {
        while (true)
        {
            await _signal.WaitAsync();
            PipelineItem item;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    if (_completed) return;
                    continue;
                }
                var entry = _queue.Dequeue();
                if (ReferenceEquals(_tail, entry)) _tail = null;
                _queuedBytes -= entry.Bytes;
                if (_queuedBytes <= _queueBytesLimit) _overBytes = false;
                item = entry.Item;
            }
            if (item.Barrier is { } barrier)
            {
                barrier.TrySetResult();
                continue;
            }
            try { await _process(item); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionMessagePipeline] Ошибка разбора кадра: {ex.Message}");
            }
        }
    }
}
