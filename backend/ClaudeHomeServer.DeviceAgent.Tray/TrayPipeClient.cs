using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>
/// Клиент pipe агента (<see cref="HandsPipe"/>) — единственный канал трея: ни сервера, ни
/// localhost-API. Подключается и переподключается сам, шлёт hello и раз в
/// <c>poll</c> просит снимок (связь с сервером, папки), кадры агента отдаёт в
/// <c>onMessage</c>. Колбэки зовутся из фонового потока — UI переносит их к себе.
/// <see cref="PipeOptions.CurrentUserOnly"/>: чужой pipe с тем же именем трей не примет за агента.
/// </summary>
internal sealed class TrayPipeClient : IAsyncDisposable
{
    private readonly string _name;
    private readonly Action _onConnected;
    private readonly Action _onDisconnected;
    private readonly Action<HandsPipeMessage> _onMessage;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private NamedPipeClientStream? _stream;
    private Task? _loop;

    public TrayPipeClient(string name, Action onConnected, Action onDisconnected, Action<HandsPipeMessage> onMessage,
        TimeSpan? poll = null, TimeSpan? retry = null)
    {
        _name = name;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _onMessage = onMessage;
        _poll = poll ?? TimeSpan.FromSeconds(5);
        _retry = retry ?? TimeSpan.FromSeconds(2);
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Отправить кадр агенту; false — pipe закрыт или запись не прошла.</summary>
    public async Task<bool> SendAsync(HandsPipeMessage message)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null) return false;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, HandsPipe.Json) + "\n");
        try { await _write.WaitAsync(_stop.Token); }
        catch (OperationCanceledException) { return false; }
        try
        {
            await stream.WriteAsync(bytes, _stop.Token);
            await stream.FlushAsync(_stop.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stream = new NamedPipeClientStream(".", _name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await stream.ConnectAsync((int)_retry.TotalMilliseconds, ct);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Агента нет, он перезапускается или pipe держит не тот пользователь — пробуем снова
                await stream.DisposeAsync();
                await DelayAsync(_retry, ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync();
                return;
            }

            Volatile.Write(ref _stream, stream);
            _onConnected();
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var poller = PollAsync(session.Token);
            try
            {
                if (!await SendAsync(new HandsPipeMessage(HandsPipeTypes.Hello, Version: HandsPipe.Version))) throw new IOException("hello не отправлен");
                await ReadAsync(stream, ct);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            finally
            {
                await session.CancelAsync();
                try { await poller; } catch (OperationCanceledException) { }
                Volatile.Write(ref _stream, null);
                await _write.WaitAsync(CancellationToken.None);
                try { await stream.DisposeAsync(); }
                catch (IOException) { }
                finally { _write.Release(); }
                if (!ct.IsCancellationRequested) _onDisconnected();
            }
            await DelayAsync(_retry, ct);
        }
    }

    private async Task ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return;
            for (var i = 0; i < n; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    if (buffer.Length >= HandsPipe.MaxMessageBytes) throw new IOException("кадр pipe длиннее потолка");
                    buffer.WriteByte(chunk[i]);
                    continue;
                }
                var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                buffer.SetLength(0);
                HandsPipeMessage? message;
                try { message = JsonSerializer.Deserialize<HandsPipeMessage>(line, HandsPipe.Json); }
                catch (JsonException) { continue; }
                if (message is not null) _onMessage(message);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_poll, ct);
            await SendAsync(new HandsPipeMessage(HandsPipeTypes.GetStatus));
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (Volatile.Read(ref _stream) is { } stream) await stream.DisposeAsync();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
