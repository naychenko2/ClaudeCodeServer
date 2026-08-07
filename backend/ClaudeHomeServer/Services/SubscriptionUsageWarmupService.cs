using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;

namespace ClaudeHomeServer.Services;

// Идл-пинг утилизации подписок: минимальный ход claude --model haiku по каждому
// ПРОСТАИВАЮЩЕМУ аккаунту пула — свежий rate_limit_event без ожидания живого чата.
// Простаивающий = с момента последней ФАКТИЧЕСКОЙ активности (живой ход чата ИЛИ прошлый
// пинг — SubscriptionActivityTracker) прошло больше ClaudeSubscriptions:IdlePingMinutes
// (дефолт 5, 0 — выключено). У аккаунтов с живыми чатами свежесть и так даёт их
// rate_limit_event — пинговать их незачем.
//
// Решение Андрея 2026-07-29: доступность/ротация не должны зависеть от протухающих
// access-токенов — идл-пинг всегда идёт setup-токеном пула (долгоживущим). OAuth-снимки
// SubscriptionOAuthUsageService таймер простоя НЕ сбрасывают (остаётся отдельным
// best-effort источником полных окон 5h/7d/per-model/перерасхода, эту фичу не заменяет).
//
// Стартовый прогрев (WarmupOnStartup, дефолт true) — по всем аккаунтам сразу. Дальше тик
// раз в минуту отбирает простаивающих (тики не накладываются — следующий WaitForNextTickAsync
// ждётся только после того, как отработали все пробы текущего). Побочно: "лимит исчерпан" →
// MarkExhausted; здоровый ответ на ранее исчерпанном → Reset (возврат в ротацию раньше
// resetsAt). Best-effort: любые ошибки логируются и не мешают старту приложения.
public sealed class SubscriptionUsageWarmupService(
    ClaudeSubscriptionPool pool,
    UsageService usage,
    LlmProviderRegistry providers,
    SubscriptionActivityTracker activity,
    IConfiguration config) : BackgroundService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private const int DefaultIdlePingMinutes = 5;
    private const string Prompt = "ping";
    private const string PingModel = "haiku";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pool.HasExtra)
            return; // одна подписка — балансировать нечего

        var timeout = TimeSpan.FromMilliseconds(
            config.GetValue($"{ClaudeSubscriptionPool.Section}:WarmupTimeoutMs", (int)DefaultTimeout.TotalMilliseconds));

        // Стартовый прогрев — по всем аккаунтам сразу.
        if (config.GetValue($"{ClaudeSubscriptionPool.Section}:WarmupOnStartup", true))
            await ProbeKeysAsync(AllKeys(), timeout, stoppingToken);

        var idleThreshold = ResolveIdleThreshold();
        if (idleThreshold is null) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var due = KeysDueForPing(idleThreshold.Value);
                if (due.Count > 0)
                    await ProbeKeysAsync(due, timeout, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Порог простоя из конфига; null — механизм выключен (IdlePingMinutes <= 0).
    // Вынесено отдельно, чтобы рубильник проверялся тестом без запуска тикающего цикла.
    internal TimeSpan? ResolveIdleThreshold()
    {
        var minutes = config.GetValue($"{ClaudeSubscriptionPool.Section}:IdlePingMinutes", DefaultIdlePingMinutes);
        return minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);
    }

    // Ключи пула, простаивающие дольше порога — кандидаты на пинг этого тика.
    // Вынесено отдельно от ExecuteAsync: проверяется тестом без ожидания реальной минуты.
    internal List<string> KeysDueForPing(TimeSpan idleThreshold) =>
        AllKeys().Where(k => activity.IsIdle(k, idleThreshold)).ToList();

    // Все настроенные подписки пула (каждая — по своему токену). Локальный Claude не пробуем:
    // warmup вообще работает только при непустом пуле (HasExtra), а вход без ключа лимиты не копит.
    private List<string> AllKeys()
        => pool.All.Where(s => s.Enabled).Select(s => s.Key).ToList();

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
        // Env подписки пула: изолированный профиль + её токен (для всех, включая "claude").
        var sub = pool.All.FirstOrDefault(s => s.Key == key);
        if (sub is null || !sub.Enabled) return;
        var env = providers.BuildOAuthCliEnv(sub.Key, sub.OAuthToken, sub.ApiKey);
        if (env is null) return;

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
        foreach (var a in BuildProbeArgs(ClaudeRuntimeSettings.HooksOffArgs(Execution.LocalProcessRunner.Instance)))
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        // Отсчёт простоя — от ПОПЫТКИ пинга, а не от успеха: иначе сбойный аккаунт (таймаут,
        // недоступный CLI) пинговался бы каждую минуту вместо раза в IdlePingMinutes.
        activity.Touch(key);

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

    // Аргументы пробного хода. Вынесены отдельно, чтобы состав флагов (в т.ч. --model —
    // пинг обязан идти самой дешёвой моделью, не тем, чем обычно ходит владелец) проверялся
    // тестом без запуска процесса.
    internal static List<string> BuildProbeArgs(IEnumerable<string> hooksOffArgs)
    {
        // stream-json в print-режиме требует --verbose; так CLI и отдаёт rate_limit_event.
        var args = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };
        // Хуки плагинов не нужны и плодят окна консоли на хосте (warmup — всегда local)
        args.AddRange(hooksOffArgs);
        args.Add("--model");
        args.Add(PingModel);
        return args;
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
    // Состояние пула трогают только известные окна (IsExhaustionWindow) — прочие идут
    // в usage для экрана и на ротацию не влияют.
    internal void RecordAndGuard(string key, RateLimitMessage m)
    {
        usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt,
            m.OverageStatus, m.OverageResetsAt, subscriptionKey: key, source: "probe");

        if (!ClaudeSubscriptionPool.IsExhaustionWindow(m.LimitType))
            return;

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
