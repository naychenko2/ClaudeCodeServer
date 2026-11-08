using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Services;

// Получает ФАКТИЧЕСКИ списанную стоимость генерации fal.ai через Platform API.
// В результате run_model/submit_job есть request_id; по нему billing-events отдаёт
// cost_estimate_nano_usd. Биллинг приходит с задержкой → опрашиваем с бэкоффом.
// Найденную стоимость отдаём наверх делегатом OnCostResolved (его ставит SessionManager).
// Реализует Core-шов IFalRequestCost (дефект 22068e9d, шаг S-SPEND-1): тот же цикл опроса
// доступен потребителям (Images) как ResolveUsdAsync — только числом, без публикаций в чат.
public class FalCostService : IFalRequestCost
{
    // Задержки между попытками (сек). Суммарно ~77с — обычно биллинг готов раньше.
    // internal init для тестов (InternalsVisibleTo): тесты ResolveUsdAsync сжимают бэкофф до [0]
    // при создании — цикл и его контракт проверяются без реальных 77с; производственный дефолт
    // [2, 5, 10, 20, 40] после создания рантайм-код не сдвигает.
    internal int[] BackoffSeconds { get; init; } = [2, 5, 10, 20, 40];

    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly string _billingEventsUrl;
    // Дедуп: один request_id приходит в run_model и в get_job_result — публикуем в чат один раз
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    // Один опрос на request_id: кто первым пришёл — тот его начал, остальные ждут тот же task.
    // Путь чата (Track) и шов (ResolveUsdAsync) делят один сетевой цикл: если чат уже опрашивает
    // этот request_id, потребитель берёт тот же результат, не начиная второй 77-секундный опрос.
    // В кэше живёт НАЧАТЫЙ или УСПЕШНЫЙ опрос. «Цены нет за окно» — не факт о биллинге: событие
    // у fal может появиться позже окна (дефект 8f6f9e06), поэтому пустой результат из кэша
    // убирается и следующий запрос по тому же request_id идёт в сеть заново. Дедуп параллельных
    // опросов это не трогает: пока task не завершён, запись на месте и все приходят к нему.
    private readonly ConcurrentDictionary<string, Polling> _pollings = new();

    // Один опрос по request_id: результат + его полоса. Живой потребитель, пришедший к опросу,
    // уже начатому бэкфиллом, поднимает его полосу до живой (S-SPEND-TAIL-1): сетевой цикл
    // остаётся общим (тот же результат, что требует дедуп), но очередь бэкфилла его не держит.
    private sealed class Polling
    {
        public readonly TaskCompletionSource<FalCostMessage?> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Завершается, когда к опросу пришёл живой потребитель: цикл опроса меняет полосу.
        public readonly TaskCompletionSource<bool> Promised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // volatile: читается циклом опроса на каждой попытке, поднимается чужим потоком.
        public volatile bool Live;
    }

    // Троттлинг опроса (дефект S-SPEND-4): всплески бэкфилла при открытии старых чатов
    // упираются в лимит fal (207 строк «billing-events 429» в журнале проде за 28.10/04–07.11).
    // Бэкфилл (Track) идёт через однослотовый гейт с минимальным интервалом между
    // стартами. Механизм временной, а не по-id: он работает и в свежем процессе (в отличие от
    // in-process дедупа _seen) — новый всплеск после рестарта так же растягивается по интервалу.
    // Живой путь (шов ResolveUsdAsync) — отдельной полосой без интервала: см. _liveGate ниже.
    // internal для тестов (тот же приём, что BackoffSeconds): тесты сжимают интервал до 0.
    internal int MinRequestIntervalMs = 1000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // internal для тестов: моделирует скачок часов назад — предыдущий запрос записан «в будущем».
    internal long _lastRequestAtMs; // Unix ms; 0 — запросов в этом процессе ещё не было

