using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

// Источник сырого среза для тестов сэмплера: изменяемый словарь файлов (абсолютные пути /sys, /proc),
// карты — подмена IGpuSampler, gddr6 — файл (читается на каждом чтении).
internal sealed class FakeNodeSource(IGpuSampler? gpu = null, string? gddr6File = null) : INodeReadingSource
{
    public Dictionary<string, string> Files { get; } = new();
    public NodeReadingStatus Status { get; set; } = NodeReadingStatus.Ok;
    public int Reads;

    public async Task<NodeReading> ReadAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Reads);
        if (Status is NodeReadingStatus.Down or NodeReadingStatus.Incompatible)
            return new NodeReading { Status = Status };
        var gpus = gpu is null ? [] : await gpu.ReadAsync(ct);
        return new NodeReading
        {
            Status = Status,
            Files = new SliceFileSource(new Dictionary<string, string>(Files)),
            Gpus = gpus,
            Gddr6 = gddr6File is not null && File.Exists(gddr6File) ? File.ReadAllText(gddr6File) : null,
        };
    }
}

/// <summary>Узловой сэмплер (Ш2c): кольцо и статус на узел, параллельные узлы, зависший узел, отказ разбора.</summary>
public class HardwareSamplerNodesTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Opts : IOptionsMonitor<LocalModelStatsOptions>
    {
        public LocalModelStatsOptions CurrentValue { get; } = new() { Enabled = true };
        public LocalModelStatsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LocalModelStatsOptions, string?> listener) => null;
    }

    // Источник, который никогда не отвечает и игнорирует отмену.
    private sealed class HangingSource : INodeReadingSource
    {
        public int Reads;
        public Task<NodeReading> ReadAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return new TaskCompletionSource<NodeReading>().Task;
        }
    }

    private sealed class ThrowingSource : INodeReadingSource
    {
        public Task<NodeReading> ReadAsync(CancellationToken ct = default) => throw new InvalidOperationException("кривой срез");
    }

    private static FakeNodeSource WithCpu(string tctl)
    {
        var s = new FakeNodeSource();
        s.Files["/sys/class/hwmon/hwmon0/name"] = "k10temp\n";
        s.Files["/sys/class/hwmon/hwmon0/temp1_input"] = tctl;
        s.Files["/sys/class/hwmon/hwmon0/temp1_label"] = "Tctl\n";
        s.Files["/proc/uptime"] = "1234.56 99.0\n";
        s.Files["/proc/stat"] = "cpu  100 0 100 800 0 0 0 0 0 0\n";
        return s;
    }

    private static HardwareSampler Build(ManualTime time, params (string key, INodeReadingSource src)[] nodes) =>
        new(new Opts(), nodes.Select(n => new HardwareSamplerNode(n.key, n.src)).ToList(),
            new Gddr6HelperReader(time), time);

    [Fact]
    public async Task RingPerNode_StatusPerNode_UptimeAndLoad()
    {
        var time = new ManualTime();
        var w = WithCpu("70000");
        var v = WithCpu("50000");
        var s = Build(time, ("worker", w), ("video", v));
        s.NodeKeys.Should().Equal("worker", "video");
        s.ForNode("нет").Should().BeNull();

        await s.PollNodeAsync("worker");
        time.Advance(TimeSpan.FromSeconds(2));
        w.Files["/proc/stat"] = "cpu  150 0 150 900 0 0 0 0 0 0\n"; // Δtotal 200, Δidle 100 → 50 %
        await s.PollNodeAsync("worker");

        s.RingCount("worker").Should().Be(2);
        s.RingCount("video").Should().Be(0, "кольцо — на узел");
        var cur = s.ForNode("worker")!.Current!;
        cur.UptimeS.Should().Be(1234);
        cur.CpuLoadPerc.Should().Be(50);
        cur.Cpus!.Single().Tctl.Should().Be(70);
        s.ForNode("video")!.Current.Should().BeNull();
    }

    [Fact]
    public async Task DownAndIncompatible_NoSample_StatusWithSince()
    {
        var time = new ManualTime();
        var src = WithCpu("70000");
        var s = Build(time, ("ai", src));
        src.Status = NodeReadingStatus.Down;
        await s.PollNodeAsync("ai");
        var v = s.ForNode("ai")!;
        v.Status.Should().Be("down");
        v.Since.Should().Be(time.GetUtcNow().ToUnixTimeMilliseconds());
        v.Current.Should().BeNull();
        var downAt = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(2));
        await s.PollNodeAsync("ai");
        v.Since.Should().Be(downAt.ToUnixTimeMilliseconds(), "Since — первое чтение в этом статусе");
        src.Status = NodeReadingStatus.Incompatible;
        await s.PollNodeAsync("ai");
        v.Status.Should().Be("incompatible");
        src.Status = NodeReadingStatus.Ok;
        await s.PollNodeAsync("ai");
        v.Status.Should().Be("ok");
        v.Since.Should().BeNull();
    }

    [Fact]
    public async Task ParseFailureOfOneNode_LoopSurvives_NodeDown()
    {
        var time = new ManualTime();
        var good = WithCpu("70000");
        var s = Build(time, ("bad", new ThrowingSource()), ("worker", good));
        var reads = 0;
        var enough = new TaskCompletionSource();
        s.WaitFunc = async (d, ct) =>
        {
            if (d == HardwareSampler.TickTimeout) { await Task.Delay(Timeout.Infinite, ct); return; }
            if (Interlocked.Increment(ref reads) >= 6) enough.TrySetResult();
            await Task.Delay(1, ct);
        };
        using var cts = new CancellationTokenSource();
        await s.StartAsync(cts.Token);
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10));
        s.ForNode("bad")!.Status.Should().Be("down");
        s.RingCount("worker").Should().BeGreaterThan(0, "исключение чужого узла не останавливает цикл");
        cts.Cancel();
        await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HungNode_DoesNotStallOthers()
    {
        var time = new ManualTime();
        var hung = new HangingSource();
        var w = WithCpu("70000");
        var v = WithCpu("50000");
        var s = Build(time, ("worker", w), ("ai", hung), ("video", v));
        var done = new TaskCompletionSource();
        // Паузу между тиками — почти нулевая; тайм-аут тика зависшего узла — короткий реальный.
        s.WaitFunc = async (d, ct) =>
        {
            if (d == HardwareSampler.TickTimeout)
            {
                await Task.Delay(150, ct);
                return;
            }
            if (w.Reads >= 5 && v.Reads >= 5 && hung.Reads >= 2) done.TrySetResult();
            await Task.Delay(1, ct);
        };
        using var cts = new CancellationTokenSource();
        await s.StartAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await s.StopAsync(CancellationToken.None);

        s.RingCount("worker").Should().BeGreaterThanOrEqualTo(4, "worker получает снимок каждый тик");
        s.RingCount("video").Should().BeGreaterThanOrEqualTo(4);
        s.RingCount("ai").Should().Be(0, "опоздавший/неполученный срез в кольцо не попадает");
        s.ForNode("ai")!.Status.Should().Be("down");
    }

    [Fact]
    public async Task ConcurrentReadWhileWrite_NoExceptions_RingBounded()
    {
        var time = new ManualTime();
        var s = Build(time, ("worker", WithCpu("70000")));
        var view = s.ForNode("worker")!;
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                view.Aggregate(x => x.Cpus?[0].Tctl, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        })).ToArray();

        for (var i = 0; i < 1_800 + 50; i++)
        {
            await s.PollNodeAsync("worker");
            time.Advance(TimeSpan.FromSeconds(2));
        }
        stop.Cancel();
        await Task.WhenAll(readers); // исключение читателя всплывёт здесь
        s.RingCount("worker").Should().Be(1_800, "кольцо не больше 1800 и заполнено до потолка");
    }
}
