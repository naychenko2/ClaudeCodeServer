using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Матрица моделей по уровням (ADR-007 §2): значение ячейки — id модели ИЛИ "preset:{id}".
    // Пустая ячейка = «спроси следующую (широкую) матрицу». "tier:*" в ячейке запрещён
    // валидацией: ячейка уже адресована уровнем, тир внутри неё — тавтология или петля.
    public string? TierStrong { get; set; }
    public string? TierMedium { get; set; }
    public string? TierWeak { get; set; }
    // Источник УРОВНЯ специальности (замена прежней роли «специальность даёт tier:*-маршрут»):
    // каким уровнем работают персоны этой специальности, если у них/задачи нет своего.
    // null — не задан (уровень берётся у персоны или дефолта места).
    public ModelTier? DefaultTier { get; set; }

    public string? TierCell(ModelTier tier) => tier switch
    {
        ModelTier.Strong => TierStrong,
        ModelTier.Weak => TierWeak,
        _ => TierMedium,
    };
}

// Именованный пресет — упорядоченная цепочка шагов (ADR-007 §1). Шаг — маршрут в существующей
// лексике LocalActionOverridesStore: tier:strong|medium|weak, id модели любого провайдера,
// local, claude, default. Шаг НЕ может быть ссылкой "preset:{id}" — вложенность запрещена
// валидацией (вложенность превращает разворачивание в обход графа с циклами; пользы нет —
// цепочку можно записать плоско). Длина цепочки 1..5 — потолок общий с фолбэком
// (FallbackSettingsStore.HardMaxSubstitutions): шаги после первого — это и есть подмены.
public class ModelRoutePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Steps { get; set; } = [];
}

// Признак пресета в объединённом списке: общий для инстанса, назначенный пользователю
// (админ, слой B9) или личный владельца. Порядок в списке — порядок резолва по id.
public enum PresetScope { Global, User, Owner }

// Слой настроек: шаблоны специальностей, «любая специальность» и пресеты-цепочки.
public class SpecialtySettingsLayer
{
    public Dictionary<string, SpecialtyTemplateSettings> Specialties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // «Любая специальность» (наследник правила "any" v1): применяется, когда у конкретной
    // специальности записи нет. Та же форма, что у записи специальности. Семантика слоёв
    // сохраняется: owner-слой DefaultSpecialty заменяет глобальный целиком.
    public SpecialtyTemplateSettings? DefaultSpecialty { get; set; }
    public List<ModelRoutePreset> Presets { get; set; } = [];

    public bool IsEmpty => Specialties.Count == 0 && Presets.Count == 0 && DefaultSpecialty is null;
}

// Итог сброса настроек моделей в слое: Changed — число ФАКТИЧЕСКИ изменённых записей
// (у предпросмотра — сколько изменится), Shadowed — состояние слоя ПОСЛЕ операции: ключи
// записей, оставшихся ради собственных прав (уровней не несут, нижний слой затеняют).
public sealed record SpecialtyResetResult(int Changed, IReadOnlyList<string> Shadowed);

// Файл стора на диске (data/specialty-settings.json). Users — слой «пользователь» (B9):
// настройки, назначенные админом конкретному пользователю; ключ — id пользователя.
public class SpecialtySettingsFile
{
    public int Version { get; set; } = SpecialtySettingsStore.FormatVersion;
    public SpecialtySettingsLayer Global { get; set; } = new();
    public Dictionary<string, SpecialtySettingsLayer> Owners { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SpecialtySettingsLayer> Users { get; set; } = new(StringComparer.Ordinal);
}

// Стор настроек специальностей и именованных пресетов-цепочек выбора модели.
// Три слоя: глобальный (инстанс), «пользователь» (B9 — админ назначает конкретному
// пользователю) и per-owner личный. Приоритет зафиксирован: личный → пользовательский →
// глобальный; запись слоя заменяет нижнюю ЦЕЛИКОМ (полевого слияния нет). Пресеты всех
// слоёв живут РЯДОМ — не затирают друг друга даже при совпадении id или имени
// (решение «Глобальные + личные рядом»), поиск по id идёт от личного вниз.
//
// Файл живёт в data/ → попадает в бэкап автоматически (BackupPaths.ShouldInclude работает
// от обратного). Формат версионирован: file.Version новее кода — содержимое игнорируется
// с warning; старше кода — мигрируется при загрузке (v1→v2, ADR-007 §6).
//
// Снимок файла держим неизменяемым объектом и заменяем целиком под write-локом — читатели
// не видят полумутированного состояния (образец: LocalActionOverridesStore).
public sealed class SpecialtySettingsStore
{
    // v2 — пресет из «сборника правил специальность→маршрут» стал именованной цепочкой Steps;
    // у специальности появились матрица моделей по уровням + DefaultTier; у слоя — DefaultSpecialty.
    public const int FormatVersion = 2;

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

