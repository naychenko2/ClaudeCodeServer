using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Modules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeHomeServer.Controllers;

// LLM-канал внешних модулей (контракт §10, ТЗ R11–R13): обмен модульного токена на
// host-токен и вызов модели по ключу действия. Модуль называет КЛЮЧ ДЕЙСТВИЯ, а не модель
// (инвариант §10) — маршрут выбирает владелец в настройках моделей, как для встроенных.
//
// СОЗНАТЕЛЬНО без [Authorize]: /api/host/** — отдельная поверхность вне HMAC-схемы ядра
// (§10.2 v1.5), вход охраняет HostChannelMiddleware (RS256; для обмена — chan=gateway|mcp,
// для остального — chan=host; гейт module-{id} → 403 module_hidden).
[ApiController]
public class HostLlmController(
    ModuleTokenService tokens, ICheapTextRunner cheap, LocalActionRouter router,
    HostLlmConcurrencyLimiter limiter, ModuleLlmUsageStore usage,
    ILogger<HostLlmController> log) : ControllerBase
{
    /// <summary>Лимит размера промпта (§10.5): больше — 413.</summary>
    public const int MaxPromptBytes = 256 * 1024;

    // Запас поверх watchdog-таймаута запроса для внутренних шагов цепочки: watchdog должен
    // сработать ПЕРВЫМ, чтобы таймаут детерминированно откартировался в 504 (а не в 503
    // от исключения таймаута внутреннего шага).
    private const int TimeoutMarginMs = 1_000;

    public sealed record CompleteRequest(
        string? Action, string? Prompt, JsonElement? ResponseFormat, int? TimeoutMs);

    // Результат валидации host-канала — положен middleware; отсутствие значит, что эндпоинт
    // вызван мимо него (конфигурационная ошибка) — закрываемся, а не работаем анонимно
    private bool TryGetCaller(out LoadedModule module, out string userId, out string userName)
    {
        module = null!;
        userId = "";
        userName = "";
        if (HttpContext.Items[HostChannelMiddleware.ModuleItem] is not LoadedModule m ||
            HttpContext.Items[HostChannelMiddleware.UserIdItem] is not string sub)
            return false;
        module = m;
        userId = sub;
        userName = HttpContext.Items[HostChannelMiddleware.UserNameItem] as string ?? "";
        return true;
    }

    // §10.2: обмен модульного токена (chan=gateway|mcp, проверен middleware) на host-токен
    // с теми же sub и aud — расход всегда атрибутирован пользователю. Rate-limit на выпуск
    // (§10.2 v1.5) — политика host-token в Program.cs.
    [HttpPost("api/host/token")]
    [EnableRateLimiting("host-token")]
    public IActionResult ExchangeToken()
    {
        if (!TryGetCaller(out var module, out var userId, out var userName))
            return Unauthorized(new { error = "unauthorized" });

        var token = tokens.Issue(module, userId, userName, "host");
        return Ok(new { token, expiresIn = (int)ModuleTokenService.HostTokenLifetime.TotalSeconds });
    }

    // §10.3–10.5: вызов модели по ключу действия. Неймспейс module:{id}:{key} достраивается
    // из aud токена — ключ чужого модуля недостижим по построению (CT-14).
    [HttpPost("api/host/llm/complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest req, CancellationToken ct)
    {
        if (!TryGetCaller(out var module, out var userId, out _))
            return Unauthorized(new { error = "unauthorized" });

        if (string.IsNullOrWhiteSpace(req.Action) || string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "invalid_request", message = "Нужны поля action и prompt" });

        // responseFormat (§10.3): строка "json" либо объект JSON-схемы; иное — 400
        object? jsonFormat = null;
        if (req.ResponseFormat is { } rf && rf.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (rf.ValueKind == JsonValueKind.String && rf.GetString() == "json") jsonFormat = "json";
            else if (rf.ValueKind == JsonValueKind.Object) jsonFormat = rf;
            else return BadRequest(new { error = "invalid_request", message = "responseFormat: \"json\" либо JSON-схема" });
        }

        // Модуль без блока llm каналом не пользуется (§10.1) — вызовы отклоняются
        if ((module.Manifest.Llm?.Actions ?? []).Count == 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "action_not_declared" });

        var action = req.Action.Trim();
        var fullKey = module.LlmActionKey(action);
        if (LocalActionCatalog.Find(fullKey) is null)
            return NotFound(new { error = "action_unknown" });

        if (Encoding.UTF8.GetByteCount(req.Prompt) > MaxPromptBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = "prompt_too_large", maxBytes = MaxPromptBytes });

        if (!limiter.TryEnter(module.Id))
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "too_many_requests", retryAfterSeconds = 5 });
        }

        try
        {
            // Таймаут: потолок профиля действия; timeoutMs может только уменьшить (§10.5)
            var ceilingMs = router.ProfileFor(fullKey).TimeoutMs;
            var effMs = req.TimeoutMs is int t && t > 0 ? Math.Min(t, ceilingMs) : ceilingMs;

            var route = router.Resolve(fullKey);
            var routeStr = route.Kind switch
            {
                RouteKind.Local => LocalActionOverridesStore.LocalRoute,
                RouteKind.Claude => LocalActionOverridesStore.ClaudeRoute,
                RouteKind.Tier => LocalActionOverridesStore.TierRoute(route.Tier ?? ModelTier.Medium),
                _ => route.Model ?? LocalActionOverridesStore.ClaudeRoute,
            };

            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(effMs);
            var started = Stopwatch.StartNew();

            try
            {
                var result = await cheap.RunDetailedAsync(fullKey, req.Prompt, fallbackModel: null,
                    ownerId: userId, timeout: TimeSpan.FromMilliseconds(effMs + TimeoutMarginMs),
                    maxTokens: null, jsonFormat, watchdog.Token);

                // R13: учёт в разрезе (moduleId, action, sub); токены/деньги — только когда
                // их отдал провайдер (локаль и прямой адаптер метрик не дают)
                usage.Record(new ModuleLlmUsageStore.Entry(
                    DateTime.UtcNow, module.Id, action, userId, routeStr,
                    Ok: true, started.ElapsedMilliseconds, result.Usage?.Model,
                    result.Usage?.TotalInputTokens, result.Usage?.OutputTokens, result.Usage?.CostUsd));

                return Ok(new
                {
                    text = result.Text,
                    model = result.Usage?.Model,
                    route = routeStr,
                    usage = result.Usage is { } u
                        ? (object?)new { inputTokens = u.TotalInputTokens, outputTokens = u.OutputTokens, costUsd = u.CostUsd }
                        : null,
                });
            }
            catch (Exception) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Watchdog сработал раньше внутренних шагов (запас TimeoutMarginMs) — честный 504.
                // Клиентский обрыв (ct) сюда не попадает — ему ответ уже не нужен.
                usage.Record(new ModuleLlmUsageStore.Entry(
                    DateTime.UtcNow, module.Id, action, userId, routeStr,
                    Ok: false, started.ElapsedMilliseconds));
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "llm_timeout" });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // CT-15: последний шаг цепочки CheapTextRunner намеренно пробрасывает исключение —
                // границей канала оно оформляется в 503, модуль деградирует, а не падает
                log.LogWarning(ex, "LLM-канал: вызов {Action} модуля {Module} не удался", fullKey, module.Id);
                usage.Record(new ModuleLlmUsageStore.Entry(
                    DateTime.UtcNow, module.Id, action, userId, routeStr,
                    Ok: false, started.ElapsedMilliseconds));
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "llm_unavailable" });
            }
        }
        finally
        {
            limiter.Exit(module.Id);
        }
    }
}
