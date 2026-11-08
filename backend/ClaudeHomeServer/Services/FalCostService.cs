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
    // internal для тестов (InternalsVisibleTo): тесты ResolveUsdAsync сжимают бэкофф до [0] —
    // цикл и его контракт проверяются без реальных 77с; производственный дефолт не меняется.
    internal int[] BackoffSeconds = [2, 5, 10, 20, 40];

    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly string _billingEventsUrl;
    // Дедуп: один request_id приходит в run_model и в get_job_result — опрашиваем один раз
    private readonly ConcurrentDictionary<string, byte> _seen = new();

    // Троттлинг опроса (дефект S-SPEND-4): всплески бэкфилла при открытии старых чатов
    // упираются в лимит fal (207 строк «billing-events 429» в журнале проде за 28.10/04–07.11).
    // Бэкфилл (Track/PollAsync) идёт через однослотовый гейт с минимальным интервалом между
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

    // Партия бэкфилла (шаг S-SPEND-5): Track ставит id в очередь, один разборщик отправляет в fal
    // пачку до MaxBatchSize id одним запросом (billing-events принимает 1–50 request_id).
    // Бэкфилл чата со 120 генерациями — 3 запроса вместо 120.
    private sealed class PendingCost(string sessionId, string requestId, long? generationMs)
    {
        public string SessionId { get; } = sessionId;
        public string RequestId { get; } = requestId;
        public long? GenerationMs { get; } = generationMs;
        public int Attempt; // индекс следующей попытки в BackoffSeconds
        public long DueAtMs; // Unix ms: когда id снова пора опрашивать
    }

    private readonly object _queueLock = new();
    private readonly List<PendingCost> _queue = [];
    private bool _processorRunning;

    // Размер пачки: лимит fal на параметр request_id — 50 значений.
    internal int MaxBatchSize = 50;
    // Окно сбора: разборщик, проснувшись на пустой очереди, ждёт, пока в неё упадёт весь
    // всплеск (Track зовётся по очереди для каждой генерации чата). internal: тесты сжимают.
    internal int CollectWindowMs = 250;
    // Лимит fal на диапазон start..end — 90 дней; берём 89 с запасом.
    internal const long MaxBatchSpanMs = 89L * 86_400_000;
    // Предохранитель пагинации ответа (10 000 записей на страницу — на 50 id это недостижимо).
    private const int MaxPages = 20;
    // Шаг опроса очереди: новый id с ранним сроком не ждёт долгого сна по чужому сроку.
    private const int QueuePollSliceMs = 500;

    // Запустить отслеживание стоимости по request_id. Идемпотентно, не блокирует вызывающего.
    // finishedAtMs — время генерации от вызывающего (Unix ms, ToolResultMessage.FinishedAt):
    // служит для окна запроса, когда время не читается из самого request_id (см. ResolveGenerationMs).
    public void Track(string sessionId, string requestId, long? finishedAtMs)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return;
        if (!_seen.TryAdd(requestId, 0)) return;
        var item = new PendingCost(sessionId, requestId, ResolveGenerationMs(finishedAtMs, requestId))
        {
            DueAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + BackoffSeconds[0] * 1000L,
        };
        bool start;
        lock (_queueLock)
        {
            _queue.Add(item);
            start = !_processorRunning;
            _processorRunning = true;
        }
        if (start) _ = Task.Run(ProcessQueueAsync);
    }

    // Реализация шва IFalRequestCost: фактическая цена по request_id либо null — когда
    // биллинга ещё нет. Без исключений: отсутствие цены — штатный исход, не ошибка.
    // Дедуп _seen здесь не участвует: он про однократную публикацию в чат, а не про вопрос.
    // Живой путь не батчится: ждущий потребитель получает свой запрос сразу, своей полосой.
    public async Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return null;
        var cost = await PollLiveAsync(requestId, ct);
        return cost?.CostUsd;
    }

    // Цикл опроса живой полосы (бэкофф → запрос по одному request_id). Сбои опроса глотаются:
    // «цены нет» → null после исчерпанного окна. См. _liveGate.
    private async Task<FalCostMessage?> PollLiveAsync(string requestId, CancellationToken ct)
    {
        // Окно времени запроса: без start API отдаёт только последние 24 ч (доки fal: start по
        // умолчанию = −24ч) — генерации старше суток не находились никогда (дефект S-SPEND-4).
        var generationMs = TryParseUuidV7Ms(requestId);
        var (startParam, endParam) = BuildWindowParams(generationMs, generationMs);
        for (var attempt = 0; attempt < BackoffSeconds.Length; attempt++)
        {
            FalCostMessage? cost = null;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[attempt]), ct);
                var fetch = await FetchBatchAsync([requestId], startParam, endParam, ct, live: true);
                if (fetch.FailedStatus is null) fetch.Found.TryGetValue(requestId, out cost);
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

    // Разборщик очереди бэкфилла: один на процесс, живёт, пока в очереди есть id. Выбирает
    // созревшие id, режет на пачки и отправляет по одной (гейт и интервал — в SendBillingAsync).
    // Тело обёрнуто в try/finally (хвост ревью R-S-SPEND-5, п. m1): любое исключение вне
    // собственного try пачки (сбойный возврат в очереди, невалидная нарезка) не должно оставить
    // _processorRunning == true — иначе Track никогда не запустит разборщика, а id будут
    // копиться в очереди навсегда. В finally флаг гасится при любом выходе; несданные пачки
    // возвращаются в очередь, непустая очередь перезапускает разборщик.
    private async Task ProcessQueueAsync()
    {
        // Единый список «несданных»: id, взятые из очереди и ещё не переданные в работу (опубликованные
        // или возвращённые в очередь). Из него id убирает тот, кто их сдал; остаток возвращает finally (m1).
        var unsent = new HashSet<PendingCost>(ReferenceEqualityComparer.Instance);
        try
        {
            if (CollectWindowMs > 0) await Task.Delay(CollectWindowMs);
            while (true)
            {
                List<PendingCost> due;
                long waitMs = 0;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) return; // штатный выход: флаг гасится в finally
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    due = _queue.Where(p => p.DueAtMs <= now).ToList();
                    if (due.Count == 0) waitMs = _queue.Min(p => p.DueAtMs) - now;
                    else _queue.RemoveAll(p => p.DueAtMs <= now);
                }
                if (due.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, QueuePollSliceMs)));
                    continue;
                }
                unsent.UnionWith(due);
                var batches = BuildBatches(due);
                foreach (var batch in batches)
                {
                    try
                    {
                        await ProcessBatchAsync(batch, unsent);
                    }
                    catch (Exception ex)
                    {
                        // Сбой разбора не должен ронять разборщик и терять id очереди. Возвращаем только
                        // ещё не сданные id пачки: уже опубликованные повторно не публикуются.
                        Console.Error.WriteLine($"[FalCost] Сбой обработки пачки ({batch.Count} id): {ex.Message}");
                        SafeReschedule(batch.Where(unsent.Contains).ToList(), unsent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalCost] Сбой разборщика очереди: {ex.Message}");
        }
        finally
        {
            // Возврат несданного в очередь (m1): попытка считается потраченной, следующий срок —
            // по бэкоффу id, поэтому устойчивая причина сбоя не крутит перезапуск по горячему.
            SafeReschedule(unsent.ToList(), unsent);
            // Флаг гасится при любом выходе; непустую очередь берёт на себя новый разборщик —
            // тот же порядок, что в Track, чтобы не поднять двух одновременно.
            bool restart;
            lock (_queueLock)
            {
                _processorRunning = false;
                if (_queue.Count > 0) { _processorRunning = true; restart = true; }
                else restart = false;
            }
            if (restart) _ = Task.Run(ProcessQueueAsync);
        }
    }

    // Нарезка созревших id на пачки: id с известным временем генерации сортируются по нему и
    // режутся так, чтобы окно запроса (end − start) не превышало MaxBatchSpanMs, а время
    // каждого id попадало в окно; id без времени идут отдельными пачками без окна.
    private List<List<PendingCost>> BuildBatches(List<PendingCost> due)
    {
        var batches = new List<List<PendingCost>>();
        var timed = due.Where(p => p.GenerationMs is not null).OrderBy(p => p.GenerationMs).ToList();
        List<PendingCost>? cur = null;
        foreach (var p in timed)
        {
            // Окно пачки = (последнее − первое время) + WindowSpanMs: добавляем, пока оно в лимите.
            if (cur is null || cur.Count >= MaxBatchSize
                || p.GenerationMs!.Value - cur[0].GenerationMs!.Value + WindowSpanMs > MaxBatchSpanMs)
            {
                cur = [];
                batches.Add(cur);
            }
            cur.Add(p);
        }
        foreach (var chunk in due.Where(p => p.GenerationMs is null).Chunk(MaxBatchSize))
            batches.Add(chunk.ToList());
        return batches;
    }

    // Каждый id по сдаче (публикация или возврат в очередь) снимается с unsent: исключение посреди пачки
    // не приведёт ни к повторной публикации, ни к повторному возврату уже сданных id.
    private async Task ProcessBatchAsync(List<PendingCost> batch, HashSet<PendingCost> unsent)
    {
        // Окно строится явно по timed-части пачки (хвост ревью R-S-SPEND-5, п. m3): наличие окна
        // должно определяться наличием времени в пачке, а не семантикой Min/Max по nullable над
        // всей пачкой. Пачка без времени — без окна (прежнее поведение: дефолт API −24ч); пачка с
        // временем — окно обязательно. Сейчас BuildBatches разносит timed и untimed, но связь
        // держится здесь, а не в согласованности нарезки.
        var timed = batch.Where(p => p.GenerationMs is not null).ToList();
        var (startParam, endParam) = timed.Count == 0
            ? ((string?)null, (string?)null)
            : BuildWindowParams(timed.Min(p => p.GenerationMs), timed.Max(p => p.GenerationMs));
        BatchFetch fetch;
        try
        {
            fetch = await FetchBatchAsync(batch.Select(p => p.RequestId).ToList(), startParam, endParam,
                CancellationToken.None, live: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalCost] Опрос billing-events (пачка {batch.Count}) упал: {ex.Message}");
            Reschedule(batch, unsent); // отказ пачки — одна попытка на всех, не по одной на id
            return;
        }

        // 400 на пачку: один невалидный id хоронит остальные — делим пополам, попытки не тратим.
        if (fetch.FailedStatus == HttpStatusCode.BadRequest && batch.Count > 1)
        {
            var half = batch.Count / 2;
            await ProcessBatchAsync(batch[..half], unsent);
            await ProcessBatchAsync(batch[half..], unsent);
            return;
        }
        if (fetch.FailedStatus is not null)
        {
            Reschedule(batch, unsent);
            return;
        }

        // Найденные публикуются сразу, ненайденные ждут следующей попытки по своему бэкоффу.
        var missing = new List<PendingCost>();
        foreach (var p in batch)
        {
            if (fetch.Found.TryGetValue(p.RequestId, out var cost))
            {
                await PublishAsync(p, cost);
                unsent.Remove(p);
            }
            else missing.Add(p);
        }
        Reschedule(missing, unsent);
    }

    // Одна неудачная попытка для каждого id списка: следующий срок — по его бэкоффу, исчерпал — отброшен.
    private void Reschedule(List<PendingCost> items, HashSet<PendingCost> unsent)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var p in items)
        {
            p.Attempt++;
            if (p.Attempt >= BackoffSeconds.Length)
            {
                unsent.Remove(p); // отброшен: сдан до записи в журнал, чтобы сбой журнала не вернул его снова
                Console.Error.WriteLine($"[FalCost] Стоимость не появилась за отведённое время: {p.RequestId}");
                continue;
            }
            p.DueAtMs = now + BackoffSeconds[p.Attempt] * 1000L;
            lock (_queueLock) _queue.Add(p);
            unsent.Remove(p); // сдан: частичный сбой не вернёт его второй раз
        }
    }

    // Возврат несданного в очереди из finally разборщика (m1): сам возврат не должен ронять
    // завершение разборщика — флаг гасится следом, очередь не остаётся вставшей.
    private void SafeReschedule(List<PendingCost> items, HashSet<PendingCost> unsent)
    {
        try
        {
            Reschedule(items, unsent);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalCost] Возврат пачки в очередь упал: {ex.Message}");
        }
    }

    private async Task PublishAsync(PendingCost p, FalCostMessage cost)
    {
        try
        {
            if (OnCostResolved is not null)
                await OnCostResolved(p.SessionId, cost);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalCost] Публикация стоимости ({p.RequestId}) упала: {ex.Message}");
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
    private static (string? Start, string? End) BuildWindowParams(long? minGenerationMs, long? maxGenerationMs)
    {
        if (minGenerationMs is null || maxGenerationMs is null) return (null, null);
        var startMs = minGenerationMs.Value - StartMarginMs;
        // Пачка: окно от самой ранней генерации до самой поздней + сутки (для одной — start + 24 ч).
        return (FormatUtc(startMs), FormatUtc(maxGenerationMs.Value - StartMarginMs + WindowSpanMs));
    }

    private static string FormatUtc(long unixMs) => Uri.EscapeDataString(
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

    // Итог запроса пачки: FailedStatus != null — запрос не удался целиком (Found пуст);
    // иначе Found — цены найденных id (ненайденных в нём нет).
    private sealed record BatchFetch(HttpStatusCode? FailedStatus, Dictionary<string, FalCostMessage> Found);

    private sealed class CostAcc
    {
        public double Nano;
        public string? EndpointId;
        public double? OutputUnits;
        public double? UnitPrice;
    }

    private async Task<BatchFetch> FetchBatchAsync(IReadOnlyList<string> ids, string? startParam, string? endParam, CancellationToken ct, bool live)
    {
        var acc = new Dictionary<string, CostAcc>();
        string? cursor = null;
        var page = 0;
        for (; page < MaxPages; page++)
        {
            var url = _billingEventsUrl + "?" + string.Join("&", ids.Select(i => "request_id=" + Uri.EscapeDataString(i)))
                + (startParam is null ? "" : $"&start={startParam}")
                + (endParam is null ? "" : $"&end={endParam}")
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var (status, json) = await SendBillingAsync(url, ids.Count, ct, live);
            // 404 — события ещё нет (нормально на ранних попытках); прочее — отказ запроса.
            // Страницы собираются целиком: частичная сумма по id дала бы заниженную цену.
            if (status == HttpStatusCode.NotFound) return new BatchFetch(null, []);
            if (json is null) return new BatchFetch(status, []);

            using var doc = JsonDocument.Parse(json);
            AccumulateEvents(ids, doc.RootElement, acc);
            if (doc.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                && doc.RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(next.GetString()))
                cursor = next.GetString();
            else
                break;
        }

        // Достигнут предел страниц (хвост ревью R-S-SPEND-5, п. m2): молча вернуть неполный набор
        // значило бы потерять цены с непросмотренных страниц — id ушли бы в missing, снова
        // натыкались бы на те же MaxPages страниц и пропали после бэкоффа. Неполная страница —
        // не только про недостающие id: события найденного id могут продолжаться дальше, а
        // частичная сумма занижает цену. Отказ пачки — Reschedule, следующая попытка.
        if (page == MaxPages)
            throw new InvalidOperationException(
                $"billing-events: ответ длиннее {MaxPages} страниц (id в запросе: {ids.Count}) — пачка возвращается в очередь");

        var found = new Dictionary<string, FalCostMessage>();
        foreach (var (id, a) in acc)
            found[id] = new FalCostMessage(id, a.EndpointId, a.Nano / 1_000_000_000d, a.OutputUnits, a.UnitPrice);
        return new BatchFetch(null, found);
    }

    // Один HTTP-запрос под троттлингом. Json == null — запрос не вернул тело (Status — причина).
    private async Task<(HttpStatusCode Status, string? Json)> SendBillingAsync(string url, int idCount, CancellationToken ct, bool live)
    {
        // Троттлинг (MinRequestIntervalMs): однослотовый гейт + минимальный интервал между
        // стартами запросов. Очередь живёт под ct: отмена вызывающего в очереди — штатный выход.
        // Живые запросы (шов ResolveUsdAsync) — своей полосой: не ждут интервала бэкфилла и не
        // встают за его очередью (сериализованы только между собой). См. _liveGate.
        var gate = live ? _liveGate : _gate;
        await gate.WaitAsync(ct);
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
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode != HttpStatusCode.NotFound)
                    Console.Error.WriteLine($"[FalCost] billing-events {(int)resp.StatusCode} (id в запросе: {idCount})");
                return (resp.StatusCode, null);
            }
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            gate.Release();
        }
    }

    // Раскладка событий ответа по request_id. Для запроса по ОДНОМУ id все события — его
    // (fal фильтрует по request_id; прежнее поведение шва не требовало совпадения id в ответе).
    private static void AccumulateEvents(IReadOnlyList<string> ids, JsonElement root, Dictionary<string, CostAcc> acc)
    {
        if (!root.TryGetProperty("billing_events", out var events) || events.ValueKind != JsonValueKind.Array)
            return;
        var wanted = ids.Count == 1 ? null : ids.ToHashSet();
        foreach (var e in events.EnumerateArray())
        {
            string? id;
            if (wanted is null) id = ids[0];
            else
            {
                id = e.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;
                if (id is null || !wanted.Contains(id)) continue;
            }
            // cost_estimate_nano_usd ещё может быть null — тогда событие пропускаем (ждём)
            if (!e.TryGetProperty("cost_estimate_nano_usd", out var nano) || nano.ValueKind != JsonValueKind.Number)
                continue;
            if (!acc.TryGetValue(id, out var a)) acc[id] = a = new CostAcc();
            a.Nano += nano.GetDouble();
            a.EndpointId ??= e.TryGetProperty("endpoint_id", out var ep) ? ep.GetString() : null;
            if (a.OutputUnits is null && e.TryGetProperty("output_units", out var ou) && ou.ValueKind == JsonValueKind.Number)
                a.OutputUnits = ou.GetDouble();
            if (a.UnitPrice is null && e.TryGetProperty("unit_price", out var up) && up.ValueKind == JsonValueKind.Number)
                a.UnitPrice = up.GetDouble();
        }
    }
}
