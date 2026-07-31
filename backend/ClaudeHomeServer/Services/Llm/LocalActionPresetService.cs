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
//   ────────────────────┼─────────────────────────────────┼──────────────────────────────────
//   Balanced            │ Small→local (нет Ollama→haiku),  │ тир Claude по Profile
//     (по сложности)    │ Text→free (нет free→Claude),     │
//                       │ Large→тир Claude                 │
//
// Balanced распределяет по РЕАЛЬНОЙ сложности (CheapProfile), а не по бинарному DefaultLocal:
// простое (Small: теги/заголовки/классификация) тянет слабая локаль qwen, среднее (Text) — на
// бесплатной облачной (мощнее локали, но без затрат), тяжёлое (Large: суммаризация/автолёрн/
// конспекты) — на Claude ради качества на больших входах. «Сильные» действия (артефакты, лицо
// продукта) всегда на Claude.
//
// Тир Claude по профилю — из конфига Recommended:ClaudeTiers; бесплатная модель — из каталога
// прямых моделей всех источников (provider={source}-direct), ранжирование — по курируемому
// порядку моделей в конфиге источника (OpenRouter:PreferredFree для openrouter, Models для
// остальных) с фолбэком на эвристику «наибольшее окно».
public enum ActionPreset { Recommended, FreeOnly, LocalFirst, Balanced, Tiers, TiersLocal }

