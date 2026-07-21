namespace ClaudeHomeServer.Models;

// Снимок использования окна лимита подписки в момент времени (из rate_limit_event).
// Utilization — доля 0..1; LimitType — five_hour/seven_day/weekly; ResetsAt — ISO-время сброса.
// SubscriptionKey — какая подписка сгенерировала снимок ("claude" — основная, ключ из пула — дополнительная).
public record UsageSnapshot(
    DateTime Timestamp,
    string LimitType,
    double? Utilization,
    string? Status,
    bool IsUsingOverage,
    string? ResetsAt,
    string? OverageStatus = null,
    string? OverageResetsAt = null,
    string SubscriptionKey = "claude");

// Информация о тарифе подписки (из ~/.claude/.credentials.json)
public record PlanInfo(string? SubscriptionType, string? RateLimitTier, string Label);

// Ответ /api/usage: история снимков + тариф + per-subscription utilisation.
// RotationThreshold — порог утилизации 5h-окна, выше которого аккаунт выведен из ротации
// новых чатов (заполняется только при наличии дополнительных подписок).
// Providers — снимки окон лимитов сторонних CLI-провайдеров (glm/deepseek): их
// Anthropic-совместимые эндпоинты тоже шлют rate_limit_event, снимок пишется под ключ провайдера.
public record UsageResponse(IReadOnlyList<UsageSnapshot> Snapshots, PlanInfo? Plan,
    Dictionary<string, SubscriptionUsage>? Subscriptions = null, double? RotationThreshold = null,
    Dictionary<string, IReadOnlyList<UsageSnapshot>>? Providers = null,
    OllamaUsageInfo? Ollama = null);

// Блок «Локальная модель (Ollama)» для экрана использования. У Ollama нет лимитов/баланса
// (локальная, бесплатная) — показываем только модель и на какие действия она заведена.
// Enabled=false → раздел показывает «не настроена». Actions — весь каталог фоновых действий
// с флагом RoutedToOllama (идёт на локаль сейчас) — прозрачно, что бесплатно, а что на claude.
public record OllamaUsageInfo(bool Enabled, string? Model, string? BaseUrl,
    IReadOnlyList<OllamaActionInfo> Actions);

// Route — исполнитель первого шага: "local", "claude" или id конкретной модели провайдера
// (дальше действие идёт по цепочке «выбранное → локаль → claude»). RoutedToOllama — начинается
// ли действие с локальной модели прямо сейчас (с учётом доступности Ollama).
// Source — откуда взято значение: "default" (каталог), "config" (Ollama:Actions) или "admin"
// (выбор в UI); по нему видно, что переопределено и что можно сбросить.
public record OllamaActionInfo(string Key, string Title, string Group, bool RoutedToOllama,
    string Source = "default", string Route = "claude");

// Utilisation одной подписки: снимки + опциональное имя + статус роутинга.
// InRotation — берёт ли пул этот аккаунт для новых чатов; Utilization — эффективная
// утилизация 5h-окна (истёкшее окно/нет данных = 0); Exhausted — жёсткое исчерпание
// (rejected/100%), при котором аккаунт выведен независимо от числа Utilization;
// Tier — ярлык тарифа ("Max 20×", "Pro", …), по нему пул приоритизирует аккаунты.
public record SubscriptionUsage(IReadOnlyList<UsageSnapshot> Snapshots, string? Name = null,
    bool InRotation = true, double Utilization = 0, bool Exhausted = false, string? Tier = null);
