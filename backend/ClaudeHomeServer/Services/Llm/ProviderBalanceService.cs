using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// AsOf — UTC-время последнего УСПЕШНОГО обновления (при отдаче протухшего кэша остаётся
// временем того успешного обновления — фронт показывает свежесть данных).
// ResetsAt — момент сброса ОСНОВНОГО (самого короткого) окна квоты, null — не применимо.
// Windows — все окна квоты подписки списком произвольной длины (GLM — три: 5 часов + неделя
// + месячный лимит веб-инструментов; Kimi/MiniMax — два; Alibaba, если когда-нибудь отдаст
// квоту, — три); у провайдеров с денежным балансом (deepseek/moonshot/openrouter) остаётся
// пустым — там TotalBalance и есть весь ответ.
// TrackHistory — годится ли TotalBalance точкой в историю графика: false, когда основным
// стало НЕ то окно, что обычно (провайдер не отдал короткое), иначе в один ряд попали бы
// точки разных окон и график запрыгал бы между шкалами.
//
// GrantedBalance — подарочный остаток (DeepSeek), валюта общая из Currency.
// PlanLabel — уровень подписки строкой без приставки («Advanced» у Kimi); подпись рисует фронт.
// KeyLimit — денежный лимит ключа (OpenRouter): это ПРЕДЕЛ, а не примечание, фронт рисует его
//   шкалой с предупреждением, как квоту.
// Spend — расход по данным самого провайдера (OpenRouter /key), НЕ наш SpendStore: у него своя
//   правда, фронт подписывает «по данным OpenRouter».
// Health — здоровье пула бесплатных моделей (FreeLLM): трафик за 24ч и состав живых платформ.
public sealed record ProviderBalance(bool Available, string Currency, string TotalBalance,
    DateTime AsOf = default, DateTime? ResetsAt = null,
    IReadOnlyList<ProviderQuotaWindow>? Windows = null, bool TrackHistory = true,
    double? GrantedBalance = null, string? PlanLabel = null,
    ProviderKeyLimit? KeyLimit = null, ProviderSpend? Spend = null,
    ProviderHealth? Health = null)
{
    // true — баланс это КВОТА (процент/счётчик), а не сумма в валюте. Whitelist: подтверждено только
    // для "%" и "count"; пустая и неразобранная валюта → false (считаем деньгами). По нему контроллер
    // режет историю не-админу — «по умолчанию закрыто» (см. GetUsage): сбой провайдера не должен
    // раскрывать кошелёк. WithoutMoney() срезает денежные поля безусловно, это правило — только истории.
    public bool IsQuota => Currency is "%" or "count";

    // Вид без денег для не-админа: убираем баланс, валюту, подарочный остаток, лимит ключа и расход
    // провайдера. Остаются квоты окон, сроки сброса, PlanLabel, Health — они объясняют поведение
    // моделей и денег не раскрывают.
    public object WithoutMoney() => new
    {
        Available, AsOf, ResetsAt, Windows, TrackHistory, PlanLabel, Health,
    };
}

// Одно окно квоты подписки: подпись для UI, значение уже отформатированной строкой,
// момент сброса и единица — "percent" (остаток в %, как у GLM/Kimi/MiniMax) или "count"
// (число вызовов модели со знаменателем "120/300", как задумано для Alibaba Coding Plan) —
// фронт по Unit выбирает, как рисовать значение, и не пишет "токенов" там, где их нет.
public sealed record ProviderQuotaWindow(string Label, string Value, DateTime? ResetsAt, string Unit);

// Денежный лимит ключа провайдера (OpenRouter /key): сколько ещё осталось из общего лимита.
// Отдельная от ProviderQuotaWindow модель: это не процент и не счётчик вызовов, а пара сумм в валюте.
public sealed record ProviderKeyLimit(double Remaining, double Total);

// Расход по данным самого провайдера (OpenRouter /key): ежедневный/недельный/месячный в валюте.
public sealed record ProviderSpend(double Daily, double Weekly, double Monthly);

// Здоровье пула бесплатных моделей (FreeLLM): трафик за 24ч и состав живых платформ. Поля
// nullable — соответствующий источник (provider_health / usage_summary) мог не ответить; фронт
// живёт без каждого по отдельности. Сам Health есть, если разобралось хоть что-то.
public sealed record ProviderHealth(double? Requests24h, double? SuccessRate,
    int? PlatformsAlive, int? PlatformsTotal);