    // Настройка шаблона специальности: per-owner → пользовательский (B9) → глобальный.
    // null — настройки нет (шаблон берётся из дефолтов кода SpecialtyCatalog).
    public SpecialtyTemplateSettings? TemplateSettings(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var file = _file;
        if (file.Owners.TryGetValue(ownerId, out var owner)
            && owner.Specialties.TryGetValue(key, out var ownerSettings))
            return ownerSettings;
        if (file.Users.TryGetValue(ownerId, out var user)
            && user.Specialties.TryGetValue(key, out var userSettings))
            return userSettings;
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

    // Эффективные пресеты владельца: личные, затем назначенные пользователю (B9),
    // затем ВСЕ глобальные. Наборы не переопределяют друг друга (в том числе с тем же
    // id или именем) — живут рядом. Порядок значим для поиска по id при разворачивании
    // preset:{id} (ExpandChain): личный блок идёт первым.
    public IReadOnlyList<ModelRoutePreset> EffectivePresets(string ownerId)
    {
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        var user = file.Users.GetValueOrDefault(ownerId);
        var globalCount = file.Global.Presets.Count;
        if ((owner is null || owner.Presets.Count == 0)
            && (user is null || user.Presets.Count == 0)) return file.Global.Presets;

        var result = new List<ModelRoutePreset>(
            (owner?.Presets.Count ?? 0) + (user?.Presets.Count ?? 0) + globalCount);
        if (owner is not null) result.AddRange(owner.Presets);
        if (user is not null) result.AddRange(user.Presets);
        result.AddRange(file.Global.Presets);
        return result;
    }

    // Объединённый список пресетов владельца с признаком слоя (общий / пользователю / мой):
    // личные впереди, затем пользовательские, затем глобальные — тот же порядок, что у
    // EffectivePresets.
    public IReadOnlyList<(ModelRoutePreset Preset, PresetScope Scope)> EffectivePresetsWithScope(string ownerId)
    {
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        var user = file.Users.GetValueOrDefault(ownerId);
        var result = new List<(ModelRoutePreset, PresetScope)>(
            (owner?.Presets.Count ?? 0) + (user?.Presets.Count ?? 0) + file.Global.Presets.Count);
        if (owner is not null)
            result.AddRange(owner.Presets.Select(p => (p, PresetScope.Owner)));
        if (user is not null)
            result.AddRange(user.Presets.Select(p => (p, PresetScope.User)));
        result.AddRange(file.Global.Presets.Select(p => (p, PresetScope.Global)));
        return result;
    }

    // Найти пресет по id среди эффективных (личные раньше глобальных). null — не найден
    // (битая ссылка preset:{id}). Используется ExpandChain и (в будущем) UI «где используется».
    public ModelRoutePreset? FindPreset(string ownerId, string presetId)
    {
        foreach (var p in EffectivePresets(ownerId))
            if (string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    // Упорядоченный список матриц специальности для разворачивания уровня (ADR-007 §2):
    // запись специальности (owner → пользовательский → глобальный, целиком без полевого
    // слияния), затем DefaultSpecialty (та же цепочка). Только записи, которые ЕСТЬ в слое;
    // пустые ячейки внутри записи рассматриваются разворачивателем (UserModelTierResolver) —
    // здесь отдаём матрицу как есть, пустые ячейки в ней означают «спроси следующую».
    public IReadOnlyList<TierMatrix> SpecialtyMatrices(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        var user = file.Users.GetValueOrDefault(ownerId);
        var result = new List<TierMatrix>(2);
        // Запись специальности: первый непустой слой (owner → user → global), целиком
        var spec = owner?.Specialties.GetValueOrDefault(key)
            ?? user?.Specialties.GetValueOrDefault(key)
            ?? file.Global.Specialties.GetValueOrDefault(key);
        if (spec is not null)
            result.Add(ToMatrix(spec));
        // DefaultSpecialty: owner-слой → пользовательский → глобальный (та же логика)
        var ds = owner?.DefaultSpecialty ?? user?.DefaultSpecialty ?? file.Global.DefaultSpecialty;
        if (ds is not null)
            result.Add(ToMatrix(ds));
        return result;
    }

    private static TierMatrix ToMatrix(SpecialtyTemplateSettings s) =>
        new(s.TierStrong, s.TierMedium, s.TierWeak);

    // Источник УРОВНЯ специальности (ADR-007 §2): каким уровнем работают персоны этой
    // специальности, если у задачи/персоны нет своего. Запись специальности (owner →
    // пользовательский → глобальный), затем DefaultSpecialty (та же цепочка). null —
    // уровень не задан специальностью.
    public ModelTier? SpecialtyDefaultTier(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var file = _file;
        var owner = file.Owners.GetValueOrDefault(ownerId);
        var user = file.Users.GetValueOrDefault(ownerId);
        var spec = owner?.Specialties.GetValueOrDefault(key)
            ?? user?.Specialties.GetValueOrDefault(key)
            ?? file.Global.Specialties.GetValueOrDefault(key);
        if (spec?.DefaultTier is { } tier) return tier;
        return owner?.DefaultSpecialty?.DefaultTier
            ?? user?.DefaultSpecialty?.DefaultTier
            ?? file.Global.DefaultSpecialty?.DefaultTier;
    }

    // Разворот маршрута в цепочку шагов (ADR-007 §3). Единая точка разворачивания цепочки:
    // обычный маршрут → [маршрут]; "preset:{id}" → шаги пресета (поиск в EffectivePresets).
    // Битая ссылка (пресет удалён) → пустой список (fail-open вниз) + warning. Шаги НЕ
    // разворачиваются рекурсивно (вложенность пресетов запрещена валидацией): tier:*-шаги
    // разворачивает вызывающий через UserModelTierResolver (по слотам владельца, без матриц).
    public IReadOnlyList<string> ExpandChain(string? route, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(route)) return [];
        if (LocalActionOverridesStore.ParsePresetRoute(route) is { } presetId)
        {
            if (FindPreset(ownerId ?? "", presetId) is { } preset)
                return preset.Steps;
            _log?.LogWarning("Ссылка на удалённый пресет {Id} (место выбора модели) — игнорирую, fail-open вниз", presetId);
            return [];
        }
        return [route.Trim()];
    }

    // --- Запись ---

    // Разовая переадресация закреплённых моделей (миграция каталога провайдера): id из карты
    // заменяется в шагах пресетов-цепочек и в ячейках матриц уровней — во ВСЕХ слоях
    // (глобальный, пользовательские, личные), включая запись «любая специальность».
    // Значения не-модели («preset:{id}», «tier:*», local/claude/default) и незнакомые id
    // остаются как были: карта адресуется точным совпадением. Возвращает число изменённых
    // записей (шаг цепочки и ячейка считаются по отдельности); 0 — файл не переписывается.
    public int RemapModels(IReadOnlyDictionary<string, string> map)
    {
        lock (_writeLock)
        {
            // Мутируем клон, не Snapshot (он отдаёт живой объект читателям)
            var next = Clone(_file);
            var changed = 0;
            foreach (var layer in Layers(next)) changed += RemapLayer(layer, map);
            if (changed == 0) return 0;
            next.Version = FormatVersion;
            Persist(next);
            return changed;
        }
    }

    private static IEnumerable<SpecialtySettingsLayer> Layers(SpecialtySettingsFile file)
    {
        yield return file.Global;
        foreach (var layer in file.Users.Values) yield return layer;
        foreach (var layer in file.Owners.Values) yield return layer;
    }

    private static int RemapLayer(SpecialtySettingsLayer layer, IReadOnlyDictionary<string, string> map)
    {
        var changed = 0;
        foreach (var preset in layer.Presets)
            for (var i = 0; i < preset.Steps.Count; i++)
                if (map.TryGetValue(preset.Steps[i].Trim(), out var step))
                {
                    preset.Steps[i] = step;
                    changed++;
                }

        foreach (var record in layer.Specialties.Values.Append(layer.DefaultSpecialty))
        {
            if (record is null) continue;
            if (Remapped(record.TierStrong, map) is { } strong) { record.TierStrong = strong; changed++; }
            if (Remapped(record.TierMedium, map) is { } medium) { record.TierMedium = medium; changed++; }
            if (Remapped(record.TierWeak, map) is { } weak) { record.TierWeak = weak; changed++; }
        }
        return changed;
    }

    // Новое значение ячейки либо null — менять нечего (пусто, не модель, не из карты)
    private static string? Remapped(string? cell, IReadOnlyDictionary<string, string> map) =>
        cell is not null && map.TryGetValue(cell.Trim(), out var next) ? next : null;

    // Заменить глобальный слой. null-ошибка = слой валиден.
    public string? SetGlobal(SpecialtySettingsLayer layer)
    {
        var error = ValidateLayer(layer);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            next.Global = NormalizeLayer(layer);
            next.Version = FormatVersion;
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
            next.Version = FormatVersion;
            Persist(next);
        }
        _log?.LogInformation("Личные настройки специальностей обновлены (owner={Owner})", ownerId);
        return null;
    }

    // Заменить слой «пользователь» (B9): настройки, назначенные админом конкретному
    // пользователю. Пустой слой снимает назначение (остаются личный и глобальный).
    // Приоритет слоёв зафиксирован: личный сильнее пользовательского, пользовательский
    // сильнее глобального.
    public string? SetUser(string userId, SpecialtySettingsLayer layer)
    {
        if (string.IsNullOrEmpty(userId)) return "Не указан пользователь";
        var error = ValidateLayer(layer);
        if (error is not null) return error;
        lock (_writeLock)
        {
            var next = Clone(_file);
            if (layer.IsEmpty) next.Users.Remove(userId);
            else next.Users[userId] = NormalizeLayer(layer);
            next.Version = FormatVersion;
            Persist(next);
        }
        _log?.LogInformation("Настройки специальностей пользователя обновлены (user={User})", userId);
        return null;
    }

    // --- Сброс уровней к наследованию ---

    // Сброс настроек моделей в слое: возврат наследования, а не запись значений.
    // ownerId = null — глобальный слой, иначе личный слой владельца; key — ключ одной
    // специальности («any» — «Любая специальность»), null — весь слой; apply = false —
    // предпросмотр (файл не трогаем, счёт тот же).
    //
    // Предикат «запись ничего своего не несёт»: права эквивалентны нижнему слою →
    // запись УДАЛЯЕТСЯ (это и есть возврат наследования). Иначе снимаются три уровня
    // и DefaultTier, а права сохраняются — такая запись продолжает затенять нижний слой
    // и попадает в Shadowed.
    public SpecialtyResetResult ResetModelSettings(string? ownerId, string? key, bool apply)
    {
        lock (_writeLock)
        {
            // Мутируем клон, не Snapshot (он отдаёт живой объект читателям)
            var next = Clone(_file);
            var layer = ownerId is null ? next.Global : next.Owners.GetValueOrDefault(ownerId);
            if (layer is null) return new SpecialtyResetResult(0, []);

            var all = key is null;
            var anyOnly = !all && IsAnyKey(key!);
            var changed = 0;

            if (all || !anyOnly)
            {
                foreach (var specKey in layer.Specialties.Keys.ToList())
                {
                    if (!all && !string.Equals(specKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var record = layer.Specialties[specKey];
                    if (RightsEquivalent(record, LowerRights(next, ownerId, specKey)))
                    {
                        layer.Specialties.Remove(specKey);
                        changed++;
                    }
                    else if (StripTiers(record)) changed++;
                }
            }

            if ((all || anyOnly) && layer.DefaultSpecialty is { } ds)
            {
                if (RightsEquivalent(ds, LowerRights(next, ownerId, SpecialtyCatalog.AnySpecialtyKey)))
                {
                    layer.DefaultSpecialty = null;
                    changed++;
                }
                else if (StripTiers(ds)) changed++;
            }

            // Shadowed — СОСТОЯНИЕ слоя после операции: записи, оставшиеся ради собственных
            // прав (уровней не несут, но затеняют нижний слой), а не дельта этого вызова
            var shadowed = new List<string>();
            if (layer.DefaultSpecialty is { } after && !CarriesTier(after))
                shadowed.Add(SpecialtyCatalog.AnySpecialtyKey);
            shadowed.AddRange(layer.Specialties.Where(kv => !CarriesTier(kv.Value))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));

            if (!apply || changed == 0) return new SpecialtyResetResult(changed, shadowed);

            // Пустой личный слой убираем из файла — как это делает SetOwner
            if (ownerId is not null && layer.IsEmpty) next.Owners.Remove(ownerId);
            next.Version = FormatVersion;
            Persist(next);
            _log?.LogInformation(
                "Сброс настроек моделей специальностей: слой={Layer}, ключ={Specialty}, изменено={Changed}",
                ownerId ?? "global", key ?? "*", changed);
            return new SpecialtyResetResult(changed, shadowed);
        }
    }

    public static bool IsAnyKey(string key) =>
        string.Equals(key, SpecialtyCatalog.AnySpecialtyKey, StringComparison.OrdinalIgnoreCase);

    // Запись адресует модель: непустая ячейка уровня или заданный DefaultTier
    private static bool CarriesTier(SpecialtyTemplateSettings s) =>
        !string.IsNullOrWhiteSpace(s.TierStrong) || !string.IsNullOrWhiteSpace(s.TierMedium)
        || !string.IsNullOrWhiteSpace(s.TierWeak) || s.DefaultTier is not null;

    // Снять уровни и DefaultTier (у DefaultTier интерфейса нет — оставленный, он остался бы
    // невидимым остатком, который продолжает гонять персон прежним уровнем)
    private static bool StripTiers(SpecialtyTemplateSettings s)
    {
        if (!CarriesTier(s)) return false;
        s.TierStrong = null;
        s.TierMedium = null;
        s.TierWeak = null;
        s.DefaultTier = null;
        return true;
    }

    // Права нижнего слоя для записи: для owner — назначение пользователя (B9), затем
    // глобальный; для global — каталожный дефолт. Нижней записи нет и каталожного дефолта
    // нет → «полный доступ без ограничений» (Access=Full, Tools=null, DisallowedTools=null)
    // — именно к нему возвращается наследование.
    private static (PersonaAccess Access, List<string>? Tools, List<string>? Disallowed) LowerRights(
        SpecialtySettingsFile file, string? ownerId, string specKey)
    {
        if (ownerId is not null)
        {
            var lower = IsAnyKey(specKey)
                ? file.Users.GetValueOrDefault(ownerId)?.DefaultSpecialty
                    ?? file.Global.DefaultSpecialty
                : file.Users.GetValueOrDefault(ownerId)?.Specialties.GetValueOrDefault(specKey)
                    ?? file.Global.Specialties.GetValueOrDefault(specKey);
            if (lower is not null) return (lower.Access, lower.Tools, lower.DisallowedTools);
        }
        if (!IsAnyKey(specKey) && SpecialtyCatalog.TryGetByKey(specKey, out var entry)
            && entry.DefaultTemplate is { } tmpl)
            return (tmpl.Access, tmpl.Tools?.ToList(), tmpl.DisallowedTools?.ToList());
        return (PersonaAccess.Full, null, null);
    }

    // Права эквивалентны нижнему слою — по НОРМАЛИЗОВАННЫМ значениям: NormalizeTools схлопывает
    // полный набор в null, а записи из миграции v1 легли без нормализации. Tools/DisallowedTools
    // сравниваются как множества (порядок в JSON не значим), запреты значимы только при Custom.
    private static bool RightsEquivalent(SpecialtyTemplateSettings s,
        (PersonaAccess Access, List<string>? Tools, List<string>? Disallowed) lower)
    {
        if (s.Access != lower.Access) return false;
        if (!SameSet(NormalizeTools(s.Tools), NormalizeTools(lower.Tools))) return false;
        if (s.Access != PersonaAccess.Custom) return true;
        return SameSet(CleanList(s.DisallowedTools), CleanList(lower.Disallowed));
    }

    private static bool SameSet(List<string>? a, List<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(b.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    // --- Валидация и нормализация ---

    // null — слой валиден; иначе текст ошибки (контроллер отдаёт его как 400).
    public static string? ValidateLayer(SpecialtySettingsLayer layer)
    {
        foreach (var key in layer.Specialties.Keys)
            if (!SpecialtyCatalog.TryGetByKey(key, out _))
                return $"Неизвестная специальность: {key}";

        foreach (var (key, settings) in layer.Specialties)
            if (ValidateTemplateSettings(settings, key) is { } e) return e;
        if (layer.DefaultSpecialty is { } ds && ValidateTemplateSettings(ds, "any") is { } de) return de;

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in layer.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
                return "У пресета пустое имя";
            if (!string.IsNullOrWhiteSpace(preset.Id) && !seenIds.Add(preset.Id))
                return $"Дублируется id пресета: {preset.Id}";
            if (preset.Steps.Count is < 1 or > FallbackSettingsStore.HardMaxSubstitutions)
                return $"Пресет «{preset.Name}»: цепочка должна быть длиной 1..{FallbackSettingsStore.HardMaxSubstitutions} шагов";
            foreach (var step in preset.Steps)
            {
                if (string.IsNullOrWhiteSpace(step))
                    return $"Пресет «{preset.Name}»: пустой шаг цепочки";
                // Шаг не может быть ссылкой на пресет — вложенность запрещена (ADR-007 §1)
                if (LocalActionOverridesStore.IsPresetRoute(step))
                    return $"Пресет «{preset.Name}»: шаг не может быть ссылкой на другой пресет — выпишите шаги подряд";
            }
        }
        return null;
    }

    // Ячейки матрицы — id модели или "preset:{id}"; "tier:*" запрещён (ячейка уже адресована
    // уровнем). name — для текста ошибки (ключ специальности или "any").
    private static string? ValidateTemplateSettings(SpecialtyTemplateSettings settings, string name)
    {
        foreach (var cell in new[] { settings.TierStrong, settings.TierMedium, settings.TierWeak })
        {
            if (string.IsNullOrWhiteSpace(cell)) continue;
            if (LocalActionOverridesStore.ParseTierRoute(cell) is not null)
                return $"Специальность «{name}»: в ячейке уровня не может быть tier:* — уровень уже выбран строкой";
        }
        return null;
    }

    // Канонический вид слоя: ключи специальностей — camelCase каталога, Tools нормализованы
    // (полный набор → null = «все»), у не-Custom запреты пусты, ячейки матриц триммированы,
    // пустые id пресетов досозданы, шаги очищены от пустых.
    private static SpecialtySettingsLayer NormalizeLayer(SpecialtySettingsLayer layer)
    {
        var specialties = new Dictionary<string, SpecialtyTemplateSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, settings) in layer.Specialties)
        {
            if (!SpecialtyCatalog.TryGetByKey(key, out var entry)) continue;
            specialties[entry.Key] = NormalizeTemplate(settings);
        }

        return new SpecialtySettingsLayer
        {
            Specialties = specialties,
            DefaultSpecialty = layer.DefaultSpecialty is { } ds ? NormalizeTemplate(ds) : null,
            Presets = layer.Presets.Select(p => new ModelRoutePreset
            {
                Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString() : p.Id.Trim(),
                Name = p.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim(),
                Steps = p.Steps.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
            }).ToList(),
        };
    }

    private static SpecialtyTemplateSettings NormalizeTemplate(SpecialtyTemplateSettings s) => new()
    {
        Access = s.Access,
        Tools = NormalizeTools(s.Tools),
        DisallowedTools = s.Access == PersonaAccess.Custom ? CleanList(s.DisallowedTools) : null,
        TierStrong = CleanCell(s.TierStrong),
        TierMedium = CleanCell(s.TierMedium),
        TierWeak = CleanCell(s.TierWeak),
        DefaultTier = s.DefaultTier,
    };

    private static string? CleanCell(string? cell) =>
        string.IsNullOrWhiteSpace(cell) ? null : cell.Trim();

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
        if (!File.Exists(_storePath)) return;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(_storePath)); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Не удалось прочитать {Path}, продолжаю с дефолтами", _storePath);
            return;
        }
        if (root is null) return;

