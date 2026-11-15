using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты FileService.WriteAtomicFromStreamAsync (Ф-6): атомарная подмена по образцу
// FilesController.WriteAtomic — при отказе по пределу и при сбое потока цель остаётся как была,
// временный файл не переживает операцию; предел держится подсчётом копий, а не Content-Length.
public class FileServiceWriteAtomicStreamTests : IDisposable
{
    private readonly string _root;
    private readonly FileService _svc = new();

    public FileServiceWriteAtomicStreamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_atomic_stream_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Поток_пишется_целиком_уведомляет_подписчиков_и_не_оставляет_temp()
    {
        var mutations = new List<(string Rel, FileMutationKind Kind)>();
        _svc.OnMutated += (_, rel, kind, _) => mutations.Add((rel, kind));

        var ok = await _svc.WriteAtomicFromStreamAsync(_root, "sub/файл.bin", new MemoryStream([1, 2, 3]), 10);

        ok.Should().BeTrue();
        (await File.ReadAllBytesAsync(Path.Combine(_root, "sub", "файл.bin"))).Should().Equal([1, 2, 3]);
        mutations.Should().Equal(("sub/файл.bin", FileMutationKind.Write));
        // temp жил только время записи: после успеха в каталоге ровно цель
        Directory.GetFiles(Path.Combine(_root, "sub")).Select(Path.GetFileName).Should().Equal("файл.bin");
    }

    [Fact]
    public async Task Ровно_на_пределе_пишет_больше_предела_отказывает_и_цель_остаётся_как_была()
    {
        var target = Path.Combine(_root, "x.bin");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);

        // Ровно на пределе — ещё можно: интервал [0, limit]
        (await _svc.WriteAtomicFromStreamAsync(_root, "y.bin", new MemoryStream(new byte[10]), 10)).Should().BeTrue();
        new FileInfo(Path.Combine(_root, "y.bin")).Length.Should().Be(10);

        // На байт больше — отказ: подмены не было, у цели старое содержимое, temp удалён
        (await _svc.WriteAtomicFromStreamAsync(_root, "x.bin", new MemoryStream(new byte[11]), 10)).Should().BeFalse();
        (await File.ReadAllBytesAsync(target)).Should().Equal([1, 2, 3]);
        Directory.GetFiles(_root).Select(Path.GetFileName).Should().BeEquivalentTo("x.bin", "y.bin");
    }

    [Fact]
    public async Task Сбой_потока_посередине_не_оставляет_temp_и_цель_целу()
    {
        var target = Path.Combine(_root, "x.bin");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);

        var act = () => _svc.WriteAtomicFromStreamAsync(_root, "x.bin", new ThrowingStream(), 100).GetAwaiter().GetResult();

        act.Should().Throw<IOException>();
        (await File.ReadAllBytesAsync(target)).Should().Equal([1, 2, 3]);
        Directory.GetFiles(_root).Select(Path.GetFileName).Should().Equal("x.bin");
    }

    [Fact]
    public async Task Перезапись_сохраняет_права_существующей_цели()
    {
        if (OperatingSystem.IsWindows()) return; // unix-права проверяются на Unix

        var target = Path.Combine(_root, "secret.txt");
        await File.WriteAllBytesAsync(target, [1]);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600

        (await _svc.WriteAtomicFromStreamAsync(_root, "secret.txt", new MemoryStream([2, 2]), 10)).Should().BeTrue();

        // Move берёт права от temp; без переноса 0600 стал бы 0644 (тот же случай, что S2c-1 п.1)
        File.GetUnixFileMode(target).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Поток, который бросает после первого куска: запись не должна оставить temp
    private sealed class ThrowingStream : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_read) { _read = true; buffer.Span[0] = 1; return 1; }
            await Task.Yield();
            throw new IOException("поток оборвался");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
