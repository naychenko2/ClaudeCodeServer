using System.Net.WebSockets;
using System.Threading.Channels;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>Подключение к /api/devices/exec. Отдельно — чтобы тесты гоняли канал без сервера.</summary>
internal interface IExecSocketConnector
{
    Task<WebSocket> ConnectAsync(string execId, CancellationToken ct);
}

/// <summary>
/// Сервер отказал в подключении окончательно (404 — исполнения у него больше нет, 403 —
/// чужое): переподключаться бессмысленно.
/// </summary>
internal sealed class ExecLinkRefusedException(string message) : Exception(message);

/// <summary>
/// Сторона устройства канала исполнения (ADR-016). Зеркало серверного DeviceExecStream:
/// исходящие кадры данных нумеруются с 1 и лежат в буфере, пока сервер их не подтвердит;
/// обрыв соединения исполнение не останавливает — связь поднимается заново с тем же execId
/// и всё неподтверждённое уходит повторно по порядку. Входящие кадры с номером не больше
/// последнего принятого — дубли досылки: дальше не отдаются (stdin не исполняется
/// дважды), но подтверждаются снова.
///
/// Буфер ограничен <see cref="DeviceExecProtocol.MaxUnackedBytes"/>: дальше отправитель
/// ждёт подтверждений, то есть вывод CLI упирается в трубу, а не растит память агента.
/// </summary>
internal sealed class ExecLink : IAsyncDisposable
{
    private readonly IExecSocketConnector _connector;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxOutage;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly LinkedList<(ulong Sequence, byte[] Frame)> _unacked = new();
    private readonly Channel<DeviceExecFrame> _incoming = Channel.CreateUnbounded<DeviceExecFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebSocket? _socket;
    private ulong _lastSent;
    private ulong _lastReceived;
    private long _unackedBytes;
    private TaskCompletionSource _ackSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;

    public ExecLink(string execId, IExecSocketConnector connector, TimeSpan maxOutage,
        ILogger? log = null, TimeProvider? time = null)
    {
        ExecId = execId;
        _connector = connector;
        _maxOutage = maxOutage;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string ExecId { get; }

    /// <summary>Связь кончилась насовсем: сервер закрыл исполнение или не вернулся за потолок простоя.</summary>
    public Task Finished => _finished.Task;

    /// <summary>Почему связь кончилась (null — штатное закрытие сервером).</summary>
    public string? FailureReason { get; private set; }

    internal int Reconnects { get; private set; }

    internal int UnackedCount
    {
        get { lock (_lock) return _unacked.Count; }
    }

    /// <summary>Первое подключение — синхронно с вызывающим: не открылось — исполнения нет.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var socket = await _connector.ConnectAsync(ExecId, ct);
        await AttachAsync(socket);
        _run = Task.Run(RunAsync);
    }

    public async ValueTask SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (channel == DeviceExecFrameChannel.Ack)
            throw new ArgumentException("Подтверждения ставит сама связь", nameof(channel));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await WaitForWindowAsync(DeviceExecProtocol.HeaderBytes + payload.Length, linked.Token);

