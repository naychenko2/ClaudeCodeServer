using System.Collections.Concurrent;
using System.Text.Json;

namespace BrowserHandsProbe;

sealed class CdpException(string message) : Exception(message);

sealed class CdpClosedException(string reason) : Exception("труба CDP закрыта: " + reason);

/// <summary>
/// Минимальный CDP-клиент поверх двух труб (--remote-debugging-pipe, режим ASCIIZ):
/// сообщение — JSON, разделитель — нулевой байт.
/// </summary>
sealed class CdpClient : IDisposable
{
    readonly Stream _toChrome;
    readonly Stream _fromChrome;
    readonly object _writeLock = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly List<(string Method, string? Session, TaskCompletionSource<JsonElement> Tcs)> _waiters = [];
    readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _nextId;

    public CdpClient(Stream toChrome, Stream fromChrome)
    {
        _toChrome = toChrome;
        _fromChrome = fromChrome;
        new Thread(ReadLoop) { IsBackground = true, Name = "cdp-read" }.Start();
    }

    /// <summary>Завершается причиной, когда чтение из трубы кончилось (EOF или ошибка).</summary>
    public Task<string> Closed => _closed.Task;

    void ReadLoop()
    {
        var buf = new byte[1 << 16];
        var acc = new MemoryStream();
        try
        {
            while (true)
            {
                int n = _fromChrome.Read(buf, 0, buf.Length);
                if (n == 0)
                {
                    Close("EOF: Chrome закрыл свой конец трубы");
                    return;
                }
                int start = 0;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != 0) continue;
                    acc.Write(buf, start, i - start);
                    Dispatch(acc.ToArray());
                    acc.SetLength(0);
                    start = i + 1;
                }
                acc.Write(buf, start, n - start);
            }
        }
        catch (Exception ex)
        {
            Close($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    void Dispatch(byte[] message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idEl))
        {
            if (!_pending.TryRemove(idEl.GetInt32(), out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new CdpException(err.GetRawText()));
            else
                tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
            return;
        }
        if (!root.TryGetProperty("method", out var m)) return;
        string method = m.GetString() ?? "";
        string? session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        JsonElement prms = root.TryGetProperty("params", out var p) ? p.Clone() : default;
        lock (_waiters)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                var w = _waiters[i];
                if (w.Method != method || w.Session != session) continue;
                w.Tcs.TrySetResult(prms);
                _waiters.RemoveAt(i);
            }
        }
    }

    void Close(string reason)
    {
        _closed.TrySetResult(reason);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new CdpClosedException(reason));
        lock (_waiters)
        {
            foreach (var w in _waiters) w.Tcs.TrySetException(new CdpClosedException(reason));
            _waiters.Clear();
        }
    }

    public async Task<JsonElement> Call(string method, Action<Utf8JsonWriter>? writeParams = null,
        string? session = null, int timeoutMs = 15000)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (_closed.Task.IsCompleted)
        {
            _pending.TryRemove(id, out _);
            throw new CdpClosedException(_closed.Task.Result);
        }

        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (session != null) w.WriteString("sessionId", session);
            w.WriteStartObject("params");
            writeParams?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        ms.WriteByte(0);

        try
        {
            lock (_writeLock)
            {
                _toChrome.Write(ms.GetBuffer(), 0, (int)ms.Length);
                _toChrome.Flush();
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new CdpClosedException($"запись в трубу: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"{method}: нет ответа за {timeoutMs} мс");
        }
    }

    /// <summary>Регистрировать ДО команды, которая вызывает событие.</summary>
    public Task<JsonElement> WaitEvent(string method, string? session)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) _waiters.Add((method, session, tcs));
        if (_closed.Task.IsCompleted) Close(_closed.Task.Result);
        return tcs.Task;
    }

    /// <summary>Закрыть только свой пишущий конец: Chrome увидит EOF на входе.</summary>
    public void CloseWriteEnd() => _toChrome.Dispose();

    public void Dispose()
    {
        try { _toChrome.Dispose(); } catch { }
        try { _fromChrome.Dispose(); } catch { }
    }
}