public sealed class LocalActionPresetService(
    LocalActionOverridesStore store, LocalActionRouter router, OllamaClient ollama,
    ModelCatalogService models, IConfiguration config,
    ILogger<LocalActionPresetService> log)
{
    // Исполнитель на каждый профиль сложности (Recommended). Дефолт — слоты тиров инстанса
    // (мелочь и середина — слабая, тяжёлое — средняя; сами модели слотов задаются в
    // «Поставщиках моделей»). Конфиг Recommended:ClaudeTiers перебивает (id модели или tier:*).
    private string TierFor(CheapProfile profile)
    {
        var key = profile switch
        {
            CheapProfile.Small => "small",
            CheapProfile.Text => "text",
            _ => "large",
        };
        var v = config[$"Recommended:ClaudeTiers:{key}"];
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return LocalActionOverridesStore.TierRoute(
            profile == CheapProfile.Large ? ModelTier.Medium : ModelTier.Weak);
    }

    // Прямые (бесплатные) модели всех OpenAI-совместимых источников (openrouter-direct, freellmapi-direct, …)
    // — Value уже с префиксом direct:. Их наличие определяет доступность пресетов с бесплатной облачной моделью.
    private async Task<IReadOnlyList<ModelCatalogService.ModelInfo>> DirectModelsAsync(CancellationToken ct) =>
        (await models.GetModelsAsync(ct))
            .Where(m => m.Provider.EndsWith("-direct", StringComparison.OrdinalIgnoreCase))
            .ToList();

    // Ключ источника прямой модели: provider заканчивается на "-direct".
    private static string SourceKeyOf(ModelCatalogService.ModelInfo m) =>
        m.Provider[..^"-direct".Length];

    // Есть ли из чего собрать бесплатный облачный маршрут (нужно FreeOnly и «сильным» в LocalFirst).
    public async Task<bool> FreeAvailableAsync(CancellationToken ct = default) =>
        (await DirectModelsAsync(ct)).Count > 0;

    // Применить пресет ко всем действиям каталога. Возвращает число затронутых действий.
    public async Task<int> ApplyAsync(ActionPreset preset, CancellationToken ct = default)
    {
        // Бесплатная облачная модель под каждый профиль — считаем один раз (список общий).
        var freeByProfile = new Dictionary<CheapProfile, string?>();
        if (preset is ActionPreset.FreeOnly or ActionPreset.LocalFirst or ActionPreset.Balanced)
        {
            var direct = await DirectModelsAsync(ct);
            foreach (var p in Enum.GetValues<CheapProfile>())
                freeByProfile[p] = PickFree(direct, p);
        }

        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in LocalActionCatalog.All)
        {
            // Старые пресеты (Recommended/FreeOnly/LocalFirst/Balanced) затрагивали только
            // фоновые one-shot действия. Новые v2-пресеты «Tiers»/«TiersLocal» управляют
            // всем списком «Применение моделей», включая агентные места «Чаты и персоны».
            if (a.Agentic && preset is not (ActionPreset.Tiers or ActionPreset.TiersLocal)) continue;
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
                // Balanced — по реальной сложности профиля. «Сильные» (артефакты) всегда на Claude.
                ActionPreset.Balanced => !a.DefaultLocal
                    ? TierFor(a.Profile)
                    : a.Profile switch
                    {
                        // Простое тянет слабая локаль; без Ollama честно уходит на дешёвый Claude.
                        CheapProfile.Small => ollama.Enabled
                            ? LocalActionOverridesStore.LocalRoute
                            : TierFor(CheapProfile.Small),
                        // Среднее — на бесплатную облачную (мощнее локали); нет free — на Claude.
                        CheapProfile.Text => freeByProfile[CheapProfile.Text] ?? TierFor(CheapProfile.Text),
                        // Тяжёлое — на Claude ради качества на больших входах.
                        _ => TierFor(a.Profile),
                    },
                // v2: каждому месту — слот по его дефолтному тиру (включая агентные).
                ActionPreset.Tiers => LocalActionOverridesStore.TierRoute(LocalActionCatalog.EffectiveDefaultTier(a)),
                // v2: фоновые лёгкие (DefaultLocal) → локаль, всё остальное → tier. Агентным
                // локаль всегда недоступна, поэтому они всегда получают tier.
                ActionPreset.TiersLocal => a.Agentic
                    ? LocalActionOverridesStore.TierRoute(LocalActionCatalog.EffectiveDefaultTier(a))
                    : a.DefaultLocal && ollama.Enabled
                        ? LocalActionOverridesStore.LocalRoute
                        : LocalActionOverridesStore.TierRoute(LocalActionCatalog.EffectiveDefaultTier(a)),
                _ => LocalActionOverridesStore.ClaudeRoute,
            };
            routes[a.Key] = route;
        }

        // keepUnlisted: старые пресеты — частичная настройка фоновых действий, оставляем
        // агентные и любые другие оверрайды без изменений. v2-пресеты — цельная картина
        // «Применение моделей», поэтому сбрасываем всё, что не вошло в routes.
        var keepUnlisted = preset is not (ActionPreset.Tiers or ActionPreset.TiersLocal);
        store.SetMany(routes, keepUnlisted);
        log.LogInformation("Применён пресет автоподбора {Preset} для {Count} действий", preset, routes.Count);
        return routes.Count;
    }

    // Бесплатная облачная модель под профиль: для каждого источника сперва первая подходящая
    // модель из его курируемого списка (окно ≥ NumCtx профиля), затем — наибольшее окно среди
    // всех direct-моделей. Value уже несёт префикс direct: — возвращаем как есть.
    private string? PickFree(IReadOnlyList<ModelCatalogService.ModelInfo> direct, CheapProfile profile)
    {
        if (direct.Count == 0) return null;
        var minCtx = router.ProfileSpec(profile).NumCtx;

        var bySource = direct
            .GroupBy(m => SourceKeyOf(m), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Порядок источников — как в конфиге CheapHttpSources; оставшиеся (legacy openrouter) — в конце.
        var sourceKeys = config.GetSection("CheapHttpSources").GetChildren()
            .Select(c => c.Key)
            .Where(k => bySource.ContainsKey(k))
            .ToList();
        foreach (var k in bySource.Keys)
            if (!sourceKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                sourceKeys.Add(k);

        foreach (var sourceKey in sourceKeys)
        {
            foreach (var id in PreferredIds(sourceKey))
            {
                var hit = direct.FirstOrDefault(m =>
                    string.Equals(SourceKeyOf(m), sourceKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(CloudCheapClient.StripPrefix(m.Value), id, StringComparison.OrdinalIgnoreCase)
                    && (m.ContextWindow ?? 0) >= minCtx);
                if (hit is not null) return hit.Value;
            }
        }

        var fit = direct.Where(m => (m.ContextWindow ?? 0) >= minCtx)
                      .OrderByDescending(m => m.ContextWindow ?? 0).FirstOrDefault()
                  ?? direct.OrderByDescending(m => m.ContextWindow ?? 0).First();
        return fit.Value;
    }

    // Курируемый порядок моделей источника: у openrouter — легаси PreferredFree,
    // у остальных (freellmapi и т.д.) — список Models из CheapHttpSources:{key}:Models.
    private string[] PreferredIds(string sourceKey)
    {
        if (string.Equals(sourceKey, "openrouter", StringComparison.OrdinalIgnoreCase))
            return config.GetSection("OpenRouter:PreferredFree").Get<string[]>() ?? [];

        var ids = new List<string>();
        foreach (var child in config.GetSection($"CheapHttpSources:{sourceKey}:Models").GetChildren())
        {
            var id = child["Id"] ?? child.Value;
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids.ToArray();
    }
}