        await _sendLock.WaitAsync(linked.Token);
        try
        {
            var sequence = ++_lastSent;
            var frame = DeviceExecFrames.Encode(channel, sequence, payload.Span);
            WebSocket? socket;
            lock (_lock)
            {
                _unacked.AddLast((sequence, frame));
                _unackedBytes += frame.Length;
                socket = _socket;
            }

            // Связи нет — кадр ждёт в буфере досылки до реконнекта
            if (socket is not null) await TrySendAsync(socket, frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Крупные данные — частями по потолку кадра.</summary>
    public async ValueTask SendChunkedAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        do
        {
            var part = payload[..Math.Min(payload.Length, DeviceExecProtocol.MaxPayloadBytes)];
            await SendAsync(channel, part, ct);
            payload = payload[part.Length..];
        } while (payload.Length > 0);
    }

    /// <summary>Входящие кадры данных по порядку, без дублей.</summary>
    public IAsyncEnumerable<DeviceExecFrame> ReadAllAsync(CancellationToken ct = default) =>
        _incoming.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Ждёт, пока сервер подтвердит всё отправленное (Exit в том числе), — иначе закрытие
    /// связи потеряло бы хвост вывода. false — не дождались.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout, _time);
        try
        {
            await WaitForWindowAsync(int.MaxValue, cts.Token, drain: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Рвёт текущее соединение (для тестов обрыва): связь поднимется сама.</summary>
    internal void AbortConnection()
    {
        WebSocket? socket;
        lock (_lock) socket = _socket;
        socket?.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();

        WebSocket? socket;
        lock (_lock)
        {
            socket = _socket;
            _socket = null;
            _ackSignal.TrySetResult();
        }

        if (socket is not null) await CloseQuietlyAsync(socket);
        if (_run is not null)
        {
            try { await _run; }
            catch (Exception) { /* цикл связи уже отчитался через Finished */ }
        }
        Finish(FailureReason);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                WebSocket? socket;
                lock (_lock) socket = _socket;

                // Соединение могло отвалиться ещё на досылке — сразу к переподключению
                if (socket is not null)
                {
                    var closedByServer = await ReadLoopAsync(socket);
                    Detach(socket);
                    if (closedByServer)
                    {
                        Finish(null);
                        return;
                    }
                }
                if (_closed.IsCancellationRequested) return;

                if (!await ReconnectAsync()) return;
            }
        }
        catch (Exception e) when (!_closed.IsCancellationRequested)
        {
            _log.LogWarning(e, "Связь исполнения {ExecId} упала", ExecId);
            Finish($"связь исполнения упала: {e.Message}");
        }
    }

    // Переподключение с растущей паузой до потолка простоя. Окончательный отказ сервера
    // (исполнения у него больше нет) — сразу конец.
    private async Task<bool> ReconnectAsync()
    {
        var started = _time.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(200);
        while (!_closed.IsCancellationRequested)
        {
            if (_time.GetElapsedTime(started) > _maxOutage)
            {
                Finish($"связь с сервером не восстановилась за {(int)_maxOutage.TotalSeconds} с");
                return false;
            }

            try
            {
                await Task.Delay(delay, _time, _closed.Token);
                var socket = await _connector.ConnectAsync(ExecId, _closed.Token);
                Reconnects++;
                await AttachAsync(socket);
                _log.LogInformation("Связь исполнения {ExecId} восстановлена", ExecId);
                return true;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
                return false;
            }
            catch (ExecLinkRefusedException e)
            {
                Finish(e.Message);
                return false;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or TimeoutException)
            {
                _log.LogDebug(e, "Переподключение исполнения {ExecId} не удалось", ExecId);
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }
        return false;
    }

    // Новое соединение: сначала «докуда дошло» от сервера, затем весь неподтверждённый хвост
    private async Task AttachAsync(WebSocket socket)
    {
        await _sendLock.WaitAsync(_closed.Token);
        try
        {
            byte[][] backlog;
            ulong received;
            lock (_lock)
            {
                _socket = socket;
                backlog = _unacked.Select(u => u.Frame).ToArray();
                received = _lastReceived;
            }

            if (received > 0 && !await TrySendAsync(socket, DeviceExecFrames.Ack(received))) return;
            foreach (var frame in backlog)
                if (!await TrySendAsync(socket, frame)) return;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // true — сервер закрыл исполнение штатно; false — обрыв
    private async Task<bool> ReadLoopAsync(WebSocket socket)
    {
        var buffer = new byte[DeviceExecProtocol.HeaderBytes + DeviceExecProtocol.MaxPayloadBytes];
        while (socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            try
            {
                do
                {
                    if (length == buffer.Length) return false;
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), _closed.Token);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                return false;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return socket.CloseStatus == WebSocketCloseStatus.NormalClosure;

            if (result.MessageType != WebSocketMessageType.Binary
                || !DeviceExecFrames.TryDecode(buffer.AsMemory(0, length), out var frame))
            {
                _log.LogWarning("Исполнение {ExecId}: битый кадр от сервера", ExecId);
                socket.Abort();
                return false;
            }

            if (frame.Channel == DeviceExecFrameChannel.Ack)
            {
                Acknowledge(frame.Sequence);
                continue;
            }

            ulong expected;
            lock (_lock) expected = _lastReceived + 1;

            if (frame.Sequence < expected)
            {
                // Дубль досылки: команда уже исполнена, серверу — повтор подтверждения
                await SendAckAsync(socket, expected - 1);
                continue;
            }

            if (frame.Sequence > expected)
            {
                _log.LogWarning("Исполнение {ExecId}: пропуск номера кадра {Got} вместо {Expected}", ExecId, frame.Sequence, expected);
                socket.Abort();
                return false;
            }

            lock (_lock) _lastReceived = frame.Sequence;
            _incoming.Writer.TryWrite(frame with { Payload = frame.Payload.ToArray() });
            await SendAckAsync(socket, frame.Sequence);
        }

        return socket.CloseStatus == WebSocketCloseStatus.NormalClosure;
    }

    private void Acknowledge(ulong upTo)
    {
        lock (_lock)
        {
            while (_unacked.First is { } first && first.Value.Sequence <= upTo)
            {
                _unackedBytes -= first.Value.Frame.Length;
                _unacked.RemoveFirst();
            }

            _ackSignal.TrySetResult();
            _ackSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // Окно досылки. drain — ждать полного опустошения буфера (конец исполнения).
    // Кадр больше пустого окна не ждёт никогда — иначе встал бы навсегда.
    private async Task WaitForWindowAsync(long frameBytes, CancellationToken ct, bool drain = false)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_unacked.Count == 0) return;
                // Связь кончилась насовсем — подтверждений больше не будет
                if (_finished.Task.IsCompleted) throw new OperationCanceledException("связь исполнения закрыта");
                if (!drain && _unackedBytes + frameBytes <= DeviceExecProtocol.MaxUnackedBytes) return;
                if (_closed.IsCancellationRequested) throw new OperationCanceledException();
                signal = _ackSignal.Task;
            }

            await signal.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task SendAckAsync(WebSocket socket, ulong sequence)
    {
        await _sendLock.WaitAsync();
        try
        {
            await TrySendAsync(socket, DeviceExecFrames.Ack(sequence));
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Ошибка записи — обрыв: соединение отцепляется, кадр остаётся в буфере досылки
    private async Task<bool> TrySendAsync(WebSocket socket, byte[] frame)
    {
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _closed.Token);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            Detach(socket);
            socket.Abort();
            return false;
        }
    }

    private void Detach(WebSocket socket)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    private void Finish(string? reason)
    {
        if (_finished.Task.IsCompleted) return;
        FailureReason ??= reason;
        _incoming.Writer.TryComplete();
        lock (_lock) _ackSignal.TrySetResult();
        _finished.TrySetResult();
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "exec finished", timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }
}
