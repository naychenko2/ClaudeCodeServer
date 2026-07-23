using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Modules;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Резолвит эффективные значения фич-флагов для пользователя:
/// override из users.json поверх дефолтов из <see cref="FeatureFlagCatalog"/>.
/// Помимо статического каталога, включает динамические флаги видимости внешних
/// модулей из <see cref="ModuleRegistry"/> — ключ "module-{id}", дефолт true
/// (модуль в реестре = подключён админом явно; тумблер позволяет скрыть per-user).
/// </summary>
public class FeatureFlagService(UserStore users, ModuleRegistry? modules = null)
{
    private IEnumerable<FeatureFlagDefinition> ModuleDefinitions() =>
        (modules?.All ?? []).Select(m => new FeatureFlagDefinition(
            Key: m.FeatureFlagKey,
            Title: $"Модуль «{m.Manifest.DisplayName}»",
            Description: string.IsNullOrWhiteSpace(m.Manifest.Description)
                ? $"Внешний модуль {m.Id}: вкладка в оболочке и его MCP-инструменты в сессиях."
                : m.Manifest.Description!,
            Default: true,
            Stage: "beta"));

    /// <summary>Каталог определений флагов (для рендера тумблеров на фронте).</summary>
    public IReadOnlyList<FeatureFlagDefinition> GetDefinitions() =>
        [.. FeatureFlagCatalog.All, .. ModuleDefinitions()];

    /// <summary>
    /// Эффективные значения для юзера: по каждому флагу из каталога берётся
    /// per-user override или дефолт. Ключи, которых нет в каталоге, игнорируются.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetEffective(string userId)
    {
        var overrides = users.GetById(userId)?.FeatureFlags;
        var defs = GetDefinitions();
        var result = new Dictionary<string, bool>(defs.Count);
        foreach (var def in defs)
            result[def.Key] = overrides != null && overrides.TryGetValue(def.Key, out var v) ? v : def.Default;
        return result;
    }

    /// <summary>Существует ли флаг (каталог или модульный) — для валидации PUT /api/feature-flags.</summary>
    public bool Exists(string key) =>
        FeatureFlagCatalog.Exists(key) || (modules?.All.Any(m => m.FeatureFlagKey == key) ?? false);

    /// <summary>Эффективное значение одного флага для юзера (для гейтов в сервисах).</summary>
    public bool IsEnabled(string userId, string key) =>
        GetEffective(userId).TryGetValue(key, out var v) && v;
}
