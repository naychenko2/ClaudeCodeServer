using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// AsOf — UTC-время последнего УСПЕШНОГО обновления (при отдаче протухшего кэша остаётся
// временем того успешного обновления — фронт показывает свежесть данных).
// ResetsAt — момент сброса ОСНОВНОГО (самого короткого) окна квоты, null — не применимо.
// Windows — все окна квоты подписки списком произвольной длины (GLM — одно, Kimi/MiniMax —
// два, Alibaba, если когда-нибудь отдаст квоту, — три); у провайдеров с денежным балансом
// (deepseek/moonshot/openrouter) остаётся пустым — там TotalBalance и есть весь ответ.
// TrackHistory — годится ли TotalBalance точкой в историю графика: false, когда основным
// стало НЕ то окно, что обычно (провайдер не отдал короткое), иначе в один ряд попали бы
// точки разных окон и график запрыгал бы между шкалами.
public sealed record ProviderBalance(bool Available, string Currency, string TotalBalance,
    DateTime AsOf = default, DateTime? ResetsAt = null,
    IReadOnlyList<ProviderQuotaWindow>? Windows = null, bool TrackHistory = true);

// Одно окно квоты подписки: подпись для UI, значение уже отформатированной строкой,
// момент сброса и единица — "percent" (остаток в %, как у GLM/Kimi/MiniMax) или "count"
// (число вызовов модели со знаменателем "120/300", как задумано для Alibaba Coding Plan) —
// фронт по Unit выбирает, как рисовать значение, и не пишет "токенов" там, где их нет.
public sealed record ProviderQuotaWindow(string Label, string Value, DateTime? ResetsAt, string Unit);

// Точка истории баланса — для графика на экране «Использование»
public sealed record ProviderBalanceSnapshot(DateTime Timestamp, double Balance, string Currency);

// Состояние аккаунта CLI-провайдера. Источник задаётся конфигом провайдера (Balance):
// "deepseek" — GET {ApiBaseUrl}/user/balance; "moonshot" — GET {ApiBaseUrl}/users/me/balance;
// "openrouter" — GET {ApiBaseUrl}/credits (деньги); "glm" — GET {BalanceUrl} (квота подписки
// Coding Plan, остаток в % 5-часового окна); "kimi" — GET {ApiBaseUrl}/usages (квота подписки
// Kimi for Coding: 5-часовое окно + недельное, оба в Windows); "minimax" — GET {BalanceUrl}
// с фолбэком на https://www.minimax.io/v1/token_plan/remains (квота Token Plan: интервальное
// окно + недельное, оба в Windows). Провайдер без источника — баланс недоступен (UI скрывает блок). Кэш 5 мин;
// каждое успешное обновление пишет снапшот в data/provider-usage-{key}.json (история для графика,
// по основному — самому короткому — окну).
public class ProviderBalanceService(IHttpClientFactory httpFactory, LlmProviderRegistry providers,
    IConfiguration config)
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
            var value = remaining.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            // Длительность окна в ответе не приходит (есть только момент сброса), поэтому
            // период в подписи не пишем: окно выбрано «по ближайшему сбросу», а не по длине
            return new ProviderBalance(true, "%", value, ResetsAt: resetsAt,
                Windows: [new ProviderQuotaWindow(WindowLabel(null), value, resetsAt, "percent")]);
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
        // История — только по окну из limits[]: свалившись на недельное, мы писали бы в тот же
        // ряд проценты другого окна
        return new ProviderBalance(true, "%", fmt(p.RemainingPct), ResetsAt: p.ResetsAt, Windows: windows,
            TrackHistory: fromLimits);
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
