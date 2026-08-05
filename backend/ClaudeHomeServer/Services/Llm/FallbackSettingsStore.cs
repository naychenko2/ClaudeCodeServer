using System.Text.Json;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Llm;

// Настройки фолбэк-оркестрации модели (ADR «Порядок резолва модели, классы ошибок
// фолбэка, защита от зацикливания» §4). Глобальное значение плюс per-owner override —
// слой хозяина сессии бьёт глобальную, отсутствие ключа у обоих → дефолт кода.
//
// Стор узкий и расширяемый: подраздел «fallback» единственный, будущие разделы
// (специальности/пресеты правил) приходят из соседних волн своим стором. Файл живёт
// в data/, попадает в бэкап автоматически (BackupPaths.ShouldInclude работает от обратного);
// секретов не содержит. Версия формата инкрементируется при ломающем изменении файла —
// стареющий код после отката не должен молча применить незнакомую структуру.
//
// Снимок файла — неизменяемый объект; запись целиком под write-локом, читатели
// не видят полумутированного состояния. Образец — LocalActionOverridesStore
// и SpecialtySettingsStore.
public sealed class FallbackSettingsStore
{
    // Версия формата файла. Старший код, читающий файл с версией выше текущей,
    // игнорирует содержимое с warning — безопаснее молчаливой каши (правило BackupSchema).
    public const int FormatVersion = 1;

    // Жёсткий потолок глубины по ADR §4: 5 записей подмен в ленте — предел полезности,
    // дальше честнее отдать ошибку. Любая пользовательская настройка клампится сюда.
    public const int HardMaxSubstitutions = 5;

    // Дефолт при отсутствии настройки у обоих слоёв — подобран чтобы быть «безопасным»
    // (не на каждом ходе прыгать по провайдерам, но и не одной попыткой задушить
    // реальный сбой) и совпадал с ADR.
    public const int DefaultMaxSubstitutions = 3;

    public sealed class FallbackSettings
    {
        // null — настройки нет (берётся слой ниже: per-owner → global → дефолт)
        public int? MaxSubstitutions { get; set; }
    }

    public sealed class FallbackSettingsFile
    {
        public int Version { get; set; } = FormatVersion;
        public FallbackSettings Global { get; set; } = new();
        // Per-owner слой: ключ — id владельца сессии (UserStore.Id как в адаптерах).
        // Сравнение Ordinal, не OrdinalIgnoreCase: идентификаторы чувствительны к регистру.
        public Dictionary<string, FallbackSettings> Owners { get; set; } =
            new(StringComparer.Ordinal);
    }

    private readonly string _storePath;
    private readonly ILogger<FallbackSettingsStore>? _log;
    private readonly object _writeLock = new();
    private volatile FallbackSettingsFile _file = new();

