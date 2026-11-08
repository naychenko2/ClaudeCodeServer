using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Батчинг бэкфилла цен fal (шаг S-SPEND-5): Track ставит id в очередь, один разборщик шлёт
// пачку до 50 request_id одним запросом; ответ раскладывается по request_id событий.
// Коллекция ProcessGlobalState: часть тестов подменяет Console.Error на свой прогон
// (сбойный лог, проверка журнальных строк) — сериализованы с прочими такими же тестами.
[Collection(TestCollections.ProcessGlobalState)]
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

    // Счётчик событий с ожиданием порога без опроса и сна: тест ждёт события, а не времени.
    private sealed class Signal
    {
        private readonly object _gate = new();
        private readonly List<(int N, TaskCompletionSource Tcs)> _waiters = [];
        private int _count;

        public void Hit()
        {
            lock (_gate)
            {
                _count++;
                foreach (var w in _waiters.Where(w => _count >= w.N).ToList())
                {
                    w.Tcs.TrySetResult();
                    _waiters.Remove(w);
                }
            }
        }

        // true — событие наступило n раз; false — дедлайн вышел раньше.
        public async Task<bool> WaitAsync(int n)
        {
            TaskCompletionSource tcs;
            lock (_gate)
            {
                if (_count >= n) return true;
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((n, tcs));
            }
            try
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class Rig
    {
        public required FalCostService Svc { get; init; }
        public ConcurrentDictionary<string, int> Published { get; } = new();
        public ConcurrentDictionary<string, double> Usd { get; } = new();
        public Signal Distinct { get; } = new();

        public Task<bool> WaitAsync(int distinct) => Distinct.WaitAsync(distinct);
    }

    // Журнал-перехватчик Console.Error: пишет в буфер и сигналит на строках с маркером; с throwOnMarker
    // ещё и роняет запись (сломанный лог бьёт ровно в реальные точки возврата, хвосты ревью R-S-SPEND-5).
    private sealed class MarkedLog(Signal hits, string[] markers, string[]? throwOn = null) : StringWriter
    {
        public override void Write(string? value)
        {
            if (value is not null && markers.Any(m => value.Contains(m))) hits.Hit();
            if (value is not null && throwOn is not null && throwOn.Any(m => value.Contains(m)))
                throw new IOException("сбой журнала");
            base.Write(value);
        }
    }

    // Писатель Console.Error, роняющий исключение на строках с маркером: моделирует сломанный
    // лог — ту самую точку, где Reschedule бросает в реальности (хвост ревью R-S-SPEND-5, m1).
    private sealed class ThrowingLog(TextWriter inner, params string[] markers) : StringWriter
    {
        public override void Write(string? value)
        {
            if (value is not null && markers.Any(m => value.Contains(m))) throw new IOException("сбой журнала");
            inner.Write(value);
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
            if (rig.Published.TryAdd(cost.RequestId, 1)) rig.Distinct.Hit();
            else rig.Published.AddOrUpdate(cost.RequestId, 1, (_, n) => n + 1);
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
        var dropped = new Signal();
        var err = new MarkedLog(dropped, ["Стоимость не появилась за отведённое время: req-never"]);
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            foreach (var id in new[] { "req-0", "req-1", "req-2", "req-3", "req-4" })
                rig.Svc.Track("s-1", id, Nov1);
            rig.Svc.Track("s-1", "req-never", Nov1);

            (await rig.WaitAsync(5)).Should().BeTrue("оставшиеся пять публикуются");
            (await dropped.WaitAsync(1)).Should().BeTrue("ненайденный исчерпывает попытки и попадает в журнал");
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
        var dropped = new Signal();
        var err = new MarkedLog(dropped, ["Стоимость не появилась"]);
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            for (var i = 0; i < 5; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);
            (await dropped.WaitAsync(5)).Should().BeTrue("обе попытки исчерпаны: все пять id отброшены");
        }
        finally
        {
            Console.SetError(prevErr);
        }
        handler.Calls.Should().HaveCount(2, "две попытки бэкоффа = два запроса пачки, а не 2×5");
        handler.Calls.Should().OnlyContain(c => c.Length == 5);
        rig.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Отказ429НаПачку_СледующаяПопытка_ЦенаКаждогоIdОпубликована()
    {
        // Хвост ревью R-S-SPEND-5, п. m4: отказ пачки не отменяет цену — id возвращаются в
        // очередь и получают её со второй попытки.
        var handler = new BatchHandler((ids, call, _) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Ok(ids));
        var rig = MakeRig(handler, backoff: [0, 0]);
        for (var i = 0; i < 5; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

        (await rig.WaitAsync(5)).Should().BeTrue("после отказа пачки цена публикуется со второй попытки");
        handler.Calls.Should().HaveCount(2);
        handler.Calls.Should().OnlyContain(c => c.Length == 5);
        for (var i = 0; i < 5; i++)
            rig.Usd[$"req-{i}"].Should().BeApproximately((i + 1) * 0.01, 1e-9);
    }

    [Fact]
    public async Task СбойРазборщикаНаПачке_НесданныеВозвращены_НовыйTrackПолучаетЦену()
    {
        // Хвост ревью R-S-SPEND-5, п. m1: исключение из catch цикла пачек (сбойный лог
        // Reschedule и самого catch) не должно оставить _processorRunning == true.
        // Сломанный лог бьёт ровно в реальные точки: «Опрос … упал» — catch ProcessBatchAsync,
        // «Сбой обработки пачки» — catch цикла пачек; всё остальное пишется обычно.
        // backoff [0,0] — возвратка не исчерпывает попытки и не логирует: несданные пачки
        // честно возвращаются в очередь и получают цену со второй попытки.
        var inner = Console.Error;
        var err = new ThrowingLog(inner, "Опрос billing-events (пачка", "Сбой обработки пачки");
        Console.SetError(err);
        try
        {
            var handler = new BatchHandler((ids, call, _) => call == 1
                ? throw new HttpRequestException("транспорт")
                : Ok(ids));
            var rig = MakeRig(handler, backoff: [0, 0]);
            rig.Svc.MaxBatchSize = 5; // 10 id = две пачки: в момент сбоя вторая ещё несдана
            for (var i = 0; i < 10; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

            (await rig.WaitAsync(10)).Should().BeTrue("несданные после сбоя разборщика возвращены и получили цену");
            rig.Svc.Track("s-1", "req-90", Nov1);

            (await rig.WaitAsync(11)).Should().BeTrue(
                "очередь не теряет id при сбое разборщика: несданные возвращены, новый Track получил цену");
            handler.Calls.Should().HaveCount(4, "отказанная попытка + пачки с возвратом + новый id");
            rig.Published.Values.Should().OnlyContain(n => n == 1, "каждый id опублирован ровно один раз");
        }
        finally
        {
            Console.SetError(inner);
        }
    }

    [Fact]
    public async Task ПагинацияДостигаетMaxPages_ОтказПачкиСВозвратом_AНеУспехСНеполнымНабором()
    {
        // Хвост ревью R-S-SPEND-5, п. m2: предел страниц — отказ пачки (Reschedule), а не
        // «успех» с усечением: неполная сумма занижает цену, а id с непросмотренных страниц
        // терялись бы после бэкоффа. 45 id одной пачкой: каждая страница говорит «has_more» с
        // тем же курсором — конец списка недостижим, обе попытки упираются в предел.
        var handler = new BatchHandler((ids, _, _) => Ok(ids, ",\"has_more\":true,\"next_cursor\":\"c1\""));
        var rig = MakeRig(handler, backoff: [0, 0]);
        var dropped = new Signal();
        var err = new MarkedLog(dropped, ["Стоимость не появилась за отведённое время: req-0"]);
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            for (var i = 0; i < 45; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1); // 45 < MaxBatchSize — одна пачка
            // две попытки по MaxPages страниц + исчерпание бэкоффа: журнал пишется после обоих опросов
            (await dropped.WaitAsync(1)).Should().BeTrue();
        }
        finally
        {
            Console.SetError(prevErr);
        }
        handler.Calls.Should().HaveCount(40, "20 страниц × 2 попытки: усечение — отказ, а не частичная публикация");
        rig.Published.Should().BeEmpty("частичная сумма по id не публикуется");
        err.ToString().Should().Contain("Стоимость не появилась за отведённое время: req-0");
    }

    [Fact]
    public async Task HasMoreБезКурсора_РовноОдинЗапросНаСтраницу_НайденныеОпубликованы()
    {
        // Хвост ревью R-S-SPEND-5, п. m5: has_more=true с пустым next_cursor и повторяющийся
        // курсор — разборщик останавливается на обрыве хвоста, а не крутится до MaxPages.
        var handler = new BatchHandler((ids, call, _) => call switch
        {
            1 => Ok(ids.Take(1), ",\"has_more\":true,\"next_cursor\":\"c2\""),
            2 => Ok(ids.Skip(1), ",\"has_more\":true,\"next_cursor\":\"c2\""), // курсор повторяется
            _ => Ok([], ",\"has_more\":true,\"next_cursor\":\"\""), // has_more без курсора — стоп
        });
        var rig = MakeRig(handler);
        rig.Svc.Track("s-1", "req-1", Nov1);
        rig.Svc.Track("s-1", "req-2", Nov1);

        (await rig.WaitAsync(2)).Should().BeTrue("обрыв хвоста пагинации не теряет найденное");
        handler.Calls.Should().HaveCount(3, "стоп на обрыве хвоста, не цикл до MaxPages");
        rig.Usd["req-1"].Should().BeApproximately(0.02, 1e-9);
        rig.Usd["req-2"].Should().BeApproximately(0.03, 1e-9);
    }

    [Fact]
    public async Task ПачкаСIdПоВремениВызывающего_УходитСОкномПоОбоимВременам()
    {
        // Хвост ревью R-S-SPEND-5, п. m3: окно строется явно по timed-части пачки. v4-id с
        // finishedAtMs — id с известным временем (ResolveGenerationMs берёт время вызывающего),
        // он в пачке с v7-id и обязан попадать в окно наравне с ним.
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var rig = MakeRig(handler);
        var gen2 = Nov1 + 3_600_000;
        rig.Svc.Track("s-1", "req-1", Nov1);
        rig.Svc.Track("s-1", "018f0000-0000-4000-8000-000000000000", gen2);

        (await rig.WaitAsync(2)).Should().BeTrue();
        handler.Calls.Should().HaveCount(1, "оба id с известным временем — одна пачка");
        var (start, end) = WindowOf(handler.Queries[0]);
        var gen1 = DateTimeOffset.FromUnixTimeMilliseconds(Nov1);
        var g2 = DateTimeOffset.FromUnixTimeMilliseconds(gen2);
        gen1.Should().BeOnOrAfter(start).And.BeBefore(end, "время v7-id в окне");
        g2.Should().BeOnOrAfter(start).And.BeBefore(end, "время вызывающего v4-id в окне");
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

        // Новый Track после сбоя получает цену (разборщик жив или поднят заново).
        rig.Svc.Track("s-1", "req-7", Nov1);
        (await rig.WaitAsync(4)).Should().BeTrue("разборщик жив: Track после сбоя получил цену");
    }

    [Fact]
    public async Task ВозвратПачкиВCatchЦикла_СбойВозвратаНеПрерываетЦикл_СледующаяПачкаОбработана()
    {
        // Хвост R-S-SPEND-TAIL-BATCH, п. 2: возврат в catch цикла пачек идёт через SafeReschedule.
        // Журнал роняет запись при отбрасывании id (Reschedule бросает) — без защиты исключение
        // вылетает из цикла, и вторая пачка в этом проходе не опрашивается (4 вызова → 3).
        var dropped = new Signal();
        var err = new MarkedLog(dropped, ["Стоимость не появилась"], throwOn: ["Стоимость не появилась"]);
        var inner = Console.Error;
        Console.SetError(err);
        try
        {
            var handler = new BatchHandler((_, _, _) => throw new HttpRequestException("транспорт"));
            var rig = MakeRig(handler, backoff: [0, 0]);
            rig.Svc.MaxBatchSize = 2; // 4 id = две пачки; обе попытки сбойные
            for (var i = 0; i < 4; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

            (await dropped.WaitAsync(4)).Should().BeTrue("каждый из четырёх id отброшен ровно один раз");
            handler.Calls.Should().HaveCount(4, "две попытки × две пачки: сбой возврата первой пачки не отнимает опрос второй");
            rig.Published.Should().BeEmpty();
        }
        finally
        {
            Console.SetError(inner);
        }
    }

    [Fact]
    public async Task СбойВторойПоловиныПослеДеления400_УжеОпубликованныеНеПубликуютсяПовторно()
    {
        // Хвост R-S-SPEND-TAIL-BATCH, п. 3: пачка делится на 400; первая половина опубликована,
        // вторая бросает наружу — возвращаются только несданные id, а не вся пачка.
        var err = new MarkedLog(new Signal(), [], throwOn: ["Опрос billing-events (пачка"]);
        var inner = Console.Error;
        Console.SetError(err);
        try
        {
            var handler = new BatchHandler((ids, call, _) => call switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.BadRequest),
                3 => throw new HttpRequestException("транспорт"),
                _ => Ok(ids),
            });
            var rig = MakeRig(handler, backoff: [0, 0]);
            for (var i = 0; i < 4; i++) rig.Svc.Track("s-1", $"req-{i}", Nov1);

            (await rig.WaitAsync(4)).Should().BeTrue("вторая половина получила цену со второй попытки");
            rig.Published.Values.Should().OnlyContain(n => n == 1, "цена публикуется ровно один раз на request_id");
        }
        finally
        {
            Console.SetError(inner);
        }
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
