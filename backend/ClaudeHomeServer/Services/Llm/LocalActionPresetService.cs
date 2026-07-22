using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Llm;

// Пресеты автоподбора исполнителя фоновых действий. Массово проставляют маршруты всех действий
// каталога по единому правилу, а дальше действие идёт по обычной цепочке (см. CheapTextRunner).
//
//                       │ лёгкие (DefaultLocal:true)      │ «сильные» (DefaultLocal:false)
//   ────────────────────┼─────────────────────────────────┼──────────────────────────────────
//   Recommended         │ local (нет Ollama → тир Claude   │ тир Claude по Profile
//     (с платными)      │   по Profile)                    │   (Small→haiku, Text/Large→sonnet)
//   ────────────────────┼─────────────────────────────────┼──────────────────────────────────
//   FreeOnly            │ бесплатная облачная (direct:)    │ бесплатная облачная (direct:)
//   ────────────────────┼─────────────────────────────────┼──────────────────────────────────
//   LocalFirst          │ local                            │ бесплатная облачная (direct:)
//
// Тир Claude по профилю — из конфига Recommended:ClaudeTiers; бесплатная модель — из каталога
// прямых моделей OpenRouter (provider=openrouter-direct, курируемый список OpenRouter:DirectModels),
// ранжирование — по OpenRouter:PreferredFree с фолбэком на эвристику «наибольшее окно».
public enum ActionPreset { Recommended, FreeOnly, LocalFirst }

public sealed class LocalActionPresetService(
    LocalActionOverridesStore store, LocalActionRouter router, OllamaClient ollama,
    ModelCatalogService models, IConfiguration config,
    ILogger<LocalActionPresetService> log)
{
    // Тир Claude на каждый профиль сложности (Recommended). Дефолт: мелочь — haiku,
    // всё серьёзнее — sonnet (потолок; Opus в фоне дорог и медленен).
    private string TierFor(CheapProfile profile)
    {
        var key = profile switch
        {
            CheapProfile.Small => "small",
            CheapProfile.Text => "text",
            _ => "large",
        };
        var def = profile == CheapProfile.Small ? "haiku" : "sonnet";
        var v = config[$"Recommended:ClaudeTiers:{key}"];
        return string.IsNullOrWhiteSpace(v) ? def : v.Trim();
    }

    // Прямые (бесплатные) модели OpenRouter из каталога — provider=openrouter-direct, Value уже
    // с префиксом direct:. Их наличие определяет доступность пресетов с бесплатной облачной моделью.
    private async Task<IReadOnlyList<ModelCatalogService.ModelInfo>> DirectModelsAsync(CancellationToken ct) =>
        (await models.GetModelsAsync(ct))
            .Where(m => m.Provider == CloudCheapClient.DirectProviderKey)
            .ToList();

    // Есть ли из чего собрать бесплатный облачный маршрут (нужно FreeOnly и «сильным» в LocalFirst).
    public async Task<bool> FreeAvailableAsync(CancellationToken ct = default) =>
        (await DirectModelsAsync(ct)).Count > 0;

    // Применить пресет ко всем действиям каталога. Возвращает число затронутых действий.
    public async Task<int> ApplyAsync(ActionPreset preset, CancellationToken ct = default)
    {
        // Бесплатная облачная модель под каждый профиль — считаем один раз (список общий).
        var freeByProfile = new Dictionary<CheapProfile, string?>();
        if (preset is ActionPreset.FreeOnly or ActionPreset.LocalFirst)
        {
            var direct = await DirectModelsAsync(ct);
            foreach (var p in Enum.GetValues<CheapProfile>())
                freeByProfile[p] = PickFree(direct, p);
        }

        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in LocalActionCatalog.All)
        {
            var route = preset switch
            {
                ActionPreset.Recommended => a.DefaultLocal && ollama.Enabled
                    ? LocalActionOverridesStore.LocalRoute
                    : TierFor(a.Profile),
                ActionPreset.FreeOnly => freeByProfile[a.Profile]
                    ?? TierFor(a.Profile), // free нет — честно падаем на дешёвый Claude
                ActionPreset.LocalFirst => a.DefaultLocal
                    ? LocalActionOverridesStore.LocalRoute
                    : freeByProfile[a.Profile] ?? TierFor(a.Profile),
                _ => LocalActionOverridesStore.ClaudeRoute,
            };
            routes[a.Key] = route;
        }

        store.SetMany(routes);
        log.LogInformation("Применён пресет автоподбора {Preset} для {Count} действий", preset, routes.Count);
        return routes.Count;
    }

    // Бесплатная облачная модель под профиль: сперва первая подходящая из PreferredFree
    // (окно ≥ NumCtx профиля), иначе — наибольшее окно среди годных, иначе — просто
    // наибольшее окно. Value модели уже несёт префикс direct: — возвращаем как есть.
    private string? PickFree(IReadOnlyList<ModelCatalogService.ModelInfo> direct, CheapProfile profile)
    {
        if (direct.Count == 0) return null;
        var minCtx = router.ProfileSpec(profile).NumCtx;

        var preferred = config.GetSection("OpenRouter:PreferredFree").Get<string[]>() ?? [];
        foreach (var id in preferred)
        {
            // PreferredFree задаётся чистыми id (как в DirectModels) — сверяем без префикса direct:.
            var hit = direct.FirstOrDefault(m =>
                string.Equals(CloudCheapClient.StripPrefix(m.Value), id, StringComparison.OrdinalIgnoreCase)
                && (m.ContextWindow ?? 0) >= minCtx);
            if (hit is not null) return hit.Value;
        }

        var fit = direct.Where(m => (m.ContextWindow ?? 0) >= minCtx)
                      .OrderByDescending(m => m.ContextWindow ?? 0).FirstOrDefault()
                  ?? direct.OrderByDescending(m => m.ContextWindow ?? 0).First();
        return fit.Value;
    }
}
