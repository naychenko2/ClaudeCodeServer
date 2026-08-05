using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Настройка шаблона специальности (слой: глобальный или per-owner). Слой, если он
// задан для специальности, заменяет шаблон ЦЕЛИКОМ (полевого слияния между слоями нет):
// не задан — берётся слой ниже (per-owner → глобальный → дефолт кода SpecialtyCatalog).
public class SpecialtyTemplateSettings
{
    public PersonaAccess Access { get; set; } = PersonaAccess.Full;
    // null — все возможности (tasks+notes+web); список — только перечисленные
    public List<string>? Tools { get; set; }
    // Имеет смысл только при Access == Custom
    public List<string>? DisallowedTools { get; set; }
}

// Правило выбора модели в пресете: для специальности (ключ или "any") — маршрут.
// Лексика маршрутов — как у LocalActionOverridesStore: tier:strong|medium|weak,
// id модели любого провайдера, local, claude, default.
public class ModelRouteRule
{
    public string Specialty { get; set; } = SpecialtyCatalog.AnySpecialtyKey;
    public string Route { get; set; } = "";
}

// Именованный пресет правил выбора модели. Состав и порядок правил значимы:
// резолв идёт по первому совпадению (SpecialtySettingsStore.ResolveRoute).
public class ModelRoutePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<ModelRouteRule> Rules { get; set; } = [];
}

// Признак пресета в объединённом списке: общий для инстанса или личный владельца
public enum PresetScope { Global, Owner }

// Слой настроек: шаблоны специальностей + пресеты правил.
public class SpecialtySettingsLayer
{
    public Dictionary<string, SpecialtyTemplateSettings> Specialties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ModelRoutePreset> Presets { get; set; } = [];

    public bool IsEmpty => Specialties.Count == 0 && Presets.Count == 0;
}

// Файл стора на диске (data/specialty-settings.json)
public class SpecialtySettingsFile
{
    public int Version { get; set; } = SpecialtySettingsStore.FormatVersion;
    public SpecialtySettingsLayer Global { get; set; } = new();
    public Dictionary<string, SpecialtySettingsLayer> Owners { get; set; } = new(StringComparer.Ordinal);
}

// Стор настроек специальностей и именованных пресетов правил выбора модели.
// Глобальные значения + per-owner слой: шаблоны специальностей переопределяют
// глобальные, а личные пресеты живут РЯДОМ с глобальными — не затирают их даже
// при совпадении id или имени (решение «Глобальные + личные рядом»).
//
// Файл живёт в data/ → попадает в бэкап автоматически (BackupPaths.ShouldInclude
// работает от обратного). Формат версионирован: file.Version новее кода — содержимое
// игнорируется с warning (философия BackupSchema: незнакомая структура не должна
// молча применяться).
//
// Снимок файла держим неизменяемым объектом и заменяем целиком под write-локом —
// читатели не видят полумутированного состояния (образец: LocalActionOverridesStore).
public sealed class SpecialtySettingsStore
{
    public const int FormatVersion = 1;

    // Ключи возможностей персоны (PersonaManager.AllTools) — по ним фильтруем Tools шаблонов
    private static readonly string[] CoreToolKeys = ["tasks", "notes", "web"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _storePath;
    private readonly ILogger<SpecialtySettingsStore>? _log;
    private readonly object _writeLock = new();
    private volatile SpecialtySettingsFile _file = new();

    public SpecialtySettingsStore(IConfiguration config, ILogger<SpecialtySettingsStore>? log = null)
    {
        _log = log;
        // Путь выводим ТОЛЬКО от DataPath (как LocalActionOverridesStore): иначе стор
        // лёг бы рядом с исполняемым файлом и терялся при деплое
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "specialty-settings.json");
        Load();
    }

    // --- Чтение ---

    public SpecialtySettingsFile Snapshot => _file;

