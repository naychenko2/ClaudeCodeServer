using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Статистика аккаунта fal.ai через Platform API (тем же ключом Fal:ApiKey, что и учёт стоимости):
// остаток баланса (кредиты) + расход по моделям и дням. Кэш на 60с, чтобы не дёргать сеть на каждый
// открытый диалог. Без ключа — Enabled=false (фича выключена).
public record FalModelSpend(string EndpointId, double Cost);
public record FalDaySpend(string Date, double Cost);
public record FalUsageSummary(int Days, double Total, IReadOnlyList<FalModelSpend> ByModel, IReadOnlyList<FalDaySpend> Series);
public record FalAccountResponse(bool Enabled, double? Balance, string? Currency, FalUsageSummary? Usage);

public class FalAccountService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _apiBase;

    private readonly object _lock = new();
    private (DateTime At, int Days, FalAccountResponse Resp)? _cache;

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    public FalAccountService(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        _apiBase = (config["Fal:ApiBase"] ?? "https://api.fal.ai/v1").TrimEnd('/');
    }

    public async Task<FalAccountResponse> GetAsync(int days)
    {
        if (!Enabled) return new FalAccountResponse(false, null, null, null);
        lock (_lock)
        {
            if (_cache is { } c && c.Days == days && DateTime.UtcNow - c.At < CacheTtl)
                return c.Resp;
        }
        var (balance, currency) = await FetchBalanceAsync();
        var usage = await FetchUsageAsync(days);
        var resp = new FalAccountResponse(true, balance, currency, usage);
        lock (_lock) _cache = (DateTime.UtcNow, days, resp);
        return resp;
    }

    private HttpRequestMessage Auth(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
        return req;
    }

    // Остаток баланса: /account/billing?expand=credits → credits.current_balance + currency
    private async Task<(double?, string?)> FetchBalanceAsync()
    {
        try
        {
            var client = _http.CreateClient("fal");
            using var resp = await client.SendAsync(Auth($"{_apiBase}/account/billing?expand=credits"));
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("credits", out var cr)) return (null, null);
            double? bal = cr.TryGetProperty("current_balance", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null;
            string? cur = cr.TryGetProperty("currency", out var c) ? c.GetString() : null;
            return (bal, cur);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[FalAccount] баланс: {ex.Message}"); return (null, null); }
    }

    // Расход: /models/usage за N дней → агрегируем total, по моделям, по дням
    private async Task<FalUsageSummary?> FetchUsageAsync(int days)
    {
        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-days);
            var url = $"{_apiBase}/models/usage?start={Uri.EscapeDataString(start.ToString("o"))}&end={Uri.EscapeDataString(end.ToString("o"))}";
            var client = _http.CreateClient("fal");
            using var resp = await client.SendAsync(Auth(url));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("time_series", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;

            double total = 0;
            var byModel = new Dictionary<string, double>();
            var byDay = new Dictionary<string, double>();
            foreach (var bucket in ts.EnumerateArray())
            {
                var day = bucket.TryGetProperty("bucket", out var bk) ? (bk.GetString() ?? "") : "";
                if (day.Length >= 10) day = day[..10]; // YYYY-MM-DD
                if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in results.EnumerateArray())
                {
                    var cost = r.TryGetProperty("cost", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetDouble() : 0;
                    if (cost <= 0) continue;
                    total += cost;
                    var ep = r.TryGetProperty("endpoint_id", out var epEl) ? (epEl.GetString() ?? "?") : "?";
                    byModel[ep] = byModel.GetValueOrDefault(ep) + cost;
                    if (day.Length > 0) byDay[day] = byDay.GetValueOrDefault(day) + cost;
                }
            }
            var models = byModel.OrderByDescending(kv => kv.Value).Select(kv => new FalModelSpend(kv.Key, kv.Value)).ToList();
            var series = byDay.OrderBy(kv => kv.Key).Select(kv => new FalDaySpend(kv.Key, kv.Value)).ToList();
            return new FalUsageSummary(days, total, models, series);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[FalAccount] usage: {ex.Message}"); return null; }
    }
}
