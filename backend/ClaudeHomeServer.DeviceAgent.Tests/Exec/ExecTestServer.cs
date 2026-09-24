using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>
/// Серверная сторона канала исполнения для тестов: та же дисциплина, что у DeviceExecStream
/// сервера (номера, кумулятивные подтверждения, досылка неподтверждённого при новом
/// подключении, отбрасывание дублей), но без ASP.NET — пара WebSocket поверх loopback TCP.
/// Рычаги: обрыв соединения, «потерянные» подтверждения, отказ в переподключении.
/// </summary>
internal sealed class ExecTestServer : IExecSocketConnector, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly LinkedList<(ulong Seq, byte[] Frame)> _unacked = new();
    private readonly Channel<DeviceExecFrame> _received = Channel.CreateUnbounded<DeviceExecFrame>();
    private readonly List<ulong> _allDataSequences = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;
    private ulong _lastSent;
    private ulong _lastReceived;
    private int _framesSinceConnect;

    public int Connections { get; private set; }

    /// <summary>Сервер «забыл» исполнение: переподключение — отказ (как 404).</summary>
    public bool RefuseReconnect { get; set; }

    /// <summary>Подтверждения агента не доходят: неподтверждённое уйдёт повторно.</summary>
    public bool IgnoreAcks { get; set; }

    /// <summary>Оборвать соединение после стольких принятых кадров данных (посреди стрима).</summary>
    public int? BreakAfterFrames { get; set; }

    /// <summary>Номера ВСЕХ пришедших кадров данных, включая дубли досылки.</summary>
    public IReadOnlyList<ulong> AllDataSequences { get { lock (_lock) return _allDataSequences.ToList(); } }

    public ChannelReader<DeviceExecFrame> Received => _received.Reader;

    public async Task<WebSocket> ConnectAsync(string execId, CancellationToken ct)
    {
        if (Connections > 0 && RefuseReconnect) throw new ExecLinkRefusedException("исполнения больше нет (404)");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(ct).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var serverSide = await accept;

        var serverSocket = WebSocket.CreateFromStream(serverSide.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan);
        var clientSocket = WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan);

        Connections++;
        await AttachAsync(serverSocket);
        _ = Task.Run(() => ReadLoopAsync(serverSocket));
        return clientSocket;
    }

    /// <summary>Отправить агенту кадр данных (stdin, control…).</summary>
    public async Task SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            var seq = ++_lastSent;
            var frame = DeviceExecFrames.Encode(channel, seq, payload.Span);
            WebSocket? socket;
            lock (_lock)
            {
                _unacked.AddLast((seq, frame));
                socket = _socket;
            }
            if (socket is not null) await TrySendAsync(socket, frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendControlAsync(ExecControl control) =>
        SendAsync(DeviceExecFrameChannel.Control, ExecJson.Serialize(control));

    public void Break()
    {
        WebSocket? socket;
        lock (_lock) socket = _socket;
        socket?.Abort();
    }

    /// <summary>Сервер закрывает исполнение штатно (как DeviceExecStream.DisposeAsync).</summary>
    public async Task CloseNormallyAsync()
    {
        WebSocket? socket;
        lock (_lock) socket = _socket;
        if (socket is null) return;
        await _sendLock.WaitAsync();
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "exec finished", CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Читает принятые кадры до Exit включительно.</summary>
    public async Task<List<DeviceExecFrame>> ReadUntilExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var frames = new List<DeviceExecFrame>();
        await foreach (var frame in _received.Reader.ReadAllAsync(cts.Token))
        {
            frames.Add(frame);
            if (frame.Channel == DeviceExecFrameChannel.Exit) break;
        }
        return frames;
    }

    private async Task AttachAsync(WebSocket socket)
    {
        await _sendLock.WaitAsync();
        try
        {
            byte[][] backlog;
            ulong received;
            lock (_lock)
            {
                _socket = socket;
                _framesSinceConnect = 0;
                backlog = _unacked.Select(u => u.Frame).ToArray();
                received = _lastReceived;
            }
            if (received > 0) await TrySendAsync(socket, DeviceExecFrames.Ack(received));
            foreach (var frame in backlog) await TrySendAsync(socket, frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(WebSocket socket)
    {
        var buffer = new byte[DeviceExecProtocol.HeaderBytes + DeviceExecProtocol.MaxPayloadBytes];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close) return;
                if (!DeviceExecFrames.TryDecode(buffer.AsMemory(0, length), out var frame))
                    throw new InvalidOperationException("агент прислал битый кадр");

                if (frame.Channel == DeviceExecFrameChannel.Ack)
                {
                    if (!IgnoreAcks)
                        lock (_lock)
                            while (_unacked.First is { } f && f.Value.Seq <= frame.Sequence) _unacked.RemoveFirst();
                    continue;
                }

                bool fresh;
                lock (_lock)
                {
                    _allDataSequences.Add(frame.Sequence);
                    if (frame.Sequence > _lastReceived + 1)
                        throw new InvalidOperationException($"пропуск номера: {frame.Sequence} после {_lastReceived}");
                    fresh = frame.Sequence == _lastReceived + 1;
                    if (fresh) _lastReceived = frame.Sequence;
                }

                if (fresh) _received.Writer.TryWrite(frame with { Payload = frame.Payload.ToArray() });

                bool breakNow;
                lock (_lock)
                {
                    _framesSinceConnect++;
                    breakNow = fresh && BreakAfterFrames is { } n && _framesSinceConnect >= n;
                    if (breakNow) BreakAfterFrames = null;
                }

                if (breakNow)
                {
                    // Обрыв ДО подтверждения: этот кадр агент обязан прислать повторно
                    socket.Abort();
                    return;
                }

                ulong ack;
                lock (_lock) ack = _lastReceived;
                await _sendLock.WaitAsync();
                try { await TrySendAsync(socket, DeviceExecFrames.Ack(ack)); }
                finally { _sendLock.Release(); }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
                if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    private static async Task TrySendAsync(WebSocket socket, byte[] frame)
    {
        try { await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        Break();
        return ValueTask.CompletedTask;
    }
}
