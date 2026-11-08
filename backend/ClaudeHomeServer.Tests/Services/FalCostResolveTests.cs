using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контракт шва IFalRequestCost (дефект 22068e9d, шаг S-SPEND-1): ResolveUsdAsync по
// request_id возвращает фактическую цену (USD) либо null, когда биллинга ещё нет —
// без исключения: «цены нет» штатный исход, не ошибка.
// Кэш общего опроса (51a805e56): держится начатый или успешный опрос — параллельные
// запросы одного request_id делят один сетевой цикл. Пустой результат НЕ кэшируется
// (дефект 8f6f9e06): «за окно не пришли» не решает за будущие запросы, и поздний биллинг
// догоняется следующим запросом.
// Бэкофф цикла сжимается до [0] через internal-init FalCostService при создании:
// цикл проверяется без реальных 77с ожидания; производственный дефолт [2,5,10,20,40]
// тестом не меняется и после создания не сдвигается.
// Плюс контракт окна времени и троттлинга опроса (дефект S-SPEND-4): ниже, после
// тестов шва и кэша.
public class FalCostResolveTests
{
    // Заглушка billing-events: отвечает по делегату и помнит последний запрос — чтобы
    // проверить, что опрос идёт именно по переданному request_id.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    // Заглушка с транспортным сбоем: первая попытка — TaskCanceledException (так же бросает
    // HttpClient при своём Timeout на неотменённом ct), дальше — ответ-эталон.
    private sealed class TimeoutThenHandler(HttpResponseMessage ok) : HttpMessageHandler
    {
        private int _attempt;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _attempt++;
            return _attempt == 1
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Транспортный таймаут"))
                : Task.FromResult(ok);
        }
    }

    // Заглушка «обычный успех»: цена 0.123 на любой запрос — для тестов, где важен не ответ,
    // а построенный URL. Ответ новый на каждый запрос (общий экземпляр был бы утилизирован).
    private static StubHandler OkPriceHandler() => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"billing_events":[{"request_id":"x","cost_estimate_nano_usd":123000000}]}
            """),
    });

    // Заглушка счёта запросов: мера «повторный запрос цены не идёт в сеть».
    private sealed class CountingStub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"billing_events":[{"request_id":"req-cache","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000}]}
                    """),
            });
        }
    }

    // Заглушка «поздний биллинг»: первые misses запросов — 404 (события ещё нет), дальше — цена.
    private sealed class LateBillingHandler(int misses, string requestId, long nanoUsd) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _calls++;
            if (_calls <= misses) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"billing_events":[{"request_id":"{{requestId}}","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":{{nanoUsd}}}]}
                    """),
            });
        }
    }

    // Заглушка «опрос ещё идёт»: висит на gate до разрешения, затем 404 — цены нет.
    private sealed class Gated404Handler(TaskCompletionSource<bool> gate) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _calls++;
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static FalCostService MakeService(HttpMessageHandler handler, int[]? backoff = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(handler));
        // Бэкофф сжимается при создании (internal-init): цикл без реальных 77с.
        var svc = new FalCostService(http.Object, config) { BackoffSeconds = backoff ?? [0] };
        svc.MinRequestIntervalMs = 0; // троттлинг сжимаем так же, как бэкофф; где он проверяется — поднимает свой
        return svc;
    }

    // Заглушка с журналом запросов: помнит времена старта и максимальную одновременность —
    // для теста троттлинга. Ответ создаётся на каждый запрос: общий экземпляр был бы
    // утилизирован первым потребителем (using var resp в FetchCostAsync).
    private sealed class TimedHandler : HttpMessageHandler
    {
        private int _inFlight;
        public List<DateTimeOffset> Starts { get; } = [];
        public int MaxConcurrent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            lock (Starts) Starts.Add(DateTimeOffset.UtcNow);
            await Task.Delay(30, ct); // держим «в полёте», чтобы параллельность была видна
            Interlocked.Decrement(ref _inFlight);
            // Цена в ответе нужна, чтобы Track-путь дошёл до публикации (OnCostResolved) —
            // тест бэкфилла ждёт оба завершенных опроса.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"billing_events":[{"request_id":"x","cost_estimate_nano_usd":123000000}]}
                    """),
            };
        }
    }

    [Fact]
    public async Task ResolveUsdAsync_БиллингЕсть_ВозвращаетЦенуПоRequestId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-1","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000,"output_units":1,"unit_price":123000000}]}
                """),
        });
        var svc = MakeService(handler);

        var usd = await svc.ResolveUsdAsync("req-1", CancellationToken.None);

        usd.Should().Be(0.123, "nanoSum/1e9 — та же конверсия, что у публикации стоимости в чат");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("request_id=req-1");
    }

    [Fact]
    public async Task ResolveUsdAsync_БиллингаНет_ВозвращаетNullБезИсключения()
    {
        // 404 — события ещё нет (штатная ранняя попытка).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = MakeService(handler);

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-404", CancellationToken.None);
        await call.Should().NotThrowAsync();
        usd.Should().BeNull();
    }

    [Fact]
    public async Task ResolveUsdAsync_СобытиеБезСтоимости_ВозвращаетNullБезИсключения()
    {
        // Событие биллинга есть, но cost_estimate_nano_usd ещё null — «ждём», не «посчитано».
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-2","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":null}]}
                """),
        });
        var svc = MakeService(handler);

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-2", CancellationToken.None);
        await call.Should().NotThrowAsync();
        usd.Should().BeNull();
    }

    [Fact]
    public async Task ResolveUsdAsync_ОтменённыйCt_ВозвращаетNullБезИсключения()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"billing_events":[]}"""),
        });
        var svc = MakeService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        double? usd = null;
        Func<Task> call = async () => usd = await svc.ResolveUsdAsync("req-cancel", cts.Token);
        await call.Should().NotThrowAsync("отмена вызывающего — штатный исход шва, а не исключение");
        usd.Should().BeNull();
        handler.LastRequest.Should().BeNull("отменённый цикл выходит до обращения к транспорту");
    }

    [Fact]
    public async Task ResolveUsdAsync_ТранспортныйТаймаут_ПродолжаетЦиклИВозвращаетЦену()
    {
        // Таймаут транспорта (ct не отменён) — не отмена вызывающего: цикл жив, следующая
        // попытка находит цену. Две попытки с нулевым бэкоффом — чтобы не ждать реальных 77 с.
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-3","endpoint_id":"fal-ai/flux/schnell","cost_estimate_nano_usd":123000000}]}
                """),
        };
        var svc = MakeService(new TimeoutThenHandler(ok), [0, 0]);

        var usd = await svc.ResolveUsdAsync("req-3", CancellationToken.None);

        usd.Should().Be(0.123, "таймаут первой попытки не убивает цикл: цена со второй попытки");
    }

    [Fact]
    public async Task ResolveUsdAsync_ПовторныйЗапросТойжеЦены_ТотЖеРезультатБезВторойСети()
    {
        var handler = new CountingStub();
        var svc = MakeService(handler);

        var first = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);
        var second = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);

        first.Should().Be(0.123);
        second.Should().Be(first, "повторный запрос цены обслуживается из общего task опроса, а не из нового цикла");
        handler.Calls.Should().Be(1, "второй запрос не пошёл в billing-events");
    }

    [Fact]
    public async Task TrackИResolveUsdAsync_ОдномуRequestId_ОдинСетевойЦиклНаДвоих()
    {
        // Сценарий ревью d524e017: чат уже опрашивает request_id через Track, потребитель
        // (Images/ImageEditor) приходит за той же ценой — берёт тот же результат,
        // а не начинает второй 77-секундный опрос.
        var handler = new CountingStub();
        var svc = MakeService(handler);
        var published = new TaskCompletionSource<FalCostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnCostResolved = (_, msg) =>
        {
            published.TrySetResult(msg);
            return Task.CompletedTask;
        };

        svc.Track("s-1", "req-cache", null);
        var usd = await svc.ResolveUsdAsync("req-cache", CancellationToken.None);

        usd.Should().Be(0.123);
        var msg = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.CostUsd.Should().Be(0.123, "чат получил ту же цену из общего опроса");
        handler.Calls.Should().Be(1, "опрос чата и потребитель делят один task: второго сетевого цикла нет");
    }

    [Fact]
    public async Task ResolveUsdAsync_ПослеИсчерпанногоОкна_ПозднийБиллингДогоняетсяВторымЗапросом()
    {
        // Дефект 8f6f9e06: первый опрос исчерпал окно (цена ещё не пришла). Его null не имеет
        // права решать за все будущие запросы — событие биллинга у fal могло появиться позже окна.
        var handler = new LateBillingHandler(1, "req-late", 123000000);
        var svc = MakeService(handler);

        var first = await svc.ResolveUsdAsync("req-late", CancellationToken.None);
        var second = await svc.ResolveUsdAsync("req-late", CancellationToken.None);

        first.Should().BeNull("окно опроса исчерпано — цены ещё нет");
        second.Should().Be(0.123, "пустой результат не кэшируется: поздний биллинг догоняется");
        handler.Calls.Should().Be(2, "второй запрос по тому же request_id пошёл в billing-events заново");
    }

    [Fact]
    public async Task ResolveUsdAsync_ПокаОпросИдёт_ВторойЗапросЖдётЕгоАНеНачинаетСвой()
    {
        // Дедуп параллельных опросов новый порядок не трогает: пока общий task не завершён,
        // второй потребитель приходит к нему. Здесь общий опрос кончается null — и он один.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Gated404Handler(gate);
        var svc = MakeService(handler);

        var first = svc.ResolveUsdAsync("req-gate", CancellationToken.None);
        var second = svc.ResolveUsdAsync("req-gate", CancellationToken.None);
        gate.TrySetResult(true);

        (await first).Should().BeNull();
        (await second).Should().BeNull();
        handler.Calls.Should().Be(1, "оба запроса ждали одного сетевого цикла, а не начали по своему");
    }

    // —— Окно времени в запросе (дефект S-SPEND-4, доработка R-S-SPEND-4): без start
    // billing-events отдаёт только последние 24 ч (доки fal: start по умолчанию = −24ч) —
    // генерации старше суток бэкфилл не находил никогда. start = время генерации − 1ч запас;
    // end — явный и строится из времени генерации (start + 24ч): без него дефолт API «сейчас»
    // делает диапазон шире лимита fal (90 дней на запрос) для старых генераций — те отвергались
    // валидацией; а end из «нашего сейчас» при отставании наших часов оставляет свежую
    // генерацию за верхней границей и может инвертировать окно (дельта-ревью R-S-SPEND-4b, п.5). ——

    // Конструктор UUIDv7 по времени: первые 48 бит — Unix ms (RFC 9562), версия — '7' в
    // начале третьей группы. Для тестов границ разбора: id с заданным временем, v4-id.
    private static string UuidV7WithTime(long ms)
    {
        var hex = ms.ToString("x12", CultureInfo.InvariantCulture);
        return $"{hex[..8]}-{hex[8..12]}-7000-8000-000000000000";
    }

    [Fact]
    public async Task TrackСВременемВызывающего_ЗапрашиваетОкноОтГенерации()
    {
        // request_id «req-old» — не UUID: окно строится по времени вызывающего,
        // start = время генерации − 1ч (длительность генерации + ход часов).
        var tcs = new TaskCompletionSource();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"req-old","cost_estimate_nano_usd":123000000}]}
                """),
        });
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        svc.Track("s-1", "req-old", genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        query.Should().Contain("request_id=req-old");
        query.Should().Contain("start=2026-11-01T11:00:00Z", "start = время генерации − 1ч запас");
    }

    [Fact]
    public async Task ResolveUsdAsyncПоUuidv7_СтроитОкноИзВремениИдентификатора()
    {
        // Реальный request_id из разведки f7e8c46d: UUIDv7, первые 48 бит — Unix ms
        // 2026-11-01T10:07:53Z (дата фактической генерации 01.11). Бэкфилл старой истории
        // получает окно из id, не завися от FinishedAt в записи (его там может не быть).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"01a19694-7252-73d0-ac86-2fc2bfe3f09d","cost_estimate_nano_usd":48900000}]}
                """),
        });
        var svc = MakeService(handler);

        var usd = await svc.ResolveUsdAsync("01a19694-7252-73d0-ac86-2fc2bfe3f09d", CancellationToken.None);

        usd.Should().Be(0.0489, "та цена, что дала разведка live-запросом с окном");
        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        query.Should().Contain("start=2026-11-01T09:07:53Z", "start = время из id − 1ч запас");
    }

    [Fact]
    public async Task TrackСЧужимВременемИUuidv7_ОкноБерётсяИзИдентификатора()
    {
        // Время из id — момент создания запроса на стороне fal (без хода часов, раньше
        // прихода результата) — приоритетнее времени вызывающего.
        var tcs = new TaskCompletionSource();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"billing_events":[{"request_id":"01a19694-7252-73d0-ac86-2fc2bfe3f09d","cost_estimate_nano_usd":48900000}]}
                """),
        });
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long wrongMs = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        svc.Track("s-1", "01a19694-7252-73d0-ac86-2fc2bfe3f09d", wrongMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T09:07:53Z", "время из id, а не от вызывающего");
    }

    [Fact]
    public async Task ResolveUsdAsyncБезВремениИНеUuid_ОпрашиваетБезОкна()
    {
        // Ни времени вызывающего, ни v7-id — прежнее поведение: запрос без start
        // (дефолт API −24ч достаточен для свежей генерации).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = MakeService(handler);

        await svc.ResolveUsdAsync("req-plain", CancellationToken.None);

        handler.LastRequest!.RequestUri!.Query.Should().NotContain("start=", "нет времени — нет окна");
        handler.LastRequest!.RequestUri!.Query.Should().NotContain("end=", "нет времени — нет окна");
    }

    [Fact]
    public async Task ResolveUsdAsyncПоСтаромуUuidv7_ОкноСЯвнымEndНеШиреЛимитаFal()
    {
        // Генерация старше 90 дней (вердикт ревью R-S-SPEND-4): без явного end дефолт API —
        // «сейчас», диапазон [ген − 1ч, сейчас) > 90 дней — fal отвергает запрос валидацией,
        // каждая попытка цикла повторяет его, цена не находится никогда. end строится из времени
        // генерации (start + 24ч, дельта-ревью R-S-SPEND-4b): окно много короче лимита и
        // содержит событие (его timestamp — момент запроса, начало окна).
        long genMs = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var id = UuidV7WithTime(genMs);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"billing_events":[{"request_id":"{{id}}","cost_estimate_nano_usd":48900000}]}
                """),
        });
        var svc = MakeService(handler);

        var usd = await svc.ResolveUsdAsync(id, CancellationToken.None);

        usd.Should().Be(0.0489);
        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        query.Should().Contain("start=2025-05-31T23:00:00Z", "start = время из id − 1ч запас");
        var endMatch = Regex.Match(query, @"end=(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)");
        endMatch.Success.Should().BeTrue("end передаётся явно — иначе окно старых генераций отвергается fal");
        var start = DateTimeOffset.Parse("2025-05-31T23:00:00Z", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(endMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        (end - start).TotalHours.Should().BeLessThanOrEqualTo(24.0,
            "окно строится из времени генерации (start + 24ч), а не из «сейчас» (R-S-SPEND-4b, п.5)");
        end.Should().BeAfter(DateTimeOffset.FromUnixTimeMilliseconds(genMs),
            "окно содержит время запроса: событие биллинга (timestamp запроса) внутри окна");
    }

    [Fact]
    public async Task TrackСОтстающимиЧасами_ОкноСтроитсяОтГенерацииАНеОтСейчас()
    {
        // Наши часы отстают от реальных на 2 ч (эквивалент: FinishedAt вызывающего опережает
        // «наше сейчас»). end из «нашего сейчас» оказался бы РАНЬШЕ start — инвертированное
        // окно, цена свежей генерации не находится никогда (дельта-ревью R-S-SPEND-4b, п.5).
        // Окно строится из времени генерации: end > start, событие внутри.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2 * 3_600_000;

        svc.Track("s-1", "req-lag", genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        var startMatch = Regex.Match(query, @"start=(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)");
        var endMatch = Regex.Match(query, @"end=(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)");
        startMatch.Success.Should().BeTrue();
        endMatch.Success.Should().BeTrue();
        var start = DateTimeOffset.Parse(startMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(endMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        end.Should().BeAfter(start, "окно не инвертируется нашими часами: end из времени генерации");
        end.Should().BeAfter(DateTimeOffset.FromUnixTimeMilliseconds(genMs), "окно содержит время генерации");
    }

    // —— Границы разбора UUIDv7 (вердикт ревью R-S-SPEND-4): время из id используется как
    // время генерации только когда правдоподобно (с 2010 по «завтра»); v7 с нулевым/будущим
    // временем и id другой версии — не является временем, окно строится от времени вызывающего. ——

    [Fact]
    public async Task TrackСНеUuid_НоЕстьВремяВызывающего_ОкноСтроитсяОтВызывающего()
    {
        // Base-случай: id не UUID вовсе → окно от времени вызывающего (тот же эталон, что в тесте выше).
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        svc.Track("s-1", "not-a-uuid", genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "нет валидного v7-id — окно от времени вызывающего");
    }

    [Fact]
    public async Task TrackСV7НулевымВременем_ОкноСтроитсяОтВремениВызывающего()
    {
        // v7-id с обнулённым временем (48 бит = 0): «здравый смысл» его отвергает —
        // окно от времени вызывающего, не от 1970 года.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        svc.Track("s-1", UuidV7WithTime(0), genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        query.Should().Contain("start=2026-11-01T11:00:00Z", "v7 с нулевым временем — не время генерации");
        query.Should().NotContain("start=1970", "окно от 1970 года — фильтр правдоподобия снят");
    }

    [Fact]
    public async Task TrackСV7БудущимВременем_ОкноСтроитсяОтВремениВызывающего()
    {
        // v7-id с временем «через два дня» (за запасом «завтра»): правдоподобным не считается —
        // окно от времени вызывающего. Время — относительно «сейчас», а не фиксированная дата:
        // hardcoded 2027-11-01 после 31.10.2027 попал бы в разрешённый диапазон и тест краснел
        // бы сам, без всякой правки кода (дельта-ревью R-S-SPEND-4b, п.7).
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var futureId = UuidV7WithTime(DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds());

        svc.Track("s-1", futureId, genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "v7 с будущим временем — не время генерации");
    }

    [Fact]
    public async Task TrackСV4Id_ОкноСтроитсяОтВремениВызывающего()
    {
        // UUIDv4 (версия «4»): времени в id нет — даже с правдоподобными битами времени,
        // окно строится от времени вызывающего. id собран из ДРУГОГО времени (2015), а не из
        // ожидаемого genMs: иначе проверка версии в разборе не держалась бы тестом — снятие её
        // прочитало бы время из тех же бит, и start совпал бы (дельта-ревью R-S-SPEND-4b, п.6).
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var hex = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds().ToString("x12", CultureInfo.InvariantCulture);
        var v4Id = $"{hex[..8]}-{hex[8..12]}-4000-8000-000000000000";

        svc.Track("s-1", v4Id, genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "v4-id не несёт времени — окно от вызывающего");
    }

    [Fact]
    public async Task TrackСUuidБезРазделителя_ОкноСтроитсяОтВремениВызывающего()
    {
        // id 36 символов, с правдоподобной версией («7») и hex-битами времени, но с сломанным
        // разделителем: это не UUID, время из него не читается. «not-a-uuid» структурную проверку
        // не держит — его hex-парсинг падает и без неё (дельта-ревью R-S-SPEND-4b, п.6). Здесь
        // без структурной проверки время прочиталось бы (01a19694…7252 — валидные биты) и окно
        // строилось бы от id (09:07), а не от вызывающего (11:00) — тест краснел бы на её снятии.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var brokenId = "01a19694-7252-73d0ac862-2fc2bfe3f09d";

        svc.Track("s-1", brokenId, genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "разбитый формат id — не источник времени");
    }

    [Fact]
    public async Task TrackССтарымUuidv7ИзПрошлого_ОкноСтроитсяОтВремениИдентификатора()
    {
        // Нижняя граница «здравого смысла» (2010) тестом не закреплена ни одним id (дельта-ревью
        // R-S-SPEND-4b, п.8): подъём её до 2020 не краснил ничего. Любая реальная генерация fal
        // много позже 2010 — этот тест держит границу на 2011: окно строится из id, а не от
        // времени вызывающего.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var oldId = UuidV7WithTime(genMs);
        long callerMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        svc.Track("s-1", oldId, callerMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2010-12-31T23:00:00Z", "окно из времени id (2011), а не от вызывающего");
    }

    // Заглушка «медленного» биллинга: каждый ответ — через delayMs (держит слот гейта в
    // транспорте), с ценой; FirstStarted — сигнал, что первый запрос вошёл в транспорт.
    private sealed class SlowPricedHandler(int delayMs) : HttpMessageHandler
    {
        public TaskCompletionSource FirstStarted { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            FirstStarted.TrySetResult();
            await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"billing_events":[{"request_id":"x","cost_estimate_nano_usd":123000000}]}
                    """),
            };
        }
    }

    [Fact]
    public async Task БэкфиллОпросыИдутЧерезГейтСИнтервалом_АНеВолной()
    {
        // Два параллельных Track (как всплеск бэкфилла при открытии старого чата):
        // гейт выпускает их по одному, с интервалом между стартами — иначе волной в 429.
        // Путь проверяется через Track: с разделением полос (хвосты R-S-SPEND-4, п.3)
        // интервал — контракт бэкфилла; живая полоса — в тесте ниже.
        var handler = new TimedHandler();
        var svc = MakeService(handler);
        svc.MinRequestIntervalMs = 200;
        var done = new TaskCompletionSource();
        var published = 0;
        svc.OnCostResolved = (sessionId, cost) =>
        {
            if (Interlocked.Increment(ref published) == 2) done.TrySetResult();
            return Task.CompletedTask;
        };

        svc.Track("s-1", "req-a", null);
        svc.Track("s-1", "req-b", null);
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task, "обе публикации состоялись");

        handler.Starts.Should().HaveCount(2);
        handler.MaxConcurrent.Should().Be(1, "запросы сериализованы однослотовым гейтом");
        var gap = (handler.Starts[1] - handler.Starts[0]).TotalMilliseconds;
        gap.Should().BeGreaterThanOrEqualTo(150, "интервал между стартами — заданный 200 мс минус допуск таймера");
    }

    // Заглушка «смешанного» биллинга: запросы с id «req-bf…» — медленные (держат слот гейта в
    // транспорте), прочие — немедленные. BfStarted/BfFinished — сигналы, что бэкфилл в полёте /
    // вышел из него: для теста живой полосы.
    private sealed class MixedHandler(int slowMs) : HttpMessageHandler
    {
        public TaskCompletionSource BfStarted { get; } = new();
        public TaskCompletionSource BfFinished { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var slow = request.RequestUri!.Query.Contains("req-bf");
            if (slow) BfStarted.TrySetResult();
            if (slow) await Task.Delay(slowMs, ct);
            if (slow) BfFinished.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"billing_events":[{"request_id":"x","cost_estimate_nano_usd":123000000}]}
                    """),
            };
        }
    }

    [Fact]
    public async Task ЖивойОпросНеЖдётЗаОчередьюБэкфилла_ИдётСвоейПолосой()
    {
        // Бэкфилл занят: первый запрос держит слот гейта в транспорте (1,5 с), второй стоит за ним
        // с интервалом. Живой запрос (шов ResolveUsdAsync — Images ждёт число для записи траты)
        // идёт своей полосой: завершается, пока бэкфилл ещё в полёте (хвосты ревью R-S-SPEND-4, п.3).
        var handler = new MixedHandler(slowMs: 1500);
        var svc = MakeService(handler);
        svc.MinRequestIntervalMs = 1000;

        svc.Track("s-1", "req-bf1", null);
        (await Task.WhenAny(handler.BfStarted.Task, Task.Delay(5000))).Should().Be(handler.BfStarted.Task,
            "бэкфилл вошёл в транспорт и держит слот гейта");
        svc.Track("s-1", "req-bf2", null); // встаёт в очередь за занятым слотом + интервалом

        var live = svc.ResolveUsdAsync("req-live", CancellationToken.None);
        (await Task.WhenAny(live, Task.Delay(800))).Should().Be(live,
            "живой запрос не ждёт слота и интервала бэкфилла");
        (await live).Should().Be(0.123);
        handler.BfFinished.Task.IsCompleted.Should().BeFalse(
            "бэкфилл ещё в транспорте — живой опрос его не ждал");
    }

    [Fact]
    public async Task СкачокЧасовНазад_БэкфиллЖдётНеБольшеПаузыИНеТратитПопытку()
    {
        // Предыдущий запрос записан «будущими» часами (скачок часов назад больше 24,8 суток):
        // minStart − now не влезает в int, каст (int) даёт отрицательное число, Task.Delay бросает
        // ArgumentOutOfRangeException, общий catch съедает его — и при сжатом бэкофлле [0] опрос
        // заканчивается без цены (хвосты ревью R-S-SPEND-4, п.1). Ждать положено ровно паузу
        // между стартами: попытка живёт, публикация приходит.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.MinRequestIntervalMs = 200;
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        svc._lastRequestAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 31L * 86_400_000;

        svc.Track("s-1", "req-jump", null);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task,
            "скачок часов не съедает попытку: ожидание ограничено паузой, опрос продолжается");
    }


    [Fact]
    public async Task ОтменаВОчередиГейта_НеОставляетЗанятыйСлотИНеЛомаетНачатыйОпрос()
    {
        // Вердикт ревью R-S-SPEND-4: «ОтменённыйCt» отменяет токен до вызова — до гейта дело
        // не доходит. Здесь — новый путь: слот занят чужим запросом (в транспорте), вызывающий
        // стоит в очереди WaitAsync и отменяется в ней. Инвариант: отмена в очереди не оставляет
        // занятый слот и не съедает интервал — следующий опрос проходит сразу после Release.
        // Путь — через живую полосу (она тоже однослотовая): инвариант держит обе полосы.
        var handler = new SlowPricedHandler(200);
        var svc = MakeService(handler);
        using var cts = new CancellationTokenSource();

        var a = svc.ResolveUsdAsync("req-hold", CancellationToken.None);
        (await Task.WhenAny(handler.FirstStarted.Task, Task.Delay(5000))).Should().Be(handler.FirstStarted.Task,
            "первый запрос вошёл в транспорт и держит слот гейта");

        var b = svc.ResolveUsdAsync("req-queue", cts.Token);
        await Task.Delay(50); // b стоит в очереди за занятым слотом (первый держит его ~200 мс)
        cts.Cancel();

        (await b).Should().BeNull("отмена в очереди за слотом — штатный исход шва, не исключение");
        (await a).Should().Be(0.123, "отмена стоящего в очереди не рвёт начатый запрос");

        // Гейт не залип: отменённый ожидающий не оставил занятый слот — следующий опрос проходит.
        var c = svc.ResolveUsdAsync("req-after", CancellationToken.None);
        (await Task.WhenAny(c, Task.Delay(3000))).Should().Be(c,
            "отмена в очереди не оставила занятый слот: следующий опрос не ждёт залипания");
        (await c).Should().Be(0.123);
    }
}
