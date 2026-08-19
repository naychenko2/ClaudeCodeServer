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
    public const string DefaultRoute = "default";
    // Слоты тиров (v2): "tier:strong|medium|weak" — ссылка на именованную модель инстанса
    // (AppSettings.ModelTier*). Легаси-значения ClaudeRoute/DefaultRoute при чтении маршрута
    // трактуются как tier:medium (LocalActionRouter.Parse) — сам стор их не переписывает.
    public const string TierPrefix = "tier:";

    // Ссылка на именованный пресет-цепочку (ADR-007 §3): "preset:{id}" — допустимое значение
    // там, где выбирается модель (слоты, ячейки матриц персоны/специальности, места каталога,
    // явная модель). Хранится как есть, разворачивается в цепочку на границе запуска хода.
    public const string PresetPrefix = "preset:";

    public static string TierRoute(ModelTier tier) =>
        TierPrefix + tier.ToString().ToLowerInvariant();

    public static ModelTier? ParseTierRoute(string route) =>
        route.ToLowerInvariant() switch
        {
            "tier:strong" => ModelTier.Strong,
            "tier:medium" => ModelTier.Medium,
            "tier:weak" => ModelTier.Weak,
            _ => null,
        };

    // Это ссылка на пресет? ("preset:{id}"). Регистр префикса не важен — как у tier:.
    public static bool IsPresetRoute(string? route) =>
        !string.IsNullOrWhiteSpace(route)
        && route!.Trim().StartsWith(PresetPrefix, StringComparison.OrdinalIgnoreCase);

    // id пресета из ссылки "preset:{id}" либо null (не ссылка/пустой id). trim + оригинальный
    // регистр id (id — Guid, но чужие значения не портим). По образцу ParseTierRoute.
    public static string? ParsePresetRoute(string? route)
    {
        if (route is null) return null;
        var span = route.AsSpan().Trim();
        if (!span.StartsWith(PresetPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = span[PresetPrefix.Length..].Trim();
        return id.IsEmpty ? null : id.ToString();
    }

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
            var prev = _overrides;
            var next = new Dictionary<string, string>(prev, StringComparer.OrdinalIgnoreCase)
            {
                [action.Key] = value,
            };
            _overrides = next;
            if (!Persist(next))
            {
                // Запись на диск не удалась — откатываем in-memory к прежнему состоянию,
                // иначе настройка была бы видна применённой, но потерялась бы при рестарте.
                // Set отдаёт false → контроллер вернёт 500, админ увидит, что не сохранилось.
                _overrides = prev;
                return false;
            }
        }
        _log?.LogInformation("Маршрут действия «{Action}» задан админом: {Route}", actionKey, value);
        return true;
    }

    // Массовая замена оверрайдов (применение пресета автоподбора). Пары с неизвестным ключом
    // или пустым значением молча отбрасываются. keepUnlisted=false — ключи вне набора снимаются
    // (пресет — цельная картина), true — сохраняются (частичное применение). Одна запись на диск.
    public void SetMany(IEnumerable<KeyValuePair<string, string>> routes, bool keepUnlisted = false)
    {
        lock (_writeLock)
        {
            var next = keepUnlisted
                ? new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, route) in routes)
            {
                if (LocalActionCatalog.Find(key) is not { } a || string.IsNullOrWhiteSpace(route)) continue;
                next[a.Key] = route.Trim();
            }
            _overrides = next;
            Persist(next);
        }
        _log?.LogInformation("Маршруты фоновых действий заданы пресетом ({Count})", _overrides.Count);
    }

    // Снять все оверрайды разом — все действия возвращаются к значению из конфига/каталога.
    public void ResetAll()
    {
        lock (_writeLock)
        {
            if (_overrides.Count == 0) return;
            _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Persist(_overrides);
        }
        _log?.LogInformation("Все оверрайды маршрутов фоновых действий сняты");
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
        _log?.LogInformation("Оверрайд маршрута действия «{Action}» снят админом", actionKey);
        return true;
    }

    // Возвращает false при ошибке записи — вызывающий (Set) откатит in-memory состояние,
    // иначе админ видел бы настройку применённой, а при рестарте она терялась (ловушка
    // финальной приёмки: успешный PUT не двигал mtime, Set молча отдавал true).
    private bool Persist(Dictionary<string, string> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot));
            return true;
        }
        catch (Exception ex)
        {
            // Раньше исключение проглатывалось (только лог), и Set возвращал успех — настройка
            // висела в памяти до рестарта и бесследно пропадала. Теперь честно отдаём失败.
            _log?.LogError(ex, "Не удалось записать {Path} — настройка не сохранена на диск", _storePath);
            return false;
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
                    _log?.LogWarning("local-actions.json: действие «{Action}» отсутствует в каталоге, игнорирую", key);
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
