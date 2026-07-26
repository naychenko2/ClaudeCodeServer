using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// AsOf — UTC-время последнего УСПЕШНОГО обновления (при отдаче протухшего кэша остаётся
// временем того успешного обновления — фронт показывает свежесть данных).
// ResetsAt — момент сброса окна квоты (UTC; у GLM и Kimi), null — не применимо.
// SecondaryLabel/SecondaryValue/SecondaryResetsAt — второе окно квоты, когда у подписки
// их два (у Kimi основное — 5-часовое, второе — недельное); у прочих провайдеров null.
public sealed record ProviderBalance(bool Available, string Currency, string TotalBalance,
    DateTime AsOf = default, DateTime? ResetsAt = null,
    string? SecondaryLabel = null, string? SecondaryValue = null, DateTime? SecondaryResetsAt = null);

// Точка истории баланса — для графика на экране «Использование»
public sealed record ProviderBalanceSnapshot(DateTime Timestamp, double Balance, string Currency);

// Состояние аккаунта CLI-провайдера. Источник задаётся конфигом провайдера (Balance):
// "deepseek" — GET {ApiBaseUrl}/user/balance; "moonshot" — GET {ApiBaseUrl}/users/me/balance;
// "openrouter" — GET {ApiBaseUrl}/credits (деньги); "glm" — GET {BalanceUrl} (квота подписки
// Coding Plan, остаток в % 5-часового окна); "kimi" — GET {ApiBaseUrl}/usages (квота подписки
// Kimi for Coding: 5-часовое окно — основное, недельное — в Secondary*). Провайдер без
// источника — баланс недоступен (UI скрывает блок). Кэш 5 мин; каждое успешное обновление
// пишет снапшот в data/provider-usage-{key}.json (история для графика).
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
                "openrouter" => await FetchOpenRouterAsync(p, ct),
                "glm" => await FetchGlmAsync(p, ct),
                "kimi" => await FetchKimiAsync(p, ct),
                _ => null,
            };
            // Протухший лучше, чем ничего: AsOf в нём остаётся временем прошлого
            // успешного обновления — фронт по нему покажет, что данные не свежие
            if (balance is null) return cache.Cached;

            cache.Cached = balance with { AsOf = DateTime.UtcNow };
            cache.CachedAt = DateTime.UtcNow;
            RecordSnapshot(p.Key, cache, balance);
            return cache.Cached;
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

    // Формат OpenRouter: { data: { total_credits, total_usage } } — GET {ApiBaseUrl}/credits.
    // Остатка отдельным полем нет: он равен разнице (сколько зачислено минус потрачено).
    // Кредиты и расход — в USD.
    private async Task<ProviderBalance?> FetchOpenRouterAsync(LlmProviderConfig p, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/credits");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            var credits = ReadNumber(data, "total_credits");
            var used = ReadNumber(data, "total_usage");
            if (double.IsNaN(credits) || double.IsNaN(used)) return null;

            var remaining = credits - used;
            return new ProviderBalance(remaining > 0, "USD",
                remaining.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Число из JSON, приходящее как number или как строка; NaN — поля нет либо не разобрать
    private static double ReadNumber(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return double.NaN;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        return double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    // Формат GLM (z.ai Coding Plan, недокументированный монитор):
    // { data: { limits: [ { type: "TOKENS_LIMIT", percentage, nextResetTime }, ... ] } }
    // TOKENS_LIMIT-элементов два — 5-часовое окно и недельное; берём с ближайшим
    // nextResetTime (самое короткое = 5-часовое). percentage — израсходовано;
    // показываем остаток (100 − percentage). Хедер Authorization БЕЗ префикса "Bearer".
    private async Task<ProviderBalance?> FetchGlmAsync(LlmProviderConfig p, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(p.BalanceUrl)
            ? $"{p.ApiBaseUrl.TrimEnd('/')}/monitor/usage/quota/limit"
            : p.BalanceUrl;
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", p.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
            if (!data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return null;

            // Среди TOKENS_LIMIT выбираем окно с ближайшим сбросом (5-часовое)
            double bestUsed = double.NaN;
            long bestReset = long.MaxValue;
            foreach (var item in limits.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t)
                    || !string.Equals(t.GetString(), "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!item.TryGetProperty("percentage", out var pct)) continue;
                var used = pct.ValueKind == JsonValueKind.Number ? pct.GetDouble()
                    : double.TryParse(pct.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
                if (double.IsNaN(used)) continue;
                var reset = item.TryGetProperty("nextResetTime", out var nr) && nr.ValueKind == JsonValueKind.Number
                    ? nr.GetInt64() : long.MaxValue;
                if (reset < bestReset) { bestReset = reset; bestUsed = used; }
            }
            if (double.IsNaN(bestUsed)) return null; // окна TOKENS_LIMIT нет — квоту показать нечем

            var remaining = Math.Clamp(100 - bestUsed, 0, 100);
            // nextResetTime — unix-время; порог отличает миллисекунды от секунд
            DateTime? resetsAt = bestReset == long.MaxValue ? null
                : (bestReset > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(bestReset)
                    : DateTimeOffset.FromUnixTimeSeconds(bestReset)).UtcDateTime;
            return new ProviderBalance(true, "%",
                remaining.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                ResetsAt: resetsAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Формат Kimi for Coding (подписка kimi.com, недокументированный эндпоинт):
    // GET {ApiBaseUrl}/usages →
    // { usage: {limit, used, remaining, resetTime},              // недельное окно
    //   limits: [{ window: {duration, timeUnit}, detail: {limit, used, remaining, resetTime} }] }
    // Числа приходят СТРОКАМИ; limit=100 — шкала уже процентная. Основным считаем самое
    // короткое окно из limits[] (5-часовое, 300 мин) — как у GLM; недельное кладём в Secondary*.
    private async Task<ProviderBalance?> FetchKimiAsync(LlmProviderConfig p, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/usages");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            return ParseKimiUsages(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Разбор ответа /usages Kimi (internal — под тестами). null — ни одного окна не разобрали.
    internal static ProviderBalance? ParseKimiUsages(JsonElement root)
    {
        // Недельное окно — корневое "usage"
        var weekly = root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
            ? ParseKimiWindow(usageEl) : null;

        // Основное — самое короткое окно из limits[] (окно 300 мин = 5-часовое)
        KimiWindow? primary = null;
        if (root.TryGetProperty("limits", out var limitsEl) && limitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limitsEl.EnumerateArray())
            {
                if (!item.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
                    continue;
                var w = ParseKimiWindow(detail);
                if (w is null) continue;
                var minutes = WindowMinutes(item);
                if (primary is null || minutes < primary.Value.Minutes)
                    primary = w.Value with { Minutes = minutes };
            }
        }
        // Без limits[] живём на одном недельном окне
        primary ??= weekly is { } w0 ? w0 with { Minutes = double.MaxValue } : null;
        if (primary is null) return null;

        var fmt = (double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var p = primary.Value;
        // Вторым окном отдаём неделю, только когда она не совпала с основным
        var hasSecondary = weekly is { } wk && wk != p;
        return new ProviderBalance(true, "%", fmt(p.RemainingPct), ResetsAt: p.ResetsAt,
            SecondaryLabel: hasSecondary ? "остаток квоты · неделя" : null,
            SecondaryValue: hasSecondary ? fmt(weekly!.Value.RemainingPct) : null,
            SecondaryResetsAt: hasSecondary ? weekly!.Value.ResetsAt : null);
    }

    // Одно окно квоты Kimi: остаток в процентах (нормализуем к 0..100, даже если limit ≠ 100) + сброс
    private readonly record struct KimiWindow(double RemainingPct, DateTime? ResetsAt, double Minutes);

    private static KimiWindow? ParseKimiWindow(JsonElement el)
    {
        var limit = ReadNumber(el, "limit");
        var remaining = ReadNumber(el, "remaining");
        // remaining может отсутствовать — выводим из limit − used
        if (double.IsNaN(remaining) && !double.IsNaN(limit))
        {
            var used = ReadNumber(el, "used");
            if (!double.IsNaN(used)) remaining = limit - used;
        }
        if (double.IsNaN(remaining) || double.IsNaN(limit) || limit <= 0) return null;

        DateTime? resetsAt = el.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String
            && DateTime.TryParse(rt.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime() : null;
        return new KimiWindow(Math.Clamp(remaining / limit * 100, 0, 100), resetsAt, double.MaxValue);
    }

    // Длительность окна limits[] в минутах (для выбора самого короткого); неизвестная — бесконечность
    private static double WindowMinutes(JsonElement limitItem)
    {
        if (!limitItem.TryGetProperty("window", out var w) || w.ValueKind != JsonValueKind.Object)
            return double.MaxValue;
        var duration = ReadNumber(w, "duration");
        if (double.IsNaN(duration) || duration <= 0) return double.MaxValue;
        var unit = w.TryGetProperty("timeUnit", out var u) ? u.GetString() ?? "" : "";
        return unit.Contains("SECOND", StringComparison.OrdinalIgnoreCase) ? duration / 60
            : unit.Contains("HOUR", StringComparison.OrdinalIgnoreCase) ? duration * 60
            : unit.Contains("DAY", StringComparison.OrdinalIgnoreCase) ? duration * 24 * 60
            : duration; // TIME_UNIT_MINUTE и неизвестные считаем минутами
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
                // Точки чужой валюты отбрасываем: смена ряда (у Kimi pay-per-token USD → квота
                // подписки в %) делает старую историю несопоставимой — график бы смешал шкалы
                list = list.Where(s => s.Timestamp >= cutoff && s.Currency == balance.Currency).ToList();
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
