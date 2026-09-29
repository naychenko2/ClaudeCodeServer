using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>Событие CDP; <see cref="SessionId"/> задан у событий вкладки (режим flatten).</summary>
public sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>
/// Соединение CDP поверх трубы: рамка — JSON плюс нулевой байт, ответы сопоставляются с
/// командами по <c>id</c>, события раздаются подписчикам по паре «метод + сессия».
/// Обрыв трубы или выход процесса браузера завершает ВСЕ ожидающие команды и ожидания событий
/// исключением <see cref="CdpDisconnectedException"/>, а не зависанием; у каждой команды свой
/// таймаут.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);

    // Полное AX-дерево тяжёлой страницы — единицы МБ; рамка без конца дальше этого потолка
    // значит сломанный поток, а не большую страницу
    const int MaxFrameBytes = 64 * 1024 * 1024;

    static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    readonly ICdpTransport _transport;
    readonly TimeSpan _commandTimeout;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, PendingCommand> _pending = new();
    readonly List<Subscription> _subscriptions = [];
    readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _stop = new();
    int _nextId;

    sealed record PendingCommand(string Method, TaskCompletionSource<JsonElement> Result);

    public CdpConnection(ICdpTransport transport, TimeSpan? commandTimeout = null)
    {
        _transport = transport;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _ = Task.Run(ReadLoopAsync);
        _ = transport.Exited.ContinueWith(_ => Close("the browser process exited"),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Завершается причиной обрыва.</summary>
    public Task<string> Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>
    /// Отправить команду и дождаться ответа. Таймаут покрывает и запись, и ответ: зависшая
    /// труба не вешает ход. Ответ-ошибка — <see cref="CdpProtocolException"/>.
    /// </summary>
    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null,
        string? sessionId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref _nextId);
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCommand(method, result);
        var limit = timeout ?? _commandTimeout;
        try
        {
            // Close мог пройти между проверкой выше и регистрацией: тогда он нас не увидел
            ThrowIfClosed();
            return await ExchangeAsync(BuildFrame(id, method, parameters, sessionId), result, cancellationToken)
                .WaitAsync(limit, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(method, limit);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task<JsonElement> ExchangeAsync(byte[] frame, TaskCompletionSource<JsonElement> result,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _transport.Write.WriteAsync(frame, cancellationToken);
            await _transport.Write.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close($"write failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
        return await result.Task;
    }

    /// <summary>
    /// Подписка на события метода. <paramref name="sessionId"/> сравнивается точно: null —
    /// события уровня браузера. Обработчик зовётся на потоке чтения и не должен блокировать.
    /// </summary>
    public IDisposable Subscribe(string method, string? sessionId, Action<CdpEvent> handler) =>
        AddSubscription(new Subscription(this, method, sessionId, handler, null));

    /// <summary>
    /// Дождаться события. Подписка ставится синхронно при вызове — зови ДО команды, которая
    /// событие вызывает, и только потом жди результат.
    /// </summary>
    public async Task<JsonElement> WaitForEventAsync(string method, string? sessionId, TimeSpan timeout,
        Func<JsonElement, bool>? predicate = null, CancellationToken cancellationToken = default)
    {
        var hit = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = AddSubscription(new Subscription(this, method, sessionId,
            e =>
            {
                if (predicate is null || predicate(e.Params)) hit.TrySetResult(e.Params);
            },
            reason => hit.TrySetException(new CdpDisconnectedException(reason))));
        try
        {
            return await hit.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(method, timeout);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Чтение из анонимной трубы отменой не прерывается: его разбудит владелец транспорта,
        // закрыв потоки, поэтому цикл чтения здесь не ждём
        _stop.Cancel();
        Close("the connection was closed");
        return ValueTask.CompletedTask;
    }

    async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var frame = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var read = await _transport.Read.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    Close("the browser closed the pipe");
                    return;
                }
                var chunk = buffer.AsSpan(0, read);
                int end;
                while ((end = chunk.IndexOf((byte)0)) >= 0)
                {
                    frame.Write(chunk[..end]);
                    Dispatch(frame.WrittenMemory);
                    frame.ResetWrittenCount();
                    chunk = chunk[(end + 1)..];
                }
                frame.Write(chunk);
                if (frame.WrittenCount > MaxFrameBytes)
                {
                    Close($"a message from the browser exceeds {MaxFrameBytes / (1024 * 1024)} MB");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Close("the connection was closed");
        }
        catch (Exception ex)
        {
            // Битый JSON тоже сюда: поток после него рассинхронизирован, продолжать нельзя
            Close($"read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    void Dispatch(ReadOnlyMemory<byte> message)
    {
        if (message.IsEmpty) return;
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            // Нет в ожидающих — ответ опоздал после таймаута, его уже никто не ждёт
            if (!_pending.TryRemove(id, out var command)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : error.GetRawText();
                command.Result.TrySetException(new CdpProtocolException(command.Method, code, text));
            }
            else
            {
                command.Result.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : EmptyObject);
            }
            return;
        }
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return;
        var sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : EmptyObject;
        Publish(new CdpEvent(methodElement.GetString()!, sessionId, parameters));
    }

    void Publish(CdpEvent e)
    {
        Subscription[] targets;
        lock (_subscriptions)
            targets = _subscriptions.Where(s => s.Method == e.Method && s.SessionId == e.SessionId).ToArray();
        foreach (var target in targets)
        {
            try
            {
                target.Handler(e);
            }
            catch
            {
                // Упавший подписчик не должен ронять цикл чтения и остальных подписчиков
            }
        }
    }

    Subscription AddSubscription(Subscription subscription)
    {
        lock (_subscriptions)
        {
            if (!IsClosed)
            {
                _subscriptions.Add(subscription);
                return subscription;
            }
        }
        subscription.OnClosed?.Invoke(_closed.Task.Result);
        return subscription;
    }

    void RemoveSubscription(Subscription subscription)
    {
        lock (_subscriptions) _subscriptions.Remove(subscription);
    }

    void Close(string reason)
    {
        if (!_closed.TrySetResult(reason)) return;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var command))
                command.Result.TrySetException(new CdpDisconnectedException(reason));
        Subscription[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
            subscription.OnClosed?.Invoke(reason);
    }

    void ThrowIfClosed()
    {
        if (IsClosed) throw new CdpDisconnectedException(_closed.Task.Result);
    }

    static byte[] BuildFrame(int id, string method, JsonObject? parameters, string? sessionId)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (sessionId is not null) writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("params");
            if (parameters is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                parameters.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        output.Write([(byte)0]);
        return output.WrittenSpan.ToArray();
    }

    sealed class Subscription(CdpConnection owner, string method, string? sessionId,
        Action<CdpEvent> handler, Action<string>? onClosed) : IDisposable
    {
        public string Method { get; } = method;
        public string? SessionId { get; } = sessionId;
        public Action<CdpEvent> Handler { get; } = handler;
        public Action<string>? OnClosed { get; } = onClosed;

        public void Dispose() => owner.RemoveSubscription(this);
    }
}
