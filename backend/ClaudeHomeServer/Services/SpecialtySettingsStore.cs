using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Секция промпта специальности в слое настроек: id из каталога (SpecialtyPromptPresets),
// enabled — явное вкл/выкл (параметр «задан» самим наличием элемента с id в слое),
// text — переопределение типового текста (null/пусто — типовой текст из кода).
public class SpecialtyPromptSectionSettings
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Text { get; set; }
}

// Типовое умение роли: привязка-заготовка, материализуемая в Persona.Bindings при
// создании персоны (модель «копия при создании», не динамическое наследование).
// Цель НЕ хранится: конкретную цель подбирает AI по каталогу владельца; исключение —
// «Навык» (Skill): там явное имя скилла (SkillName), отсутствующие скиллы при
// материализации пропускаются молча.
public class SpecialtyDefaultBinding
{
    public PersonaBindingType Type { get; set; }
    public PersonaBindingMode Mode { get; set; } = PersonaBindingMode.Auto;
    // Условие «когда применять» — попадает в индекс системного промпта (как Condition привязки)
    public string Condition { get; set; } = "";
    // Имя скилла из каталога владельца — только при Type == Skill
    public string? SkillName { get; set; }
}

// Настройка шаблона специальности в глобальном слое. Слоёв больше нет (v5): значение
// либо задано глобально, либо берётся дефолт кода —
//  - права и модели (Access/Tools/DisallowedTools/матрицы/DefaultTier) — запись, если она
//    есть для специальности, заменяет шаблон ЦЕЛИКОМ (полевого слияния нет): нет записи —
//    дефолт кода (SpecialtyCatalog);
//  - PromptSections и DefaultBindings — наследуются ПОПАРАМЕТРНО от дефолтов кода
//    (см. EffectivePromptSections/EffectiveDefaultBindings): заданный параметр перекрывает
//    дефолт, а незаданный его не затирает.
public class SpecialtyTemplateSettings
{
    public PersonaAccess Access { get; set; } = PersonaAccess.Full;
    // null — все возможности (tasks+notes+web); список — только перечисленные
    public List<string>? Tools { get; set; }
    // Имеет смысл только при Access == Custom
    public List<string>? DisallowedTools { get; set; }
    // Секции промпта специальности (null/пусто — слой секций не задаёт, наследование вниз)
    public List<SpecialtyPromptSectionSettings>? PromptSections { get; set; }
    // Типовой профиль умений роли (null/пусто — слой профиль не задаёт, наследование вниз)
    public List<SpecialtyDefaultBinding>? DefaultBindings { get; set; }

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

// Итог сброса настроек моделей в слое: Changed — число ФАКТИЧЕСКИ изменённых записей
// (у предпросмотра — сколько изменится), Shadowed — состояние слоя ПОСЛЕ операции: ключи
// записей, оставшихся ради собственных прав (уровней не несут, нижний слой затеняют).
public sealed record SpecialtyResetResult(int Changed, IReadOnlyList<string> Shadowed);

// Файл стора на диске (data/specialty-settings.json). Owners и Users — легаси-словари
// снятых слоёв: с v5 они всегда пусты (миграция вливает админский личный слой в Global
// и чистит их), поля сохранены ради чтения старых файлов и точечных операций PresetStore.
public class SpecialtySettingsFile
{
    public int Version { get; set; } = SpecialtySettingsStore.FormatVersion;
    public SpecialtySettingsLayer Global { get; set; } = new();
    public Dictionary<string, SpecialtySettingsLayer> Owners { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SpecialtySettingsLayer> Users { get; set; } = new(StringComparer.Ordinal);
}

// Стор настроек специальностей и именованных пресетов-цепочек выбора модели.
// Слой ОДИН — глобальный (инстанс): специальности общие для всех пользователей, правит их
// админ. Резолв везде плоский: запись глобального слоя либо дефолт кода. Личный и
// пользовательский слои сняты в v5 — их содержимое влито в глобальный миграцией.
//
// Публичные методы резолва сохраняют параметр ownerId: он остался у вызывающих (персоны,
// маршрутизация моделей, промпт хода) и означает теперь только «от чьего имени спрашивают» —
// на результат не влияет. Изоляции per-owner в настройках специальностей больше нет
// осознанно: это общие значения инстанса, а не личные данные.
//
// Файл живёт в data/ → попадает в бэкап автоматически (BackupPaths.ShouldInclude работает
// от обратного). Формат версионирован: file.Version новее кода — содержимое игнорируется
// с warning; старше кода — мигрируется при загрузке (v1→v2 ADR-007 §6, v4→v5 — снятие слоёв).
//
// Снимок файла держим неизменяемым объектом и заменяем целиком под write-локом — читатели
// не видят полумутированного состояния (образец: LocalActionOverridesStore).
public sealed class SpecialtySettingsStore
{
    // v2 — пресет из «сборника правил специальность→маршрут» стал именованной цепочкой Steps;
    // у специальности появились матрица моделей по уровням + DefaultTier; у слоя — DefaultSpecialty.
    // v3 — аддитивно: секции промптов (PromptSections) и типовой профиль умений
    // (DefaultBindings) в записи специальности; файлы v2 читаются как есть, разноски не требуют.
    // v4 — персонализация отображения ролей (словарь Display в слое) убрана из кода, но
    // версия НЕ понижена: файлы, записанные с Version=4 и полем Display, продолжают
    // читаться — Display при десериализации молча игнорируется. Понижение до 3 объявило
    // бы такие файлы «новее кода» и обнулило настройки на дефолты.
    // v5 — слои сняты: специальности стали общими для инстанса (ADR-012). Разовая миграция
    // при чтении вливает личные слои админов в глобальный (при конфликте выигрывает значение
    // админа — он настраивал «у себя» то, что и было фактическим) и чистит Owners/Users.
    // Личные слои рядовых пользователей теряются осознанно: раздельных настроек больше нет,
    // а тихо назначать чужие значения всему инстансу нельзя.
    public const int FormatVersion = 5;

