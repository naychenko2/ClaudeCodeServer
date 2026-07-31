using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Статистика аккаунта glif.app через MCP-инструмент whoami: JSON-RPC tools/call на
// https://glif.app/api/mcp тем же токеном Glif:McpToken, что инжектит сервер glif в конфиг
// хода. Кэш на 60с, чтобы не дёргать сеть на каждый открытый диалог. Без токена —
// Enabled=false (фича выключена).
// Форма whoami проверена на живом токене (2026-07-31): { identity:{userId,username},
// session:{apiTokenLabel}, billing:{ plan:{productId,status,periodEnd},
// credits:{available,subscription,extra}, spend:{last24h,last7d,last30d} } } — данные в
// structuredContent (дублируются JSON-строкой в text-блоке content[]). Это КРЕДИТЫ, не USD;
// расход — готовые агрегаты за 24ч/7д/30д (сервер не даёт серию по дням, поэтому параметр
// days эндпоинта участвует только в ключе кэша). Парсинг всё равно устойчивый:
// отсутствующие/переименованные поля → null, а не падение.
public record GlifSpend(double? Last24h, double? Last7d, double? Last30d);
public record GlifAccountResponse(bool Enabled, string? Plan, string? PlanStatus,
    double? Balance, string? Currency, GlifSpend? Spend);

public class GlifAccountService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const string McpUrl = "https://glif.app/api/mcp";

    private readonly IHttpClientFactory _http;
    private readonly string? _token;

    private readonly object _lock = new();
    private (DateTime At, int Days, GlifAccountResponse Resp)? _cache;

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    public GlifAccountService(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _token = config["Glif:McpToken"];
    }

    public async Task<GlifAccountResponse> GetAsync(int days)
    {
        if (!Enabled) return new GlifAccountResponse(false, null, null, null, null, null);
        lock (_lock)
        {
            if (_cache is { } c && c.Days == days && DateTime.UtcNow - c.At < CacheTtl)
                return c.Resp;
        }
        var resp = await FetchWhoamiAsync();
        lock (_lock) _cache = (DateTime.UtcNow, days, resp);
        return resp;
    }

    // Вызов whoami: endpoint stateless (mcp-session-id не выдаётся), прямой tools/call без
    // initialize-рукопожатия; ответ приходит SSE-кадром «event: message» с data:-строкой
    private async Task<GlifAccountResponse> FetchWhoamiAsync()
    {
        try
        {
            var client = _http.CreateClient("glif");
            using var req = new HttpRequestMessage(HttpMethod.Post, McpUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            // MCP Streamable HTTP: POST обязан принимать оба типа контента
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            req.Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"whoami","arguments":{}}}""",
                Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return Empty();
            var body = await resp.Content.ReadAsStringAsync();
            var payload = ExtractJsonRpcPayload(body);
            if (payload is null) return Empty();

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return Empty();
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return Empty();
            if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                return Empty();

            // Данные whoami: structuredContent (основной), либо JSON в text-блоке content[]
            var data = FindWhoamiData(result);
            if (data is null) return Empty();
            return Map(data.Value);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[GlifAccount] whoami: {ex.Message}"); return Empty(); }
    }

    private static GlifAccountResponse Empty() => new(true, null, null, null, null, null);

    // Тело ответа — чистый JSON либо SSE-поток (data:-строки); берём последний data-кадр
    private static string? ExtractJsonRpcPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{')) return trimmed;
        string? last = null;
        foreach (var line in body.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("data:", StringComparison.Ordinal))
                last = l["data:".Length..].Trim();
        }
        return last is { Length: > 0 } ? last : null;
    }

    private static JsonElement? FindWhoamiData(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var sc) && sc.ValueKind == JsonValueKind.Object)
            return sc;
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "text") continue;
            if (!block.TryGetProperty("text", out var txt)) continue;
            var text = txt.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var s = text.Trim();
            if (!s.StartsWith('{')) continue;
            try
            {
                using var d = JsonDocument.Parse(s);
                // JsonDocument живёт до конца using — возвращаем клон, чтобы элемент пережил парсер
                return d.RootElement.Clone();
            }
            catch (JsonException) { /* не JSON — пропускаем текстовый блок */ }
        }
        return null;
    }

    // Маппинг живой формы: billing.plan.{productId,status} → plan/planStatus,
    // billing.credits.available → balance (кредиты!), billing.spend → агрегаты окон
    private static GlifAccountResponse Map(JsonElement data)
    {
        if (!TryGet(data, out var billing, "billing") || billing.ValueKind != JsonValueKind.Object)
            return Empty();

        string? plan = null, planStatus = null;
        if (TryGet(billing, out var planObj, "plan") && planObj.ValueKind == JsonValueKind.Object)
        {
            plan = GetString(planObj, "productId", "product_id", "name", "id");
            planStatus = GetString(planObj, "status");
        }

        double? balance = null;
        if (TryGet(billing, out var credits, "credits") && credits.ValueKind == JsonValueKind.Object)
            balance = GetNumber(credits, "available", "balance");

        GlifSpend? spend = null;
        if (TryGet(billing, out var spendObj, "spend") && spendObj.ValueKind == JsonValueKind.Object)
        {
            spend = new GlifSpend(
                GetNumber(spendObj, "last24h", "last24H", "last_24h"),
                GetNumber(spendObj, "last7d", "last_7d"),
                GetNumber(spendObj, "last30d", "last_30d"));
        }

        return new GlifAccountResponse(true, plan, planStatus, balance,
            balance is not null ? "credits" : null, spend);
    }

    // Регистронезависимый поиск по нескольким вариантам имени (страховка от переименований)
    private static bool TryGet(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
            foreach (var name in names)
                if (prop.NameEquals(name) || string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names)
        => TryGet(obj, out var v, names) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetNumber(JsonElement obj, params string[] names)
        => TryGet(obj, out var v, names) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