        // Версия — нечувствительно к регистру (Persist пишет PascalCase «Version»,
        // мигрированные/внешние файлы могут нести «version»). JsonNode индексатор
        // case-sensitive, поэтому ищем ключ вручную.
        var version = ReadVersion(root);
        if (version > FormatVersion)
        {
            // Файл снят более новым кодом (например, восстановлен из свежего бэкапа):
            // незнакомую структуру не применяем — дефолты безопаснее молчаливой каши
            _log?.LogWarning(
                "specialty-settings.json имеет формат {FileVersion} новее поддерживаемого {Version} — стартую с дефолтами",
                version, FormatVersion);
            return;
        }

        SpecialtySettingsFile file;
        try
        {
            file = version < FormatVersion
                ? MigrateFromV1(root)
                : root.Deserialize<SpecialtySettingsFile>(JsonOpts)!;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "specialty-settings.json: не удалось разобрать (version={Version}), стартую с дефолтами", version);
            return;
        }

        file.Global ??= new SpecialtySettingsLayer();
        file.Owners ??= new Dictionary<string, SpecialtySettingsLayer>(StringComparer.Ordinal);
        file.Users ??= new Dictionary<string, SpecialtySettingsLayer>(StringComparer.Ordinal);
        file.Version = FormatVersion;
        _file = file;
    }

    // Версия формата из корня файла (case-insensitive). 1 — если поле нет (старый файл).
    private static int ReadVersion(JsonNode root)
    {
        if (root is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase)
                    && value is not null)
                {
                    try { return value.GetValue<int>(); } catch { }
                }
            }
        }
        return 1;
    }

    // Миграция v1 → v2 (ADR-007 §6). Цель: ни одна заданная человеком привязка
    // «специальность → маршрут» не пропадает. v1-пресет был сборником правил «специальность
    // (или any) → ОДИН маршрут», применяемых автоматически ко всем ходам; v2-пресет — цепочка,
    // сущность другого вида. Поэтому правила разносятся по матрицам специальностей, а сами
    // v1-пресеты (имена и контейнеры правил) удаляются — осознанная потеря.
    //
    // Семантика разноски (та же, что у v1 ResolveRoute — первое совпадение выигрывает):
    //   маршрут "tier:T"  → Specialty[X].DefaultTier = T (матрица пуста → разворот уровня
    //                       провалится в слоты владельца, байт-в-байт как v1 «работай T-моделью владельца»);
    //   маршрут = модель M → все три ячейки Specialty[X] = M (в v1 маршрут-модель означал
    //                       «эта специальность всегда ходит M» — заполнение строки сохраняет это);
    //   правило {specialty:"any"} → то же в DefaultSpecialty слоя.
    // X первой подходящей специальности выставляется один раз (последующие правила для X в v1
    // были мертвы — ResolveRoute брал первое совпадение).
    private SpecialtySettingsFile MigrateFromV1(JsonNode root)
    {
        var file = new SpecialtySettingsFile { Version = FormatVersion };
        file.Global = MigrateLayer(root["global"]);
        var owners = root["owners"]?.AsObject();
        if (owners is not null)
        {
            foreach (var (ownerId, ownerNode) in owners)
                if (ownerNode is not null)
                    file.Owners[ownerId] = MigrateLayer(ownerNode);
        }
        _log?.LogInformation("specialty-settings.json: миграция v1→v2 — правила пресетов перенесены в матрицы специальностей");
        return file;
    }

    private static SpecialtySettingsLayer MigrateLayer(JsonNode? node)
    {
        var layer = new SpecialtySettingsLayer();
        if (node is null) return layer;

        // Шаблоны специальностей (Access/Tools) переносятся как есть — матрицы у них пустые
        var specs = node["specialties"]?.AsObject();
        if (specs is not null)
        {
            foreach (var (key, specNode) in specs)
            {
                if (specNode?.Deserialize<SpecialtyTemplateSettings>(JsonOpts) is { } tmpl
                    && SpecialtyCatalog.TryGetByKey(key, out var entry))
                    layer.Specialties[entry.Key] = tmpl;
            }
        }

        // Правила v1-пресетов разносятся по матрицам. Обход: пресеты по порядку списка,
        // правила по порядку; первое совпадение специальности (или any) выигрывает.
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyAssigned = false;
        var presets = node["presets"]?.AsArray();
        if (presets is not null)
        {
            foreach (var presetNode in presets)
            {
                var rules = presetNode?["rules"]?.AsArray();
                if (rules is null) continue;
                foreach (var ruleNode in rules)
                {
                    var specialty = (string?)ruleNode?["specialty"] ?? SpecialtyCatalog.AnySpecialtyKey;
                    var route = (string?)ruleNode?["route"];
                    if (string.IsNullOrWhiteSpace(route)) continue;
                    var routeTrim = route!.Trim();
                    var isAny = string.Equals(specialty, SpecialtyCatalog.AnySpecialtyKey, StringComparison.OrdinalIgnoreCase);

                    if (isAny)
                    {
                        if (anyAssigned) continue;
                        layer.DefaultSpecialty = ApplyRoute(layer.DefaultSpecialty, routeTrim);
                        anyAssigned = true;
                    }
                    else
                    {
                        if (!SpecialtyCatalog.TryGetByKey(specialty!, out var entry)) continue;
                        if (!assigned.Add(entry.Key)) continue;
                        layer.Specialties[entry.Key] = ApplyRoute(layer.Specialties.GetValueOrDefault(entry.Key), routeTrim);
                    }
                }
            }
        }
        return layer;
    }

    // Применить v1-маршрут к (возможно существующей) записи специальности: tier:* → DefaultTier,
    // модель → все три ячейки. Существующие шаблонные поля (Access/Tools) сохраняем.
    private static SpecialtyTemplateSettings ApplyRoute(SpecialtyTemplateSettings? existing, string route)
    {
        var s = existing ?? new SpecialtyTemplateSettings();
        if (LocalActionOverridesStore.ParseTierRoute(route) is { } tier)
        {
            s.DefaultTier ??= tier; // «если пуст»
        }
        else
        {
            // Конкретная модель — все три ячейки (в v1 маршрут-модель = «всегда этой моделью»)
            s.TierStrong ??= route;
            s.TierMedium ??= route;
            s.TierWeak ??= route;
        }
        return s;
    }
}
