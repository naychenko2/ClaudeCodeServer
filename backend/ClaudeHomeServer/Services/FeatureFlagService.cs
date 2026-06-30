using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Резолвит эффективные значения фич-флагов для пользователя:
/// override из users.json поверх дефолтов из <see cref="FeatureFlagCatalog"/>.
/// </summary>
public class FeatureFlagService(UserStore users)
{
    /// <summary>Каталог определений флагов (для рендера тумблеров на фронте).</summary>
    public IReadOnlyList<FeatureFlagDefinition> GetDefinitions() => FeatureFlagCatalog.All;

    /// <summary>
    /// Эффективные значения для юзера: по каждому флагу из каталога берётся
    /// per-user override или дефолт. Ключи, которых нет в каталоге, игнорируются.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetEffective(string userId)
    {
        var overrides = users.GetById(userId)?.FeatureFlags;
        var result = new Dictionary<string, bool>(FeatureFlagCatalog.All.Count);
        foreach (var def in FeatureFlagCatalog.All)
            result[def.Key] = overrides != null && overrides.TryGetValue(def.Key, out var v) ? v : def.Default;
        return result;
    }
}
