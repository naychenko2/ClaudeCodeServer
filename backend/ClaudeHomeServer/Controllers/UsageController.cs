using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/usage")]
public class UsageController(UsageService usage, ClaudeSubscriptionPool? subscriptionPool,
    LlmProviderRegistry providers, LocalActionRouter localRouter, ILocalLlmClient ollama,
    SubscriptionOAuthUsageService oauthUsage,
    LocalActionOverridesStore localActions, SpecialtySettingsStore specialty) : ControllerBase
{
    // История снимков использования лимитов подписки + тариф + per-subscription (для экрана usage)
    [HttpGet]
    public IActionResult Get()
    {
        var all = usage.GetAll();
        var plan = usage.GetPlan();
        var bySub = usage.GetAllBySubscription();
        var ollamaInfo = BuildOllamaInfo();

        // Снимки окон лимитов сторонних CLI-провайдеров: их Anthropic-совместимые
        // эндпоинты тоже шлют rate_limit_event, и снимок пишется под ключ провайдера.
        // Отдаём отдельным блоком — вкладки провайдеров на экране usage показывают
        // те же окна (5ч/недельное, сброс), что и у Claude.
        var providerKeys = new HashSet<string>(providers.All.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<UsageSnapshot>>? providerSnaps = null;
        foreach (var (key, snaps) in bySub)
        {
            if (!providerKeys.Contains(key)) continue;
            providerSnaps ??= new Dictionary<string, IReadOnlyList<UsageSnapshot>>(StringComparer.OrdinalIgnoreCase);
            providerSnaps[key] = snaps;
        }

        // Статусы опроса api/oauth/usage per-аккаунт: по "unauthorized" вкладка честно
        // показывает «нужен claude login», а не гадает по свежести снимков
        var pollStatuses = oauthUsage.Statuses.Count > 0 ? oauthUsage.Statuses : null;

        // Для подписок из пула — проставляем DisplayName + статус роутинга (в ротации / выведен)
        if (subscriptionPool?.HasExtra == true)
        {
            // Показываем ВСЕ настроенные подписки пула (включая "claude", если она — подписка
            // с токеном), даже если снимков у аккаунта ещё нет: вкладке нужен статус опроса.
            // Чужие снапшоты per-subscription стора не попадают: ключи сторонних провайдеров
            // (уходят в блок Providers) и сироты после переименования аккаунта отсекаются.
            var named = new Dictionary<string, SubscriptionUsage>();
            // Пометки «модель недоступна на подписке» — админское состояние ротации: снимает их
            // только админ (ClearModelAvailability ниже), значит и видеть их должен он же.
            // Не-админу отдаём пустой список: карточка просто не рисует блок.
            var isAdmin = User.IsInRole("admin");
            foreach (var sub in subscriptionPool.All)
            {
                var key = sub.Key;
                var snaps = bySub.TryGetValue(key, out var s) ? s : new List<UsageSnapshot>();
                named[key] = new SubscriptionUsage(snaps, sub.DisplayName,
                    InRotation: subscriptionPool.IsInRotation(key),
                    Utilization: subscriptionPool.EffectiveUtilization(key),
                    Exhausted: subscriptionPool.IsExhausted(key),
                    Tier: subscriptionPool.TierLabel(key),
                    LoginCommand: oauthUsage.LoginCommandFor(key),
                    SupportsOpus: sub.SupportsOpus,
                    Supports1M: sub.Supports1M,
                    WeeklyUtilization: subscriptionPool.WeeklyUtilization(key),
                    // Живые пометки «модель недоступна на этой подписке»: без них модель молча
                    // не выбирается при полностью здоровой на вид подписке.
                    UnavailableModels: isAdmin ? subscriptionPool.ModelUnavailableMarks(key) : []);
            }
            // Фактическая цель роутинга (куда ушёл бы новый чат) — детерминированный выбор,
            // чтобы бейдж не мигал между равными аккаунтами при обновлении экрана
            return Ok(new UsageResponse(all, plan, named, subscriptionPool.SoftThreshold, providerSnaps, ollamaInfo, pollStatuses,
                RoutingTarget: subscriptionPool.PickForDisplay(),
                WeeklyThreshold: subscriptionPool.WeeklyThreshold));
        }

        return Ok(new UsageResponse(all, plan, null, null, providerSnaps, ollamaInfo, pollStatuses));
    }

    // Снять пометку «модель недоступна на этой подписке» досрочно — кнопка «Проверить сейчас».
    // Третий путь возврата пары в ротацию рядом с двумя автоматическими (истечение TTL и успешный
    // ход этой модели на этой подписке): пометка ставится по одному отказу, а причина могла уйти
    // за минуту (пополнили кредиты, включили доступ) — ждать сутки TTL человеку незачем.
    // Только админ: пометки глобальные для инстанса, как и вся ротация подписок.
    // Модель — в теле, а не в пути: в именах моделей встречаются точки и суффикс окна «[1m]»,
    // сегмент маршрута с ними приходится экранировать на каждом вызывающем.
    [HttpPost("subscriptions/{key}/model-availability/clear")]
    [Authorize(Roles = "admin")]
    public IActionResult ClearModelAvailability(string key, [FromBody] ClearModelAvailabilityRequest body)
    {
        if (subscriptionPool is null) return NotFound(new { error = "Пул подписок не настроен" });
        if (string.IsNullOrWhiteSpace(body.Model)) return BadRequest(new { error = "Не указана модель" });
        // Неизвестный ключ — 404, а не молчаливый успех: иначе опечатка в ключе выглядит как
        // «сбросили», и человек ждёт от пары работы, которой не будет.
        if (!subscriptionPool.All.Any(s => s.Key == key))
            return NotFound(new { error = $"Подписка «{key}» не найдена" });

        subscriptionPool.ClearModelUnavailable(key, body.Model);
        return Ok(new { unavailableModels = subscriptionPool.ModelUnavailableMarks(key) });
    }

    public record ClearModelAvailabilityRequest(string? Model);

    // Блок локальной модели: настройки Ollama + маршрут каждого фонового действия (локаль/claude)
    private OllamaUsageInfo BuildOllamaInfo()
    {
        var globalPresets = specialty.Snapshot.Global.Presets;
        var actions = LocalActionCatalog.All
            .Select(a =>
            {
                var route = localRouter.Resolve(a.Key);
                return new OllamaActionInfo(a.Key, a.Title, a.Group,
                    RoutedToOllama: route.Kind == RouteKind.Local && ollama.Enabled,
                    Source: route.Source.ToString().ToLowerInvariant(),
                    Route: route.Kind switch
                    {
                        RouteKind.Local => LocalActionOverridesStore.LocalRoute,
                        RouteKind.Claude => LocalActionOverridesStore.ClaudeRoute,
                        RouteKind.Tier => LocalActionOverridesStore.TierRoute(route.Tier ?? ModelTier.Medium),
                        _ => route.Model ?? LocalActionOverridesStore.ClaudeRoute,
                    },
                    RequiresStrong: !a.DefaultLocal,
                    Agentic: a.Agentic,
                    // Та же логика, что в LocalActionsAdminController.Describe — без дубля:
                    // preset раскрывается по сырому значению стора и общим пресетам.
                    Preset: LocalActionsAdminController.DescribePreset(localActions.TryGet(a.Key), globalPresets));
            })
            .ToList();
        return new OllamaUsageInfo(ollama.Enabled, ollama.Enabled ? ollama.TextModel : null,
            ollama.Enabled ? ollama.BaseUrl : null, actions, Provider: ollama.ProviderKey);
    }
}
