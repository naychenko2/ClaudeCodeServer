using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// M2 (финальное ревью): билдер снимка GPU различает идущую и ждущую задачу.
// Идущая (позиция 0, в queue_running) → Running=true, Ahead=null (не «Следующая»);
// ждущая (позиция > 0) → Running=false, Ahead=число впереди.
public class ComfyGpuSnapshotBuilderTests
{
    // Фейк: очередь с заданными running/pending (prompt_id); Down — ComfyUI недоступен
    private sealed class QueueFake : HttpMessageHandler
    {
        public List<string> Running { get; } = [];
        public List<string> Pending { get; } = [];
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (Down) throw new HttpRequestException("connection refused");
            if (path == "/queue")
            {
                var body = new JsonObject
                {
                    ["queue_running"] = new JsonArray([.. Running.Select((id, n) => (JsonNode)new JsonArray(n, id, new JsonObject()))]),
                    ["queue_pending"] = new JsonArray([.. Pending.Select((id, n) => (JsonNode)new JsonArray(100 + n, id, new JsonObject()))]),
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                });
            }
            if (path == "/system_stats")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"system":{"comfyui_version":"v0.37"}}""", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed record Harness(QueueFake Fake, ComfyQueueMonitor Monitor, ComfyGpuSnapshotBuilder Builder);

    private static Harness Build()
    {
        var fake = new QueueFake();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:Enabled"] = "true",
        }).Build();
        var ledger = new ComfyPromptLedger();
        var client = new ComfyClient(new FakeComfyFactory(fake), config);
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        return new Harness(fake, monitor, new ComfyGpuSnapshotBuilder(monitor, ledger, null, NullLogger<ComfyGpuSnapshotBuilder>.Instance));
    }

    [Fact]
    public async Task Идущая_Задача_RunningTrue_AheadNull()
    {
        var h = Build();
        h.Fake.Running.Add("r1");
        h.Fake.Pending.Add("w1");
        await h.Monitor.TickAsync(CancellationToken.None);

        // r1 идёт (позиция 0)
        var gpu = h.Builder.Build("r1", 0);

        gpu.Should().NotBeNull();
        gpu!.Running.Should().BeTrue("идущая задача помечена Running");
        gpu.Ahead.Should().BeNull("идущая — не «Следующая»: Ahead=null, а не 0");
    }

    [Fact]
    public async Task Ждущая_Задача_RunningFalse_AheadЧисло()
    {
        var h = Build();
        h.Fake.Running.Add("r1");
        h.Fake.Pending.Add("w1");
        h.Fake.Pending.Add("w2");
        await h.Monitor.TickAsync(CancellationToken.None);

        // w1 ждёт: впереди 1 идущая → позиция 1
        var gpu = h.Builder.Build("w1", 1);

        gpu.Should().NotBeNull();
        gpu!.Running.Should().BeFalse("ждущая задача — Running=false");
        gpu.Ahead.Should().Be(1, "ждущая: Ahead = число впереди");
    }

    [Fact]
    public async Task Ждущая_Вторая_Вочереди_AheadДва()
    {
        var h = Build();
        h.Fake.Running.Add("r1");
        h.Fake.Pending.Add("w1");
        h.Fake.Pending.Add("w2");
        await h.Monitor.TickAsync(CancellationToken.None);

        // w2 ждёт: впереди 1 идущая + 1 ждущая → позиция 2
        var gpu = h.Builder.Build("w2", 2);

        gpu.Should().NotBeNull();
        gpu!.Running.Should().BeFalse();
        gpu.Ahead.Should().Be(2);
    }

    // M1: стенд недоступен — снимок НЕ null, несёт downSince (unix-мс), остальное null/Running=false
    [Fact]
    public async Task СтендDown_GpuНесётDownSince()
    {
        var h = Build();
        h.Fake.Down = true;
        await h.Monitor.TickAsync(CancellationToken.None);

        var gpu = h.Builder.Build("any-ticket", 0);

        gpu.Should().NotBeNull("down — снимок не null");
        gpu!.DownSince.Should().NotBeNull("down — downSince задан");
        gpu.DownSince!.Value.Should().BeGreaterThan(0, "downSince — unix-мс, число");
        gpu.Running.Should().BeFalse();
        gpu.Ahead.Should().BeNull();
    }

    // M1: живой стенд — downSince null
    [Fact]
    public async Task СтендЖив_GpuDownSinceNull()
    {
        var h = Build();
        h.Fake.Running.Add("r1");
        await h.Monitor.TickAsync(CancellationToken.None);

        var gpu = h.Builder.Build("r1", 0);

        gpu.Should().NotBeNull();
        gpu!.DownSince.Should().BeNull("живой стенд — downSince null");
    }

    // Minor 3 (финальное ревью) — примитивы: PositionInSnapshot + Build. Позиция задачи из
    // pending снимка (> 0) → Build → Running=false, не ложный «идёт». Адаптер НЕ трогает:
    // мутация «Build(ticket, 0)» в catch (ComfyException) адаптера его не красит — её ловит
    // тест адаптера Gpu_адаптер_СтендDown_ЗадачаВPending_НеЛожныйRunning (LocalImageMediaAdapterTests).
    [Fact]
    public async Task Minor3_ЗадачаВPending_НеЛожныйRunning()
    {
        var h = Build();
        // Задача в pending (не running): впереди 1 идущая.
        h.Fake.Running.Add("other");
        h.Fake.Pending.Add("my-task");
        await h.Monitor.TickAsync(CancellationToken.None);

        // Позиция из снимка: задача в pending → позиция = running.Count + pendingIndex = 1 + 0 = 1.
        var snap = h.Monitor.Snapshot!;
        var position = ComfyGpuSnapshotBuilder.PositionInSnapshot(snap, "my-task");
        position.Should().Be(1, "задача в pending: позиция = running.Count + index");

        var gpu = h.Builder.Build("my-task", position);
        gpu.Should().NotBeNull();
        gpu!.Running.Should().BeFalse("задача не в queue_running снимка → Running=false, не ложный «идёт»");
    }

    // Ревью Глеба (minor): билдер вызывается из catch (ComfyException) адаптеров; если бросит,
    // исключение выйдет наружу и опрос роняет. Битый снимок (null-коллекции) → null, не исключение
    [Fact]
    public void БитыйСнимок_BuildНеБросает()
    {
        var h = Build();
        SetSnapshot(h.Monitor, new ComfyQueueMonitorSnapshot(false, null, null, null, null, null, null));

        var gpu = h.Builder.Build("unknown-ticket", 1);

        gpu.Should().BeNull("битый снимок — null, а не исключение");
    }

    // Снимок ставится через reflection: у монитора private set
    private static void SetSnapshot(ComfyQueueMonitor monitor, ComfyQueueMonitorSnapshot snap)
    {
        var prop = typeof(ComfyQueueMonitor).GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        prop.SetValue(monitor, snap);
    }
}
