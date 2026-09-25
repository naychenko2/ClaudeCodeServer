using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Канал исполнения в одном процессе: вместо WebSocket устройства — пара очередей кадров.
/// <see cref="Agent"/> играет агента устройства и получает сторону устройства каждого
/// открытого потока.
/// </summary>
internal sealed class InProcessDeviceExecChannel : IDeviceExecChannel
{
    public Func<InProcessExecStream, Task>? Agent { get; init; }
    public DeviceExecRefusedException? Refuse { get; set; }
    public string Platform { get; init; } = "linux";
    public ConcurrentQueue<InProcessExecStream> Opened { get; } = new();

    public DeviceExecStatus? GetStatus(string ownerId, string deviceId) =>
        new(deviceId, "test", Online: true, Platform, "1.0", "2.1.0", "2.1.0",
            [DeviceCapabilities.Exec], HarnessReady: true, HarnessProblem: null);

    public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default)
    {
        if (Refuse is not null) throw Refuse;
        var stream = new InProcessExecStream(Guid.NewGuid().ToString("N"));
        Opened.Enqueue(stream);
        if (Agent is not null) stream.AgentTask = Task.Run(() => Agent(stream));
        return Task.FromResult<IDeviceExecStream>(stream);
    }
}

internal sealed class InProcessExecStream(string execId) : IDeviceExecStream
{
    private readonly Channel<DeviceExecFrame> _toDevice = Channel.CreateUnbounded<DeviceExecFrame>();
    private readonly Channel<DeviceExecFrame> _toServer = Channel.CreateUnbounded<DeviceExecFrame>();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _serverSeq;
    private long _deviceSeq;

    public string ExecId { get; } = execId;
    public Task? AgentTask { get; set; }
    public Task Disposed => _disposed.Task;

    // ---------- сторона сервера (раннер) ----------

    public ValueTask SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_disposed.Task.IsCompleted) throw new ObjectDisposedException(nameof(InProcessExecStream));
        return _toDevice.Writer.WriteAsync(
            new DeviceExecFrame(channel, (ulong)Interlocked.Increment(ref _serverSeq), payload.ToArray()), ct);
    }

    public async IAsyncEnumerable<DeviceExecFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var f in _toServer.Reader.ReadAllAsync(ct)) yield return f;
    }

    public ValueTask DisposeAsync()
    {
        _toDevice.Writer.TryComplete();
        _toServer.Writer.TryComplete();
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    // ---------- сторона устройства (агент) ----------

    public ChannelReader<DeviceExecFrame> FromServer => _toDevice.Reader;

    // После DisposeAsync запись молча теряется — как вывод процесса, чей канал уже закрыт
    public ValueTask DeviceSendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload)
    {
        _toServer.Writer.TryWrite(new DeviceExecFrame(channel, (ulong)Interlocked.Increment(ref _deviceSeq), payload.ToArray()));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeviceSendLineAsync(string line) =>
        DeviceSendAsync(DeviceExecFrameChannel.Stdout, System.Text.Encoding.UTF8.GetBytes(line + "\n"));

    public ValueTask DeviceExitAsync(int? code, string? signal = null) =>
        DeviceSendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(code, signal)));
}