    // Живая полоса (хвосты ревью R-S-SPEND-4, п.3): у ResolveUsdAsync есть потребитель, который ЖДЁТ
    // число (Images записывает трату генерации). Живой запрос не должен стоять в общей очереди за
    // всплеском бэкфилла (задержка до N×(1с+RTT)). Отдельный однослотовый гейт: живые запросы не
    // параллельны между собой, но не ждут интервала бэкфилла и не встают за ним. Механизм интервала
    // (_gate + _lastRequestAtMs) остаётся защитой бэкфилла от 429-всплесков; живой поток по природе
    // мал (один запрос на генерацию) и своей сериализацией защищён от собственной волны.
    // При совпадении request_id с уже начатым бэкфилловым опросом живой потребитель поднимает
    // полосу этого опроса до живой (S-SPEND-TAIL-1): инвариант живой полосы держится и при
    // общем опросе — см. Polling.Promised.
    private readonly SemaphoreSlim _liveGate = new(1, 1);

    // Публикация найденной стоимости (broadcast в SignalR + запись в историю). Ставит SessionManager.
    public Func<string, FalCostMessage, Task>? OnCostResolved { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    public FalCostService(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        _billingEventsUrl = config["Fal:BillingEventsUrl"]
            ?? "https://api.fal.ai/v1/models/billing-events";
        if (Enabled)
            Console.WriteLine("[FalCost] Учёт стоимости fal.ai включён");
    }

    // Запустить отслеживание стоимости по request_id. Идемпотентно, не блокирует вызывающего.
    // finishedAtMs — время генерации от вызывающего (Unix ms, ToolResultMessage.FinishedAt):
    // служит для окна запроса, когда время не читается из самого request_id (см. ResolveGenerationMs).
    public void Track(string sessionId, string requestId, long? finishedAtMs)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return;
        if (!_seen.TryAdd(requestId, 0)) return;
        // Начинаем опрос (или берём уже начатый потребителем через шов); публикация
        // в чат — один раз, по дедупу _seen. finishedAtMs идёт в построитель окна: если
        // время не читается из самого request_id, окно строится от времени генерации (S-SPEND-4).
        var poll = GetOrStartPolling(requestId, finishedAtMs, CancellationToken.None, live: false);
        _ = PublishAsync(sessionId, requestId, poll.Tcs.Task);
    }

