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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"billing_events":[]}"""),
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
    // end — явный, min(сейчас, start + 89д): без него дефолт API «сейчас» делает диапазон
    // шире лимита fal (90 дней на запрос) для старых генераций — те отвергались валидацией. ——

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
        // каждая попытка цикла повторяет его, цена не находится никогда. end = min(сейчас,
        // start + 89д): окно остаётся 89 дней и содержит событие (его timestamp — момент запроса).
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
        (end - start).TotalDays.Should().BeLessThanOrEqualTo(90.0, "диапазон не превышает лимит fal (90 дней на запрос)");
        end.Should().BeAfter(DateTimeOffset.FromUnixTimeMilliseconds(genMs),
            "окно содержит время запроса: событие биллинга (timestamp запроса) внутри окна");
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
        // v7-id с временем «через год» (за запасом «завтра»): правдоподобным не считается —
        // окно от времени вызывающего.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var futureId = UuidV7WithTime(new DateTimeOffset(2027, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());

        svc.Track("s-1", futureId, genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "v7 с будущим временем — не время генерации");
    }

    [Fact]
    public async Task TrackСV4Id_ОкноСтроитсяОтВремениВызывающего()
    {
        // UUIDv4 (версия «4»): времени в id нет — даже с правдоподобными битами окна,
        // окно строится от времени вызывающего.
        var tcs = new TaskCompletionSource();
        var handler = OkPriceHandler();
        var svc = MakeService(handler);
        svc.OnCostResolved = (sessionId, cost) => { tcs.TrySetResult(); return Task.CompletedTask; };
        long genMs = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var hex = genMs.ToString("x12", CultureInfo.InvariantCulture);
        var v4Id = $"{hex[..8]}-{hex[8..12]}-4000-8000-000000000000";

        svc.Track("s-1", v4Id, genMs);
        (await Task.WhenAny(tcs.Task, Task.Delay(5000))).Should().Be(tcs.Task, "публикация стоимости ожидаема");

        Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query)
            .Should().Contain("start=2026-11-01T11:00:00Z", "v4-id не несёт времени — окно от вызывающего");
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
    public async Task ОпросыБиллинга_ИдутЧерезГейтСИнтервалом_АНеВолной()
    {
        // Два параллельных запроса (как всплеск бэкфилла при открытии старого чата):
        // гейт выпускает их по одному, с интервалом между стартами — иначе волной в 429.
        var handler = new TimedHandler();
        var svc = MakeService(handler);
        svc.MinRequestIntervalMs = 200;

        var a = svc.ResolveUsdAsync("req-a", CancellationToken.None);
        var b = svc.ResolveUsdAsync("req-b", CancellationToken.None);
        await Task.WhenAll(a, b);

        handler.Starts.Should().HaveCount(2);
        handler.MaxConcurrent.Should().Be(1, "запросы сериализованы однослотовым гейтом");
        var gap = (handler.Starts[1] - handler.Starts[0]).TotalMilliseconds;
        gap.Should().BeGreaterThanOrEqualTo(150, "интервал между стартами — заданный 200 мс минус допуск таймера");
    }

    [Fact]
    public async Task ОтменаВОчередиГейта_НеОставляетЗанятыйСлотИНеЛомаетНачатыйОпрос()
    {
        // Вердикт ревью R-S-SPEND-4: «ОтменённыйCt» отменяет токен до вызова — до гейта дело
        // не доходит. Здесь — новый путь: слот занят чужим запросом (в транспорте), вызывающий
        // стоит в очереди WaitAsync и отменяется в ней. Инвариант: отмена в очереди не оставляет
        // занятый слот и не съедает интервал — следующий опрос проходит сразу после Release.
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
