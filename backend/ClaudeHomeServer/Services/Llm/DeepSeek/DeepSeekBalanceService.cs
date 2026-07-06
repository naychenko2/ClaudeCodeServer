using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

public sealed record DeepSeekBalance(bool Available, string Currency, string TotalBalance);

// Точка истории баланса — для графика на экране «Использование»
public sealed record DeepSeekBalanceSnapshot(DateTime Timestamp, double Balance, string Currency);

// Баланс аккаунта DeepSeek (GET /user/balance) с кэшем — для индикатора в шапке чата.
// Каждое успешное обновление пишет снапшот в data/deepseek-usage.json (история для графика).
public class DeepSeekBalanceService(IHttpClientFactory httpFactory, IOptions<DeepSeekOptions> options,
    IConfiguration config)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(8);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DeepSeekBalance? _cached;
    private DateTime _cachedAt;

    private readonly string _usagePath = Path.Combine(
        Path.GetDirectoryName(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data"),
        "deepseek-usage.json");
    private readonly object _usageLock = new();

    public bool Enabled => options.Value.Enabled;

    public async Task<DeepSeekBalance?> GetAsync(CancellationToken ct)
    {
        if (!Enabled) return null;
        if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheTtl) return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheTtl) return _cached;

            var opts = options.Value;
            var client = httpFactory.CreateClient("deepseek");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{opts.BaseUrl.TrimEnd('/')}/user/balance");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
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
            _cached = new DeepSeekBalance(available, currency, total);
            _cachedAt = DateTime.UtcNow;
            RecordSnapshot(_cached);
            return _cached;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DeepSeekBalance] Не удалось получить баланс: {ex.Message}");
            return _cached; // протухший лучше, чем ничего
        }
        finally { _lock.Release(); }
    }

    // История баланса за последние дни — для графика на экране «Использование»
    public IReadOnlyList<DeepSeekBalanceSnapshot> GetSnapshots()
    {
        lock (_usageLock)
            return LoadSnapshots();
    }

    private void RecordSnapshot(DeepSeekBalance balance)
    {
        if (!double.TryParse(balance.TotalBalance,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var value)) return;
        try
        {
            lock (_usageLock)
            {
                var list = LoadSnapshots();
                var cutoff = DateTime.UtcNow - SnapshotRetention;
                list = list.Where(s => s.Timestamp >= cutoff).ToList();
                // Кэш баланса живёт 5 мин — каждое обновление и есть естественный троттлинг
                list.Add(new DeepSeekBalanceSnapshot(DateTime.UtcNow, value, balance.Currency));
                File.WriteAllText(_usagePath, JsonSerializer.Serialize(list));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DeepSeekBalance] Не удалось сохранить снапшот: {ex.Message}");
        }
    }

    private List<DeepSeekBalanceSnapshot> LoadSnapshots()
    {
        try
        {
            if (File.Exists(_usagePath)
                && JsonSerializer.Deserialize<List<DeepSeekBalanceSnapshot>>(File.ReadAllText(_usagePath)) is { } list)
                return list;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DeepSeekBalance] Не удалось прочитать снапшоты: {ex.Message}");
        }
        return [];
    }
}