    // Реализация шва IFalRequestCost: фактическая цена по request_id либо null — когда
    // биллинга ещё нет. Без исключений: отсутствие цены — штатный исход, не ошибка.
    // Дедуп _seen здесь не участвует: он про однократную публикацию в чат, а не про вопрос.
    // Опрос общий: повторный запрос цены берётся из начатого или уже успешного опроса, не идя
    // в сеть; после пустого — начинается новый (дефект 8f6f9e06).
    public async Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return null;
        var poll = GetOrStartPolling(requestId, null, ct, live: true);
        var cost = await poll.Tcs.Task;
        return cost?.CostUsd;
    }

    // Начать опрос по request_id или вернуть уже начатый — единственная точка входа в цикл.
    // GetOrAdd значением, не фабрикой: на гонке лишнего опроса не возникает вовсе — фабрику
    // вызвал бы каждый проигравший. Полоса (live) и время окна (finishedAtMs) — у первого
    // начавшего: последующие берут тот же task, а не своё окно и не свой гейт.
    private Polling GetOrStartPolling(string requestId, long? finishedAtMs, CancellationToken ct, bool live)
    {
        var own = new Polling { Live = live };
        var existing = _pollings.GetOrAdd(requestId, own);
        if (!ReferenceEquals(existing, own))
        {
            // Опрос уже начат кем-то — берём его результат. Живой потребитель, пришедший к
            // бэкфилловому опросу того же id, поднимает его полосу (S-SPEND-TAIL-1): общий
            // сетевой цикл сохраняется, но очередь бэкфилла его не держит — см. FetchCostAsync.
            if (live && !existing.Live)
            {
                existing.Live = true;
                existing.Promised.TrySetResult(true);
            }
            return existing;
        }
        // Живой старт: own.Live уже истинен — цикл с первой попытки идёт по _liveGate, Promised не нужен.
        _ = FinishPollingAsync(requestId, finishedAtMs, own, ct);
        return own;
    }

    // Завершить свой опрос: найденную цену оставить в кэше (повторные запросы берут её, не идя
    // в сеть), пустой результат — убрать: «за окно не пришли» не решает за будущие запросы,
    // что цены нет вообще. Свой ct решает судьбу только этого общего опроса — следующим
    // запросам путь он не перекрывает.
    private async Task FinishPollingAsync(string requestId, long? finishedAtMs, Polling poll, CancellationToken ct)
    {
        FalCostMessage? cost;
        try
        {
            cost = await PollForCostAsync(requestId, finishedAtMs, ct, poll);
        }
        catch (Exception ex)
        {
            // Страховка: цикл опроса глотает свои сбои сам, но незавершённым task оставить
            // подписчиков нельзя — они ждут результат, а не вечное ожидание.
            Console.Error.WriteLine($"[FalCost] Опрос billing-events ({requestId}) дал неожиданное исключение: {ex.Message}");
            cost = null;
        }
        if (cost is null) _pollings.TryRemove(requestId, out _);
        poll.Tcs.TrySetResult(cost);
    }

    // Единый цикл опроса billing-events (бэкофф → FetchCostAsync по request_id). Начинает его
    // только GetOrStartPolling — путь чата и потребители шва ждут результат одного task.
    // Сбои опроса глотаются, как прежде: «цены нет» → null после исчерпанного окна.
    // Полоса — у poll: живая (шов ResolveUsdAsync, у которого есть ждущий потребитель) или
    // бэкфилловая (Track, очередь с интервалом); флаг может быть поднят живым потребителем уже
    // после начала опроса — см. Polling. См. _liveGate.
    private async Task<FalCostMessage?> PollForCostAsync(string requestId, long? finishedAtMs, CancellationToken ct, Polling poll)
    {
        // Окно времени запроса: без start API отдаёт только последние 24 ч (доки fal: start по
        // умолчанию = −24ч) — генерации старше суток не находились никогда (дефект S-SPEND-4).
        var (startParam, endParam) = BuildWindowParams(ResolveGenerationMs(finishedAtMs, requestId));
        for (var attempt = 0; attempt < BackoffSeconds.Length; attempt++)
        {
            FalCostMessage? cost;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[attempt]), ct);
                cost = await FetchCostAsync(requestId, startParam, endParam, ct, poll);
            }
            // Отмена вызывающего и таймаут транспорта — разные исходы (тот же приём, что в
            // McpProbeService/McpOAuthService): таймаут HttpClient бросает TaskCanceledException
            // при неотменённом ct → следующий catch → continue, цикл жив.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null; // вызывающий отменил запрос — штатный исход, не ошибка
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FalCost] Опрос billing-events ({requestId}) упал: {ex.Message}");
                continue; // цены ещё нет / сбой транспорта — ждём следующую попытку
            }
            if (cost is not null) return cost;
        }
        return null; // окно опроса исчерпано — цены нет
    }

    // Публикация найденной стоимости в чат: ждёт общий опрос, публикует один раз
    // (однократность держит Track своим дедупом _seen до подписки на task).
    private async Task PublishAsync(string sessionId, string requestId, Task<FalCostMessage?> poll)
    {
        var cost = await poll;
        if (cost is null)
        {
            Console.Error.WriteLine($"[FalCost] Стоимость не появилась за отведённое время: {requestId}");
            return;
        }
        try
        {
            if (OnCostResolved is not null)
                await OnCostResolved(sessionId, cost);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalCost] Публикация стоимости ({requestId}) упала: {ex.Message}");
        }
    }

    // Запас к времени вызывающего для start окна: длительность генерации (request_id
    // создаётся до прихода результата) + ход часов. Для v7-id базой служит сам момент
    // создания запроса — запас только страховка.
    private const long StartMarginMs = 3_600_000;

    // Время генерации (Unix ms) для окна запроса. Сначала request_id, если он UUIDv7:
    // его первые 48 бит — Unix ms (RFC 9562). Реальный id из разведки f7e8c46d
    // (01a19694-7252-73d0…) — именно такой: читается как 2026-11-01T10:07:53Z, дата
    // фактической генерации (01.11). Время из id — момент создания запроса на стороне fal,
    // без хода часов и раньше прихода результата, поэтому приоритетнее времени вызывающего.
    // Для id другой формы (v4) — время вызывающего; ни того, ни другого — null, окно не
    // добавляем (прежнее поведение: запрос без окна).
    private static long? ResolveGenerationMs(long? finishedAtMs, string requestId)
        => TryParseUuidV7Ms(requestId) ?? finishedAtMs;

    private static long? TryParseUuidV7Ms(string requestId)
    {
        // UUID 8-4-4-4-12; версия 7 — первая цифра третьей группы.
        if (requestId.Length != 36 || requestId[8] != '-' || requestId[13] != '-'
            || requestId[18] != '-' || requestId[23] != '-' || requestId[14] != '7')
            return null;
        if (!long.TryParse(requestId[..8] + requestId[9..13], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ms))
            return null;
        // Здравый смысл: с 2010 по «завтра» — v7 с бессмысленным временем не является временем.
        const long minMs = 1_262_304_000_000; // 2010-01-01T00:00:00Z
        return ms >= minMs && ms <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 86_400_000 ? ms : null;
    }

    // Ширина окна: 24 ч от start (86 400 000 мс = сутки). Значительно под лимитом fal (90 дней
    // на запрос, доки) и достаточно, чтобы покрыть любое правдоподобное отставание наших часов
    // (см. BuildWindowParams).
    private const long WindowSpanMs = 86_400_000;

    // Окно запроса: start = время генерации − запас; end — ЯВНЫЙ (вердикт ревью R-S-SPEND-4):
    // без end дефолт API — его «сейчас», и для генерации старше ~90 дней диапазон
    // [ген − 1ч, сейчас) превышает лимит fal: запрос отвергается валидацией, каждая попытка
    // цикла повторяет тот же отвергнутый запрос — цена старых генераций не находится никогда.
    // end строится ИЗ ВРЕМЕНИ ГЕНЕРАЦИИ, а не из «наших сейчас» (дельта-ревью R-S-SPEND-4b, п.5):
    // timestamp события в billing-events — время запроса на стороне fal (доки: «Request timestamp»,
    // end исключительный). end = min(наше сейчас, …) при отставании наших часов от реальных
    // больше интервала «запрос → начало опроса» оставляет свежую генерацию ЗА верхней границей
    // (её цена не находится никогда), а при отставании > 1ч окно инвертируется (end < start —
    // вывод пробы M2). end = start + 24 ч: окно вообще не зависит от наших часов и содержит
    // событие при любом их отставании < 23 ч и опережении ≤ 1 ч (start-маргинал): timestamp
    // события — время запроса, оно не позже генерации и не раньше её на длительность генерации.
    // Инвариант: для любой генерации с известным временем запрос к fal содержит
    // событие биллинга этой генерации, а диапазон ≤ 24ч ≪ лимита. Нет времени генерации — окна
    // нет (прежнее поведение: дефолт API −24ч достаточен для свежей генерации).
    private static (string? Start, string? End) BuildWindowParams(long? generationMs)
    {
        if (generationMs is null) return (null, null);
        var startMs = generationMs.Value - StartMarginMs;
        return (FormatUtc(startMs), FormatUtc(startMs + WindowSpanMs));
    }

    private static string FormatUtc(long unixMs) => Uri.EscapeDataString(
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private async Task<FalCostMessage?> FetchCostAsync(string requestId, string? startParam, string? endParam, CancellationToken ct, Polling poll)
    {
        // Троттлинг (MinRequestIntervalMs): однослотовый гейт + минимальный интервал между
        // стартами запросов. Очередь живёт под ct: отмена вызывающего в очереди — штатный выход.
        // Живые запросы (шов ResolveUsdAsync) — своей полосой: не ждут интервала бэкфилла и не
        // встают за его очередью (сериализованы только между собой). См. _liveGate.
        // Живой потребитель, пришедший к уже начатому бэкфилловому опросу того же request_id,
        // поднимает его полосу (S-SPEND-TAIL-1): опрос не дожидается очереди _gate — переходит
        // на живую полосу, сетевой цикл остаётся общим (без второй сети). Подъём полосы до
        // входа в очередь не меняет поведение: живой опрос изначально идёт по _liveGate.
        var live = poll.Live;
        SemaphoreSlim gate;
        if (live)
        {
            gate = _liveGate;
            await _liveGate.WaitAsync(ct);
        }
        else
        {
            var backfillWait = _gate.WaitAsync(ct);
            if (await Task.WhenAny(backfillWait, poll.Promised.Task).ConfigureAwait(false) == backfillWait)
            {
                // Тот же порядок, что был: отмена в очереди выходит из опроса исключением,
                // а не входом в try без занятого слота (иначе Release отдаст чужой).
                // Если живой потребитель пришёл уже после занятия слота — опрос вне очереди:
                // ожидание ограничено интервалом и транспортом этой же попытки, следующая
                // попытка цикла уже пойдёт по живой полосе (флаг poll.Live).
                await backfillWait.ConfigureAwait(false);
                gate = _gate;
            }
            else
            {
                // Живой потребитель ждёт этот опрос — уходим из очереди бэкфилла на живую полосу.
                // Позиция в _gate при этом не остаётся: дождавшийся слот освобождается, иначе
                // гейт залипает. Полоса становится живой и дальше: интервал бэкфилла этот опрос
                // уже не касается — он живой, и _lastRequestAtMs не двигает.
                _ = ReleaseIfAdmittedAsync(backfillWait);
                live = true;
                gate = _liveGate;
                await _liveGate.WaitAsync(ct);
            }
        }
        try
        {
            if (!live)
            {
                var minStart = _lastRequestAtMs + MinRequestIntervalMs;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Ждём ровно паузу между стартами, а не разницу часов (хвосты ревью R-S-SPEND-4, п.1):
                // при скачке часов назад minStart − now больше int (>24,8 сут) — каст (int) даёт
                // отрицательное число, Task.Delay бросает ArgumentOutOfRangeException, общий catch
                // съедает его и попытка цикла потеряна. Верхняя граница — сам интервал: смысл
                // механизма — разнесение стартов, а не выжидание скачка часов.
                var waitMs = Math.Min(minStart - now, (long)MinRequestIntervalMs);
                if (waitMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct);
                _lastRequestAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            var client = _httpFactory.CreateClient("fal");
            // Клиент "fal" имеет свой таймаут регистрации (15 с, Program.cs; хвосты ревью R-S-SPEND-4,
            // п.2): без него дефолт HttpClient 100 с держал бы слот гейта на всё время зависшего
            // запроса. На экземпляре здесь ничего не ставим: HttpClient.Timeout нельзя задать после
            // первого запроса на инстансе (а инстанс может быть переиспользован), и короткий
            // опрос биллинга уже закрыт регистрационным таймаутом.
            var url = $"{_billingEventsUrl}?request_id={Uri.EscapeDataString(requestId)}"
                + (startParam is null ? "" : $"&start={startParam}")
                + (endParam is null ? "" : $"&end={endParam}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 — события ещё нет (нормально на ранних попытках); прочее — логируем
                if (resp.StatusCode != HttpStatusCode.NotFound)
                    Console.Error.WriteLine($"[FalCost] billing-events {(int)resp.StatusCode} для {requestId}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            return ParseBillingEvents(requestId, json);
        }
        finally
        {
            gate.Release();
        }
    }

    // Оставленное ожидание _gate (ушли на живую полосу) может всё же получить слот позже —
    // освободить его, иначе гейт залипает и следующий бэкфилл не проходит никогда.
    // Отменённое ожидание слота не берёт: ничего не освобождать.
    private async Task ReleaseIfAdmittedAsync(Task wait)
    {
        try { await wait.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        _gate.Release();
    }

    private static FalCostMessage? ParseBillingEvents(string requestId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("billing_events", out var events)
            || events.ValueKind != JsonValueKind.Array)
            return null;

        double nanoSum = 0;
        bool any = false;
        string? endpointId = null;
        double? outputUnits = null;
        double? unitPrice = null;
        foreach (var e in events.EnumerateArray())
        {
            // cost_estimate_nano_usd ещё может быть null — тогда событие пропускаем (ждём)
            if (!e.TryGetProperty("cost_estimate_nano_usd", out var nano) || nano.ValueKind != JsonValueKind.Number)
                continue;
            nanoSum += nano.GetDouble();
            any = true;
            endpointId ??= e.TryGetProperty("endpoint_id", out var ep) ? ep.GetString() : null;
            if (outputUnits is null && e.TryGetProperty("output_units", out var ou) && ou.ValueKind == JsonValueKind.Number)
                outputUnits = ou.GetDouble();
            if (unitPrice is null && e.TryGetProperty("unit_price", out var up) && up.ValueKind == JsonValueKind.Number)
                unitPrice = up.GetDouble();
        }
        if (!any) return null;

        return new FalCostMessage(requestId, endpointId, nanoSum / 1_000_000_000d, outputUnits, unitPrice);
    }
}
