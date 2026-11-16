using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

    public static IEnumerable<object[]> Repeats() => Enumerable.Range(0, 20).Select(i => new object[] { i });

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
        s.ExecuteTask!.IsCompleted.Should().BeFalse("исключение разбора узла не роняет ExecuteAsync");
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
            if (s.RingCount("worker") >= 10 && s.RingCount("video") >= 10 && hung.Reads >= 2) done.TrySetResult();
            await Task.Delay(1, ct);
        };
        using var cts = new CancellationTokenSource();
        await s.StartAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await s.StopAsync(CancellationToken.None);

        s.RingCount("worker").Should().BeGreaterThanOrEqualTo(10, "worker получает снимок каждый тик");
        s.RingCount("video").Should().BeGreaterThanOrEqualTo(10);
        s.RingCount("ai").Should().Be(0, "опоздавший/неполученный срез в кольцо не попадает");
        s.ForNode("ai")!.Status.Should().Be("down");
        s.ForNode("ai")!.Since.Should().NotBeNull("у down есть Since");
    }

    // 20 повторов: гонка без блокировки ловится не всегда, на мутации хватает одного красного.
    [Theory]
    [MemberData(nameof(Repeats))]
    public async Task ConcurrentReadWhileWrite_NoExceptions_RingBounded(int repeat)
    {
        _ = repeat;
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

    [Fact]
    public async Task CpuLoad_FirstSnapshotNull_SecondFromJiffiesDelta()
    {
        var time = new ManualTime();
        var src = WithCpu("70000");
        src.Files["/proc/stat"] = "cpu  100 0 100 800 0 0 0 0 0 0\n";   // total 1000, idle 800
        var s = Build(time, ("worker", src));
        await s.PollNodeAsync("worker");
        s.ForNode("worker")!.Current!.CpuLoadPerc.Should().BeNull("prev нет");
        src.Files["/proc/stat"] = "cpu  450 0 500 1050 0 0 0 0 0 0\n";   // total 2000, idle 1050: Δ1000 / Δ250
        time.Advance(TimeSpan.FromSeconds(2));
        await s.PollNodeAsync("worker");
        s.ForNode("worker")!.Current!.CpuLoadPerc.Should().Be(75);
        s.ForNode("worker")!.Current!.UptimeS.Should().Be(1234, "целая часть /proc/uptime");
    }

    // Эталон снят на master (f49fc4d24) старым сэмплером на той же фикстуре; ручка не меняется.
    [Fact]
    public async Task Report_WorkerRegression_JsonMatchesMasterBaseline()
    {
        var time = new ManualTime();
        time.Advance(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero) - time.GetUtcNow()); // дата эталона
        var dir = Path.Combine(Path.GetTempPath(), "gold_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var helper = Path.Combine(dir, "temps.json");
            File.WriteAllText(helper, $"{{\"v\":1,\"at\":{time.GetUtcNow().ToUnixTimeMilliseconds()},\"gpus\":[{{\"busId\":\"0000:0b:00.0\",\"deviceId\":\"0x2204\",\"memTempC\":68,\"hotspotTempC\":75}}]}}");
            var src = new FakeNodeSource(new OneGpu(), helper);
            var f = src.Files;
            const string R = "/sys/class/hwmon/";
            f[R + "hwmon0/name"] = "k10temp\n";
            f[R + "hwmon0/temp1_input"] = "76250"; f[R + "hwmon0/temp1_label"] = "Tctl";
            f[R + "hwmon0/temp3_input"] = "78000"; f[R + "hwmon0/temp3_label"] = "Tccd1";
            f[R + "hwmon0/temp4_input"] = "49750"; f[R + "hwmon0/temp4_label"] = "Tccd2";
            f[R + "hwmon6/name"] = "asusec\n";
            f[R + "hwmon6/temp1_input"] = "78000"; f[R + "hwmon6/temp1_label"] = "Chipset";
            f[R + "hwmon6/temp5_input"] = "53000"; f[R + "hwmon6/temp5_label"] = "VRM";
            f[R + "hwmon6/fan2_input"] = "1159"; f[R + "hwmon6/fan2_label"] = "Chipset";
            f[R + "hwmon1/name"] = "nct6798\n";
            f[R + "hwmon1/fan2_input"] = "1159"; f[R + "hwmon1/fan7_input"] = "4821";
            f["/proc/meminfo"] = "MemTotal:       67108864 kB\nMemAvailable:   33554432 kB\n";
            var s = Build(time, ("worker", src));
            s.NodeKeys.Should().Equal("worker");
            await s.PollNodeAsync("worker");

            // Реестра узлов нет → ровно один «worker» (побайтно сегодняшняя ручка).
            var report = new HardwareReportService(s, new ConfigurationBuilder().Build()).Build(TimeSpan.FromMinutes(5));
            report.Should().NotBeNull("узла worker при отсутствии реестра");
            var json = System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            StripNewFields(json).Should().Be(File.ReadAllText(BaselinePath()),
                "отличается ровно node/uptimeS/cpuLoadPerc и плитка+ряд board");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string BaselinePath([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "Fixtures", "hardware-report-worker-master.json");

    // Из ответа убирается ровно то, что добавилось к эталону Ш2c: три поля узла (node, uptimeS,
    // cpuLoadPerc) и плитка+ряд платы. Остальное сравнивается строка в строку — порядок ключей и
    // экранирование при этом не меняются (JsonNode хранит то, что было в тексте).
    private static string StripNewFields(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        foreach (var key in new[] { "node", "uptimeS", "cpuLoadPerc" }) root.Remove(key);
        foreach (var arr in new[] { root["nodes"]!.AsArray(), root["series"]!.AsArray() })
            for (var i = arr.Count - 1; i >= 0; i--)
                if (arr[i]!["key"]!.GetValue<string>() == "board") arr.RemoveAt(i);
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private sealed class OneGpu : IGpuSampler
    {
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpuSample>>([new GpuSample
            {
                Index = 0, Uuid = "GPU-1", Name = "RTX 3090", Temperature = 70, BusId = "00000000:0B:00.0", SwPowerCap = true,
            }]);
    }
}