// Точка истории баланса — для графика на экране «Использование»
public sealed record ProviderBalanceSnapshot(DateTime Timestamp, double Balance, string Currency);

/// <summary>Контракт сервиса баланса для контроллера и подмены в тестах ролей.</summary>
public interface IProviderBalanceService
{
    LlmProviderConfig? GetSupported(string key);
    Task<ProviderBalance?> GetAsync(string key, CancellationToken ct);
    IReadOnlyList<ProviderBalanceSnapshot> GetSnapshots(string key);
}

// Состояние аккаунта CLI-провайдера. Источник задаётся конфигом провайдера (Balance):
// "deepseek" — GET {ApiBaseUrl}/user/balance; "moonshot" — GET {ApiBaseUrl}/users/me/balance;
// "openrouter" — GET {ApiBaseUrl}/credits (деньги); "glm" — GET {BalanceUrl} (квота подписки
// Coding Plan: окна токенов 5 часов + неделя в %, плюс месячный лимит вызовов веб-инструментов
// count "currentValue/usage", все три в Windows); "kimi" — GET {ApiBaseUrl}/usages (квота подписки
// Kimi for Coding: 5-часовое окно + недельное, оба в Windows); "minimax" — GET {BalanceUrl}
// с фолбэком на https://www.minimax.io/v1/token_plan/remains (квота Token Plan: интервальное
// окно + недельное, оба в Windows). Провайдер без источника — баланс недоступен (UI скрывает блок). Кэш 5 мин;
// каждое успешное обновление пишет снапшот в data/provider-usage-{key}.json (история для графика,
// по основному — самому короткому — окну).
public class ProviderBalanceService(IHttpClientFactory httpFactory, LlmProviderRegistry providers,
    IConfiguration config) : IProviderBalanceService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(8);
    // Пауза после отказа провайдера: запрос идёт с таймаутом 10с и сериализуется на семафоре,
    // так что без неё три захода на экран подряд заставляли третий ждать полминуты
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(45);

    private sealed class ProviderCache
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public ProviderBalance? Cached;
        public DateTime CachedAt;
        public DateTime? FailedAt;
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

        // Ключ кэша — в нижнем регистре: тот же экземпляр (и его UsageLock) достаёт GetSnapshots
        var cache = _caches.GetOrAdd(p.Key.ToLowerInvariant(), _ => new ProviderCache());
        if (cache.Cached is not null && DateTime.UtcNow - cache.CachedAt < CacheTtl) return cache.Cached;

        await cache.Lock.WaitAsync(ct);
        try
        {
            if (cache.Cached is not null && DateTime.UtcNow - cache.CachedAt < CacheTtl) return cache.Cached;
            // Негативный кэш: провайдер только что не ответил — не стучимся в него на каждый
            // заход экрана и домашнего виджета, отдаём то, что есть (возможно, ничего)
            if (cache.FailedAt is { } failedAt && DateTime.UtcNow - failedAt < FailureBackoff)
                return cache.Cached;

            var balance = p.Balance switch
            {
                "deepseek" => await FetchDeepSeekAsync(p, ct),
                "moonshot" => await FetchMoonshotAsync(p, ct),
                "openrouter" => await FetchOpenRouterAsync(p, ct),
                "glm" => await FetchGlmAsync(p, ct),
                "kimi" => await FetchKimiAsync(p, ct),
                "minimax" => await FetchMiniMaxAsync(p, ct),
                "freellm" => await FetchFreeLlmAsync(p, ct),
                _ => null,
            };
            // Протухший лучше, чем ничего: AsOf в нём остаётся временем прошлого
            // успешного обновления — фронт по нему покажет, что данные не свежие
            if (balance is null)
            {
                cache.FailedAt = DateTime.UtcNow;
                return cache.Cached;
            }

            cache.FailedAt = null;
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
            double? granted = null;
            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array
                && infos.GetArrayLength() > 0)
            {
                var first = infos[0];
                currency = first.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                total = first.TryGetProperty("total_balance", out var t) ? t.GetString() ?? "" : "";
                granted = DeepSeekGrantedBalance(first);
            }
            return new ProviderBalance(available, currency, total, GrantedBalance: granted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Подарочный остаток DeepSeek (internal — под тестами): granted_balance > 0, иначе null
    // (ноль/отсутствие не шумим). Живой запрос требует ключа, формат фиксируем фикстурой.
    internal static double? DeepSeekGrantedBalance(JsonElement info)
    {
        var granted = ReadNumber(info, "granted_balance");
        return double.IsNaN(granted) || granted <= 0 ? null : granted;
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
            // /key самостоятелен: его отказ НЕ должен ронять деньги из /credits
            var key = await TryReadOpenRouterKeyAsync(p, ct);
            return new ProviderBalance(remaining > 0, "USD",
                remaining.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                KeyLimit: key?.KeyLimit, Spend: key?.Spend);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Второй запрос баланса OpenRouter — GET {ApiBaseUrl}/key: расход по периодам и лимит ключа.
    // /key самостоятелен и НЕ должен ронять основной баланс из /credits: не ответил/не разобрался
    // → Spend/KeyLimit нет, деньги живут как раньше. Таймаут/ошибки — как у соседей (10с, swallow в null)
    private async Task<OpenRouterKeyData?> TryReadOpenRouterKeyAsync(LlmProviderConfig p, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/key");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            return ParseOpenRouterKey(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить расход ключа {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Разбор ответа GET /key OpenRouter (internal — под тестами): { data: { usage_daily,
    // usage_weekly, usage_monthly, limit, limit_remaining } }. Расход (Spend) и лимит ключа
    // (KeyLimit) — независимые поля: не разобрался один — нет только он, а не весь результат.
    internal sealed record OpenRouterKeyData(ProviderSpend? Spend, ProviderKeyLimit? KeyLimit);

    internal static OpenRouterKeyData? ParseOpenRouterKey(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        // Spend — все три периода обязаны разобраться, иначе поля нет (лучше никакой, чем частичный)
        ProviderSpend? spend = null;
        var daily = ReadNumber(data, "usage_daily");
        var weekly = ReadNumber(data, "usage_weekly");
        var monthly = ReadNumber(data, "usage_monthly");
        if (!double.IsNaN(daily) && !double.IsNaN(weekly) && !double.IsNaN(monthly))
            spend = new ProviderSpend(daily, weekly, monthly);

        // KeyLimit — limit приходит null (лимита нет) либо числом; null/мусор/нет remaining → поля нет
        ProviderKeyLimit? keyLimit = null;
        if (data.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number)
        {
            var total = limEl.GetDouble();
            var remaining = ReadNumber(data, "limit_remaining");
            if (!double.IsNaN(remaining))
                keyLimit = new ProviderKeyLimit(remaining, total);
        }

        return spend is null && keyLimit is null ? null : new OpenRouterKeyData(spend, keyLimit);
    }

    // Число из JSON, приходящее как number или как строка; NaN — поля нет либо не разобрать.
    // ValueKind проверяем явно: GetString() на bool/object/array бросает, а разборщики
    // ответов обязаны переживать любой мусор от провайдера, а не падать целиком
    private static double ReadNumber(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return double.NaN;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind != JsonValueKind.String) return double.NaN;
        return double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private const double WeekMinutes = 7 * 24 * 60;

    // Подпись окна выводим из ФАКТИЧЕСКОЙ длительности, а не из позиции в списке: провайдер
    // может перестать отдавать короткое окно, и «5 часов» над недельным значением — прямая
    // ложь пользователю. Длительность неизвестна — «Окно квоты» без периода.
    // В подписи ТОЛЬКО период: расход это или остаток, решает экран (он приводит все окна
    // к языку расхода), поэтому оценочных слов вроде «остаток квоты» тут быть не должно.
    private static string WindowLabel(double? minutes)
    {
        if (minutes is not { } m || double.IsNaN(m) || double.IsInfinity(m) || m <= 0)
            return "Окно квоты";
        if (Math.Abs(m - WeekMinutes) < 60) return "Неделя";
        if (m >= 28 * 24 * 60 && m <= 31 * 24 * 60) return "Месяц";
        if (m >= 24 * 60 && Math.Abs(m % (24 * 60)) < 1)
            return Plural(m / (24 * 60), "день", "дня", "дней");
        if (m >= 60 && Math.Abs(m % 60) < 1)
            return Plural(m / 60, "час", "часа", "часов");
        return Plural(m, "минута", "минуты", "минут");
    }

    // Русская форма числительного: 1 час, 2 часа, 5 часов
    private static string Plural(double value, string one, string few, string many)
    {
        var n = (long)Math.Round(value);
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? many
            : mod10 == 1 ? one
            : mod10 is >= 2 and <= 4 ? few
            : many;
        return $"{n} {word}";
    }

    // Формат GLM (z.ai Coding Plan, недокументированный монитор):
    // GET {BalanceUrl} → { data: { limits: [ {type:"TOKENS_LIMIT", unit, number, percentage,
    // nextResetTime}, {type:"TIME_LIMIT", unit, number, currentValue, usage, nextResetTime, ...} ] } }.
    // TOKENS_LIMIT (unit=3 — часы, unit=6 — недели) → окна остатка токенов в процентах;
    // TIME_LIMIT (unit=5 — месячный лимит вызовов веб-инструментов подписки search-prime/
    // web-reader/zread) → окно count "currentValue/usage". Общей безоконной квоты токенов нет.
    // Хедер Authorization БЕЗ префикса "Bearer". Разбор — ParseGlmQuota (фиксируется тестами).
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
            return ParseGlmQuota(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Разбор ответа квоты GLM (internal — под тестами). null — массива limits нет либо ни одного
    // percent-окна (TOKENS_LIMIT) не разобрали: основного баланса в процентах без него нет.
    // TOKENS_LIMIT-окна идут списком, отсортированными по длительности (короткое первым, с
    // неизвестной длительностью — в конец); TIME_LIMIT достраивается последним как count-окно.
    internal static ProviderBalance? ParseGlmQuota(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
        if (!data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return null;

        // (окно, длительность в минутах, флаг «часовое») — длительность нужна для сортировки,
        // флаг — для решения, вести ли историю (иначе в один ряд легли бы проценты разных окон)
        var tokenWindows = new List<(ProviderQuotaWindow Window, double? Minutes, bool Hourly)>();
        ProviderQuotaWindow? toolWindow = null;
        foreach (var item in limits.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;
            var type = t.GetString();

            if (string.Equals(type, "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                // percentage — израсходовано; показываем остаток (100 − percentage)
                var pct = ReadNumber(item, "percentage");
                if (double.IsNaN(pct)) continue; // не разобрать процент — окно пропускаем, не роняя остальные
                var (minutes, hourly) = GlmWindowSpan(item);
                var value = Math.Clamp(100 - pct, 0, 100)
                    .ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                tokenWindows.Add((new ProviderQuotaWindow(WindowLabel(minutes), value,
                    ReadUnixTime(item, "nextResetTime"), "percent"), minutes, hourly));
            }
            else if (string.Equals(type, "TIME_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                // Месячный лимит вызовов веб-инструментов подписки — НЕ токены: значение
                // "currentValue/usage" (фронт по unit=count допишет «запросов»). Подпись — НЕ
                // период: «Месяц» рядом с токен-окнами читался бы как общая месячная квота токенов
                var current = ReadNumber(item, "currentValue");
                var usage = ReadNumber(item, "usage");
                if (double.IsNaN(current) || double.IsNaN(usage)) continue; // не разобрать числа — окно не добавляем
                toolWindow = new ProviderQuotaWindow("Веб-инструменты", $"{(long)current}/{(long)usage}",
                    ReadUnixTime(item, "nextResetTime"), "count");
            }
        }

        if (tokenWindows.Count == 0) return null; // percent-окна нет — основной баланс показать нечем

        // Короткое окно первым; неизвестная длительность (null) уходит в конец
        tokenWindows.Sort((a, b) => (a.Minutes ?? double.MaxValue).CompareTo(b.Minutes ?? double.MaxValue));

        var windows = tokenWindows.Select(w => w.Window).ToList();
        if (toolWindow is not null) windows.Add(toolWindow);

        var primary = tokenWindows[0];
        // История — только когда основное окно часовое (unit=3): иначе (провайдер отдал одно
        // недельное) в общий ряд легли бы проценты разных окон, и график запрыгал бы между шкалами
        return new ProviderBalance(true, "%", primary.Window.Value, ResetsAt: primary.Window.ResetsAt,
            Windows: windows, TrackHistory: primary.Hourly);
    }

    // Длительность окна GLM в минутах по паре (unit, number): unit=3 — часы, unit=6 — недели.
    // Код unit недокументирован — маппим только наблюдённые значения; неизвестный → null, тогда
    // подпись окна идёт без периода («Окно квоты»). Hourly = окно часовое (unit=3) — по нему
    // решаем, вести ли историю; у недельного и неизвестного она не ведётся
    private static (double? Minutes, bool Hourly) GlmWindowSpan(JsonElement item)
    {
        var unit = ReadNumber(item, "unit");
        var number = ReadNumber(item, "number");
        if (double.IsNaN(unit) || double.IsNaN(number) || number <= 0) return (null, false);
        var perUnit = unit switch
        {
            3 => 60.0,            // час
            6 => 7 * 24 * 60.0,   // неделя
            _ => (double?)null    // неизвестный код — длительность не выдумываем
        };
        return perUnit is { } m ? (m * number, unit == 3) : (null, false);
    }

    // Формат Kimi for Coding (подписка kimi.com, недокументированный эндпоинт):
    // GET {ApiBaseUrl}/usages →
    // { usage: {limit, used, remaining, resetTime},              // недельное окно
    //   limits: [{ window: {duration, timeUnit}, detail: {limit, used, remaining, resetTime} }] }
    // Числа приходят СТРОКАМИ; limit=100 — шкала уже процентная. Основным считаем самое
    // короткое окно из limits[] (5-часовое, 300 мин) — как у GLM; недельное — вторым окном в Windows.
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

    // Формат MiniMax Coding/Token Plan (недокументированный эндпоинт консоли):
    // GET https://www.minimax.io/v1/token_plan/remains, Authorization: Bearer <ApiKey> →
    // { model_remains: [ { model_name, end_time, current_interval_remaining_percent,
    //                       weekly_end_time, current_weekly_remaining_percent, ... }, ... ],
    //   base_resp: { status_code, status_msg } }
    // model_remains содержит несколько строк по типу ресурса (наблюдались "general" и "video" —
    // видеогенерация к CLI-квоте отношения не имеет); берём строку "general". end_time/
    // weekly_end_time — unix-миллисекунды. Формат зафиксирован живым запросом 04.08.2026,
    // фикстура — MiniMaxRemainsTests.
    private const string MiniMaxRemainsUrl = "https://www.minimax.io/v1/token_plan/remains";

    private async Task<ProviderBalance?> FetchMiniMaxAsync(LlmProviderConfig p, CancellationToken ct)
    {
        // Адрес — из конфига (BalanceUrl), как у GLM: переезд ручки не должен требовать пересборки
        var url = string.IsNullOrWhiteSpace(p.BalanceUrl) ? MiniMaxRemainsUrl : p.BalanceUrl;
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            return ParseMiniMaxRemains(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Разбор ответа /v1/token_plan/remains MiniMax (internal — под тестами).
    // null — status_code != 0, строки "general" нет, либо оба окна не разобрать.
    internal static ProviderBalance? ParseMiniMaxRemains(JsonElement root)
    {
        if (root.TryGetProperty("base_resp", out var baseResp) && baseResp.ValueKind == JsonValueKind.Object
            && baseResp.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number
            && sc.GetInt32() != 0)
            return null;
        if (!root.TryGetProperty("model_remains", out var remainsEl) || remainsEl.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement general = default;
        var found = false;
        foreach (var item in remainsEl.EnumerateArray())
        {
            if (item.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String
                && string.Equals(mn.GetString(), "general", StringComparison.OrdinalIgnoreCase))
            {
                general = item;
                found = true;
                break;
            }
        }
        if (!found) return null;

        var fiveHourPct = ReadNumber(general, "current_interval_remaining_percent");
        var weeklyPct = ReadNumber(general, "current_weekly_remaining_percent");
        if (double.IsNaN(fiveHourPct) && double.IsNaN(weeklyPct)) return null;

        var fmt = (double v) => Math.Clamp(v, 0, 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        // Длительность интервального окна считаем по самому ответу (start_time..end_time):
        // нет границ — подпись идёт без периода, «пять часов» из воздуха не берём.
        // У недельного окна семантика в имени полей (weekly_*), поэтому при неразобранных
        // границах остаётся неделя
        var intervalMinutes = SpanMinutes(general, "start_time", "end_time");
        var weeklyMinutes = SpanMinutes(general, "weekly_start_time", "weekly_end_time") ?? WeekMinutes;
        var windows = new List<ProviderQuotaWindow>();
        if (!double.IsNaN(fiveHourPct))
            windows.Add(new ProviderQuotaWindow(WindowLabel(intervalMinutes), fmt(fiveHourPct),
                ReadUnixTime(general, "end_time"), "percent"));
        if (!double.IsNaN(weeklyPct))
            windows.Add(new ProviderQuotaWindow(WindowLabel(weeklyMinutes), fmt(weeklyPct),
                ReadUnixTime(general, "weekly_end_time"), "percent"));
        if (windows.Count == 0) return null;

        var primary = windows[0];
        // В историю точка идёт, только когда основное окно — интервальное: иначе (провайдер
        // отдал одно недельное) в один ряд легли бы проценты разных окон
        return new ProviderBalance(true, "%", primary.Value, ResetsAt: primary.ResetsAt, Windows: windows,
            TrackHistory: !double.IsNaN(fiveHourPct));
    }

    // Формат FreeLLM (локальный роутер бесплатных моделей, нет квот/денег): состояние пула и
    // трафик читаются через его MCP POST {BalanceUrl} (JSON-RPC 2.0 stateless, авторизация —
    // тот же unified-ключ, что в ApiKey, Bearer). URL по умолчанию — ApiBaseUrl без хвоста /v1 + /mcp.
    // Два вызова tools/call: provider_health → состав платформ (окно count «Провайдеры» + Health);
    // usage_summary range=24h → трафик за 24ч (часть Health).
    // Один упал — живём на втором; оба → null. TrackHistory false: это не расход, в историю точки не идут.
    private async Task<ProviderBalance?> FetchFreeLlmAsync(LlmProviderConfig p, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(p.BalanceUrl)
            ? $"{FreeLlmBase(p.ApiBaseUrl)}/mcp" : p.BalanceUrl;
        try
        {
            var client = httpFactory.CreateClient("llm-provider");

            // provider_health → состав платформ (живых/всего), флаг Available и окно «Провайдеры» (count).
            // жива платформа с keys.healthy > 0
            (int Alive, int Total)? platforms = null;
            var available = false;
            if (await CallFreeLlmToolAsync(client, url, p.ApiKey, "provider_health", null, ct) is { } health
                && ParseFreeLlmHealth(health) is { } h)
            {
                platforms = h;
                available = h.Alive > 0;
            }

            // usage_summary range=24h → трафик за 24ч (часть Health)
            FreeLlmUsageData? usage = null;
            if (await CallFreeLlmToolAsync(client, url, p.ApiKey, "usage_summary", new { range = "24h" }, ct)
                is { } usageEl)
                usage = ParseFreeLlmUsageData(usageEl);

            // Оба вызова ничего не дали → баланса нет
            if (platforms is null && usage is null) return null;

            var value = platforms is { } pp ? $"{pp.Alive}/{pp.Total}" : null;
            var windows = value is null ? null : new List<ProviderQuotaWindow>
            {
                new("Провайдеры", value, null, "count")
            };
            return new ProviderBalance(available, "count", value ?? "", ResetsAt: null,
                Windows: windows, TrackHistory: false,
                Health: ComposeFreeLlmHealth(platforms, usage));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProviderBalance] Не удалось получить баланс {p.Key}: {ex.Message}");
            return null;
        }
    }

    // Базовый URL FreeLLM без хвоста /v1 — MCP-ручка живёт в корне сервиса, а не под OpenAPI-префиксом
    private static string FreeLlmBase(string apiBaseUrl)
    {
        var b = apiBaseUrl.TrimEnd('/');
        const string suffix = "/v1";
        return b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? b[..^suffix.Length] : b;
    }

    // Один вызов tools/call к MCP FreeLLM: result.content[0].text — pretty-printed JSON, его и
    // возвращаем корневым элементом (Clone — переживает dispose внутреннего документа). null —
    // ответ не JSON-RPC, нет result/content/text или text не JSON (мусор). Таймаут 10с как у соседей
    private async Task<JsonElement?> CallFreeLlmToolAsync(HttpClient client, string url, string apiKey,
        string tool, object? arguments, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = System.Net.Http.Json.JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = arguments ?? new { } },
        });
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await client.SendAsync(req, timeoutCts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0)
            return null;
        if (!content[0].TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            return null;
        var text = t.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { using var inner = JsonDocument.Parse(text!); return inner.RootElement.Clone(); }
        catch { return null; }
    }

    // Разбор provider_health FreeLLM (internal — под тестами): { "<platform>": { keys: {healthy, …},
    // … }, … }. Платформа жива, если keys.healthy > 0. Возвращаем (живых/всего); null — пусто/не объект
    internal static (int Alive, int Total)? ParseFreeLlmHealth(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var alive = 0;
        var total = 0;
        foreach (var prop in root.EnumerateObject())
        {
            total++;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object)
                continue;
            var healthy = ReadNumber(keys, "healthy");
            if (!double.IsNaN(healthy) && healthy > 0) alive++;
        }
        return total > 0 ? (alive, total) : null;
    }

    // Разбор usage_summary FreeLLM (internal — под тестами): { requests, success_rate }.
    // requests обязателен — без него данных нет; success_rate опционален. Health берёт оба.
    internal sealed record FreeLlmUsageData(double Requests24h, double? SuccessRate);

    internal static FreeLlmUsageData? ParseFreeLlmUsageData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var requests = ReadNumber(root, "requests");
        if (double.IsNaN(requests)) return null;
        var success = ReadNumber(root, "success_rate");
        return new FreeLlmUsageData(requests, double.IsNaN(success) ? null : success);
    }

    // Сборка Health FreeLLM из двух источников: provider_health (платформы) и usage_summary (трафик).
    // Health есть, если разобралось хоть что-то — фронт живёт без каждого поля по отдельности.
    internal static ProviderHealth? ComposeFreeLlmHealth((int Alive, int Total)? platforms, FreeLlmUsageData? usage)
    {
        if (platforms is null && usage is null) return null;
        return new ProviderHealth(usage?.Requests24h, usage?.SuccessRate,
            platforms?.Alive, platforms?.Total);
    }

    // Unix-время из числового поля; порог отличает миллисекунды от секунд (как у GLM).
    // Отсутствует/нечисловое/<=0/вне диапазона дат — null: непонятное время сброса гасит
    // ТОЛЬКО ResetsAt, а не окно и не весь баланс (иначе один кривой таймстемп оставлял
    // экран пустым при полностью разобранных процентах)
    private static DateTime? ReadUnixTime(JsonElement obj, string name)
    {
        var v = ReadNumber(obj, name);
        if (double.IsNaN(v) || v <= 0) return null;
        try
        {
            return v > 100_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)v).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds((long)v).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // Длительность окна в минутах по паре границ; неразобранные границы — null (неизвестна)
    private static double? SpanMinutes(JsonElement obj, string startName, string endName)
    {
        if (ReadUnixTime(obj, startName) is not { } start || ReadUnixTime(obj, endName) is not { } end)
            return null;
        var minutes = (end - start).TotalMinutes;
        return minutes > 0 ? minutes : null;
    }

    // Разбор ответа /usages Kimi (internal — под тестами). null — ни одного окна не разобрали.
    internal static ProviderBalance? ParseKimiUsages(JsonElement root)
    {
        // Недельное окно — корневое "usage" (длительность в ответе не приходит, она в контракте)
        var weekly = root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
            ? ParseKimiWindow(usageEl, WeekMinutes) : null;

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
                // Окно неизвестной длины считаем самым длинным — короткое всегда выигрывает
                if (primary is null || (minutes ?? double.MaxValue) < (primary.Value.Minutes ?? double.MaxValue))
                    primary = w.Value with { Minutes = minutes };
            }
        }
        // Без limits[] живём на одном недельном окне — и подписываем его неделей, а не пятью
        // часами: длительность корневого usage задана контрактом эндпоинта
        var fromLimits = primary is not null;
        primary ??= weekly;
        if (primary is not { } p) return null;

        var fmt = (double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var windows = new List<ProviderQuotaWindow>
            { new(WindowLabel(p.Minutes), fmt(p.RemainingPct), p.ResetsAt, "percent") };
        // Вторым окном отдаём неделю, только когда она не совпала с основным. Сравниваем
        // явно по значению и сбросу: равенство record struct тянет ещё и Minutes, из-за чего
        // совпавшее окно то задваивалось, то пропадало
        if (weekly is { } wk && (wk.RemainingPct != p.RemainingPct || wk.ResetsAt != p.ResetsAt))
            windows.Add(new ProviderQuotaWindow(WindowLabel(wk.Minutes), fmt(wk.RemainingPct), wk.ResetsAt, "percent"));
        // Параллельные сессии — отдельное count-окно последним (после квотных): «занято/лимит».
        // limit и занятость (details.length) приходят строками — через ReadNumber, как весь ответ Kimi
        var parallel = KimiParallelWindow(root);
        if (parallel is not null) windows.Add(parallel);
        // Уровень подписки → поле PlanLabel (без приставки «Подписка:», её рисует интерфейс)
        var plan = KimiPlanLabel(root);
        // История — только по окну из limits[]: свалившись на недельное, мы писали бы в тот же
        // ряд проценты другого окна
        return new ProviderBalance(true, "%", fmt(p.RemainingPct), ResetsAt: p.ResetsAt, Windows: windows,
            TrackHistory: fromLimits, PlanLabel: plan);
    }

    // Одно окно квоты Kimi: остаток в процентах (нормализуем к 0..100, даже если limit ≠ 100),
    // сброс и длительность окна (null — неизвестна)
    private readonly record struct KimiWindow(double RemainingPct, DateTime? ResetsAt, double? Minutes);

    private static KimiWindow? ParseKimiWindow(JsonElement el, double? minutes = null)
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

        // Время читаем как UTC даже без суффикса Z: RoundtripKind оставил бы Kind=Unspecified,
        // и ToUniversalTime() посчитал бы его локальным — сброс уезжал на часовой пояс машины
        DateTime? resetsAt = el.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(rt.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.UtcDateTime : null;
        return new KimiWindow(Math.Clamp(remaining / limit * 100, 0, 100), resetsAt, minutes);
    }

    // Длительность окна limits[] в минутах (для выбора самого короткого); null — неизвестна
    private static double? WindowMinutes(JsonElement limitItem)
    {
        if (!limitItem.TryGetProperty("window", out var w) || w.ValueKind != JsonValueKind.Object)
            return null;
        var duration = ReadNumber(w, "duration");
        if (double.IsNaN(duration) || duration <= 0) return null;
        var unit = w.TryGetProperty("timeUnit", out var u) ? u.GetString() ?? "" : "";
        return unit.Contains("SECOND", StringComparison.OrdinalIgnoreCase) ? duration / 60
            : unit.Contains("HOUR", StringComparison.OrdinalIgnoreCase) ? duration * 60
            : unit.Contains("DAY", StringComparison.OrdinalIgnoreCase) ? duration * 24 * 60
            : duration; // TIME_UNIT_MINUTE и неизвестные считаем минутами
    }

    // Окно параллельных сессий Kimi: { parallel: { limit, details: [...] } } → count «занято/лимит»,
    // сброс неприменим. limit (строка) и длина details (массив) — неразбор хотя бы одного → null
    private static ProviderQuotaWindow? KimiParallelWindow(JsonElement root)
    {
        if (!root.TryGetProperty("parallel", out var par) || par.ValueKind != JsonValueKind.Object)
            return null;
        var limit = ReadNumber(par, "limit");
        if (double.IsNaN(limit) || limit <= 0) return null;
        var used = par.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Array
            ? (double)det.GetArrayLength() : double.NaN;
        if (double.IsNaN(used)) return null;
        return new ProviderQuotaWindow("Параллельные сессии", $"{(long)used}/{(long)limit}", null, "count");
    }

    // Уровень подписки Kimi (internal — под тестами): user.membership.level приходит как
    // LEVEL_<ИМЯ>; <ИМЯ> разворачиваем с большой буквы (LEVEL_ADVANCED → «Advanced»). Без
    // приставки «Подписка:» — её рисует интерфейс. Чужой формат уровня — null
    internal static string? KimiPlanLabel(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("membership", out var mem) || mem.ValueKind != JsonValueKind.Object
            || !mem.TryGetProperty("level", out var lvl) || lvl.ValueKind != JsonValueKind.String)
            return null;
        var level = lvl.GetString() ?? "";
        const string prefix = "LEVEL_";
        if (!level.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || level.Length <= prefix.Length)
            return null;
        var name = level[prefix.Length..]; // STANDARD, PRO, FREE …
        return char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }

    // История баланса за последние дни — для графика на экране «Использование»
    public IReadOnlyList<ProviderBalanceSnapshot> GetSnapshots(string key)
    {
        var cache = _caches.GetOrAdd(key.ToLowerInvariant(), _ => new ProviderCache());
        lock (cache.UsageLock)
            return LoadSnapshots(key);
    }

    // Регистр ключа приводим здесь: реестр провайдеров регистронезависим, и без этого
    // /api/providers/MiniMax/usage писал бы один файл, а читал другой (на Linux — пустой график)
    private string UsagePath(string key) => Path.Combine(_dataDir, $"provider-usage-{key.ToLowerInvariant()}.json");
    // Прежнее имя файла истории DeepSeek — читаем, если нового ещё нет
    private string LegacyDeepSeekPath => Path.Combine(_dataDir, "deepseek-usage.json");

    private void RecordSnapshot(string key, ProviderCache cache, ProviderBalance balance)
    {
        // Точка не того окна в общий ряд не идёт: график «израсходовано окна» иначе прыгал бы
        // между шкалами разных окон (см. TrackHistory)
        if (!balance.TrackHistory) return;
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
        if (!File.Exists(path) && string.Equals(key, "deepseek", StringComparison.OrdinalIgnoreCase)
            && File.Exists(LegacyDeepSeekPath))
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
