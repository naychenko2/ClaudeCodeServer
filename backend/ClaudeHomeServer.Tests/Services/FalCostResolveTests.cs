using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контракт шва IFalRequestCost (дефект 22068e9d, шаг S-SPEND-1): ResolveUsdAsync по
// request_id возвращает фактическую цену (USD) либо null, когда биллинга ещё нет —
// без исключения: «цены нет» штатный исход, не ошибка.
// Бэкофф цикла сжимается до [0] через internal-шов FalCostService (тот же приём, что
// internal-хуки FileWatcherService): цикл проверяется без реальных 77с ожидания,
// производственный дефолт [2,5,10,20,40] тестом не меняется.
// Плюс контракт окна времени и троттлинга опроса (дефект S-SPEND-4): ниже, после
// тестов шва.
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

    private static FalCostService MakeService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fal:ApiKey"] = "test-key" })
            .Build();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("fal")).Returns(new HttpClient(handler));
        var svc = new FalCostService(http.Object, config);
        svc.BackoffSeconds = [0];
        svc.MinRequestIntervalMs = 0; // троттлинг сжимаем так же, как бэкофф; где он проверяется — поднимает свой
        svc.CollectWindowMs = 0; // окно сбора пачки сжимаем; тесты батчинга поднимают своё
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
        var svc = MakeService(new TimeoutThenHandler(ok));
        svc.BackoffSeconds = [0, 0];

        var usd = await svc.ResolveUsdAsync("req-3", CancellationToken.None);

        usd.Should().Be(0.123, "таймаут первой попытки не убивает цикл: цена со второй попытки");
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
        // Две пачки (id разнесены по времени дальше окна 89 дней — в одну пачку не влезают),
        // как всплеск бэкфилла при открытии старого чата: гейт выпускает запросы по одному,
        // с интервалом между стартами — иначе волной в 429. Путь — через Track: интервал —
        // контракт бэкфилла; живая полоса — в тесте ниже.
        var handler = new TimedHandler();
        var svc = MakeService(handler);
        svc.MinRequestIntervalMs = 200;
        svc.CollectWindowMs = 100;
        var done = new TaskCompletionSource();
        var published = 0;
        svc.OnCostResolved = (sessionId, cost) =>
        {
            if (Interlocked.Increment(ref published) == 2) done.TrySetResult();
            return Task.CompletedTask;
        };

        svc.Track("s-1", "req-a", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        svc.Track("s-1", "req-b", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
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

    // —— Батчинг бэкфилла (шаг S-SPEND-5): Track ставит id в очередь, один разборщик шлёт
    // пачку до 50 request_id одним запросом; ответ раскладывается по request_id событий. ——

    // Заглушка пачек: помнит id каждого запроса, ответ — по делегату (id, номер вызова, курсор).
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

    private static HttpResponseMessage Ok(IEnumerable<string> ids, string tail = "")
    {
        var events = string.Join(",", ids.Select(i => $$"""{"request_id":"{{i}}","cost_estimate_nano_usd":100000000}"""));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"billing_events":[{{events}}]{{tail}}}"""),
        };
    }

    // Сервис с публикациями в список и сигналом «опубликовано n».
    private static (FalCostService Svc, List<string> Published, Func<int, Task<bool>> Wait) MakeBatchService(
        HttpMessageHandler handler, int[]? backoff = null)
    {
        var svc = MakeService(handler);
        svc.CollectWindowMs = 150;
        if (backoff is not null) svc.BackoffSeconds = backoff;
        var published = new List<string>();
        svc.OnCostResolved = (sid, cost) => { lock (published) published.Add(cost.RequestId); return Task.CompletedTask; };
        async Task<bool> Wait(int n)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                lock (published) if (published.Count >= n) return true;
                await Task.Delay(20);
            }
            return false;
        }
        return (svc, published, Wait);
    }

    private static readonly long Nov1 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Бэкфилл120Генераций_ТриЗапросаВместо120_ЦенаКаждойОпубликована()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var (svc, published, wait) = MakeBatchService(handler);

        for (var i = 0; i < 120; i++) svc.Track("s-1", $"req-{i}", Nov1);

        (await wait(120)).Should().BeTrue("цена каждой из 120 генераций публикуется поштучно");
        handler.Calls.Select(c => c.Length).Should().BeEquivalentTo(new[] { 50, 50, 20 });
        published.Distinct().Should().HaveCount(120);
    }

    [Fact]
    public async Task ГенерацииРазнесеныДальшеОкна_РежутсяНаОтдельныеПачкиСОкномВЛимите()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var (svc, _, wait) = MakeBatchService(handler);
        var old = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var mid = old + 40L * 86_400_000; // в окне 89 дней от old
        svc.Track("s-1", "req-old", old);
        svc.Track("s-1", "req-mid", mid);
        svc.Track("s-1", "req-new", Nov1); // > 89 дней от old

        (await wait(3)).Should().BeTrue();
        handler.Calls.Should().HaveCount(2, "old+mid в одной пачке, new — в другой");
        handler.Calls.Should().Contain(c => c.Length == 2 && c.Contains("req-old") && c.Contains("req-mid"));
        foreach (var q in handler.Queries)
        {
            var start = DateTimeOffset.Parse(Regex.Match(q, @"start=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(Regex.Match(q, @"end=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            (end - start).TotalDays.Should().BeLessThanOrEqualTo(89, "окно пачки не шире лимита fal");
        }
    }

    [Fact]
    public async Task ПачкаУровняГраницыОкна_ВремяКаждогоIdВнутриОкна()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var (svc, _, wait) = MakeBatchService(handler);
        var a = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        svc.Track("s-1", "req-a", a);
        svc.Track("s-1", "req-b", a + 30L * 86_400_000);
        (await wait(2)).Should().BeTrue();

        var q = handler.Queries.Single();
        var start = DateTimeOffset.Parse(Regex.Match(q, @"start=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(Regex.Match(q, @"end=([^&]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        start.Should().BeBefore(DateTimeOffset.FromUnixTimeMilliseconds(a));
        end.Should().BeAfter(DateTimeOffset.FromUnixTimeMilliseconds(a + 30L * 86_400_000), "самая поздняя генерация внутри окна");
    }

    [Fact]
    public async Task IdБезВремени_ИдутОтдельнойПачкойБезОкна()
    {
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var (svc, _, wait) = MakeBatchService(handler);
        svc.Track("s-1", "req-t", Nov1);
        svc.Track("s-1", "req-n1", null);
        svc.Track("s-1", "req-n2", null);

        (await wait(3)).Should().BeTrue();
        handler.Calls.Should().HaveCount(2);
        var untimed = handler.Queries.Single(q => q.Contains("req-n1"));
        untimed.Should().Contain("req-n2").And.NotContain("start=").And.NotContain("end=");
    }

    [Fact]
    public async Task ОтветИзНесколькихСтраниц_ПроходитсяКаждаяСтраница()
    {
        var handler = new BatchHandler((ids, call, cursor) => cursor is null
            ? Ok(ids.Take(1), ",\"has_more\":true,\"next_cursor\":\"c2\"")
            : Ok(ids.Skip(1), ",\"has_more\":false,\"next_cursor\":null"));
        var (svc, published, wait) = MakeBatchService(handler);
        svc.Track("s-1", "req-1", Nov1);
        svc.Track("s-1", "req-2", Nov1);

        (await wait(2)).Should().BeTrue("id со второй страницы тоже найден");
        handler.Calls.Should().HaveCount(2, "две страницы одной пачки");
        handler.Queries[1].Should().Contain("cursor=c2");
        published.Should().BeEquivalentTo("req-1", "req-2");
    }

    [Fact]
    public async Task НеполныйОтвет_НайденныеПубликуютсяСразу_НеНайденныеЖдутСледующейПопытки()
    {
        var handler = new BatchHandler((ids, call, _) => call == 1
            ? Ok(ids.Where(i => i != "req-late"))
            : Ok(ids));
        var (svc, published, wait) = MakeBatchService(handler, backoff: [0, 0]);
        svc.Track("s-1", "req-1", Nov1);
        svc.Track("s-1", "req-late", Nov1);
        svc.Track("s-1", "req-3", Nov1);

        (await wait(3)).Should().BeTrue("ненайденный id найден со второй попытки");
        handler.Calls[0].Should().HaveCount(3);
        handler.Calls[1].Should().BeEquivalentTo(new[] { "req-late" }, "повторно опрашивается только ненайденный");
        published.Take(2).Should().BeEquivalentTo("req-1", "req-3");
    }

    [Fact]
    public async Task Ответ400НаПачку_ДелитПополам_НевалидныйIdНеХоронитОстальные()
    {
        var handler = new BatchHandler((ids, _, _) => ids.Contains("req-bad")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : Ok(ids));
        var (svc, published, wait) = MakeBatchService(handler, backoff: [0]);
        foreach (var id in new[] { "req-1", "req-2", "req-bad", "req-4", "req-5" })
            svc.Track("s-1", id, Nov1);

        (await wait(4)).Should().BeTrue("четыре валидных id опубликованы");
        published.Should().BeEquivalentTo("req-1", "req-2", "req-4", "req-5");
        handler.Calls.Should().Contain(c => c.Length == 1 && c[0] == "req-bad", "невалидный id изолирован до одиночного запроса");
    }

    [Fact]
    public async Task Отказ429НаПачку_ЭтоОднаПопыткаНаПачку_НеПоОднойНаId()
    {
        var handler = new BatchHandler((ids, _, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var (svc, published, _) = MakeBatchService(handler, backoff: [0, 0]);
        for (var i = 0; i < 5; i++) svc.Track("s-1", $"req-{i}", Nov1);

        await Task.Delay(1500);
        handler.Calls.Should().HaveCount(2, "две попытки бэкоффа = два запроса пачки, а не 2×5");
        handler.Calls.Should().OnlyContain(c => c.Length == 5);
        published.Should().BeEmpty();
    }

    [Fact]
    public async Task ПачкаИОдиночныйОпрос_ДаютОдинаковуюЦенуОдногоId()
    {
        // Инвариант шага: цена находится и попадает в учёт независимо от того, была ли генерация в пачке.
        var handler = new BatchHandler((ids, _, _) => Ok(ids));
        var (svc, _, wait) = MakeBatchService(handler);
        svc.Track("s-1", "req-1", Nov1);
        svc.Track("s-1", "req-2", Nov1);
        (await wait(2)).Should().BeTrue();

        var solo = await svc.ResolveUsdAsync("req-live", CancellationToken.None);
        solo.Should().Be(0.1);
    }
}
