using System.Diagnostics;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Файлы рабочего дерева, которые бэкенд читает и пишет на хосте: чтение одним дескриптором
/// не больше потолка + 1 байт (FIFO не вешает поток), папка артефактов и console.log конвейера
/// не создаются сквозь подложенные ссылки.
/// </summary>
public class TreeFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-treefiles-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _cleanup = [];

    public TreeFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in _cleanup.Prepend(_root))
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    private string Outside()
    {
        var outside = TreeLinks.Outside("ccs-treefiles-out-");
        _cleanup.Add(outside);
        return outside;
    }

    // Поток, который заявляет о себе 10 байт, а отдаёт бесконечно (как /dev/zero с длиной по
    // fstat); прочитано больше лимита — бросает, чтобы чтение «до конца» не висло в тесте
    private sealed class EndlessStream(long limit) : Stream
    {
        public long Consumed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 10;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Consumed > limit) throw new InvalidOperationException($"прочитано {Consumed} байт — больше потолка");
            Array.Fill(buffer, (byte)'a', offset, count);
            Consumed += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadBounded_НеБольшеПотолкаПлюсОдинБайт_КакБыПотокНиВрал()
    {
        const int max = 1000;
        var stream = new EndlessStream(limit: 10 * max);

        TreeFiles.ReadBounded(stream, max).Should().BeNull("данных больше потолка — отказ, а не чтение до конца");
        stream.Consumed.Should().Be(max + 1, "с дескриптора читается ровно потолок + 1 байт");
    }

    [Fact]
    public void ReadInTree_ПотолокПустоКаталог()
    {
        var exact = Path.Combine(_root, "Exact.csproj");
        File.WriteAllText(exact, new string('x', 100));
        var big = Path.Combine(_root, "Big.csproj");
        File.WriteAllText(big, new string('x', 101));
        var empty = Path.Combine(_root, "Empty.csproj");
        File.WriteAllText(empty, "");
        Directory.CreateDirectory(Path.Combine(_root, "Dir.csproj"));

        TreeFiles.ReadInTree(_root, exact, 100).Should().HaveCount(100);
        TreeFiles.ReadInTree(_root, big, 100).Should().BeNull();
        TreeFiles.ReadInTree(_root, empty, 100).Should().BeNull();
        TreeFiles.ReadInTree(_root, Path.Combine(_root, "Dir.csproj"), 100).Should().BeNull();
        TreeFiles.ReadInTree(_root, Path.Combine(_root, "None.csproj"), 100).Should().BeNull();
    }

    [Fact]
    public async Task ReadInTree_FIFO_НеВешаетПоток()
    {
        if (!OperatingSystem.IsLinux()) return; // FIFO — только Linux (как DockerProcessRunnerKillTests)
        var fifo = Path.Combine(_root, "Fifo.csproj");
        using (var mkfifo = Process.Start("mkfifo", [fifo])) await mkfifo.WaitForExitAsync();

        var read = await Task.Run(() => TreeFiles.ReadInTree(_root, fifo, 100)).WaitAsync(TimeSpan.FromSeconds(10));

        read.Should().BeNull("открытие FIFO без писателя не ждёт, а не-перематываемый поток — отказ");
        DotnetBuildProgress.EstimateTotal(_root, "Fifo.csproj").Should().BeNull();
    }

    [Fact]
    public void ReadInTree_ФайлСсылкаНаружу_НеЧитается()
    {
        var victim = Path.Combine(Outside(), "secret.csproj");
        File.WriteAllText(victim, "<Project />");
        var link = Path.Combine(_root, "Link.csproj");
        if (!TreeLinks.TryLink(link, victim, directory: false)) return; // проверка живёт в CI на Linux

        if (OperatingSystem.IsLinux())
            TreeFiles.ReadInTree(_root, link, 100).Should().BeNull("реальный путь дескриптора — вне дерева");
    }

    [Fact]
    public void Артефакты_СсылкаВместоTestRuns_НаружуНичегоНеПишется()
    {
        var outside = Outside();
        Directory.CreateDirectory(Path.Combine(_root, ".cc-attachments"));
        if (!TreeLinks.TryLink(Path.Combine(_root, ".cc-attachments", TestRunService.ArtifactsSubdir), outside, directory: true))
            return;

        var artifacts = PhasePipeline.CreateArtifacts(_root, TestRunService.ArtifactsSubdir);
        using var log = PhasePipeline.OpenLog(_root, artifacts);

        artifacts.InTree.Should().BeFalse();
        log.Should().BeNull("лог в папку за ссылкой не пишется");
        Directory.EnumerateFileSystemEntries(outside, "*", SearchOption.AllDirectories).Should()
            .BeEmpty("сквозь ссылку на каталог ничего не создано");
    }

    [Fact]
    public void Артефакты_СсылкаВместоConsoleLog_ЧужойФайлЦел()
    {
        var victim = Path.Combine(Outside(), "users.json");
        File.WriteAllText(victim, "чужое");
        var artifacts = PhasePipeline.CreateArtifacts(_root, TestRunService.ArtifactsSubdir);
        artifacts.InTree.Should().BeTrue();
        if (!TreeLinks.TryLink(Path.Combine(artifacts.Full, PhasePipeline.LogName), victim, directory: false)) return;

        using (var log = PhasePipeline.OpenLog(_root, artifacts))
        {
            log.Should().BeNull("console.log уже есть (ссылка) — новый файл не создаётся, сквозь ссылку не пишем");
            log?.Write("вывод агента");
        }

        File.ReadAllText(victim).Should().Be("чужое");
    }

    [Fact]
    public void Артефакты_ОбычноеДерево_ЛогПишется()
    {
        var artifacts = PhasePipeline.CreateArtifacts(_root, DotnetBuildService.ArtifactsSubdir);
        using (var log = PhasePipeline.OpenLog(_root, artifacts))
            log!.Write("строка");

        artifacts.InTree.Should().BeTrue();
        File.ReadAllText(Path.Combine(artifacts.Full, PhasePipeline.LogName)).Should().Be("строка");
        PhasePipeline.OpenLog(_root, artifacts).Should().BeNull("второй раз — файл уже есть, CreateNew отказывает");
    }
}
