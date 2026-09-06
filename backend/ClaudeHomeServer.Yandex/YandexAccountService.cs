using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Yandex;

// Остаток на биллинг-аккаунте Yandex Cloud (Billing API v1). Кэш на 60с — как у fal/glif:
// баланс смотрят глазами, а не в цикле, и дёргать сеть на каждый открытый экран незачем.
// Без ключа сервисного аккаунта Enabled=false — фича просто выключена.
//
// Чего здесь нет и не будет: РАСХОДА по услугам. Billing API отдаёт баланс, прайс и список
// услуг, но не отвечает на вопрос «сколько ушло на SpeechKit за неделю» — детализация живёт
// только в CSV-выгрузке (разовой или в бакет Object Storage). Поэтому расход на озвучку
// продукт считает сам (SpendStore, источник tts): тарификация SpeechKit идёт за запрос,
// а запросы мы отправляем и знаем точно.
public sealed record YandexBillingAccount(string Id, string? Name, string? Balance,
    string? Currency, bool Active);

public sealed record YandexAccountResponse(bool Enabled, YandexBillingAccount? Account,
    DateTime? AsOf, string? Error);

public sealed class YandexAccountService(IHttpClientFactory http, IConfiguration config,
    YandexIamTokenProvider iam, ILogger<YandexAccountService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const string ListEndpoint = "https://billing.api.cloud.yandex.net/billing/v1/billingAccounts";

    // Явный аккаунт нужен, только когда их несколько: пусто — берём первый из списка
    private readonly string? _accountId = config["Yandex:Billing:BillingAccountId"];

    private readonly object _lock = new();
    private (DateTime At, YandexAccountResponse Resp)? _cache;

    public bool Enabled => iam.IsConfigured;

    public async Task<YandexAccountResponse> GetAsync(CancellationToken ct = default)
    {
        if (!Enabled) return new YandexAccountResponse(false, null, null, null);

        lock (_lock)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < CacheTtl) return c.Resp;
        }

        var resp = await FetchAsync(ct);
        // В кэш кладём и отказ: иначе мёртвый ключ означал бы поход в сеть на каждый показ
        lock (_lock) _cache = (DateTime.UtcNow, resp);
        return resp;
    }

    private async Task<YandexAccountResponse> FetchAsync(CancellationToken ct)
    {
        var token = await iam.GetAsync(ct);
        if (token is null)
            return new YandexAccountResponse(true, null, null,
                "Не удалось получить IAM-токен — проверь ключ сервисного аккаунта");

        try
        {
            var client = http.CreateClient(YandexIamTokenProvider.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, ListEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var http_resp = await client.SendAsync(req, ct);
            var body = await http_resp.Content.ReadAsStringAsync(ct);
            if (!http_resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Billing API ответил {Status}: {Body}", (int)http_resp.StatusCode, body);
                return new YandexAccountResponse(true, null, null,
                    $"Billing API ответил {(int)http_resp.StatusCode}");
            }

            var account = Parse(body, _accountId);
            return account is null
                // Пустой список при живом токене — почти всегда роль выдана на облако, а не на
                // биллинг-аккаунт: отказа Яндекс в этом случае не даёт, просто не показывает ничего
                ? new YandexAccountResponse(true, null, null,
                    "Биллинг-аккаунт не виден: нужна роль billing.accounts.viewer НА БИЛЛИНГ-АККАУНТЕ")
                : new YandexAccountResponse(true, account, DateTime.UtcNow, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            return new YandexAccountResponse(true, null, null, "Яндекс не ответил");
        }
    }

    // Баланс приходит СТРОКОЙ («1234.56») — так его и отдаём наружу, без разбора в double:
    // формат чужой, а любая арифметика над ним всё равно никому здесь не нужна.
    internal static YandexBillingAccount? Parse(string body, string? wantedId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("billingAccounts", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return null;

            foreach (var a in arr.EnumerateArray())
            {
                var id = a.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id is null) continue;
                if (!string.IsNullOrWhiteSpace(wantedId) && id != wantedId) continue;

                return new YandexBillingAccount(
                    id,
                    a.TryGetProperty("name", out var n) ? n.GetString() : null,
                    a.TryGetProperty("balance", out var b) ? b.GetString() : null,
                    a.TryGetProperty("currency", out var c) ? c.GetString() : null,
                    a.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
