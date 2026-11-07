using System.Collections.Concurrent;
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
    // На гонке GetOrAdd может вызвать фабрику больше одного раза — дубль опроса идемпотентен
    // (тот же request_id), лишь тратит лишний запрос; результат всегда из сохранённого task.
    private readonly ConcurrentDictionary<string, Task<FalCostMessage?>> _pollings = new();

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
    public void Track(string sessionId, string requestId)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return;
        if (!_seen.TryAdd(requestId, 0)) return;
        // Начинаем опрос (или берём уже начатый потребителем через шов); публикация
        // в чат — один раз, по дедупу _seen.
        var poll = GetOrStartPolling(requestId, CancellationToken.None);
        _ = PublishAsync(sessionId, requestId, poll);
    }

    // Реализация шва IFalRequestCost: фактическая цена по request_id либо null — когда
    // биллинга ещё нет. Без исключений: отсутствие цены — штатный исход, не ошибка.
    // Дедуп _seen здесь не участвует: он про однократную публикацию в чат, а не про вопрос.
    // Сам опрос общий: повторный запрос той же цены берётся из начатого task, не идя в сеть.
    public async Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(requestId)) return null;
        var cost = await GetOrStartPolling(requestId, ct);
        return cost?.CostUsd;
    }

    // Начать опрос по request_id или вернуть уже начатый — единственная точка входа в цикл.
    // Помета (отложенный риск из ревью d524e017): ct первого вызывающего решает судьбу общего
    // опроса — придёт первым с реальным ct и отменится, null останется в кэше за всех.
    // На всех текущих путях ct = CancellationToken.None (Track, ImageGenerationService,
    // ImageEditJobService); если на каком-то пути появится реальный ct — общий опрос надо
    // начинать без него.
    private Task<FalCostMessage?> GetOrStartPolling(string requestId, CancellationToken ct) =>
        _pollings.GetOrAdd(requestId, _ => PollForCostAsync(requestId, ct));

    // Единый цикл опроса billing-events (бэкофф → FetchCostAsync по request_id). Начинает его
    // только GetOrStartPolling — путь чата и потребители шва ждут результат одного task.
    // Сбои опроса глотаются, как прежде: «цены нет» → null после исчерпанного окна.
    private async Task<FalCostMessage?> PollForCostAsync(string requestId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < BackoffSeconds.Length; attempt++)
        {
            FalCostMessage? cost;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[attempt]), ct);
                cost = await FetchCostAsync(requestId, ct);
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

    private async Task<FalCostMessage?> FetchCostAsync(string requestId, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("fal");
        var url = $"{_billingEventsUrl}?request_id={Uri.EscapeDataString(requestId)}";
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