    public FallbackSettingsStore(IConfiguration config, ILogger<FallbackSettingsStore>? log = null)
    {
        _log = log;
        // Путь выводим ТОЛЬКО от DataPath (как LocalActionOverridesStore): иначе стор ляжет
        // рядом с исполняемым файлом и настройка станет эфемерной.
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "model-fallback.json");
        Load();
    }

    public FallbackSettingsFile Snapshot => _file;

    // Эффективный потолок глубины цепочки для владельца: per-owner → global → дефолт.
    // Значение всегда клампится в 1..HardMaxSubstitutions — потолок жёсткий по ADR §4.
    // null/пустой ownerId ведут к global (тот же путь, что у личных слотов без владельца —
    // безопаснее, чем читать чужой per-owner).
    public int ResolveMaxSubstitutions(string? ownerId)
    {
        var file = _file;
        var value = FindOwnerMax(file, ownerId) ?? file.Global?.MaxSubstitutions;
        return ClampMaxSubstitutions(value);
    }

    private static int? FindOwnerMax(FallbackSettingsFile file, string? ownerId)
    {
        if (string.IsNullOrEmpty(ownerId)) return null;
        return file.Owners.TryGetValue(ownerId, out var owner) ? owner?.MaxSubstitutions : null;
    }

    // Кламп в жёсткий диапазон 1..HardMaxSubstitutions. null/вне диапазона → дефолт.
    // Держится в одном месте, чтобы изменение потолка или дефолта касалось всех читателей.
    public static int ClampMaxSubstitutions(int? value)
    {
        if (value is not { } v || v < 1 || v > HardMaxSubstitutions) return DefaultMaxSubstitutions;
        return v;
    }

    // --- Запись ---
    // Методы нужны полной картиной для админского UI в будущих волнах; пока
    // пишутся только тестами. Контроллера фолбэк-настроек ещё нет — он не вошёл в эту
    // задачу, и контракт стора его не обязывает.

    public string? SetGlobal(int? maxSubstitutions)
    {
        var error = ValidateMaxSubstitutions(maxSubstitutions);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            next.Global = new FallbackSettings { MaxSubstitutions = maxSubstitutions };
            Persist(next);
        }
        _log?.LogInformation("Глобальный потолок подмен обновлён: {Value}", maxSubstitutions);
        return null;
    }

    public string? SetOwner(string ownerId, int? maxSubstitutions)
    {
        if (string.IsNullOrEmpty(ownerId)) return "Не указан владелец";
        var error = ValidateMaxSubstitutions(maxSubstitutions);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            if (maxSubstitutions is null) next.Owners.Remove(ownerId);
            else next.Owners[ownerId] = new FallbackSettings { MaxSubstitutions = maxSubstitutions };
            Persist(next);
        }
        _log?.LogInformation("Личный потолок подмен обновлён (owner={Owner}): {Value}", ownerId, maxSubstitutions);
        return null;
    }

    private static string? ValidateMaxSubstitutions(int? value)
    {
        if (value is null) return null;
        if (value < 1 || value > HardMaxSubstitutions)
            return $"Потолок подмен должен быть в диапазоне 1..{HardMaxSubstitutions}";
        return null;
    }

    // --- Персистентность ---

    private static FallbackSettingsFile Clone(FallbackSettingsFile file) =>
        JsonSerializer.Deserialize<FallbackSettingsFile>(JsonSerializer.Serialize(file))!;

    private void Persist(FallbackSettingsFile next)
    {
        _file = next;
        try
        {
            JsonFileStore.Save(_storePath, next);
        }
        catch (Exception ex)
        {
            // Настройка уже применена в памяти — теряем только персистентность до рестарта
            _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
        }
    }

    private void Load()
    {
        var file = JsonFileStore.Load<FallbackSettingsFile>(_storePath, logger: _log);
        if (file is null) return;
        if (file.Version > FormatVersion)
        {
            // Файл снят более новым кодом (восстановлен из свежего бэкапа): незнакомую
            // структуру не применяем — дефолты безопаснее.
            _log?.LogWarning(
                "model-fallback.json имеет формат {FileVersion} новее поддерживаемого {Version} — стартую с дефолтами",
                file.Version, FormatVersion);
            return;
        }
        file.Global ??= new FallbackSettings();
        file.Owners ??= new Dictionary<string, FallbackSettings>(StringComparer.Ordinal);
        // Защита per-owner изоляции: значения, хранимые в файле, клампятся по загрузке тоже.
        // Иначе подкрученный руками JSON мог бы поставить чужой владельцу 0 или 99.
        if (file.Global.MaxSubstitutions is { } gv
            && (gv < 1 || gv > HardMaxSubstitutions))
        {
            _log?.LogWarning(
                "model-fallback.json: global.maxSubstitutions={Value} вне диапазона, игнорирую", gv);
            file.Global.MaxSubstitutions = null;
        }
        foreach (var (key, owner) in file.Owners.ToList())
        {
            if (owner.MaxSubstitutions is { } ov && (ov < 1 || ov > HardMaxSubstitutions))
            {
                _log?.LogWarning(
                    "model-fallback.json: owners[{Owner}].maxSubstitutions={Value} вне диапазона, игнорирую",
                    key, ov);
                owner.MaxSubstitutions = null;
            }
        }
        _file = file;
    }
}