    // Порог бенчмарка резолва EffectivePromptSectionStates (план «Секции промптов» этап 3,
    // риск «цена резолва per-turn»): вызывается на КАЖДЫЙ ход персоны специальности, резолвер —
    // чистые словари/списки в памяти, без I/O. Бенчмарк (100 вызовов) держит среднее время
    // вызова ниже порога; превышение — сигнал завести кэш per-specialty с инвалидацией при
    // записи слоя (SetGlobal/SetOwner/SetUser), а не немедленно строить кэш заранее.
    public const double PromptSectionsResolveBenchmarkThresholdMs = 5.0;

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
    // Нужен ровно одному месту — миграции v4→v5: роли живут в UserStore, а вливать в
    // глобальный слой надо личные слои админов (ADR-012). Зависимость обязательная:
    // «мигрировать вслепую, если стор не передали» — молчаливая потеря настроек.
    private readonly UserStore _users;
    private readonly object _writeLock = new();
    private volatile SpecialtySettingsFile _file = new();

    public SpecialtySettingsStore(IConfiguration config, UserStore users,
        ILogger<SpecialtySettingsStore>? log = null)
    {
        _log = log;
        _users = users;
        // Путь выводим ТОЛЬКО от DataPath (как LocalActionOverridesStore): иначе стор
        // лёг бы рядом с исполняемым файлом и терялся при деплое
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "specialty-settings.json");
        Load();
    }

    // --- Чтение ---

    public SpecialtySettingsFile Snapshot => _file;

    // Настройка шаблона специальности из глобального слоя. null — настройки нет
    // (шаблон берётся из дефолтов кода SpecialtyCatalog).
    public SpecialtyTemplateSettings? TemplateSettings(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        return _file.Global.Specialties.TryGetValue(key, out var settings) ? settings : null;
    }

    // Эффективный шаблон специальности: глобальная настройка либо дефолт кода;
    // null — шаблона нет вовсе (специальность без шаблона).
    public SpecialtyTemplate? EffectiveTemplate(string ownerId, PersonaSpecialty specialty)
    {
        if (TemplateSettings(ownerId, specialty) is { } settings)
            return new SpecialtyTemplate(settings.Access, settings.Tools, settings.DisallowedTools);
        return SpecialtyCatalog.Get(specialty).DefaultTemplate;
    }

    // Пресеты инстанса: набор один — глобальный (личные и назначенные слои сняты в v5).
    public IReadOnlyList<ModelRoutePreset> EffectivePresets(string ownerId) => _file.Global.Presets;

