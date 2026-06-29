using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Получает ФАКТИЧЕСКИ списанную стоимость генерации fal.ai через Platform API.
// В результате run_model/submit_job есть request_id; по нему billing-events отдаёт
// cost_estimate_nano_usd. Биллинг приходит с задержкой → опрашиваем с бэкоффом.
// Найденную стоимость отдаём наверх делегатом OnCostResolved (его ставит SessionManager).
public class FalCostService
{
    // Задержки между попытками (сек). Суммарно ~77с — обычно биллинг готов раньше.
    private static readonly int[] BackoffSeconds = [2, 5, 10, 20, 40];

    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly string _billingEventsUrl;
    // Дедуп: один request_id приходит в run_model и в get_job_result — опрашиваем один раз
    private readonly ConcurrentDictionary<string, byte> _seen = new();

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
        _ = PollAsync(sessionId, requestId);
    }

    private async Task PollAsync(string sessionId, string requestId)
    {
        for (var attempt = 0; attempt < BackoffSeconds.Length; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[attempt]));
            FalCostMessage? cost;
            try
            {
                cost = await FetchCostAsync(requestId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FalCost] Опрос billing-events ({requestId}) упал: {ex.Message}");
                continue;
            }
            if (cost is null) continue; // ещё не посчитано — ждём следующую попытку
            try
            {
                if (OnCostResolved is not null)
                    await OnCostResolved(sessionId, cost);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FalCost] Публикация стоимости ({requestId}) упала: {ex.Message}");
            }
            return;
        }
        Console.Error.WriteLine($"[FalCost] Стоимость не появилась за отведённое время: {requestId}");
    }

    private async Task<FalCostMessage?> FetchCostAsync(string requestId)
    {
        var client = _httpFactory.CreateClient("fal");
        var url = $"{_billingEventsUrl}?request_id={Uri.EscapeDataString(requestId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);

        using var resp = await client.SendAsync(req);
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
