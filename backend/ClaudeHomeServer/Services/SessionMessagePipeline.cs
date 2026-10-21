using System.Threading.Channels;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Элемент очереди сессии. Work — рассылка (SignalR), Msg+Info — сообщение для наблюдателей
// OnSessionMessage; Barrier — маркер «дождаться всего, что легло раньше» (DrainAsync).
// Droppable — кадр-дельта, который при переполнении можно не класть: он уже лёг снимком в
// TurnAccumulator и восстановится из history.json.
internal readonly record struct PipelineItem(
    Func<Task>? Work,
    ServerMessage? Msg,
    Session? Info,
    bool Droppable,
    long EnqueuedAt,
    TaskCompletionSource? Barrier = null);

// Ограниченная очередь одной сессии между чтением stdout CLI и рассылкой/наблюдателями.
// Раньше read-loop адаптера ждал всю цепочку (SignalR Group.SendAsync медленного клиента,
// затем каждый наблюдатель по очереди), пока CLI стоял на записи в полный пайп — MCP-вызовы
// агента ждали десятки секунд (замер 21.10). Теперь обработчик сообщения кладёт кадр сюда и
// сразу возвращается; ОДИН потребитель на сессию разбирает очередь строго по порядку — порядок
// событий в ленте и у наблюдателей сохраняется.
//
// Переполнение: при полной очереди не кладутся (сливаются) только кадры-дельты — TextDelta,
// ThinkingDelta и ToolInputDelta (см. IsDroppable). Всё остальное — ToolUse, ToolStarted,
// ToolResult, Result, Error, Exited, PermissionRequest, AskQuestion, PlanReview, Hands*,
// SessionStarted, статусы и пр. — НИКОГДА не теряется: писатель ждёт место в очереди
// (back-pressure допустим, эти кадры редкие).
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

    private readonly Channel<PipelineItem> _channel;
    private readonly Func<PipelineItem, Task> _process;
    private readonly Task _consumer;
    private int _dropped;

    public SessionMessagePipeline(Func<PipelineItem, Task> process, int capacity = DefaultCapacity)
    {
        _process = process;
        _channel = Channel.CreateBounded<PipelineItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    // Сколько дельт слито за время жизни очереди (диагностика и тесты)
    public int Dropped => Volatile.Read(ref _dropped);

    // Кадры, которые можно слить при переполнении: чистый поток текста/мыслей/частичного JSON
    // инструмента. Полное содержимое уже в аккумуляторе (дельты) либо придёт целиком в
    // ToolUseMessage (ToolInputDelta), поэтому потеря кадра — разрыв живой ленты, а не данных.
    internal static bool IsDroppable(ServerMessage msg) =>
        msg is TextDeltaMessage or ThinkingDeltaMessage or ToolInputDeltaMessage;

    public ValueTask EnqueueAsync(PipelineItem item)
    {
        if (item.Droppable)
        {
            if (!_channel.Writer.TryWrite(item)) Interlocked.Increment(ref _dropped);
            return ValueTask.CompletedTask;
        }
        return WriteCriticalAsync(item);
    }

    private async ValueTask WriteCriticalAsync(PipelineItem item)
    {
        try { await _channel.Writer.WriteAsync(item); }
        // Очередь закрыта удалением сессии — кадру умирающего процесса некому служить
        catch (ChannelClosedException) { item.Barrier?.TrySetResult(); }
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
        _channel.Writer.TryComplete();
        try { await _consumer.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[SessionMessagePipeline] Дренаж очереди не уложился в потолок, продолжаем без ожидания");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
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
