using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Подделка трубы CDP: каждое чтение соединения получает ровно один кусок, положенный тестом
/// (так склейка и разрезка рамок детерминированы), а записанное соединением режется по нулевому
/// байту в очередь рамок.
/// </summary>
sealed class FakeCdpTransport : ICdpTransport
{
    static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(5);

    readonly ChunkReadStream _read = new();
    readonly FrameCaptureStream _write = new();
    readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Stream Read => _read;
    public Stream Write => _write;
    public Task Exited => _exited.Task;

    /// <summary>Положить сырой кусок байтов для одного чтения.</summary>
    public void Push(byte[] chunk) => _read.Chunks.Writer.TryWrite(chunk);

    public void Push(string text) => Push(Encoding.UTF8.GetBytes(text));

    /// <summary>Одна целая рамка (JSON + \0) одним чтением.</summary>
    public void Frame(string json) => Push(json + "\0");

    public void EndOfStream() => _read.Chunks.Writer.TryComplete();

    public void Exit() => _exited.TrySetResult();

    public void BreakWrites() => _write.Broken = true;

    /// <summary>Следующая рамка, записанная соединением.</summary>
    public async Task<JsonElement> NextSentAsync()
    {
        var bytes = await _write.Frames.Reader.ReadAsync().AsTask().WaitAsync(FrameWait);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    sealed class ChunkReadStream : Stream
    {
        public readonly Channel<byte[]> Chunks = Channel.CreateUnbounded<byte[]>();
        byte[] _rest = [];

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_rest.Length == 0)
            {
                if (!await Chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _rest = await Chunks.Reader.ReadAsync(cancellationToken);
            }
            var n = Math.Min(buffer.Length, _rest.Length);
            _rest.AsMemory(0, n).CopyTo(buffer);
            _rest = _rest[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class FrameCaptureStream : Stream
    {
        public readonly Channel<byte[]> Frames = Channel.CreateUnbounded<byte[]>();
        public volatile bool Broken;
        readonly MemoryStream _current = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Broken) throw new IOException("The pipe is broken");
            lock (_current)
            {
                for (var i = offset; i < offset + count; i++)
                {
                    if (buffer[i] != 0)
                    {
                        _current.WriteByte(buffer[i]);
                        continue;
                    }
                    Frames.Writer.TryWrite(_current.ToArray());
                    _current.SetLength(0);
                }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
