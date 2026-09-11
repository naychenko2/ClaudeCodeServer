namespace ClaudeHomeServer.Services;

// RuleRuntimeState — в Core (Models/RuleRuntimeState.cs): TriggerContext несёт это состояние,
// а TriggerSources — отдельная сборка, видящая только Core.

// Per-persona runtime-состояние: содержит состояния правил + общий счётчик троттлинга
// (потолок реакций в час — per-persona, не per-rule).
public sealed class PersonaRuntimeState
{
    public Dictionary<string, RuleRuntimeState> Rules { get; set; } = new();
    // Потолок N реакций в час: окно и счётчик. Сбрасывается при выходе из часового окна.
    public int HourBucketCount { get; set; }
    public DateTime? HourBucketStart { get; set; }
}

// Стор runtime-состояния автоматизаций. Один словарь personaId → PersonaRuntimeState под своим
// _stateLock (НЕ PersonaManager._saveLock). Сохранение — JsonFileStore, формат data/persona-automation-state.json.
public sealed class AutomationStateStore
{
    private readonly Dictionary<string, PersonaRuntimeState> _store;
    private readonly string _storePath;
    private readonly Lock _stateLock = new();

    public AutomationStateStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "persona-automation-state.json");
        _store = JsonFileStore.Load<Dictionary<string, PersonaRuntimeState>>(_storePath) ?? new();
    }

    // Состояние правила (создаётся при первом обращении). Возвращаемый объект мутируется источниками
    // и executor'ом напрямую (ссылка); персистентность — через Save(). Тик однопоточный (PeriodicTimer),
    // поэтому гонки в пределах тика нет; единственный конкурентент — Mention (push) — трогает только
    // LastFiredAt/RunCount, гонки там доброкачественны (идемпотентность + потолок спасают).
    public RuleRuntimeState GetRule(string personaId, string ruleId)
    {
        lock (_stateLock)
        {
            var p = GetOrCreate(personaId);
            if (!p.Rules.TryGetValue(ruleId, out var r)) p.Rules[ruleId] = r = new RuleRuntimeState();
            return r;
        }
    }

    // Проверка квоты БЕЗ потребления — pre-check тика до опроса снапшот-источников:
    // при исчерпанном потолке детекцию откладываем (снапшот не продвигаем), а не сжигаем события.
    public bool HasHourlyBudget(string personaId, int cap, DateTime nowUtc)
    {
        lock (_stateLock)
        {
            var p = GetOrCreate(personaId);
            if (p.HourBucketStart is null || nowUtc - p.HourBucketStart >= TimeSpan.FromHours(1))
                return true;   // окно истекло — квота обнулится при первом Consume
            return p.HourBucketCount < cap;
        }
    }

    // Потолок реакций в час per-persona: true — квота есть (и уже consumed), false — превышен.
    // Вызывается executor'ом перед запуском LLM — fail-fast до дорогого вызова.
    public bool TryConsumeHourly(string personaId, int cap, DateTime nowUtc)
    {
        lock (_stateLock)
        {
            var p = GetOrCreate(personaId);
            if (p.HourBucketStart is null || nowUtc - p.HourBucketStart >= TimeSpan.FromHours(1))
            {
                p.HourBucketStart = nowUtc;
                p.HourBucketCount = 0;
            }
            if (p.HourBucketCount >= cap) return false;
            p.HourBucketCount++;
            return true;
        }
    }

    // Очистка состояния при удалении правила (best-effort; orphaned-записи безопасны).
    public void RemoveRule(string personaId, string ruleId)
    {
        lock (_stateLock)
        {
            if (GetOrCreate(personaId).Rules.Remove(ruleId)) Save();
        }
    }

    public void Save() { lock (_stateLock) JsonFileStore.Save(_storePath, _store); }

    private PersonaRuntimeState GetOrCreate(string personaId)
    {
        if (!_store.TryGetValue(personaId, out var p)) _store[personaId] = p = new PersonaRuntimeState();
        return p;
    }
}
