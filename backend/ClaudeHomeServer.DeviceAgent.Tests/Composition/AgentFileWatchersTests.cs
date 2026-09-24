using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Ватчер агента: правка файла в дереве проекта доезжает донесением с относительным путём,
/// служебные каталоги (TreeExcludes) — нет. Ждём событие, а не паузу.
/// </summary>
public sealed class AgentFileWatchersTests : IDisposable
{
    private readonly AgentSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private sealed class Sink : IFilesChangedSink
    {
        public readonly List<DeviceFilesChanged> Reports = [];
        public readonly TaskCompletionSource<DeviceFilesChanged> Got = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<DeviceFilesChanged, bool> Until = _ => true;

        public Task ReportAsync(DeviceFilesChanged report, CancellationToken ct)
        {
            lock (Reports) Reports.Add(report);
            if (Until(report)) Got.TrySetResult(report);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ПравкаФайла_ДоезжаетОтносительнымПутём_СлужебныеКаталогиНет()
    {
        var sink = new Sink { Until = r => r.Paths.Contains("src/new.txt") };
        using var watchers = new AgentFileWatchers(sink);
        Directory.CreateDirectory(Path.Combine(_box.Project, "src"));
        Directory.CreateDirectory(Path.Combine(_box.Project, "node_modules"));
        var root = _box.Policy().ProjectRoot(_box.Project);

        watchers.Touch("p1", root);
        File.WriteAllText(Path.Combine(_box.Project, "node_modules", "junk.js"), "x");
        File.WriteAllText(Path.Combine(_box.Project, "src", "new.txt"), "x");

        var report = await sink.Got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        report.ProjectId.Should().Be("p1");
        report.Full.Should().BeFalse();
        lock (sink.Reports)
            sink.Reports.SelectMany(r => r.Paths).Should().NotContain(p => p.StartsWith("node_modules"));
    }

    [Fact]
    public void ПовторныйTouch_НеПлодитНаблюдателей()
    {
        using var watchers = new AgentFileWatchers(new Sink());
        var root = _box.Policy().ProjectRoot(_box.Project);

        watchers.Touch("p1", root);
        watchers.Touch("p1", root);

        watchers.Watched.Should().Equal("p1");
    }
}
