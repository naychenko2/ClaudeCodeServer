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
    // Все запросы идут через однослотовый гейт с минимальным интервалом между стартами.
    // Механизм временной, а не по-id: он работает и в свежем процессе (в отличие от in-process
    // дедупа _seen) — новый всплеск после рестарта так же растягивается по интервалу.
    // internal для тестов (тот же приём, что BackoffSeconds): тесты сжимают интервал до 0.
    internal int MinRequestIntervalMs = 1000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRequestAtMs; // Unix ms; 0 — запросов в этом процессе ещё не было

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
        _ = PollAsync(sessionId, requestId, finishedAtMs);
    }

    // Реализация шва IFalRequestCost: фактическая цена по request_id либо null — когда
    // биллинга ещё нет. Без исключений: отсутствие цены — штатный исход, не ошибка.
    // Дедуп _seen здесь не участвует: он про однократную публикацию в чат, а не про вопрос.
    public async Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return null;
        var cost = await PollForCostAsync(requestId, null, ct);
        return cost?.CostUsd;
    }

    // Единый цикл опроса billing-events (бэкофф → FetchCostAsync по request_id). Зовут его
    // и ResolveUsdAsync (шов), и PollAsync (путь чата с публикацией) — цикл один.
    // Сбои опроса глотаются, как прежде: «цены нет» → null после исчерпанного окна.
    private async Task<FalCostMessage?> PollForCostAsync(string requestId, long? finishedAtMs, CancellationToken ct)
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
                cost = await FetchCostAsync(requestId, startParam, endParam, ct);
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

    private async Task PollAsync(string sessionId, string requestId, long? finishedAtMs)
    {
        var cost = await PollForCostAsync(requestId, finishedAtMs, CancellationToken.None);
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

    // Верх границы окна: 89 дней — под лимитом fal (90 дней на запрос) с запасом на ход часов.
    private const long MaxWindowMs = 89L * 86_400_000;

    // Окно запроса: start = время генерации − запас; end — ЯВНЫЙ (вердикт ревью R-S-SPEND-4):
    // без end дефолт API — его «сейчас», и для генерации старше ~90 дней диапазон
    // [ген − 1ч, сейчас) превышает лимит fal: запрос отвергается валидацией, каждая попытка
    // цикла повторяет тот же отвергнутый запрос — цена старых генераций не находится никогда.
    // end = min(сейчас, start + 89 дней): для свежей генерации окно то же, что был дефолт;
    // для старой — срезается до 89 дней и всё равно содержит событие (его timestamp —
    // момент запроса, начало окна). Инвариант: для генерации старше 24 ч запрос к fal
    // содержит событие биллинга этой генерации. Нет времени генерации — окна нет (прежнее
    // поведение: дефолт API −24ч достаточен для свежей генерации).
    private static (string? Start, string? End) BuildWindowParams(long? generationMs)
    {
        if (generationMs is null) return (null, null);
        var startMs = generationMs.Value - StartMarginMs;
        var endMs = Math.Min(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), startMs + MaxWindowMs);
        return (FormatUtc(startMs), FormatUtc(endMs));
    }

    private static string FormatUtc(long unixMs) => Uri.EscapeDataString(
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private async Task<FalCostMessage?> FetchCostAsync(string requestId, string? startParam, string? endParam, CancellationToken ct)
    {
        // Троттлинг (MinRequestIntervalMs): однослотовый гейт + минимальный интервал между
        // стартами запросов. Очередь живёт под ct: отмена вызывающего в очереди — штатный выход.
        await _gate.WaitAsync(ct);
        try
        {
            var minStart = _lastRequestAtMs + MinRequestIntervalMs;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < minStart) await Task.Delay((int)(minStart - now), ct);
            _lastRequestAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var client = _httpFactory.CreateClient("fal");
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
            _gate.Release();
        }
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
