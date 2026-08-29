namespace ClaudeHomeServer.Services.Llm;

// Откуда взято действующее значение маршрута — нужно UI, чтобы честно показать,
// переопределил ли админ значение и есть ли что сбрасывать.
public enum RouteSource { Default, Config, Admin }

// Исполнитель ПЕРВОГО шага действия. Дальше цепочка одинакова для всех:
// выбранное → локальная модель (если настроена) → claude.
// Local  — локальная модель Ollama;
// Claude — модель действия по умолчанию (та, что оно берёт из своего конфига);
// Tier   — слот тира инстанса (сильная/средняя/слабая, AppSettings.ModelTier*);
//          пустой слот откатывается на модель действия, как Claude;
// Model  — конкретная модель конкретного провайдера (Model заполнено её id).
public enum RouteKind { Local, Claude, Tier, Model }

// Действующий маршрут действия: чем начинаем, какой моделью (для Kind=Model),
// каким слотом (для Kind=Tier) и откуда взято.
public sealed record ActionRoute(RouteKind Kind, string? Model, RouteSource Source,
    ModelTier? Tier = null);

// Решает, идёт ли конкретное фоновое действие на локальную модель (Ollama) или на
// существующий механизм (claude one-shot). Приоритет источников: оверрайд админа из UI
// (LocalActionOverridesStore) → секция Ollama:Actions конфига → дефолт каталога
// (политика A: при настроенном Ollama рекомендованные действия уходят на локаль).
// Плюс резолв профиля вызова с учётом переопределений Ollama:Profiles.
//
// Роутер — singleton, но админский слой читается из стора на каждом вызове, поэтому
// переключение тумблера действует сразу, без рестарта.
public sealed class LocalActionRouter
{
    private readonly ILocalLlmClient _ollama;
    private readonly LocalActionOverridesStore _store;
    private readonly SpecialtySettingsStore? _specialty;
    private readonly Dictionary<string, bool> _overrides;
    private readonly Dictionary<CheapProfile, CheapProfileSpec> _profiles;
    private readonly ILogger<LocalActionRouter> _log;

    public LocalActionRouter(ILocalLlmClient ollama, LocalActionOverridesStore store,
        IConfiguration config, ILogger<LocalActionRouter> log,
        SpecialtySettingsStore? specialty = null)
    {
        _ollama = ollama;
        _store = store;
        _specialty = specialty;
        _log = log;
        _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Ollama:Actions — словарь ключ→bool. Неизвестные ключи не молчим: это опечатка в конфиге.
        foreach (var child in config.GetSection("Ollama:Actions").GetChildren())
        {
            if (!LocalActionCatalog.IsKnown(child.Key))
            {
                _log.LogWarning("Ollama:Actions — неизвестный ключ действия «{Action}», игнорирую", child.Key);
                continue;
            }
            if (bool.TryParse(child.Value, out var v)) _overrides[child.Key] = v;
        }

        // Профили: дефолты каталога, переопределяемые Ollama:Profiles:{small|text|large}.
        _profiles = new Dictionary<CheapProfile, CheapProfileSpec>();
        foreach (var (profile, def) in LocalActionCatalog.ProfileDefaults)
        {
            var s = config.GetSection($"Ollama:Profiles:{profile.ToString().ToLowerInvariant()}");
            _profiles[profile] = new CheapProfileSpec(
                NumCtx: s.GetValue("NumCtx", def.NumCtx),
                NumPredict: s.GetValue("NumPredict", def.NumPredict),
                TimeoutMs: s.GetValue("TimeoutMs", def.TimeoutMs),
                CloudTimeoutMs: s.GetValue("CloudTimeoutMs", def.CloudTimeoutMs),
                CloudNumPredict: s.GetValue("CloudNumPredict", def.CloudNumPredict));
        }
    }

    public bool OllamaEnabled => _ollama.Enabled;

    // Начинается ли действие с локальной модели. Требует настроенного Ollama; иначе — нет.
    public bool UsesLocal(string actionKey) =>
        _ollama.Enabled && Resolve(actionKey).Kind == RouteKind.Local;