    // Тот же набор с признаком слоя — признак остался у API и UI, значение теперь всегда
    // Global (других слоёв нет).
    public IReadOnlyList<(ModelRoutePreset Preset, PresetScope Scope)> EffectivePresetsWithScope(string ownerId) =>
        _file.Global.Presets.Select(p => (p, PresetScope.Global)).ToList();

    // Найти пресет по id среди пресетов инстанса. null — не найден
    // (битая ссылка preset:{id}). Используется ExpandChain и (в будущем) UI «где используется».
    public ModelRoutePreset? FindPreset(string ownerId, string presetId)
    {
        foreach (var p in EffectivePresets(ownerId))
            if (string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    // Упорядоченный список матриц специальности для разворачивания уровня (ADR-007 §2):
    // запись специальности, затем DefaultSpecialty («любая специальность») — обе из
    // глобального слоя. Только записи, которые ЕСТЬ в слое; пустые ячейки внутри записи
    // рассматриваются разворачивателем (UserModelTierResolver) — здесь отдаём матрицу как
    // есть, пустые ячейки в ней означают «спроси следующую».
    public IReadOnlyList<TierMatrix> SpecialtyMatrices(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var global = _file.Global;
        var result = new List<TierMatrix>(2);
        if (global.Specialties.GetValueOrDefault(key) is { } spec)
            result.Add(ToMatrix(spec));
        if (global.DefaultSpecialty is { } ds)
            result.Add(ToMatrix(ds));
        return result;
    }

    private static TierMatrix ToMatrix(SpecialtyTemplateSettings s) =>
        new(s.TierStrong, s.TierMedium, s.TierWeak);

    // Источник УРОВНЯ специальности (ADR-007 §2): каким уровнем работают персоны этой
    // специальности, если у задачи/персоны нет своего. Запись специальности, затем
    // DefaultSpecialty глобального слоя. null — уровень не задан специальностью.
    public ModelTier? SpecialtyDefaultTier(string ownerId, PersonaSpecialty specialty)
    {
        var key = SpecialtyCatalog.KeyOf(specialty);
        var global = _file.Global;
        if (global.Specialties.GetValueOrDefault(key)?.DefaultTier is { } tier) return tier;
        return global.DefaultSpecialty?.DefaultTier;
    }

    // --- Секции промпта и типовые умения (посекочное наследование) ---

    // Источник значения параметра секции (для UI-бейджа и тестов). Значения User и Owner
    // после снятия слоёв (v5) недостижимы, но остаются в enum: он живёт в wire-контракте
    // фронта и в чужих файлах — сужение отдельной задачей после мержа (ADR-012, «Хвосты»).
    public enum SectionSource { Code, Global, User, Owner }

    // Эффективное состояние секции промпта: enabled и text наследуются КАЖДЫЙ СВОИМ
    // параметром (см. EffectivePromptSectionStates).
    public sealed record EffectivePromptSection(
        string Id, bool Enabled, string Text,
        SectionSource EnabledSource, SectionSource TextSource);

    // ВАЖНО: здесь вторая семантика наследования стора. Права и модели (выше) — «запись
    // заменяет дефолт целиком»; секции промпта — ПОПАРАМЕТРНО: enabled и text каждой секции
    // берутся из настройки инстанса, ЕСЛИ там заданы, иначе из дефолта кода
    // (SpecialtyPromptPresets); настройка одной секции не трогает соседние. «Замена целиком»
    // здесь была бы ошибкой: настройка одной секции сносила бы дефолтные тексты остальных.
    public IReadOnlyList<EffectivePromptSection> EffectivePromptSectionStates(
        string ownerId, PersonaSpecialty specialty)
    {
        if (specialty == PersonaSpecialty.None) return [];
        var key = SpecialtyCatalog.KeyOf(specialty);
        var record = _file.Global.Specialties.GetValueOrDefault(key);

        var result = new List<EffectivePromptSection>(SpecialtyPromptPresets.Sections.Count);
        foreach (var meta in SpecialtyPromptPresets.Sections)
        {
            var enabled = SpecialtyPromptPresets.DefaultEnabled(meta.Id, specialty);
            var text = SpecialtyPromptPresets.DefaultText(meta.Id, specialty);
            var enabledSource = SectionSource.Code;
            var textSource = SectionSource.Code;

            var entry = record?.PromptSections?
                .FirstOrDefault(p => string.Equals(p.Id, meta.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                enabled = entry.Enabled;
                enabledSource = SectionSource.Global;
            }
            if (!string.IsNullOrWhiteSpace(entry?.Text))
            {
                text = entry.Text!.Trim();
                textSource = SectionSource.Global;
            }
            result.Add(new EffectivePromptSection(meta.Id, enabled, text, enabledSource, textSource));
        }
        return result;
    }

    // Эффективные секции промпта специальности (контракт вклейки в системный промпт):
    // только включённые, в порядке каталога, с уже развёрнутым текстом.
    public IReadOnlyList<EffectivePromptSection> EffectivePromptSections(
        string ownerId, PersonaSpecialty specialty) =>
        EffectivePromptSectionStates(ownerId, specialty).Where(s => s.Enabled).ToList();

    // Типовой профиль умений роли: НЕПУСТОЙ DefaultBindings настройки инстанса, иначе
    // дефолт кода (SpecialtyPromptPresets). Наследуется полем записи (точечно, как секции):
    // переопределение уровней моделей не должно сносить типовые умения, и наоборот.
    public IReadOnlyList<SpecialtyDefaultBinding> EffectiveDefaultBindings(
        string ownerId, PersonaSpecialty specialty)
    {
        if (specialty == PersonaSpecialty.None) return [];
        var key = SpecialtyCatalog.KeyOf(specialty);
        return _file.Global.Specialties.GetValueOrDefault(key)?.DefaultBindings
            ?? SpecialtyPromptPresets.DefaultBindingsProfile(specialty);
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

    // Заменить per-owner слой. ЛЕГАСИ: в резолве личный слой не участвует с v5 (миграция
    // вливает админский слой в глобальный и чистит словарь) — метод остался ради точечных
    // операций PresetStore, которые адресуют слой по признаку найденного пресета.
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

    // Заменить слой «пользователь» (B9). ЛЕГАСИ, как и SetOwner: в резолве слой не
    // участвует с v5, метод остался ради точечных операций PresetStore.
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

    // Сброс настроек моделей инстанса: возврат к дефолтам кода, а не запись значений.
    // key — ключ одной специальности («any» — «Любая специальность»), null — весь слой;
    // apply = false — предпросмотр (файл не трогаем, счёт тот же).
    //
    // Предикат «запись ничего своего не несёт»: права эквивалентны дефолту кода →
    // запись УДАЛЯЕТСЯ (это и есть возврат к дефолту). Иначе снимаются три уровня
    // и DefaultTier, а права сохраняются — такая запись продолжает затенять дефолт
    // и попадает в Shadowed.
    public SpecialtyResetResult ResetModelSettings(string? key, bool apply)
    {
        lock (_writeLock)
        {
            // Мутируем клон, не Snapshot (он отдаёт живой объект читателям)
            var next = Clone(_file);
            var layer = next.Global;

            var all = key is null;
            var anyOnly = !all && IsAnyKey(key!);
            var changed = 0;

            if (all || !anyOnly)
            {
                foreach (var specKey in layer.Specialties.Keys.ToList())
                {
                    if (!all && !string.Equals(specKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var record = layer.Specialties[specKey];
                    // Удаляем запись только когда и прав своих нет, и секций/профиля умений:
                    // сброс настроек МОДЕЛЕЙ не должен сносить секции промптов
                    if (RightsEquivalent(record, DefaultRights(specKey))
                        && !CarriesPromptSettings(record))
                    {
                        layer.Specialties.Remove(specKey);
                        changed++;
                    }
                    else if (StripTiers(record)) changed++;
                }
            }

            if ((all || anyOnly) && layer.DefaultSpecialty is { } ds)
            {
                if (RightsEquivalent(ds, DefaultRights(SpecialtyCatalog.AnySpecialtyKey))
                    && !CarriesPromptSettings(ds))
                {
                    layer.DefaultSpecialty = null;
                    changed++;
                }
                else if (StripTiers(ds)) changed++;
            }

            // Shadowed — СОСТОЯНИЕ слоя после операции: записи, оставшиеся ради собственных
            // прав (уровней не несут, но затеняют дефолт кода), а не дельта этого вызова
            var shadowed = new List<string>();
            if (layer.DefaultSpecialty is { } after && !CarriesTier(after))
                shadowed.Add(SpecialtyCatalog.AnySpecialtyKey);
            shadowed.AddRange(layer.Specialties.Where(kv => !CarriesTier(kv.Value))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));

            if (!apply || changed == 0) return new SpecialtyResetResult(changed, shadowed);

            next.Version = FormatVersion;
            Persist(next);
            _log?.LogInformation(
                "Сброс настроек моделей специальностей: ключ={Specialty}, изменено={Changed}",
                key ?? "*", changed);
            return new SpecialtyResetResult(changed, shadowed);
        }
    }

    public static bool IsAnyKey(string key) =>
        string.Equals(key, SpecialtyCatalog.AnySpecialtyKey, StringComparison.OrdinalIgnoreCase);

    // Запись адресует модель: непустая ячейка уровня или заданный DefaultTier
    private static bool CarriesTier(SpecialtyTemplateSettings s) =>
        !string.IsNullOrWhiteSpace(s.TierStrong) || !string.IsNullOrWhiteSpace(s.TierMedium)
        || !string.IsNullOrWhiteSpace(s.TierWeak) || s.DefaultTier is not null;

    // Запись несёт секции промптов или профиль типовых умений (своё, что не «наследование
    // моделей»): удаление такой записи сбросом моделей уничтожило бы их настройки
    private static bool CarriesPromptSettings(SpecialtyTemplateSettings s) =>
        s.PromptSections is { Count: > 0 } || s.DefaultBindings is { Count: > 0 };

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

    // Права дефолта кода для записи: каталожный шаблон специальности. Каталожного дефолта
    // нет (и у «любой специальности» его нет вовсе) → «полный доступ без ограничений»
    // (Access=Full, Tools=null, DisallowedTools=null) — именно к нему возвращает сброс.
    private static (PersonaAccess Access, List<string>? Tools, List<string>? Disallowed) DefaultRights(
        string specKey)
    {
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

        // Секции промптов: id из каталога, без дублей, текст в лимите (общий с UI счётчиком)
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in settings.PromptSections ?? [])
        {
            if (!SpecialtyPromptPresets.TryGetSection(section.Id, out var meta))
                return $"Специальность «{name}»: неизвестная секция промпта: {section.Id}";
            if (!seenSections.Add(meta.Id))
                return $"Специальность «{name}»: секция промпта задаётся дважды: {meta.Id}";
            if (section.Text?.Length > SpecialtyPromptPresets.SectionTextLimit)
                return $"Специальность «{name}»: текст секции «{meta.Label}» длиннее {SpecialtyPromptPresets.SectionTextLimit} символов";
        }

        // Типовой профиль умений: валидные тип/режим, имя скилла — только у «Навыка»
        if (settings.DefaultBindings is { Count: > SpecialtyPromptPresets.MaxDefaultBindings })
            return $"Специальность «{name}»: типовых умений не больше {SpecialtyPromptPresets.MaxDefaultBindings}";
        foreach (var binding in settings.DefaultBindings ?? [])
        {
            if (!Enum.IsDefined(binding.Type))
                return $"Специальность «{name}»: неизвестный тип типового умения";
            if (!Enum.IsDefined(binding.Mode))
                return $"Специальность «{name}»: неизвестный режим типового умения";
            if (binding.Type == PersonaBindingType.Skill && string.IsNullOrWhiteSpace(binding.SkillName))
                return $"Специальность «{name}»: у типового умения типа «Навык» не указано имя скилла";
            if (binding.Type != PersonaBindingType.Skill && !string.IsNullOrWhiteSpace(binding.SkillName))
                return $"Специальность «{name}»: имя скилла имеет смысл только у типового умения типа «Навык»";
            if (binding.Condition.Length > SpecialtyPromptPresets.MaxConditionLength)
                return $"Специальность «{name}»: условие типового умения длиннее {SpecialtyPromptPresets.MaxConditionLength} символов";
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
        PromptSections = NormalizeSections(s.PromptSections),
        DefaultBindings = NormalizeDefaultBindings(s.DefaultBindings),
    };

    // Канонический вид секций: id из каталога, text триммирован (пустой → null = типовой
    // текст кода). Неизвестные id молча выпадают (валидация API их отсекает до записи).
    private static List<SpecialtyPromptSectionSettings>? NormalizeSections(
        List<SpecialtyPromptSectionSettings>? sections)
    {
        if (sections is not { Count: > 0 }) return null;
        var result = new List<SpecialtyPromptSectionSettings>();
        foreach (var section in sections)
        {
            if (!SpecialtyPromptPresets.TryGetSection(section.Id, out var meta)) continue;
            var text = section.Text?.Trim();
            result.Add(new SpecialtyPromptSectionSettings
            {
                Id = meta.Id,
                Enabled = section.Enabled,
                Text = string.IsNullOrEmpty(text) ? null : text,
            });
        }
        return result.Count > 0 ? result : null;
    }

    // Канонический вид типового профиля: тримм полей, SkillName — только у «Навыка»;
    // записи с невалидными тип/режим (мусор из внешнего файла) молча выпадают.
    private static List<SpecialtyDefaultBinding>? NormalizeDefaultBindings(
        List<SpecialtyDefaultBinding>? bindings)
    {
        if (bindings is not { Count: > 0 }) return null;
        var result = new List<SpecialtyDefaultBinding>();
        foreach (var b in bindings)
        {
            if (!Enum.IsDefined(b.Type) || !Enum.IsDefined(b.Mode)) continue;
            var skill = b.SkillName?.Trim();
            result.Add(new SpecialtyDefaultBinding
            {
                Type = b.Type,
                Mode = b.Mode,
                Condition = b.Condition?.Trim() ?? "",
                SkillName = b.Type == PersonaBindingType.Skill && !string.IsNullOrWhiteSpace(skill)
                    ? skill
                    : null,
            });
        }
        return result.Count > 0 ? result : null;
    }

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
            // v1 → разноска правил пресетов по матрицам; v2+ читается как есть: неизвестные
            // ключи (как Display из файлов версии 4) при десериализации молча игнорируются
            file = version == 1
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

        // v<5 → снятие слоёв (ADR-012): личные слои админов вливаются в глобальный, словари
        // слоёв чистятся. Файл переписывается сразу — иначе миграция повторялась бы каждый
        // старт (и каждый раз воскрешала уже удалённые администратором значения).
        var migrated = version < 5 && MigrateToV5(file, version);
        file.Version = FormatVersion;
        _file = file;
        if (migrated) Persist(file);
    }

    // Миграция v≤4 → v5 (ADR-012). Переносим НЕ слой, а то, что админ видел на экране:
    // инвариант — эффективный резолв по всем ролям у админа до и после совпадает. Отсюда
    // разная гранулярность влития (она повторяет прежние семантики наследования):
    //   права + ячейки уровней + DefaultTier + DefaultSpecialty — запись целиком;
    //   PromptSections — посекционно (по id, отдельно Enabled и Text): замена записи целиком
    //     снесла бы секции, заданные админом в глобальном слое;
    //   DefaultBindings — непустой owner-список заменяет глобальный, пустой не трогает;
    //   Presets — конкатенация (owner раньше глобальных) с дедупом по Id: пресеты слоёв
    //     жили рядом и не переопределяли друг друга (ADR-007 §1).
    //
    // Источник — слои ВСЕХ владельцев с ролью admin в порядке users.json; при конфликте
    // ключа выигрывает более ранний админ, глобальный слой — базис под ними. «Первый админ»
    // без слова «все» потерял бы боевую конфигурацию: на проде первый в списке слоя не имеет.
    // Слои рядовых пользователей (Users и не-админские Owners) отбрасываются: раздельных
    // настроек больше нет, а назначать чужие значения всему инстансу молча нельзя.
    // Возвращает true, если файл нужно переписать.
    private bool MigrateToV5(SpecialtySettingsFile file, int fromVersion)
    {
        var admins = _users.GetAll()
            .Where(u => u.Role == "admin" && file.Owners.ContainsKey(u.Id))
            .Select(u => u.Id)
            .ToList();

        // Ранний админ сильнее позднего: ключи, уже влитые предыдущим, не перетираем
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adminId in admins)
        {
            var merged = MergeIntoGlobal(file.Global, file.Owners[adminId], claimed);
            _log?.LogInformation(
                "specialty-settings.json: слой админа {Admin} влит в глобальный (специальностей: {Specialties}, пресетов: {Presets}, «любая специальность»: {Any})",
                adminId, merged.Specialties, merged.Presets, merged.DefaultSpecialty);
        }

        var dropped = file.Owners.Count - admins.Count + file.Users.Count;
        file.Owners.Clear();
        file.Users.Clear();
        // Страховка ДО первой записи v5: Owners/Users в файле не останутся, и разбирать
        // «куда делась настройка» будет нечем (ADR-012). Провал копии миграцию не срывает.
        BackupSourceFile(fromVersion);
        _log?.LogInformation(
            "specialty-settings.json: миграция v{From}→5 — слои сняты (влито админских слоёв: {Merged}, отброшено чужих: {Dropped})",
            fromVersion, admins.Count, dropped);
        return true;
    }

    // Сколько чего влито из одного слоя — для лога миграции
    private sealed record MergeCount(int Specialties, int Presets, bool DefaultSpecialty);

    // Влить слой в глобальный. claimed — ключи специальностей, уже занятые более ранним
    // админом (их не трогаем); «любую специальность» занимает первый, у кого она задана.
    private static MergeCount MergeIntoGlobal(SpecialtySettingsLayer global,
        SpecialtySettingsLayer source, HashSet<string> claimed)
    {
        var specialties = 0;
        foreach (var (key, record) in source.Specialties)
        {
            if (!claimed.Add(key)) continue;
            global.Specialties[key] = MergeRecord(global.Specialties.GetValueOrDefault(key), record);
            specialties++;
        }

        var anyMerged = false;
        if (source.DefaultSpecialty is { } ds && claimed.Add(SpecialtyCatalog.AnySpecialtyKey))
        {
            global.DefaultSpecialty = MergeRecord(global.DefaultSpecialty, ds);
            anyMerged = true;
        }

        // Пресеты владельца впереди глобальных — тот же порядок, в котором их резолвил
        // прежний EffectivePresets (личные раньше общих)
        var ids = global.Presets.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = source.Presets.Where(p => ids.Add(p.Id)).ToList();
        global.Presets.InsertRange(0, fresh);
        return new MergeCount(specialties, fresh.Count, anyMerged);
    }

    // Запись владельца поверх глобальной: права, уровни, DefaultTier — целиком из owner-записи
    // (резолв и был «первый заданный слой целиком»), секции — посекционно, типовые умения —
    // полем (непустой список владельца заменяет глобальный).
    private static SpecialtyTemplateSettings MergeRecord(
        SpecialtyTemplateSettings? global, SpecialtyTemplateSettings owner)
    {
        var merged = new SpecialtyTemplateSettings
        {
            Access = owner.Access,
            Tools = owner.Tools,
            DisallowedTools = owner.DisallowedTools,
            TierStrong = owner.TierStrong,
            TierMedium = owner.TierMedium,
            TierWeak = owner.TierWeak,
            DefaultTier = owner.DefaultTier,
            DefaultBindings = owner.DefaultBindings is { Count: > 0 }
                ? owner.DefaultBindings
                : global?.DefaultBindings,
            PromptSections = MergeSections(global?.PromptSections, owner.PromptSections),
        };
        return merged;
    }

    // Секции посекционно: параметр владельца перекрывает глобальный по id, отдельно Enabled
    // и отдельно Text (пустой текст владельца = «не задан», значит остаётся глобальный).
    private static List<SpecialtyPromptSectionSettings>? MergeSections(
        List<SpecialtyPromptSectionSettings>? global, List<SpecialtyPromptSectionSettings>? owner)
    {
        if (owner is not { Count: > 0 }) return global;
        var result = new List<SpecialtyPromptSectionSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in owner)
        {
            if (!seen.Add(section.Id)) continue;
            var lower = global?.FirstOrDefault(s =>
                string.Equals(s.Id, section.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(new SpecialtyPromptSectionSettings
            {
                Id = section.Id,
                Enabled = section.Enabled,
                Text = string.IsNullOrWhiteSpace(section.Text) ? lower?.Text : section.Text,
            });
        }
        // Секции, заданные только глобально, остаются как были
        foreach (var section in global ?? [])
            if (seen.Add(section.Id))
                result.Add(section);
        return result;
    }

    // Копия файла перед первой записью нового формата: specialty-settings.v{from}.bak рядом
    // со стором. Уже существующую копию не перезаписываем — она от первой (настоящей)
    // миграции, а повторный проход возможен только после ручного отката версии.
    private void BackupSourceFile(int fromVersion)
    {
        var backup = Path.Combine(Path.GetDirectoryName(_storePath)!,
            $"specialty-settings.v{fromVersion}.bak");
        try
        {
            if (!File.Exists(backup)) File.Copy(_storePath, backup);
            _log?.LogInformation("specialty-settings.json: копия исходного файла сохранена в {Path}", backup);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Не удалось сохранить копию {Path} перед миграцией", backup);
        }
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