    // Настройка шаблона специальности: per-owner слой → глобальный. null — настройки нет
    // (шаблон берётся из дефолтов кода SpecialtyCatalog).
    public SpecialtyTemplateSettings? TemplateSettings(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var file = _file;
        if (file.Owners.TryGetValue(ownerId, out var owner)
            && owner.Specialties.TryGetValue(key, out var ownerSettings))
            return ownerSettings;
        return file.Global.Specialties.TryGetValue(key, out var globalSettings) ? globalSettings : null;
    }

    // Эффективный шаблон специальности для владельца: настройка (личная/глобальная)
    // либо дефолт кода; null — шаблона нет вовсе (специальность без шаблона).
    public SpecialtyTemplate? EffectiveTemplate(string ownerId, PersonaSpecialty specialty)
    {
        if (TemplateSettings(ownerId, specialty) is { } settings)
            return new SpecialtyTemplate(settings.Access, settings.Tools, settings.DisallowedTools);
        return SpecialtyCatalog.Get(specialty).DefaultTemplate;
    }

    // Эффективные пресеты владельца: личные, затем ВСЕ глобальные. Личные пресеты
    // не переопределяют глобальные (в том числе с тем же id или именем) — оба набора
    // живут рядом и вместе участвуют в резолве. Порядок значим для ResolveRoute:
    // личный блок идёт первым, поэтому личное правило бьёт раньше глобального.
    public IReadOnlyList<ModelRoutePreset> EffectivePresets(string ownerId)
    {
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        if (owner is null || owner.Presets.Count == 0) return file.Global.Presets;

        var result = new List<ModelRoutePreset>(owner.Presets.Count + file.Global.Presets.Count);
        result.AddRange(owner.Presets);
        result.AddRange(file.Global.Presets);
        return result;
    }

    // Объединённый список пресетов владельца с признаком слоя (общий / мой):
    // личные впереди, затем глобальные — тот же порядок обхода у ResolveRoute.
    public IReadOnlyList<(ModelRoutePreset Preset, PresetScope Scope)> EffectivePresetsWithScope(string ownerId)
    {
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        var result = new List<(ModelRoutePreset, PresetScope)>(
            (owner?.Presets.Count ?? 0) + file.Global.Presets.Count);
        if (owner is not null)
            result.AddRange(owner.Presets.Select(p => (p, PresetScope.Owner)));
        result.AddRange(file.Global.Presets.Select(p => (p, PresetScope.Global)));
        return result;
    }

