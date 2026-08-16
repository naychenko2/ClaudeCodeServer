using System.Text.Json;

namespace ClaudeHomeServer.Services.Images;

// Настройка генерации картинок инстанса: у КАЖДОГО места (иконка проекта, аватар персоны)
// свой провайдер (auto|fal|glif) и своя модель у каждого провайдера. Файл —
// data/image-generation.json; в бэкап попадает автоматически (BackupPaths.ShouldInclude
// работает от обратного), секретов не содержит — ключи провайдеров остаются в
// appsettings.Local.json.
//
// Слои: место в сторе (правит админ в UI) → секция конфига Images (общая на все места)
// → дефолт кода auto. Образец — FallbackSettingsStore: снимок неизменяемый, запись целиком
// под локом.
public sealed class ImageGenerationSettingsStore
{
    // Версия формата файла; инкремент при ломающем изменении структуры (правило BackupSchema).
    // 1 — один выбор на инстанс, 2 — выбор по местам (читается и первый, см. Load).
    public const int FormatVersion = 2;

    // Выбор одного места
    public sealed class PlaceSettings
    {
        // auto | fal | glif. null — не задано, берётся слой ниже (конфиг → auto)
        public string? Provider { get; set; }

        // Ключ провайдера → id модели. Отсутствие ключа — дефолт слоя ниже.
        public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ImageGenerationSettingsFile
    {
        public int Version { get; set; } = FormatVersion;

        // Ключ места (ImagePlaces.*) → его настройка
        public Dictionary<string, PlaceSettings> Places { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Формат 1: один выбор на весь инстанс. Читается только ради миграции (раскладывается
        // на все места) и больше не пишется — отсюда JsonIgnore у пустых значений.
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Provider { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Models { get; set; }
    }

    private readonly string _storePath;
    private readonly ImageGenerationOptions _defaults;
    private readonly ILogger<ImageGenerationSettingsStore>? _log;
    private readonly object _writeLock = new();
    private volatile ImageGenerationSettingsFile _file = new();

    public ImageGenerationSettingsStore(IConfiguration config, ILogger<ImageGenerationSettingsStore>? log = null)
    {
        _log = log;
        // Путь выводим ТОЛЬКО от DataPath (как остальные сторы): иначе файл ляжет рядом
        // с исполняемым и настройка станет эфемерной.
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "image-generation.json");
        _defaults = config.GetSection(ImageGenerationOptions.SectionName).Get<ImageGenerationOptions>()
                    ?? new ImageGenerationOptions();
        Load();
    }

    public ImageGenerationSettingsFile Snapshot => _file;

    // Эффективный режим места: стор → конфиг → auto. Всегда в нижнем регистре.
    public string ProviderFor(string place)
    {
        var value = Place(place)?.Provider;
        if (string.IsNullOrWhiteSpace(value)) value = _defaults.Provider;
        return string.IsNullOrWhiteSpace(value)
            ? ImageGenerationOptions.Auto
            : value.Trim().ToLowerInvariant();
    }

    // Модель провайдера в этом месте: стор → конфиг → null (дефолт драйвера)
    public string? ModelFor(string place, string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return null;
        if (Place(place) is { } settings
            && settings.Models.TryGetValue(providerKey, out var model) && !string.IsNullOrWhiteSpace(model))
            return model.Trim();
        if (_defaults.Models.TryGetValue(providerKey, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
            return fallback.Trim();
        return null;
    }

    // Патч-семантика одного места: null — поле не прислали (оставить прежним), "" — сброс к
    // слою ниже. Валидацию места, провайдера и модели делает ImageGenerationService
    // (он знает состав драйверов).
    public void Save(string place, string? provider, IDictionary<string, string?>? models = null)
    {
        if (string.IsNullOrWhiteSpace(place)) return;
        lock (_writeLock)
        {
            var current = Place(place);
            var next = new PlaceSettings
            {
                Provider = provider is null
                    ? current?.Provider
                    : (string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant()),
                Models = current is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(current.Models, StringComparer.OrdinalIgnoreCase),
            };
            if (models is not null)
            {
                foreach (var (key, value) in models)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (string.IsNullOrWhiteSpace(value)) next.Models.Remove(key);
                    else next.Models[key] = value.Trim();
                }
            }

            var file = new ImageGenerationSettingsFile
            {
                Version = FormatVersion,
                Places = new Dictionary<string, PlaceSettings>(_file.Places, StringComparer.OrdinalIgnoreCase),
            };
            file.Places[place] = next;
            _file = file;
            try
            {
                JsonFileStore.Save(_storePath, file);
            }
            catch (Exception ex)
            {
                // Настройка уже применена в памяти — теряем только персистентность до рестарта
                _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
            }
        }
    }

    private PlaceSettings? Place(string? place) =>
        !string.IsNullOrWhiteSpace(place) && _file.Places.TryGetValue(place, out var settings) ? settings : null;

    // Чтение устойчиво к регистру имён свойств: стор пишет PascalCase, а руками файл
    // правят в camelCase — иначе настройка молча игнорируется (ловушка FallbackSettingsStore).
    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private void Load()
    {
        var file = JsonFileStore.Load<ImageGenerationSettingsFile>(_storePath, LoadOptions, _log);
        if (file is null) return;
        if (file.Version > FormatVersion)
        {
            // Файл снят более новым кодом (восстановлен из свежего бэкапа): незнакомую
            // структуру не применяем — дефолты безопаснее.
            _log?.LogWarning(
                "image-generation.json имеет формат {FileVersion} новее поддерживаемого {Version} — стартую с дефолтами",
                file.Version, FormatVersion);
            return;
        }

        file.Places = file.Places is null
            ? new Dictionary<string, PlaceSettings>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlaceSettings>(file.Places, StringComparer.OrdinalIgnoreCase);
        foreach (var settings in file.Places.Values)
            settings.Models = settings.Models is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(settings.Models, StringComparer.OrdinalIgnoreCase);

        // Миграция формата 1: прежний общий выбор раскладываем на все места — иначе человек
        // после обновления получил бы сброс к auto там, где сознательно выбрал сервис.
        // В памяти, без перезаписи файла: следующее сохранение места запишет уже формат 2.
        var hasLegacy = !string.IsNullOrWhiteSpace(file.Provider) || file.Models is { Count: > 0 };
        if (file.Places.Count == 0 && hasLegacy)
        {
            foreach (var place in ImagePlaces.All)
                file.Places[place] = new PlaceSettings
                {
                    Provider = file.Provider,
                    Models = file.Models is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(file.Models, StringComparer.OrdinalIgnoreCase),
                };
            _log?.LogInformation(
                "image-generation.json формата 1: общий выбор «{Provider}» разложен на места {Places}",
                file.Provider ?? ImageGenerationOptions.Auto, string.Join(", ", ImagePlaces.All));
        }

        file.Provider = null;
        file.Models = null;
        _file = file;
    }
}
