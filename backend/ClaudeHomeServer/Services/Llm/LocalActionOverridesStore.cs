using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Рантайм-оверрайды маршрута фоновых действий, которые админ задаёт из UI. Слой ПОВЕРХ
// конфига Ollama:Actions: конфиг остаётся дефолтом развёртывания, а стор — оперативной
// настройкой без рестарта сервера.
//
// Значение — исполнитель ПЕРВОГО шага: LocalRoute («локальная модель») либо id конкретной
// модели любого настроенного провайдера. Дальше цепочка одинакова для всех: выбранное →
// локаль → claude (см. CheapTextRunner).
//
// Читается на каждом фоновом вызове, пишется редко (клик в UI), поэтому снимок держим
// в неизменяемом словаре и при записи заменяем ЦЕЛИКОМ — читатели никогда не видят
// полумутированное состояние и не нуждаются в блокировке.
public sealed class LocalActionOverridesStore
{
    // Псевдо-значения маршрута (не id моделей): локальная модель Ollama и «модель Claude
    // по умолчанию для этого действия» — та, что действие исторически берёт из конфига
    // (Notes:AiModel, Tasks:AiModel и т.п.).
    public const string LocalRoute = "local";
    public const string ClaudeRoute = "claude";

    private readonly string _storePath;
    private readonly ILogger<LocalActionOverridesStore>? _log;
    private readonly object _writeLock = new();
    private volatile Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public LocalActionOverridesStore(IConfiguration config, ILogger<LocalActionOverridesStore>? log = null)
    {
        _log = log;
        // Путь выводим ТОЛЬКО от DataPath: иначе стор ляжет рядом с исполняемым файлом и
        // настройка станет эфемерной (потеряется при следующем деплое).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "local-actions.json");
        Load();
    }

    // Оверрайд админа для действия; null — не задан (значение берётся из конфига/каталога).
    public string? TryGet(string actionKey) =>
        _overrides.TryGetValue(actionKey, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> All => _overrides;

    // Задать маршрут: LocalRoute или id модели. Неизвестный ключ отвергаем — это опечатка
    // вызывающего, а молчаливое сохранение мусора потом всплыло бы «настройка не применяется».
    public bool Set(string actionKey, string route)
    {
        var action = LocalActionCatalog.Find(actionKey);
        if (action is null || string.IsNullOrWhiteSpace(route)) return false;

        var value = route.Trim();
        lock (_writeLock)
        {
            var next = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase)
            {
                [action.Key] = value,
            };
            _overrides = next;
            Persist(next);
        }
        _log?.LogInformation("Маршрут действия «{Key}» задан админом: {Route}", actionKey, value);
        return true;
    }

    // Снять оверрайд — действие возвращается к значению из конфига/каталога.
    public bool Reset(string actionKey)
    {
        var action = LocalActionCatalog.Find(actionKey);
        if (action is null) return false;

        lock (_writeLock)
        {
            if (!_overrides.ContainsKey(action.Key)) return true;
            var next = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
            next.Remove(action.Key);
            _overrides = next;
            Persist(next);
        }
        _log?.LogInformation("Оверрайд маршрута действия «{Key}» снят админом", actionKey);
        return true;
    }

    private void Persist(Dictionary<string, string> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            // Настройка уже применена в памяти — теряем только персистентность до рестарта.
            _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
        }
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (loaded is null) return;

            // Отсеиваем ключи, исчезнувшие из каталога (действие переименовали/удалили) —
            // иначе они висели бы в файле мёртвым грузом и путали при отладке.
            var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in loaded)
            {
                if (LocalActionCatalog.Find(key) is not { } a)
                {
                    _log?.LogWarning("local-actions.json: действие «{Key}» отсутствует в каталоге, игнорирую", key);
                    continue;
                }
                // Формат до появления выбора модели: true = локаль, false = claude. Молча мигрируем.
                var route = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.True => LocalRoute,
                    JsonValueKind.False => ClaudeRoute,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(route)) clean[a.Key] = route!;
            }
            _overrides = clean;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Не удалось прочитать {Path}, продолжаю без оверрайдов", _storePath);
        }
    }
}