    // Маршрут для специальности по пресетам владельца: обходятся ОБА набора (личный,
    // затем глобальный); первое правило, где специальность совпала или правило помечено
    // "any". null — ни один пресет не сработал.
    public string? ResolveRoute(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        foreach (var preset in EffectivePresets(ownerId))
            foreach (var rule in preset.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Route)) continue;
                if (string.Equals(rule.Specialty, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rule.Specialty, SpecialtyCatalog.AnySpecialtyKey, StringComparison.OrdinalIgnoreCase))
                    return rule.Route;
            }
        return null;
    }

    // --- Запись ---

    // Заменить глобальный слой. null-ошибка = слой валиден.
    public string? SetGlobal(SpecialtySettingsLayer layer)
    {
        var error = ValidateLayer(layer);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            next.Global = NormalizeLayer(layer);
            Persist(next);
        }
        _log?.LogInformation("Глобальные настройки специальностей обновлены");
        return null;
    }

    // Заменить per-owner слой. Пустой слой снимает личные переопределения владельца
    // (запись удаляется — остаются глобальные значения).
    public string? SetOwner(string ownerId, SpecialtySettingsLayer layer)
    {
        var error = ValidateLayer(layer);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            if (layer.IsEmpty) next.Owners.Remove(ownerId);
            else next.Owners[ownerId] = NormalizeLayer(layer);
            Persist(next);
        }
        _log?.LogInformation("Личные настройки специальностей обновлены (owner={Owner})", ownerId);
        return null;
    }

    // --- Валидация и нормализация ---

    // null — слой валиден; иначе текст ошибки (контроллер отдаёт его как 400).
    public static string? ValidateLayer(SpecialtySettingsLayer layer)
    {
        foreach (var key in layer.Specialties.Keys)
            if (!SpecialtyCatalog.TryGetByKey(key, out _))
                return $"Неизвестная специальность: {key}";

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in layer.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
                return "У пресета правил пустое имя";
            if (!string.IsNullOrWhiteSpace(preset.Id) && !seenIds.Add(preset.Id))
                return $"Дублируется id пресета: {preset.Id}";
            foreach (var rule in preset.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Route))
                    return $"Пресет «{preset.Name}»: у правила пустой маршрут";
                if (!string.Equals(rule.Specialty, SpecialtyCatalog.AnySpecialtyKey, StringComparison.OrdinalIgnoreCase)
                    && !SpecialtyCatalog.TryGetByKey(rule.Specialty, out _))
                    return $"Пресет «{preset.Name}»: неизвестная специальность правила {rule.Specialty}";
            }
        }
        return null;
    }

    // Канонический вид слоя: ключи специальностей — camelCase каталога, Tools —
    // нормализованы (полный набор → null = «все»), у не-Custom профиля запреты пусты,
    // пустые id пресетов досозданы.
    private static SpecialtySettingsLayer NormalizeLayer(SpecialtySettingsLayer layer)
    {
        var specialties = new Dictionary<string, SpecialtyTemplateSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, settings) in layer.Specialties)
        {
            if (!SpecialtyCatalog.TryGetByKey(key, out var entry)) continue;
            specialties[entry.Key] = new SpecialtyTemplateSettings
            {
                Access = settings.Access,
                Tools = NormalizeTools(settings.Tools),
                DisallowedTools = settings.Access == PersonaAccess.Custom
                    ? CleanList(settings.DisallowedTools)
                    : null,
            };
        }

        var presets = layer.Presets.Select(p => new ModelRoutePreset
        {
            Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString() : p.Id.Trim(),
            Name = p.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim(),
            Rules = p.Rules.Select(r => new ModelRouteRule
            {
                Specialty = SpecialtyCatalog.TryGetByKey(r.Specialty, out var e)
                    ? e.Key
                    : SpecialtyCatalog.AnySpecialtyKey,
                Route = r.Route.Trim(),
            }).ToList(),
        }).ToList();

        return new SpecialtySettingsLayer { Specialties = specialties, Presets = presets };
    }

    private static List<string>? NormalizeTools(List<string>? tools)
    {
        if (tools is null) return null;
        var clean = tools.Select(t => t.Trim().ToLowerInvariant())
            .Where(t => CoreToolKeys.Contains(t)).Distinct().ToList();
        return CoreToolKeys.All(clean.Contains) ? null : clean;
    }

    private static List<string>? CleanList(List<string>? items)
    {
        var clean = items?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
        return clean is { Count: > 0 } ? clean : null;
    }

    // --- Персистентность ---

    private static SpecialtySettingsFile Clone(SpecialtySettingsFile file) =>
        JsonSerializer.Deserialize<SpecialtySettingsFile>(JsonSerializer.Serialize(file, JsonOpts), JsonOpts)!;

    private void Persist(SpecialtySettingsFile next)
    {
        _file = next;
        try
        {
            JsonFileStore.Save(_storePath, next, JsonOpts);
        }
        catch (Exception ex)
        {
            // Настройка уже применена в памяти — теряем только персистентность до рестарта
            _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
        }
    }

    private void Load()
    {
        var file = JsonFileStore.Load<SpecialtySettingsFile>(_storePath, JsonOpts, _log);
        if (file is null) return;
        if (file.Version > FormatVersion)
        {
            // Файл снят более новым кодом (например, восстановлен из свежего бэкапа):
            // незнакомую структуру не применяем — дефолты безопаснее молчаливой каши
            _log?.LogWarning(
                "specialty-settings.json имеет формат {FileVersion} новее поддерживаемого {Version} — стартую с дефолтами",
                file.Version, FormatVersion);
            return;
        }
        file.Global ??= new SpecialtySettingsLayer();
        file.Owners ??= new Dictionary<string, SpecialtySettingsLayer>(StringComparer.Ordinal);
        _file = file;
    }
}
