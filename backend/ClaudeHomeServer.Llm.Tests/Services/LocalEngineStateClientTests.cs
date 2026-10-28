using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Клиент /state арбитра GPU (LocalEngineStateClient): кэш 1 с и single-flight, отказ —
// последний удачный снимок с reachable:false, часы арбитра пересчитываются на нашу ось,
// тело/причины/аренды — недоверенные (потолки, обрезка всех причин на входе, обнуление поля неверного типа).
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
                            var bytes = Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentType = "application/json";
                            if (delay > 0)
                            {
                                // Заголовки и первая часть — сразу, остальное позже: меряется бюджет
                                // медленного ТЕЛА, а не медленных заголовков.
                                var cut = Math.Min(512, bytes.Length);
                                await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, cut));
                                await Task.Delay(delay);
                                await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(cut));
                            }
                            else
                            {
                                // WriteAsync отдаёт тело частями: если клиент ушёл рано (потолок 64 КБ
                                // или бюджет), хвост не дописывается — это меряют тесты потребления.
                                await ctx.Response.OutputStream.WriteAsync(bytes);
                            }
                            ctx.Response.Close();
                        }
                        catch { ClientLeftEarly = true; } // клиент ушёл: хвост тела не дописан.
                    });
                }
            });
        }

        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        // Клиент закрыл соединение до конца тела: хвост не был отправлен (потолок/бюджет сработали).
        public bool ClientLeftEarly { get; private set; }
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

    // MAJOR-2: отмена одного вызывающего не даёт второму ложный reachable:false:
    // общий запрос ведётся без ct вызывающего. Первый — OperationCanceledException,
    // второй — живой снимок. Мутация — вернуть ct в сетевой запрос: второй получает
    // reachable:false, тест краснеет.
    [Fact]
    public async Task CancelOneCaller_OtherGetsLiveSnapshot()
    {
        using var stub = new Stub(_ => (200, GoodBody(), 300)); // задержка, чтобы отмена пришлась в середину запроса
        var client = ClientFor(stub);
        using var cts1 = new CancellationTokenSource();
        var t1 = client.GetAsync(Engine, cts1.Token);
        var t2 = client.GetAsync(Engine);
        cts1.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => t1);
        var second = await t2;
        Assert.NotNull(second);
        Assert.True(second.Reachable);
        Assert.Equal(1, stub.RequestCount);
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

    // Хвост ревью: абсурдные часы арбитра не должны терять весь снимок.
    // `since > ts` (возраст отрицательный) и гигантский возраст (вне диапазона часов)
    // обнуляют только sinceAt; остальное читается. Мутация — старая формула: первый снимок
    // краснеет на NotNull(SinceAt), второй — на Reachable (ArgumentOutOfRangeException).
    [Fact]
    public async Task AbsurdClocks_ZeroOnlySinceAt_SnapshotKept()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var future = "{\"bonsai\":\"yielded\",\"yields\":7,\"ts\":" + nowUnix + ",\"since\":" + (nowUnix + 600) + "}";
        var huge = "{\"bonsai\":\"yielded\",\"yields\":8,\"ts\":1e18,\"since\":1699999990}";
        using var stub = new Stub(n => (200, n == 1 ? future : huge, 0));
        var client = ClientFor(stub);

        var a = await client.GetAsync(Engine);
        Assert.NotNull(a);
        Assert.True(a.Reachable);
        Assert.Null(a.SinceAt);
        Assert.Equal(EngineArbiterSnapshot.StateYielded, a.Bonsai);
        Assert.Equal(7, a.Yields);

        await Task.Delay(1100); // окно кэша истекло — будет второй запрос
        var b = await client.GetAsync(Engine);
        Assert.NotNull(b);
        Assert.True(b.Reachable);
        Assert.Null(b.SinceAt);
        Assert.Equal(8, b.Yields);
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

    // B6 (замечание 2 финала): ВСЕ причины режутся на входе единым правилом: «lease:» (и текст
    // аренды) — по-прежнему 64; «gpu:» (pid + командная строка) и «comfy:» (id) — общим потолком 80,
    // командная строка из «gpu:» не уходит в UI админа целиком. Мутация — вернуть старую выборочную
    // обрезку (только «lease:»): Reasons[1] останется целой строкой, тест краснеет на Equal(80, …).
    [Fact]
    public async Task LongReasons_AllTruncatedAtEntry_Lease64_GpuComfy80()
    {
        var longLease = new string('x', 500);
        var gpuReason = "gpu:1234:/usr/bin/python some --long cmd " + new string('y', 100);
        var comfyReason = "comfy:" + new string('c', 200);
        var body = "{\"bonsai\":\"yielded\",\"reasons\":[\"lease:" + longLease + "\",\"" + gpuReason +
            "\",\"" + comfyReason + "\"]," +
            "\"leases\":{\"a\":{\"reason\":\"" + new string('r', 500) + "\",\"left_s\":120.5}}," +
            "\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(64, s.Reasons![0].Length);
        Assert.StartsWith("lease:", s.Reasons[0]);
        Assert.Equal(80, s.Reasons[1].Length);
        Assert.StartsWith("gpu:1234:/usr/bin/python", s.Reasons[1]);
        Assert.Equal(80, s.Reasons[2].Length);
        Assert.StartsWith("comfy:", s.Reasons[2]);
        Assert.Equal(64, s.Leases![0].Reason!.Length);
        Assert.Equal(120.5, s.Leases[0].LeftS);
    }

    // B6 (MAJOR ревью): обрезка не рвёт суррогатную пару. «lease:» + 57 символа + эмодзи —
    // старший суррогат ровно на границе потолка 64 (индекс 63); «gpu:» + 75 символа + эмодзи —
    // на границе 80 (индекс 79). Без правки s[..maxLen] оставляет висячий старший суррогат,
    // и он становится U+FFFD при сериализации. Мутация — вернуть s[..maxLen]: краснеют
    // длина (63/79) и IsHighSurrogate.
    [Fact]
    public async Task SurrogateAtTruncateBoundary_PairNotSplit_NoReplacementCharInJson()
    {
        var emoji = "\U0001F600"; // вне BMP: старший суррогат D83D + младший DE00
        var body = "{\"bonsai\":\"yielded\",\"reasons\":[\"lease:" + new string('x', 57) + emoji +
            "\",\"gpu:" + new string('y', 75) + emoji + "\"],\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        // пара убрана целиком: «lease:»(6) + 57 = 63, «gpu:»(4) + 75 = 79 — граница пары, не потолок.
        Assert.Equal(63, s.Reasons![0].Length);
        Assert.Equal(79, s.Reasons[1].Length);
        Assert.False(char.IsHighSurrogate(s.Reasons[0][^1]));
        Assert.False(char.IsHighSurrogate(s.Reasons[1][^1]));
        // следствие по всей цепочке: в JSON нет U+FFFD — висячий старший суррогат при
        // сериализации становится символом замены U+FFFD (проверено отдельным скретчем:
        // одиночный D83D даёт в JSON U+FFFD, валидная пара — свою пару D83D/DE00).
        // Читаем обратно через JsonDocument: проверка по декодированному значению,
        // а не по регистру escape-последовательности в тексте JSON.
        var json = JsonSerializer.Serialize(s.Reasons);
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
            Assert.False(el.GetString()!.Contains((char)0xFFFD), $"в JSON причины есть U+FFFD: {json}");
    }

    // B6 (хвост ревью): причина БЕЗ префикса (на контроллере kind пустой) тоже режется —
    // общим потолком 80. Мутация — вернуть старую выборочную обрезку (только «lease:»):
    // строка осталась бы длиной 100, тест краснеет на Equal(80, …).
    [Fact]
    public async Task NoPrefixReason_TruncatedAt80()
    {
        var body = "{\"bonsai\":\"yielded\",\"reasons\":[\"" + new string('z', 100) +
            "\"],\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(80, s.Reasons![0].Length);
        Assert.StartsWith(new string('z', 20), s.Reasons[0]);
    }

    // Короткие причины (как их отдаёт живой арбитр: «gpu:<pid>:<cmd≤60>», «comfy:<id≤8>») не меняются —
    // потолок не трогает то, что и так в пределах.
    [Fact]
    public async Task ShortReasons_UnderCaps_Unchanged()
    {
        var body = "{\"bonsai\":\"yielded\",\"reasons\":[\"gpu:4321:/usr/bin/python serve.py --port 8080\"," +
            "\"comfy:1a2b3c4d\",\"lease:ok\"],\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.Equal(
            ["gpu:4321:/usr/bin/python serve.py --port 8080", "comfy:1a2b3c4d", "lease:ok"],
            s.Reasons);
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

    // MAJOR-1 (c1): тело 64 МБ — заведомо больше всех буферов этой машины (tcp_rmem max
    // 32 МБ + tcp_wmem max 4 МБ, проверено по /proc). Клиент читает до потолка 64 КБ и уходит,
    // заглушка физически не может дописать хвост — сигнал детерминичен, без TCP-таймингов.
    // Снимок отвергнут без исключения. 10 МБ для этого не годятся: целиком умещаются в буферы,
    // заглушка успевает дописать тело до ухода клиента (на 10 МБ тест flake'ил: зелёный/красный
    // по везению). Мутация — вернуть ResponseContentRead: клиент буферизует всё тело сам,
    // заглушка дописывает его до конца, тест краснеет на ClientLeftEarly.
    [Fact]
    public async Task Body64Mb_TailNotRead_SnapshotRejected()
    {
        var body = "{\"bonsai\":\"ready\",\"junk\":\"" + new string('a', 64_000_000) + "\"}";
        using var stub = new Stub(_ => (200, body, 0));
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.False(s.Reachable);

        // Заглушка блокируется на хвосте и замечает уход клиента сразу после его закрытия;
        // ждём флаг с потолком, чтобы не прочитать его раньше, чем он родился.
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!stub.ClientLeftEarly && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
        Assert.True(stub.ClientLeftEarly, "клиент дочитал 64 МБ до конца: хвост не остался в соединении");
    }

    // MAJOR-1 (c1): медленный арбитр отдаёт валидное тело по байту — общий бюджет 800 мс
    // на заголовки и тело обрывает чтение, снимок отвергнут. Без бюджета (client.Timeout
    // при потоковом чтении держит только заголовки) тело дочиталось бы за 1,2 с и было принято.
    [Fact]
    public async Task SlowBody_Budget800ms_CancelsRead_SnapshotRejected()
    {
        var body = "{\"bonsai\":\"ready\",\"reasons\":[\"gpu:ok\"],\"ts\":1700000000,\"since\":1699999990}";
        using var stub = new Stub(_ => (200, body, 1200)); // тело валидное, но позже бюджета
        var s = await ClientFor(stub).GetAsync(Engine);

        Assert.NotNull(s);
        Assert.False(s.Reachable);
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
