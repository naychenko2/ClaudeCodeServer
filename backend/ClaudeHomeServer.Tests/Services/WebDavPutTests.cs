using System.Text;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// PUT пишет тело во временный файл и подменяет цель rename'ом: сбой чтения тела (лимит,
// обрыв клиентом) раньше оставлял на месте файла пустышку нулевой длины.
public class WebDavPutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webdav-put-" + Guid.NewGuid().ToString("N"));

    public WebDavPutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private static DefaultHttpContext Ctx(Stream body, long? contentLength, long? maxPutBytes = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxPutBytes is not null) settings["WebDav:MaxPutBytes"] = maxPutBytes.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider(),
        };
        ctx.Request.Method = "PUT";
        ctx.Request.Body = body;
        ctx.Request.ContentLength = contentLength;
        return ctx;
    }

    private string[] LeftoverTempFiles() =>
        Directory.GetFiles(_root, "*.davput-*", SearchOption.AllDirectories);

    [Fact]
    public async Task УспешныйPut_ПишетСодержимое()
    {
        var existing = Path.Combine(_root, "docs", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "старое");
        var payload = Encoding.UTF8.GetBytes("новое содержимое");

        var ctx = Ctx(new MemoryStream(payload), payload.Length);
        await WebDavHandler.HandlePutAsync(ctx, _root, "docs/a.txt");

        ctx.Response.StatusCode.Should().Be(204);
        (await File.ReadAllBytesAsync(existing)).Should().Equal(payload);
        LeftoverTempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task НовыйФайл_201()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var ctx = Ctx(new MemoryStream(payload), payload.Length);

        await WebDavHandler.HandlePutAsync(ctx, _root, "new/b.bin");

        ctx.Response.StatusCode.Should().Be(201);
        (await File.ReadAllBytesAsync(Path.Combine(_root, "new", "b.bin"))).Should().Equal(payload);
    }

    [Fact]
    public async Task ОбрывТелаПосередине_ФайлНеТронут_ВременныхНет()
    {
        var target = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(target, "исходник");

        var ctx = Ctx(new FailingStream(bytesBeforeFailure: 1000), contentLength: 5000);
        await WebDavHandler.HandlePutAsync(ctx, _root, "a.txt");

        ctx.Response.StatusCode.Should().Be(400);
        (await File.ReadAllTextAsync(target)).Should().Be("исходник");
        LeftoverTempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ContentLengthБольшеЛимита_413_ФайлНеТронут()
    {
        var target = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(target, "исходник");

        var ctx = Ctx(new MemoryStream(new byte[100]), contentLength: 100, maxPutBytes: 10);
        await WebDavHandler.HandlePutAsync(ctx, _root, "a.txt");

        ctx.Response.StatusCode.Should().Be(413);
        (await File.ReadAllTextAsync(target)).Should().Be("исходник");
        LeftoverTempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ТелоБезContentLengthБольшеЛимита_413_ФайлНеТронут()
    {
        var target = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(target, "исходник");

        var ctx = Ctx(new MemoryStream(new byte[100]), contentLength: null, maxPutBytes: 10);
        await WebDavHandler.HandlePutAsync(ctx, _root, "a.txt");

        ctx.Response.StatusCode.Should().Be(413);
        (await File.ReadAllTextAsync(target)).Should().Be("исходник");
        LeftoverTempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ПустоеТело_СоздаётПустойФайл()
    {
        var ctx = Ctx(new MemoryStream(), contentLength: 0);

        await WebDavHandler.HandlePutAsync(ctx, _root, "empty.docx");

        ctx.Response.StatusCode.Should().Be(201);
        var created = Path.Combine(_root, "empty.docx");
        File.Exists(created).Should().BeTrue();
        new FileInfo(created).Length.Should().Be(0);
        LeftoverTempFiles().Should().BeEmpty();
    }

    /// <summary>Тело, которое отдаёт часть байт и рвётся как сброшенное соединение.</summary>
    private sealed class FailingStream(int bytesBeforeFailure) : Stream
    {
        private int _sent;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent >= bytesBeforeFailure) throw new IOException("Connection reset by peer");
            var n = Math.Min(buffer.Length, bytesBeforeFailure - _sent);
            buffer.Span[..n].Fill((byte)'z');
            _sent += n;
            return ValueTask.FromResult(n);
        }

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
}
