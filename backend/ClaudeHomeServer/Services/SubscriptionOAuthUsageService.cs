using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Llm.Claude;

namespace ClaudeHomeServer.Services;

// Периодический опрос точной утилизации подписок Claude через OAuth-эндпоинт
// GET api.anthropic.com/api/oauth/usage (тот же источник, что интерактивный экран /usage CLI):
// в отличие от rate_limit_event, он отдаёт проценты ВСЕХ окон (5-часового, недельного,
// per-model Opus/Sonnet и перерасхода) с временем сброса — и без траты хода. Снимки пишутся
// в UsageService под ключ аккаунта, откуда их читают виджет «Использование» и вкладки
// аккаунтов на экране usage. rate_limit_event ходов остаётся вторым источником —
// в момент активности он свежее.
//
// Best-effort: основной аккаунт опрашивается токеном из ~/.claude/.credentials.json
// (CLI сам его обновляет; перечитываем перед каждым тиком — рефреш токена не наша забота),
// дополнительные — их OAuthToken из конфига. Не-2xx по аккаунту — пропуск тика с фиксацией
// статуса (401/403 = «токен не подходит», см. StatusOf) и логом при смене статуса:
// для такого аккаунта остаются данные warmup-хода и живых rate_limit_event.
public sealed partial class SubscriptionOAuthUsageService(
    ClaudeSubscriptionPool pool,
    UsageService usage,
    Llm.LlmProviderRegistry providers,
    IHttpClientFactory httpFactory,
    IConfiguration config) : BackgroundService
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private const int DefaultPollMinutes = 10;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    // Потолок экспоненциального backoff по 429: интервал удваивается с каждым
    // последовательным отказом, но не разрастается дольше часа
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    // Версия CLI на случай, если `claude --version` недоступен: без User-Agent
    // claude-code/<версия> эндпоинт кладёт запрос в агрессивно лимитируемый бакет
    // (вечные 429 — issues #31021/#31637 в repo claude-code)
    private const string FallbackCliVersion = "2.1.169";

    // Статусы последнего опроса per-аккаунт: ok — эндпоинт отвечает; unauthorized —
    // токен не принят (401/403: setup-токен sk-ant-oat01 вместо полноценного логина
    // эндпоинт не пускает); error — прочий не-2xx. Читает /api/usage (плашка на вкладке).
    public const string StatusOk = "ok";
    public const string StatusUnauthorized = "unauthorized";
    public const string StatusError = "error";

    private readonly ConcurrentDictionary<string, string> _status = new();

    // Состояние backoff по ключу подписки: до какого момента не опрашивать
    // и сколько 429 подряд уже словили (для удвоения интервала)
    private readonly Dictionary<string, (DateTime AllowedAt, int Strikes)> _backoff = new();
    private string? _userAgent;
    private int _pollMinutes = DefaultPollMinutes;

    /// <summary>Статус последнего опроса аккаунта (null — поллер до него ещё не доходил).</summary>
    public string? StatusOf(string key) => _status.TryGetValue(key, out var s) ? s : null;

    /// <summary>Все известные статусы опроса — блок pollStatuses в /api/usage.</summary>
    public IReadOnlyDictionary<string, string> Statuses => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("ClaudeUsage:Enabled", true)) return;
        // Новый ключ ClaudeUsage:PollMinutes; прежний ClaudeSubscriptions:UsagePollMinutes
        // остаётся как fallback (его используют существующие конфиги и тесты)
        var minutes = config.GetValue("ClaudeUsage:PollMinutes",
            config.GetValue($"{ClaudeSubscriptionPool.Section}:UsagePollMinutes", DefaultPollMinutes));
        if (minutes <= 0) return;
        _pollMinutes = minutes;

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            await PollAllAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await PollAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var (key, token) in EnumerateAccounts())
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await PollAsync(key, token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OAuthUsage] Опрос usage подписки '{key}' не удался: {ex.Message}");
            }
        }
    }

    // Основной аккаунт + дополнительные подписки пула с OAuth-токеном
    // (аккаунты на чистом ApiKey опросить нечем).
    private IEnumerable<(string Key, string Token)> EnumerateAccounts()
    {
        var primary = ResolvePrimaryToken();
        if (!string.IsNullOrWhiteSpace(primary))
            yield return (ClaudeSubscriptionPool.PrimaryKey, primary!);

        foreach (var sub in pool.All)
        {
            // Полноценный access-токен из изолированного профиля подписки (если в нём
            // делали `claude login`) предпочтительнее setup-токена: его эндпоинт
            // отдаёт без часового лимита
            var profileToken = ReadCredentialsAccessToken(Path.Combine(providers.ProfilesDir, "sub-" + sub.Key));
            var token = !string.IsNullOrWhiteSpace(profileToken) ? profileToken : sub.OAuthToken;
            if (!string.IsNullOrWhiteSpace(token))
                yield return (sub.Key, token!);
        }
    }

    // Токен основной подписки — В ТОМ ЖЕ порядке, в котором его берёт сам claude.exe
    // при запуске ходов: env CLAUDE_CODE_OAUTH_TOKEN (Program.cs кладёт туда
    // Claude:OAuthToken из конфига; прод задаёт env стартовым скриптом) перекрывает
    // логин ~/.claude. Иначе опрос уйдёт не в тот аккаунт, которым сервер реально ходит.
    private string? ResolvePrimaryToken()
    {
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken;

        var cfgToken = config["Claude:OAuthToken"];
        if (!string.IsNullOrWhiteSpace(cfgToken)) return cfgToken;

        return ReadCredentialsAccessToken();
    }

    private string? ReadCredentialsAccessToken()
    {
        var profileDir = config["ClaudeUserProfileDir"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return ReadCredentialsAccessToken(profileDir);
    }

    private static string? ReadCredentialsAccessToken(string profileDir)
    {
        try
        {
            var credsPath = Path.Combine(profileDir, ".credentials.json");
            if (!File.Exists(credsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(credsPath));
            return doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var at)
                ? at.GetString() : null;
        }
        catch { return null; }
    }

    internal async Task PollAsync(string key, string token, CancellationToken ct)
    {
        if (_backoff.TryGetValue(key, out var b) && DateTime.UtcNow < b.AllowedAt)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        var client = httpFactory.CreateClient("anthropic-oauth");
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("User-Agent", await ResolveUserAgentAsync(cts.Token));

        using var resp = await client.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // Экспоненциальный backoff: интервал ×2 с каждым 429 подряд, потолок час;
            // Retry-After сервера (если пришёл) уважаем как есть
            var strikes = b.Strikes + 1;
            var backoff = TimeSpan.FromMinutes(Math.Min(
                _pollMinutes * Math.Pow(2, strikes), MaxBackoff.TotalMinutes));
            var delay = resp.Headers.RetryAfter?.Delta ?? backoff;
            _backoff[key] = (DateTime.UtcNow + delay, strikes);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            // 401/403 — токен не подходит (setup-токен вместо полноценного логина;
            // живой 403 «Request not allowed» именно от sk-ant-oat01). Прочие коды —
            // временная ошибка эндпоинта. У аккаунта остаются прежние снимки.
            var code = (int)resp.StatusCode;
            SetStatus(key, code is 401 or 403 ? StatusUnauthorized : StatusError, code);
            return;
        }
        _backoff.Remove(key); // успех — вернуться к обычному интервалу опроса
        SetStatus(key, StatusOk);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        RecordWindows(key, doc.RootElement);
    }

    // Лог не чаще раза на аккаунт: только при смене статуса (иначе 401 каждые 10 минут
    // засорял бы журнал). Токен в лог не пишем никогда.
    private void SetStatus(string key, string status, int httpCode = 0)
    {
        var prev = _status.TryGetValue(key, out var p) ? p : null;
        _status[key] = status;
        if (prev == status) return;
        if (status == StatusUnauthorized)
            Console.Error.WriteLine($"[OAuthUsage] аккаунт '{key}': HTTP {httpCode} — токен не принят эндпоинтом usage " +
                "(setup-токен вместо полноценного входа?); нужен `claude login` в профиле подписки");
        else if (status == StatusError)
            Console.Error.WriteLine($"[OAuthUsage] аккаунт '{key}': HTTP {httpCode}");
        else if (prev is not null)
            Console.Error.WriteLine($"[OAuthUsage] аккаунт '{key}': опрос восстановился");
    }

    // Для тестов: не дёргать `claude --version` при опросе
    internal void OverrideUserAgent(string ua) => _userAgent = ua;

    // User-Agent claude-code/<версия установленного CLI> — обязателен (см. FallbackCliVersion).
    // Версию узнаём один раз за жизнь процесса через `claude --version`.
    private async Task<string> ResolveUserAgentAsync(CancellationToken ct)
    {
        if (_userAgent is not null) return _userAgent;
        var version = await TryGetCliVersionAsync(ct) ?? FallbackCliVersion;
        return _userAgent = $"claude-code/{version}";
    }

    private static async Task<string?> TryGetCliVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ClaudeCliLocator.FindClaudeExecutable())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var m = CliVersionRegex().Match(output);
            return m.Success ? m.Value : null;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex CliVersionRegex();

    // Динамический перебор окон ответа: любое свойство-объект с utilization/resets_at —
    // окно лимита (five_hour, seven_day, per-model seven_day_opus/sonnet/fable и любые
    // будущие подхватываются без правок кода); extra_usage устроен иначе — разбирается отдельно.
    private void RecordWindows(string key, JsonElement root)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (prop.Name == "extra_usage") RecordExtraUsage(key, root);
            else RecordWindow(key, prop.Name, prop.Value);
        }
    }

    // Окно из ответа: { "utilization": 51.0 (проценты), "resets_at": ISO }.
    private void RecordWindow(string key, string window, JsonElement w)
    {
        var utilization = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() / 100.0 : (double?)null;
        var resetsAt = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        if (utilization is null && resetsAt is null) return;

        usage.Record(window, utilization, "allowed", isUsingOverage: false, resetsAt, subscriptionKey: key);
    }

    // Перерасход: { "is_enabled": bool, "monthly_limit": N, "used_credits": N, "utilization": 0..100 }.
    // Пишем отдельным окном extra_usage (рендер на фронте общий, по limitType);
    // выключенный перерасход не показываем вовсе — не шуметь у тех, кому он не нужен.
    private void RecordExtraUsage(string key, JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var e) || e.ValueKind != JsonValueKind.Object) return;
        if (!e.TryGetProperty("is_enabled", out var en) || en.ValueKind != JsonValueKind.True) return;

        var utilization = e.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() / 100.0 : (double?)null;
        if (utilization is null) return;

        // isUsingOverage не ставим: окно и так называется «Перерасход», а флаг красил бы
        // его в danger при любой ненулевой трате — уровень тревоги пусть идёт по проценту
        usage.Record("extra_usage", utilization, "allowed", isUsingOverage: false,
            resetsAt: null, subscriptionKey: key);
    }
}
