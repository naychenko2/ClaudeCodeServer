using System.Net.WebSockets;
using System.Threading.Channels;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Поток одного исполнения поверх WebSocket устройства (ADR-016). Соединение сменяемо:
/// обрыв поток не закрывает, реконнект устройства с тем же execId подхватывает его.
///
/// Досылка: исходящие кадры данных нумеруются с 1 и лежат в буфере, пока устройство их не
/// подтвердит (подтверждение кумулятивное); при новом подключении всё неподтверждённое
/// уходит заново по порядку. Входящие кадры с номером не больше последнего принятого —
/// дубли досылки: не отдаются потребителю, но подтверждаются снова. Пропуск номера —
/// сбой протокола, соединение закрывается.
/// </summary>
internal sealed class DeviceExecStream : IDeviceExecStream
{
    private readonly Action<DeviceExecStream> _onDisposed;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly LinkedList<(ulong Sequence, byte[] Frame)> _unacked = new();
    private readonly Channel<DeviceExecFrame> _incoming = Channel.CreateUnbounded<DeviceExecFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();

    private WebSocket? _socket;
    private ulong _lastSent;
    private ulong _lastReceived;
    private long _unackedBytes;
    private TaskCompletionSource _ackSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeviceExecStream(string execId, string ownerId, string deviceId, Action<DeviceExecStream> onDisposed)
    {
        ExecId = execId;
        OwnerId = ownerId;
        DeviceId = deviceId;
        _onDisposed = onDisposed;
    }

    public string ExecId { get; }
    public string OwnerId { get; }
    public string DeviceId { get; }

    /// <summary>Первое подключение устройства к потоку.</summary>
    public Task Attached => _attached.Task;

    internal bool IsConnected
    {
        get { lock (_lock) return _socket is not null; }
    }

    internal int UnackedCount
    {
        get { lock (_lock) return _unacked.Count; }
    }

    public async ValueTask SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (channel == DeviceExecFrameChannel.Ack)
            throw new ArgumentException("Подтверждения ставит сам поток", nameof(channel));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await WaitForWindowAsync(DeviceExecProtocol.HeaderBytes + payload.Length, linked.Token);

        await _sendLock.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_closed.IsCancellationRequested, this);

            var sequence = ++_lastSent;
            var frame = DeviceExecFrames.Encode(channel, sequence, payload.Span);
            WebSocket? socket;
            lock (_lock)
            {
                _unacked.AddLast((sequence, frame));
                _unackedBytes += frame.Length;
                socket = _socket;
            }

            // Нет соединения — кадр ждёт в буфере досылки до реконнекта
            if (socket is not null) await TrySendAsync(socket, frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public IAsyncEnumerable<DeviceExecFrame> ReadAllAsync(CancellationToken ct = default) =>
        _incoming.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Обслуживает подключение устройства до его закрытия: подменяет прежнее соединение,
    /// досылает неподтверждённое и читает входящие кадры.
    /// </summary>
    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        WebSocket? previous;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_closed.IsCancellationRequested)
            {
                await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "stream closed");
                return;
            }

            byte[][] backlog;
            ulong received;
            lock (_lock)
            {
                previous = _socket;
                _socket = socket;
                backlog = _unacked.Select(u => u.Frame).ToArray();
                received = _lastReceived;
            }

            // Сначала — докуда дошло от устройства: ему незачем досылать уже принятое
            if (received > 0 && !await TrySendAsync(socket, DeviceExecFrames.Ack(received))) return;
            foreach (var frame in backlog)
                if (!await TrySendAsync(socket, frame)) return;
        }
        finally
        {
            _sendLock.Release();
        }

        previous?.Abort();
        _attached.TrySetResult();

        try
        {
            await ReadLoopAsync(socket, ct);
        }
        finally
        {
            Detach(socket);
        }
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

        _incoming.Writer.TryComplete();
        _attached.TrySetCanceled();
        if (socket is not null) await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "exec finished");
        _onDisposed(this);
    }

    private async Task ReadLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[DeviceExecProtocol.HeaderBytes + DeviceExecProtocol.MaxPayloadBytes];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);

        while (socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            try
            {
                do
                {
                    if (length == buffer.Length)
                    {
                        await CloseLockedAsync(socket, WebSocketCloseStatus.MessageTooBig, "frame too big");
                        return;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(length), linked.Token);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Binary
                || !DeviceExecFrames.TryDecode(buffer.AsMemory(0, length), out var frame))
            {
                await CloseLockedAsync(socket, WebSocketCloseStatus.ProtocolError, "bad frame");
                return;
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
                // Дубль досылки: потребитель его уже получил, устройству — повтор подтверждения
                await SendAckAsync(socket, expected - 1);
                continue;
            }

            if (frame.Sequence > expected)
            {
                await CloseLockedAsync(socket, WebSocketCloseStatus.ProtocolError, "sequence gap");
                return;
            }

            lock (_lock) _lastReceived = frame.Sequence;
            _incoming.Writer.TryWrite(frame with { Payload = frame.Payload.ToArray() });
            await SendAckAsync(socket, frame.Sequence);
        }
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

    // Потолок досылки: отправитель ждёт подтверждений, а не растит буфер без предела.
    // Кадр больше пустого окна не ждёт никогда — иначе встал бы навсегда.
    private async Task WaitForWindowAsync(int frameBytes, CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_unacked.Count == 0 || _unackedBytes + frameBytes <= DeviceExecProtocol.MaxUnackedBytes) return;
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

    // У WebSocket одна отправка за раз, а закрытие — тоже отправка
    private async Task CloseLockedAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        await _sendLock.WaitAsync();
        try
        {
            await CloseQuietlyAsync(socket, status, description);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Ошибка записи — это обрыв: соединение отцепляется, кадр остаётся в буфере досылки
    private async Task<bool> TrySendAsync(WebSocket socket, byte[] frame)
    {
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _closed.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            Detach(socket);
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

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }
}
