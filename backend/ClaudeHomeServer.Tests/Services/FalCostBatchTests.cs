using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Батчинг бэкфилла цен fal (шаг S-SPEND-5): Track ставит id в очередь, один разборщик шлёт
// пачку до 50 request_id одним запросом; ответ раскладывается по request_id событий.
public class FalCostBatchTests
{
    // Заглушка пачек: помнит id и запрос каждого вызова, ответ — по делегату (id, номер вызова, курсор).
    private sealed class BatchHandler(Func<string[], int, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string[]> Calls { get; } = [];
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var ids = q.GetValues("request_id") ?? [];
            int index;
            lock (Calls)
            {
                Calls.Add(ids);
                Queries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
                index = Calls.Count;
            }
            return Task.FromResult(respond(ids, index, q["cursor"]));
        }
    }

    // Цена события id: (номер в имени + 1) × 0.01 USD — у каждого id своя, сумма проверяема.
    private static long NanoFor(string id) => (long.Parse(id[(id.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture) + 1) * 10_000_000L;

    private static HttpResponseMessage Ok(IEnumerable<string> ids, string tail = "")
    {
        var events = string.Join(",", ids.Select(i =>
            $$"""{"request_id":"{{i}}","cost_estimate_nano_usd":{{NanoFor(i)}}}"""));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"billing_events":[{{events}}]{{tail}}}"""),
        };
    }

    private sealed class Rig
    {
        public required FalCostService Svc { get; init; }
        public ConcurrentDictionary<string, int> Published { get; } = new();
        public ConcurrentDictionary<string, double> Usd { get; } = new();

        public async Task<bool> WaitAsync(int distinct)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                if (Published.Count >= distinct) return true;
                await Task.Delay(20);
            }
            return false;
        }
    }

    private static Rig MakeRig(HttpMessageHandler handler, int[]? backoff = null, Func<string, bool>? throwOn = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(handler));
        var svc = new FalCostService(http.Object, config)
        {
            BackoffSeconds = backoff ?? [0],
            MinRequestIntervalMs = 0,
            CollectWindowMs = 150,
        };
        var rig = new Rig { Svc = svc };
        svc.OnCostResolved = (sid, cost) =>
        {
            rig.Published.AddOrUpdate(cost.RequestId, 1, (_, n) => n + 1);
            rig.Usd[cost.RequestId] = cost.CostUsd;
            if (throwOn?.Invoke(cost.RequestId) == true) throw new InvalidOperationException("сбой публикации");
            return Task.CompletedTask;
        };
        return rig;
    }

    private static readonly long Nov1 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static (DateTimeOffset Start, DateTimeOffset End) WindowOf(string query) => (
        DateTimeOffset.Parse(Regex.Match(query, @"start=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(Regex.Match(query, @"end=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture));

    [Fact]
    public async Task Бэкфилл120Генераций_ТриЗапросаВМаксимум50Id_ЦенаКаждойОпубликованаОдинРаз()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler);

        for (var i = 0; i < 120; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

        (await rig.WaitAsync(120)).Should().BeTrue("цена каждой из 120 генераций публикуется поштучно");
        handler.Calls.Should().HaveCount(3, "ceil(120/50)");
        handler.Calls.Should().OnlyContain(c => c.Length <= 50);
        rig.Published.Values.Should().OnlyContain(n => n == 1, "ровно один вызов OnCostResolved на id");
        for (var i = 0; i < 120; i++)
            rig.Usd[$"req-{i}"].Should().BeApproximately((i + 1) * 0.01, 1e-9, "сумма из событий именно этого id");
    }

    [Fact]
    public async Task ОкноПачки_РазбросВремени200Дней_ВЛимитеИВремяКаждогоIdВнутриОкна()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler);
        var gens = new Dictionary<string, long>();
        for (var i = 0; i < 20; i++) // шаг 10 дней: 0..190 дней от старой генерации
        {
            var id = $"req-{i}";
            gens[id] = Nov1 - (200L - i * 10) * 86_400_000;
            rig.Svc.Track("s-1", id, gens[id]);
        }
        rig.Svc.Track("s-1", "req-100", null);
        rig.Svc.Track("s-1", "req-101", null);

        (await rig.WaitAsync(22)).Should().BeTrue();
        handler.Calls.Should().HaveCountGreaterThan(2);
        for (var k = 0; k < handler.Calls.Count; k++)
        {
            var ids = handler.Calls[k];
            if (ids.All(i => !gens.ContainsKey(i)))
            {
                handler.Queries[k].Should().NotContain("start=").And.NotContain("end=", "id без времени — отдельный запрос без окна");
                continue;
            }
            ids.Should().OnlyContain(i => gens.ContainsKey(i), "id без времени не смешиваются с id с окном");
            var (start, end) = WindowOf(handler.Queries[k]);
            (end - start).TotalDays.Should().BeLessThanOrEqualTo(89, "окно пачки в лимите fal");
            foreach (var id in ids)
            {
                var g = DateTimeOffset.FromUnixTimeMilliseconds(gens[id]);
                g.Should().BeOnOrAfter(start).And.BeBefore(end, "время генерации id ∈ [start, end)");
            }
        }
        handler.Calls.Should().Contain(c => c.Length == 2 && c.Contains("req-100") && c.Contains("req-101"));
    }

    [Fact]
    public async Task НеполныйОтвет_НайденныеПубликуютсяСразу_ОстальныеНаСледующейПопытке_НеНайденныйНикогдаЛогируется()
    {
        // 5 id: первая попытка отдаёт 3, остальные 2 — со второй; req-never не появляется вовсе.
        var handler = new BatchHandler((ids, call, _) => call == 1
            ? Ok(ids.Where(i => i is "req-0" or "req-1" or "req-2"))
            : Ok(ids.Where(i => i != "req-never")));
        var rig = MakeRig(handler, backoff: [0, 0]);
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            foreach (var id in new[] { "req-0", "req-1", "req-2", "req-3", "req-4" })
                rig.Svc.Track("s-1", id, Nov1);
            rig.Svc.Track("s-1", "req-never", Nov1);

            (await rig.WaitAsync(5)).Should().BeTrue("оставшиеся пять публикуются");
            await Task.Delay(500); // дать исчерпать попытки ненайденному
        }
        finally
        {
            Console.SetError(prevErr);
        }

        handler.Calls[0].Should().HaveCount(6);
        handler.Calls[1].Should().BeEquivalentTo(new[] { "req-3", "req-4", "req-never" }, "повтор — только ненайденные");
        rig.Published.Keys.Should().NotContain("req-never");
        err.ToString().Should().Contain("Стоимость не появилась за отведённое время: req-never");
    }

    [Fact]
    public async Task ОтветИзНесколькихСтраниц_СледующийЗапросНесётCursor_IdСоВторойСтраницыОпубликованы()
    {
        var handler = new BatchHandler((ids, call, cursor) => cursor is null
            ? Ok(ids.Take(1), ",\"has_more\":true,\"next_cursor\":\"c2\"")
            : Ok(ids.Skip(1), ",\"has_more\":false,\"next_cursor\":null"));
        var rig = MakeRig(handler);
        rig.Svc.Track("s-1", "req-1", Nov1);
        rig.Svc.Track("s-1", "req-2", Nov1);

        (await rig.WaitAsync(2)).Should().BeTrue("id со второй страницы тоже найден");
        handler.Calls.Should().HaveCount(2, "две страницы одной пачки");
        handler.Queries[1].Should().Contain("cursor=c2");
    }

    [Fact]
    public async Task Ответ400НаЗапросСIdBad_ОстальныеIdТойЖеПачкиОпубликованы()
    {
        var handler = new BatchHandler((ids, _, _) => ids.Contains("bad-0")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : Ok(ids));
        var rig = MakeRig(handler, backoff: [0]);
        foreach (var id in new[] { "req-1", "req-2", "bad-0", "req-4", "req-5" })
            rig.Svc.Track("s-1", id, Nov1);

        (await rig.WaitAsync(4)).Should().BeTrue("четыре валидных id опубликованы");
        rig.Published.Keys.Should().BeEquivalentTo("req-1", "req-2", "req-4", "req-5");
        handler.Calls.Should().Contain(c => c.Length == 1 && c[0] == "bad-0", "невалидный id изолирован до одиночного запроса");
    }

    [Fact]
    public async Task Отказ429НаПачку_ОднаПопыткаНаПачку_НеПоОднойНаId()
    {
        var handler = new BatchHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var rig = MakeRig(handler, backoff: [0, 0]);
        for (var i = 0; i < 5; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

        await Task.Delay(1500);
        handler.Calls.Should().HaveCount(2, "две попытки бэкоффа = два запроса пачки, а не 2×5");
        handler.Calls.Should().OnlyContain(c => c.Length == 5);
        rig.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ВосемьПотоковПо50Id_КаждыйИз400ОпубликованРовноОдинРаз()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) rig.Svc.Track("s-1", $"req-{t * 50 + i}", Nov1);
        })));

        (await rig.WaitAsync(400)).Should().BeTrue("потерянных id нет");
        await Task.Delay(300);
        rig.Published.Should().HaveCount(400);
        rig.Published.Values.Should().OnlyContain(n => n == 1, "ровно один раз");
        handler.Calls.SelectMany(c => c).Should().OnlyHaveUniqueItems("id не уходят в fal дважды");
    }

    [Fact]
    public async Task СбойПубликацииНаОдномId_ОстальныеОпубликованыИРазборщикЖив()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler, throwOn: id => id == "req-1");
        foreach (var id in new[] { "req-0", "req-1", "req-2" }) rig.Svc.Track("s-1", id, Nov1);

        (await rig.WaitAsync(3)).Should().BeTrue("исключение на req-1 не мешает req-0 и req-2");

        // Разборщик, отработав пачку, уснул; новый Track после сбоя получает цену.
        await Task.Delay(300);
        rig.Svc.Track("s-1", "req-7", Nov1);
        (await rig.WaitAsync(4)).Should().BeTrue("разборщик жив: Track после сбоя получил цену");
    }

    [Fact]
    public async Task ЖивойОпрос_ИдётОдиночнымЗапросомМимоОчереди()
    {
        // Инвариант шага: живой путь не батчится и находит цену независимо от очереди бэкфилла.
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler);
        rig.Svc.Track("s-1", "req-0", Nov1);

        var usd = await rig.Svc.ResolveUsdAsync("req-5", CancellationToken.None);

        usd.Should().BeApproximately(0.06, 1e-9);
        handler.Calls.Should().Contain(c => c.Length == 1 && c[0] == "req-5");
    }
}
