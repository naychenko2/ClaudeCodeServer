using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

public sealed record DeepSeekBalance(bool Available, string Currency, string TotalBalance);

// Баланс аккаунта DeepSeek (GET /user/balance) с кэшем — для индикатора в шапке чата
public class DeepSeekBalanceService(IHttpClientFactory httpFactory, IOptions<DeepSeekOptions> options)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DeepSeekBalance? _cached;
    private DateTime _cachedAt;

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
}
