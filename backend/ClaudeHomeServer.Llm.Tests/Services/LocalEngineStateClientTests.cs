using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Клиент /state арбитра GPU (LocalEngineStateClient): кэш 1 с и single-flight, отказ —
// последний удачный снимок с reachable:false, часы арбитра пересчитываются на нашу ось,
// тело/причины/аренды — недоверенные (потолки, обрезка lease, обнуление поля неверного типа).
// Заглушка арбитра — HttpListener, как в ConsumersHookTests.
public class LocalEngineStateClientTests
{
    private const string Engine = "bonsai";

    private sealed class PlainHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Заглушка /state: handler получает номер запроса (1-based) и отвечает статусом/телом/задержкой.
    private sealed class Stub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _requests = [];
        private readonly object _gate = new();
        public string Url { get; }

        public Stub(Func<int, (int Status, string Body, int DelayMs)> handler)
        {
            var port = new Random().Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    int n;
                    lock (_gate)
                    {
                        _requests.Add(ctx.Request.Url!.PathAndQuery);
                        n = _requests.Count;
                    }
                    var (status, body, delay) = handler(n);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (delay > 0) await Task.Delay(delay);
                            var bytes = Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(bytes);
                            ctx.Response.Close();
                        }
                        catch { /* клиент ушёл по таймауту */ }
                    });
                }
            });
        }

        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        public void Dispose() => _listener.Stop();
    }

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    private static LocalEngineStateClient ClientFor(Stub stub, bool withStateUrl = true)
    {
        var engines = new NamedLocalEngines(new PlainHttpFactory(), Config(new()
        {
            ["LocalEngines:bonsai:BaseUrl"] = "http://127.0.0.1:1",
            ["LocalEngines:bonsai:Model"] = "bonsai-9b",
            ["LocalEngines:bonsai:StateUrl"] = withStateUrl ? stub.Url + "/state" : null,
        }), NullLoggerFactory.Instance);
        return new LocalEngineStateClient(engines, new PlainHttpFactory());
    }

    // Тело как у живого арбитра (gpu-arbiter.py, do_GET /state): ts/since — его часы.
    private static string GoodBody(long ts = 1_700_000_000, long since = 1_699_999_900) =>
        "{\"bonsai\":\"yielded\",\"reasons\":[\"lease:lora-train\"],\"since\":" + since +
        ",\"yields\":3,\"returns\":2,\"last_yield_s\":0.4,\"last_return_s\":1.2," +
        "\"mode\":\"exclusive\",\"quiet_left_s\":null," +
        "\"leases\":{\"id1\":{\"reason\":\"lora-train\",\"left_s\":120.5}},\"ts\":" + ts + "}";

    // c1: два параллельных вызова в пределах 1 с → один HTTP-запрос (оба получают тот же снимок).
    [Fact]
    public async Task SingleFlight_TwoParallelCalls_OneHttpRequest()
    {
        using var stub = new Stub(_ => (200, GoodBody(), 300)); // задержка, чтобы второй вызов успел присоединиться
        var client = ClientFor(stub);
        var t1 = client.GetAsync(Engine);
        var t2 = client.GetAsync(Engine);
        var both = await Task.WhenAll(t1, t2);

        Assert.Equal(1, stub.RequestCount);
        Assert.True(both[0]!.Reachable);
        Assert.Same(both[0], both[1]);
    }

    // Кэш 1 с: второй опрос сразу после удачного — без нового запроса.
    [Fact]
    public async Task Cache_SecondCallWithinOneSecond_NoNewRequest()
    {
        using var stub = new Stub(_ => (200, GoodBody(), 0));
        var client = ClientFor(stub);
        var first = await client.GetAsync(Engine);
        var second = await client.GetAsync(Engine);

        Assert.Equal(1, stub.RequestCount);
        Assert.Equal(EngineArbiterSnapshot.StateYielded, first!.Bonsai);
        Assert.Same(first, second);
    }

    // c1: арбитр недоступен до первого успеха — reachable:false и все поля null.
    [Fact]
    public async Task Unreachable_BeforeFirstSuccess_AllFieldsNull()
    {
        using var stub = new Stub(_ => (503, "", 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.False(s.Reachable);
        Assert.Null(s.Bonsai);
        Assert.Null(s.Reasons);
        Assert.Null(s.SinceAt);
        Assert.Null(s.Yields);
        Assert.Null(s.Returns);
        Assert.Null(s.Mode);
        Assert.Null(s.Leases);
        Assert.Null(s.SeenAt);
    }

    // c1: после удачного снимка отказ — те же поля последнего удачного, reachable:false.
    [Fact]
    public async Task Unreachable_AfterSuccess_LastSnapshotKept()
    {
        using var stub = new Stub(n => n == 1 ? (200, GoodBody(), 0) : (503, "", 0));
        var client = ClientFor(stub);
        var good = await client.GetAsync(Engine);
        await Task.Delay(1100); // окно кэша истекло — будет новый, неудачный запрос
        var bad = await client.GetAsync(Engine);

        Assert.True(good!.Reachable);
        Assert.False(bad!.Reachable);
        Assert.Equal(2, stub.RequestCount);
        Assert.Equal(EngineArbiterSnapshot.StateYielded, bad.Bonsai);
        Assert.Equal(3, bad.Yields);
        Assert.Equal(good.SeenAt, bad.SeenAt);
        Assert.Equal(good.SinceAt, bad.SinceAt);
    }

    // c2: часы арбитра сдвинуты на +600 с — возраст состояния берётся по его часам (ts − since),
    // sinceAt на нашей оси отличается от ожидаемого не больше чем на 1 с.
    [Fact]
    public async Task ArbiterClockShifted_Plus600s_SinceAtOnOurAxis()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var stub = new Stub(_ => (200, GoodBody(ts: nowUnix + 600, since: nowUnix + 600 - 42), 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        var expected = DateTimeOffset.UtcNow.AddSeconds(-42);
        Assert.True(Math.Abs((s.SinceAt!.Value - expected).TotalSeconds) <= 1.0,
            $"sinceAt {s.SinceAt} вне ±1 с от ожидаемого {expected}");
    }

    // c3: неверный тип поля обнуляет только это поле, остальные читаются.
    [Fact]
    public async Task BadFieldType_ZeroesOnlyThatField()
    {
        var body = "{\"bonsai\":\"ready\",\"reasons\":[\"lease:ok\"],\"yields\":\"oops\",\"returns\":2," +
            "\"mode\":5,\"quiet_left_s\":\"soon\",\"last_yield_s\":0.4,\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.True(s.Reachable);
        Assert.Equal(EngineArbiterSnapshot.StateReady, s.Bonsai);
        Assert.Equal(["lease:ok"], s.Reasons);
        Assert.Null(s.Yields);        // строка вместо числа
        Assert.Equal(2, s.Returns);   // соседнее поле живое
        Assert.Null(s.Mode);          // число вместо строки
        Assert.Null(s.QuietLeftS);
        Assert.Equal(0.4, s.LastYieldS);
    }

    // c3: текст `lease:` (и в причинах, и в арендах) — недоверенный, обрезается до 64 символов;
    // `gpu:`-причина арбитром уже ограничена и остаётся как есть.
    [Fact]
    public async Task LeaseText_TruncatedTo64_OtherReasonsKept()
    {
        var longLease = new string('x', 500);
        var gpuReason = "gpu:1234:/usr/bin/python some --long cmd " + new string('y', 100);
        var body = "{\"bonsai\":\"yielded\",\"reasons\":[\"lease:" + longLease + "\",\"" + gpuReason + "\"]," +
            "\"leases\":{\"a\":{\"reason\":\"" + new string('r', 500) + "\",\"left_s\":120.5}}," +
            "\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(64, s.Reasons![0].Length);
        Assert.StartsWith("lease:", s.Reasons[0]);
        Assert.Equal(gpuReason, s.Reasons[1]);
        Assert.Equal(64, s.Leases![0].Reason!.Length);
        Assert.Equal(120.5, s.Leases[0].LeftS);
    }

    // Тело 100 КБ — отказ без исключения; до первого успеха — reachable:false с null-полями.
    [Fact]
    public async Task BodyOver64Kb_RefusedWithoutException()
    {
        var body = "{\"bonsai\":\"ready\",\"junk\":\"" + new string('a', 100_000) + "\"}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.False(s.Reachable);
        Assert.Null(s.Bonsai);
    }

    // Больше 32 причин — наружу только 32; лишние аренды — так же.
    [Fact]
    public async Task TooManyReasons_Capped32()
    {
        var reasons = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"comfy:{i}\""));
        var leases = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"l{i}\":{{\"reason\":\"r{i}\",\"left_s\":1}}"));
        using var stub = new Stub(_ => (200,
            "{\"bonsai\":\"yielded\",\"reasons\":[" + reasons + "],\"leases\":{" + leases + "}," +
            "\"ts\":1700000000,\"since\":1699999990}", 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(32, s.Reasons!.Count);
        Assert.Equal(32, s.Leases!.Count);
    }

    // Значение `bonsai` вне списка состояний → unknown; `mode` вне exclusive/shared → null.
    [Fact]
    public async Task UnknownBonsai_BecomesUnknown_BadMode_Null()
    {
        using var stub = new Stub(_ => (200,
            "{\"bonsai\":\"dancing\",\"mode\":\"turbo\",\"ts\":1700000000,\"since\":1699999990}", 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(EngineArbiterSnapshot.StateUnknown, s.Bonsai);
        Assert.Null(s.Mode);
    }

    // Нет `StateUrl` или неизвестный движок — null: «арбитра нет», без HTTP-попыток.
    [Fact]
    public async Task NoStateUrl_OrUnknownEngine_ReturnsNull()
    {
        using var stub = new Stub(_ => (200, GoodBody(), 0));
        var client = ClientFor(stub, withStateUrl: false);

        Assert.Null(await client.GetAsync(Engine));
        Assert.Null(await ClientFor(stub).GetAsync("no-such-engine"));
        Assert.Equal(0, stub.RequestCount);
    }

    // `StateUrl` читается из конфига в spec (рядом с ReadyUrl).
    [Fact]
    public void Config_StateUrl_LandsInSpec()
    {
        var engines = new NamedLocalEngines(new PlainHttpFactory(), Config(new()
        {
            ["LocalEngines:bonsai:BaseUrl"] = "http://192.168.7.107:18030",
            ["LocalEngines:bonsai:Model"] = "bonsai-9b",
            ["LocalEngines:bonsai:ReadyUrl"] = "http://192.168.7.107:18031/ready",
            ["LocalEngines:bonsai:StateUrl"] = "http://192.168.7.107:18031/state",
        }), NullLoggerFactory.Instance);

        Assert.Equal("http://192.168.7.107:18031/state", engines.Get(Engine)!.Spec.StateUrl);
    }
}
