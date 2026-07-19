using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Периодический опрос точной утилизации подписок Claude через OAuth-эндпоинт
// GET api.anthropic.com/api/oauth/usage (тот же источник, что интерактивный экран /usage CLI):
// в отличие от rate_limit_event, он отдаёт проценты ОБОИХ окон (5-часового и недельного)
// с временем сброса — и без траты хода. Снимки пишутся в UsageService под ключ аккаунта,
// откуда их читают виджет «Использование» и вкладки аккаунтов на экране usage.
//
// Best-effort: основной аккаунт опрашивается токеном из ~/.claude/.credentials.json
// (CLI сам его обновляет), дополнительные — их OAuthToken из конфига. Ошибка/429 по
// аккаунту (setup-токены эндпоинт принимает не всегда) — просто пропуск: для такого
// аккаунта остаются данные warmup-хода и живых rate_limit_event.
public sealed class SubscriptionOAuthUsageService(
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
    // Setup-токены (sk-ant-oat) эндпоинт жёстко лимитирует (429 с Retry-After ~1ч) —
    // уважаем и не долбим раньше срока; полноценные access-токены отвечают без лимита
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromHours(1);
    private readonly Dictionary<string, DateTime> _retryAfter = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = config.GetValue($"{ClaudeSubscriptionPool.Section}:UsagePollMinutes", DefaultPollMinutes);
        if (minutes <= 0) return;

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

    private async Task PollAsync(string key, string token, CancellationToken ct)
    {
        if (_retryAfter.TryGetValue(key, out var allowedAt) && DateTime.UtcNow < allowedAt)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        var client = httpFactory.CreateClient("anthropic-oauth");
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var resp = await client.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _retryAfter[key] = DateTime.UtcNow + (resp.Headers.RetryAfter?.Delta ?? DefaultRetryAfter);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            // 401 — токен не подходит: не шумим, у аккаунта остаются данные
            // warmup-хода и живых rate_limit_event
            return;
        }
        _retryAfter.Remove(key);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        RecordWindow(key, doc.RootElement, "five_hour");
        RecordWindow(key, doc.RootElement, "seven_day");
    }

    // Окно из ответа: { "utilization": 51.0 (проценты), "resets_at": ISO }.
    private void RecordWindow(string key, JsonElement root, string window)
    {
        if (!root.TryGetProperty(window, out var w) || w.ValueKind != JsonValueKind.Object) return;

        var utilization = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() / 100.0 : (double?)null;
        var resetsAt = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        if (utilization is null && resetsAt is null) return;

        usage.Record(window, utilization, "allowed", isUsingOverage: false, resetsAt, subscriptionKey: key);
    }
}