    // Действующий маршрут и его источник — БЕЗ учёта доступности Ollama: UI показывает
    // настройку и при выключенной локали (иначе выбор выглядел бы сброшенным).
    public ActionRoute Resolve(string actionKey)
    {
        if (_store.TryGet(actionKey) is { } admin)
        {
            // preset:{id} → первый шаг цепочки (ADR-007 §3). Полный цикл шагов пресета для
            // фоновых мест не ведём (нет оркестратора, как у агентного фолбэка): первый шаг
            // становится «выбранным», при сбое — штатная local→claude. Битая ссылка — fail-open
            // (пустой шаг) → дефолт каталога ниже. tier:*-шаг разворачивается CheapTextRunner'ом
            // по слоту владельца (через EffectiveFallback с ownerId).
            if (LocalActionOverridesStore.IsPresetRoute(admin) && _specialty is not null)
            {
                var firstStep = _specialty.ExpandChain(admin, ownerId: null)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (firstStep is not null) return Parse(firstStep, RouteSource.Admin);
            }
            else
            {
                return Parse(admin, RouteSource.Admin);
            }
        }
        if (_overrides.TryGetValue(actionKey, out var cfg))
            return new ActionRoute(cfg ? RouteKind.Local : RouteKind.Claude, null, RouteSource.Config);
        // Без настройки админа и конфига место идёт на свой слот тира из каталога
        // (сильная/средняя/слабая): слово «по умолчанию» означает одно и то же во всём
        // продукте. Модель действия (обычно haiku) остаётся фолбэком — её берёт
        // CheapTextRunner.EffectiveFallback, когда слот пуст.
        var action = LocalActionCatalog.Find(actionKey);
        return action?.DefaultLocal == true
            ? new ActionRoute(RouteKind.Local, null, RouteSource.Default)
            : new ActionRoute(RouteKind.Tier, null, RouteSource.Default,
                action is null ? ModelTier.Medium : LocalActionCatalog.EffectiveDefaultTier(action));
    }

    private static ActionRoute Parse(string route, RouteSource source) => route switch
    {
        LocalActionOverridesStore.LocalRoute => new ActionRoute(RouteKind.Local, null, source),
        // Легаси-значения одиночной «модели по умолчанию» (v1): оба означали «обычная модель,
        // не локаль» — с появлением слотов это средняя
        LocalActionOverridesStore.ClaudeRoute or LocalActionOverridesStore.DefaultRoute =>
            new ActionRoute(RouteKind.Tier, null, source, ModelTier.Medium),
        _ when LocalActionOverridesStore.ParseTierRoute(route) is { } tier =>
            new ActionRoute(RouteKind.Tier, null, source, tier),
        _ => new ActionRoute(RouteKind.Model, route, source),
    };

    public CheapProfileSpec ProfileSpec(CheapProfile profile) => _profiles[profile];

    public CheapProfileSpec ProfileFor(string actionKey)
    {
        var p = LocalActionCatalog.Find(actionKey)?.Profile ?? CheapProfile.Text;
        return _profiles[p];
    }

    // Таймаут вызова ПО МАРШРУТУ: локаль живёт со своим (Ollama-параметры), облачные
    // маршруты — со своим, заметно большим (см. CheapProfileSpec). Когда вызов реально
    // идёт на локаль (UsesLocal требует живую Ollama) — локальный потолок; иначе цепочка
    // может закончиться на claude, и потолок обязан быть облачным.
    public int TimeoutMsFor(string actionKey) =>
        UsesLocal(actionKey) ? ProfileFor(actionKey).TimeoutMs : CloudTimeoutMsFor(actionKey);

    // Эффективный потолок ожидания ОБЛАЧНОГО шага: пер-местное значение из каталога
    // (место с нестандартной скоростью исполнителя), иначе потолок профиля. Единственная
    // точка склейки — CheapTextRunner берёт лимит только отсюда.
    public int CloudTimeoutMsFor(string actionKey) =>
        LocalActionCatalog.Find(actionKey)?.CloudTimeoutMs
            ?? ProfileFor(actionKey).CloudTimeoutMs;

    // Модель, которой пойдёт локальный вызов (для UI использования)
    public string LocalModel => _ollama.TextModel;
}
