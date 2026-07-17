using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;

namespace ClaudeHomeServer.Services;

// Стартовый прогрев утилизации подписок: один минимальный ход claude по каждому аккаунту,
// чтобы получить свежий rate_limit_event и записать актуальную утилизацию 5h-окна в usage.json.
// Дальше пул (ClaudeSubscriptionPool) роутит новые чаты по этим снимкам, а не по устаревшим.
// Побочно: если аккаунт отвечает "лимит исчерпан" — помечаем его exhausted в пуле.
//
// Запускается только при наличии дополнительных подписок (иначе роутить некого) и за флагом
// ClaudeSubscriptions:WarmupOnStartup (дефолт true). Best-effort: любые ошибки логируются и
// не мешают старту приложения.
public sealed class SubscriptionUsageWarmupService(
    ClaudeSubscriptionPool pool,
    UsageService usage,
    LlmProviderRegistry providers,
    IConfiguration config) : BackgroundService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private const int DefaultRecheckMinutes = 15;
    private const string Prompt = "ping";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pool.HasExtra)
            return; // одна подписка — балансировать нечего

        var timeout = TimeSpan.FromMilliseconds(
            config.GetValue($"{ClaudeSubscriptionPool.Section}:WarmupTimeoutMs", (int)DefaultTimeout.TotalMilliseconds));

        // Стартовый прогрев — по всем аккаунтам сразу.
        if (config.GetValue($"{ClaudeSubscriptionPool.Section}:WarmupOnStartup", true))
            await ProbeKeysAsync(AllKeys(), timeout, stoppingToken);

        // Периодический переопрос ВЫВЕДЕННЫХ из ротации аккаунтов: ловим возврат раньше resetsAt
        // (и с реальной цифрой), не трогая здоровые (они и так получают ходы). 0 — выключено.
        var interval = config.GetValue($"{ClaudeSubscriptionPool.Section}:RecheckIntervalMinutes", DefaultRecheckMinutes);
        if (interval <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var parked = AllKeys().Where(k => !pool.IsInRotation(k)).ToList();
                if (parked.Count > 0)
                    await ProbeKeysAsync(parked, timeout, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Основной аккаунт + все дополнительные подписки.
    private List<string> AllKeys()
    {
        var keys = new List<string> { ClaudeSubscriptionPool.PrimaryKey };
        keys.AddRange(pool.All.Where(s => s.Enabled).Select(s => s.Key));
        return keys;
    }

    // Пробуем последовательно, чтобы не плодить процессы claude и не жечь окна параллельно.
    private async Task ProbeKeysAsync(IEnumerable<string> keys, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await ProbeAsync(key, timeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SubscriptionWarmup] Пробинг подписки '{key}' не удался: {ex.Message}");
            }
        }
    }

    private async Task ProbeAsync(string key, TimeSpan timeout, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "claude-warmup");
        Directory.CreateDirectory(workDir);

        var utf8NoBom = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = ClaudeCliLocator.FindClaudeExecutable(),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true,
        };
        // stream-json в print-режиме требует --verbose; так CLI и отдаёт rate_limit_event.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");

        // Env подписки: для дополнительной — изолированный профиль + её токен; для основной
        // ('claude') оверрайдов нет, используется базовый ~/.claude (текущий логин).
        if (key != ClaudeSubscriptionPool.PrimaryKey)
        {
            var sub = pool.All.FirstOrDefault(s => s.Key == key);
            if (sub is null || !sub.Enabled) return;
            var env = providers.BuildOAuthCliEnv(sub.Key, sub.OAuthToken, sub.ApiKey);
            if (env is null) return;
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("не удалось запустить claude");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await process.StandardInput.WriteAsync(Prompt.AsMemory(), cts.Token);
            process.StandardInput.Close();

            RateLimitMessage? last = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                RateLimitMessage? parsed = TryParseLine(line);
                if (parsed is not null) last = parsed;
            }

            await process.WaitForExitAsync(cts.Token);

            if (last is null)
            {
                Console.Error.WriteLine($"[SubscriptionWarmup] '{key}': rate_limit_event не пришёл (exit {process.ExitCode})");
                return;
            }

            RecordAndGuard(key, last);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Console.Error.WriteLine($"[SubscriptionWarmup] '{key}': таймаут пробинга");
        }
    }

    private static RateLimitMessage? TryParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "rate_limit_event") return null;
            return ClaudeRateLimitParser.TryParse(root, out var msg) ? msg : null;
        }
        catch (JsonException)
        {
            return null; // не JSON-строка стрима — пропускаем
        }
    }

    // Записываем снимок утилизации и правим состояние пула по свежему ответу:
    // отбит (rejected/100%) → MarkExhausted; здоровый ответ на ранее исчерпанном аккаунте →
    // снимаем пометку (возврат в ротацию раньше resetsAt). Та же логика exhausted, что в SessionManager.
    private void RecordAndGuard(string key, RateLimitMessage m)
    {
        usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt,
            m.OverageStatus, m.OverageResetsAt, subscriptionKey: key);

        if (m.Status == "rejected" || (m.Utilization >= 1.0 && !m.IsUsingOverage))
        {
            var resetsAt = m.ResetsAt is not null && DateTime.TryParse(m.ResetsAt, out var dt)
                ? (DateTime?)dt.ToUniversalTime() : null;
            pool.MarkExhausted(key, resetsAt);
            Console.Error.WriteLine($"[SubscriptionWarmup] '{key}': лимит исчерпан (status={m.Status}), выведена из ротации");
        }
        else if (pool.IsExhausted(key))
        {
            pool.Reset(key); // аккаунт снова отвечает — вернуть в ротацию, не дожидаясь resetsAt
            Console.Error.WriteLine($"[SubscriptionWarmup] '{key}': лимит восстановлен, возвращена в ротацию");
        }
    }
}
