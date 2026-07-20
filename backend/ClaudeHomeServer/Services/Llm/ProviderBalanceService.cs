using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

public sealed record ProviderBalance(bool Available, string Currency, string TotalBalance);

// Точка истории баланса — для графика на экране «Использование»
public sealed record ProviderBalanceSnapshot(DateTime Timestamp, double Balance, string Currency);

// Состояние аккаунта CLI-провайдера. Источник задаётся конфигом провайдера (Balance):
// "deepseek" — GET {ApiBaseUrl}/user/balance; "moonshot" — GET {ApiBaseUrl}/users/me/balance
// (деньги). Провайдер без источника —
// баланс недоступен (UI скрывает блок). Кэш 5 мин; каждое успешное обновление пишет
// снапшот в data/provider-usage-{key}.json (история для графика).
public class ProviderBalanceService(IHttpClientFactory httpFactory, LlmProviderRegistry providers,
    IConfiguration config)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(8);

    private sealed class ProviderCache
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public ProviderBalance? Cached;
        public DateTime CachedAt;
        public readonly object UsageLock = new();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderCache> _caches = new();

    private readonly string _dataDir =
        Path.GetDirectoryName(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");

    // Провайдер с настроенным источником баланса (и ключом) — иначе null
    public LlmProviderConfig? GetSupported(string key) =>
        providers.GetByKey(key) is { Enabled: true } p
        && !string.IsNullOrWhiteSpace(p.Balance) && !string.IsNullOrWhiteSpace(p.ApiBaseUrl)
            ? p : null;

    public async Task<ProviderBalance?> GetAsync(string key, CancellationToken ct)
    {
        var p = GetSupported(key);
        if (p is null) return null;

        var cache = _caches.GetOrAdd(p.Key, _ => new ProviderCache());
        if (cache.Cached is not null && DateTime.UtcNow - cache.CachedAt < CacheTtl) return cache.Cached;

        await cache.Lock.WaitAsync(ct);
        try
        {
            if (cache.Cached is not null && DateTime.UtcNow - cache.CachedAt < CacheTtl) return cache.Cached;

            var balance = p.Balance switch
            {
                "deepseek" => await FetchDeepSeekAsync(p, ct),
                "moonshot" => await FetchMoonshotAsync(p, ct),
                _ => null,
            };
            if (balance is null) return cache.Cached; // протухший лучше, чем ничего

            cache.Cached = balance;
            cache.CachedAt = DateTime.UtcNow;
            RecordSnapshot(p.Key, cache, balance);
            return balance;
        }
        finally { cache.Lock.Release(); }
    }

    // Формат DeepSeek: { is_available, balance_infos: [{ currency, total_balance }] }
    private async Task<ProviderBalance?> FetchDeepSeekAsync(LlmProviderConfig p, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/user/balance");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            var root = doc.RootElement;
            var available = root.TryGetProperty("is_available", out var av) && av.ValueKind == JsonValueKind.True;
            string currency = "", total = "";
            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array
                && infos.GetArrayLength() > 0)
            {
                var first = infos[0];
                currency = first.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                total = first.TryGetProperty("total_balance", out var t) ? t.GetString() ?? "" : "";
            }
            return new ProviderBalance(available, currency, total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Формат Moonshot (Kimi): { status, data: { available_balance, voucher_balance, cash_balance } }
    // available_balance — остаток в USD (наличные + ваучеры). GET {ApiBaseUrl}/users/me/balance
    private async Task<ProviderBalance?> FetchMoonshotAsync(LlmProviderConfig p, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/users/me/balance");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            var root = doc.RootElement;
            var available = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True;
            string total = "";
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("available_balance", out var bal))
                total = bal.ValueKind == JsonValueKind.Number
                    ? bal.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : bal.GetString() ?? "";
            return new ProviderBalance(available, "USD", total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // История баланса за последние дни — для графика на экране «Использование»
    public IReadOnlyList<ProviderBalanceSnapshot> GetSnapshots(string key)
    {
        var cache = _caches.GetOrAdd(key.ToLowerInvariant(), _ => new ProviderCache());
        lock (cache.UsageLock)
            return LoadSnapshots(key);
    }

    private string UsagePath(string key) => Path.Combine(_dataDir, $"provider-usage-{key}.json");
    // Прежнее имя файла истории DeepSeek — читаем, если нового ещё нет
    private string LegacyDeepSeekPath => Path.Combine(_dataDir, "deepseek-usage.json");

    private void RecordSnapshot(string key, ProviderCache cache, ProviderBalance balance)
    {
        if (!double.TryParse(balance.TotalBalance,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var value)) return;
        try
        {
            lock (cache.UsageLock)
            {
                var list = LoadSnapshots(key);
                var cutoff = DateTime.UtcNow - SnapshotRetention;
                list = list.Where(s => s.Timestamp >= cutoff).ToList();
                // Кэш баланса живёт 5 мин — каждое обновление и есть естественный троттлинг
                list.Add(new ProviderBalanceSnapshot(DateTime.UtcNow, value, balance.Currency));
                File.WriteAllText(UsagePath(key), JsonSerializer.Serialize(list));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось сохранить снапшот {key}: {ex.Message}");
        }
    }

    private List<ProviderBalanceSnapshot> LoadSnapshots(string key)
    {
        var path = UsagePath(key);
        if (!File.Exists(path) && key == "deepseek" && File.Exists(LegacyDeepSeekPath))
            path = LegacyDeepSeekPath;
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<List<ProviderBalanceSnapshot>>(File.ReadAllText(path)) is { } list)
                return list;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось прочитать снапшоты {key}: {ex.Message}");
        }
        return [];
    }
}
