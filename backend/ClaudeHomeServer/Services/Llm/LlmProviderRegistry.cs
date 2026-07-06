using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Реестр CLI-провайдеров (секция конфига "LlmProviders"). Провайдер вычисляется
// из Session.Model и НЕ персистится: единственный источник правды — модель.
// null от ResolveByModel = родной Claude (подписка, без env-оверрайдов).
public class LlmProviderRegistry
{
    public const string Section = "LlmProviders";

    private readonly Dictionary<string, LlmProviderConfig> _providers;

    public LlmProviderRegistry(IConfiguration config)
    {
        _providers = new Dictionary<string, LlmProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in config.GetSection(Section).GetChildren())
        {
            var cfg = child.Get<LlmProviderConfig>();
            if (cfg is null) continue;
            cfg.Key = child.Key.ToLowerInvariant();
            _providers[cfg.Key] = cfg;
        }
    }

    public IReadOnlyCollection<LlmProviderConfig> All => _providers.Values;

    public IEnumerable<LlmProviderConfig> Enabled => _providers.Values.Where(p => p.Enabled);

    public LlmProviderConfig? GetByKey(string? key) =>
        key is not null && _providers.TryGetValue(key, out var p) ? p : null;

    // Провайдер по модели: сперва точное совпадение с каталогом провайдера,
    // затем по префиксу (модели из GET /models, не описанные в конфиге).
    // null → Claude. Выключенные провайдеры тоже резолвятся — доступность
    // проверяется отдельно (IsAvailable), чтобы отличать «не Claude» от «не настроен».
    public LlmProviderConfig? ResolveByModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var p in _providers.Values)
            if (p.FindModel(model) is not null)
                return p;
        foreach (var p in _providers.Values)
            if (model.StartsWith(p.EffectiveModelPrefix, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    // Wire-токен провайдера модели ("claude" | key) — для guard смены провайдера и фронта
    public string ProviderKey(string? model) => ResolveByModel(model)?.Key ?? "claude";

    public LlmCapabilities CapabilitiesFor(string? model) =>
        ResolveByModel(model) is { } p ? CapabilitiesOf(p) : LlmCapabilitiesCatalog.Claude;

    // CLI-провайдер наследует весь функционал claude CLI; провайдеро-специфичны
    // только изображения (ограничение API) и имя для UI
    public static LlmCapabilities CapabilitiesOf(LlmProviderConfig p) => LlmCapabilitiesCatalog.Claude with
    {
        Provider = p.Key,
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
        SupportsImages = p.SupportsImages,
    };

    // Env процесса claude CLI для стороннего провайдера (per-turn: модель может меняться).
    // null → модель родная Claude, env не нужны.
    public IReadOnlyDictionary<string, string>? BuildCliEnv(string? model)
    {
        var p = ResolveByModel(model);
        if (p is null) return null;
        if (!p.Enabled)
            throw new InvalidOperationException(
                $"Провайдер «{p.DisplayName}» не настроен: задай LlmProviders:{p.Key}:ApiKey в appsettings.Local.json");

        var main = string.IsNullOrWhiteSpace(model) ? p.Models.FirstOrDefault()?.Id ?? "" : model!;
        var small = string.IsNullOrWhiteSpace(p.SmallModel) ? main : p.SmallModel;
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = p.AnthropicBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = p.ApiKey,
            ["ANTHROPIC_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = small,
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = small,
        };
        foreach (var (k, v) in p.ExtraEnv)
            env[k] = v;
        return env;
    }

    // Стоимость хода по ценам конфига модели. CLI на чужом эндпоинте считает
    // total_cost_usd по ценам Anthropic — доверять ему нельзя, пересчитываем сами.
    // null — модель родная Claude или цены не заданы (стоимость не показываем).
    public double? ComputeCost(string? model, UsageInfo? usage)
    {
        if (usage is null) return null;
        var m = ResolveByModel(model)?.FindModel(model);
        if (m is null || (m.PriceInMissPer1M == 0 && m.PriceOutPer1M == 0)) return null;
        // cache_creation тарифицируется как обычный (miss) вход
        return (usage.InputTokens * m.PriceInMissPer1M
                + usage.CacheCreationTokens * m.PriceInMissPer1M
                + usage.CacheReadTokens * m.PriceInHitPer1M
                + usage.OutputTokens * m.PriceOutPer1M) / 1_000_000;
    }
}
