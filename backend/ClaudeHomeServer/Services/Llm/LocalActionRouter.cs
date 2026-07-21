namespace ClaudeHomeServer.Services.Llm;

// Решает, идёт ли конкретное фоновое действие на локальную модель (Ollama) или на
// существующий механизм (claude one-shot). Источник — секция Ollama:Actions (словарь
// ключ→bool) поверх дефолтов каталога (политика A: при настроенном Ollama рекомендованные
// действия уходят на локаль, если не сказано иначе). Плюс резолв профиля вызова с учётом
// переопределений Ollama:Profiles.
public sealed class LocalActionRouter
{
    private readonly OllamaClient _ollama;
    private readonly Dictionary<string, bool> _overrides;
    private readonly Dictionary<CheapProfile, CheapProfileSpec> _profiles;
    private readonly ILogger<LocalActionRouter> _log;

    public LocalActionRouter(OllamaClient ollama, IConfiguration config, ILogger<LocalActionRouter> log)
    {
        _ollama = ollama;
        _log = log;
        _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Ollama:Actions — словарь ключ→bool. Неизвестные ключи не молчим: это опечатка в конфиге.
        foreach (var child in config.GetSection("Ollama:Actions").GetChildren())
        {
            if (!LocalActionCatalog.IsKnown(child.Key))
            {
                _log.LogWarning("Ollama:Actions — неизвестный ключ действия «{Key}», игнорирую", child.Key);
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
                TimeoutMs: s.GetValue("TimeoutMs", def.TimeoutMs));
        }
    }

    public bool OllamaEnabled => _ollama.Enabled;

    // Идёт ли действие на локальную модель. Требует настроенного Ollama; иначе всегда claude.
    // Внутри — override из конфига поверх дефолта каталога.
    public bool UsesLocal(string actionKey)
    {
        if (!_ollama.Enabled) return false;
        if (_overrides.TryGetValue(actionKey, out var v)) return v;
        return LocalActionCatalog.Find(actionKey)?.DefaultLocal ?? false;
    }

    public CheapProfileSpec ProfileSpec(CheapProfile profile) => _profiles[profile];

    public CheapProfileSpec ProfileFor(string actionKey)
    {
        var p = LocalActionCatalog.Find(actionKey)?.Profile ?? CheapProfile.Text;
        return _profiles[p];
    }

    // Модель, которой пойдёт локальный вызов (для UI использования)
    public string LocalModel => _ollama.TextModel;
}
