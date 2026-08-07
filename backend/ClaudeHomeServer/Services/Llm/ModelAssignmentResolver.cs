using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Резолвер модели для АГЕНТНЫХ мест каталога (группа «Чаты и персоны»: новый чат, чат
// персоны, исполнитель задач, сабагенты, LLM-канал модулей). Единая точка, где маршрут
// (явная модель / уровень / preset / слот) превращается в конкретную модель хода:
//
//   явная модель → назначение админа (модель | слот | preset) → дефолт места (слот)
//
// Уровень (tier) разворачивается в модель единственной точкой — UserModelTierResolver
// (перегрузка с узкими матрицами «персона → специальность → слоты», ADR-007 §2). Пресет
// разворачивается в цепочку единственной точкой — SpecialtySettingsStore.ExpandChain
// (ADR-007 §3). Маркеры tier:*/preset:* наружу (в Session.Model) не текут — разворачиваются
// здесь. Отличие от фоновых действий: у агентных мест нет цепочки «локаль → claude"
// (CheapTextRunner) — local и direct:-шаги пресета им непригодны и пропускаются.
public sealed class ModelAssignmentResolver(
    AppSettingsService appSettings,
    LocalActionOverridesStore? store = null,
    UserModelTierResolver? userTiers = null,
    SpecialtySettingsStore? specialty = null)
{
    /// <summary>
    /// Модель персоны как «явная» для места (ADR-007 §2): своя модель сильнее её уровня,
    /// уровень — сильнее специальности, та — сильнее назначения места. Уровень разворачивается
    /// по самым узким матрицам (персона → специальность → слоты владельца). Ячейка матрицы
    /// или явная модель может быть preset:{id} — разворачивается в первый пригодный шаг.
    /// null — ни один шаг не сработал: решает место. Разворачивается при создании чата
    /// (SessionManager) и замораживается в Session.Model (§5.2).
    ///
    /// placeDefaultTier — дефолтный уровень места (LocalActionCatalog.DefaultTierOf), последний
    /// резерв уровня, когда ни Persona.ModelTier, ни Specialty.DefaultTier не заданы. Без него
    /// персона с заполненной ячейкой, но без явного уровня, свою ячейку не задействовала —
    /// настройка сохранялась и молча не работала (дефект 2-й итерации). Этим уровнем матрица
    /// разворачивается всё в той же точке — UserModelTierResolver.ModelFor.
    /// </summary>
    public string? PersonaModel(Persona? persona, string? ownerId = null, ModelTier? placeDefaultTier = null)
    {
        if (persona is null) return null;
        return PersonaModelDetailed(persona, persona.Specialty, ownerId, placeDefaultTier, null).Model;
    }

    /// <summary>
    /// Модель исполнителя задачи (ADR-007 §5.3): уровень задачи (постановщик) сильнее
    /// персоны, далее — PersonaModel (модель персоны → её уровень с матрицами). Уровень
    /// задачи тоже разворачивается по матрицам персоны (если исполнитель — персона).
    /// null — ни один шаг не сработал: сессия возьмёт модель по назначению места.
    /// </summary>
    public string? ExecutorModel(TaskItem task, Persona? persona, string? ownerId)
    {
        if (task.ModelTier is { } taskTier)
        {
            var matrices = persona is null ? [] : PersonaMatrices(persona, ownerId);
            var value = userTiers?.ModelFor(taskTier, ownerId, matrices) ?? appSettings.TierModel(taskTier);
            return PrimaryOf(value, ownerId);
        }
        // Без уровня задачи — модель персоны; дефолт места «исполнитель задач» (Strong)
        // подхватывает ячейку персоны без явного уровня.
        return PersonaModel(persona, ownerId, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.TasksExecutor));
    }

    /// <summary>
    /// Модель места (первый пригодный шаг). null — модель не определена, решает CLI.
    /// Разворачивает preset:{id}/tier:* явной модели и назначения админа; local/direct
    /// агентному месту непригодны и пропускаются (как сегодня).
    /// </summary>
    public string? Resolve(string usageKey, string? explicitModel = null, string? ownerId = null) =>
        ResolveChain(usageKey, explicitModel, ownerId).FirstOrDefault();

    /// <summary>
    /// Цепочка конкретных моделей хода (первая = основная, остальные = план фолбэка,
    /// ADR-007 §4). Источник: явная модель → назначение админа → дефолт места (слот).
    /// Разворачивает preset:{id} через ExpandChain, tier:*-шаги по слотам владельца (БЕЗ
    /// матриц — пресет не знает, откуда на него сослались, §1), local/direct агентному
    /// месту непригодны и пропускаются. Защита от цикла «слот ↔ пресет» — набор посещённых.
    /// </summary>
    public IReadOnlyList<string> ResolveChain(string usageKey, string? explicitModel = null, string? ownerId = null)
    {
        var action = LocalActionCatalog.Find(usageKey);
        var agentic = action?.Agentic ?? false;
        var route = ResolveRawRoute(usageKey, explicitModel, ownerId, action);
        return ExpandRouteToModels(route, ownerId, agentic);
    }

    // Сырой маршрут места (что выбрано, до разворачивания маркеров): явная модель →
    // назначение админа → дефолт каталога (tier). Делегирует в ResolveRawRouteWithSource.
    private string? ResolveRawRoute(string usageKey, string? explicitModel, string? ownerId, LocalAction? action)
        => ResolveRawRouteWithSource(usageKey, explicitModel, ownerId, action).Route;

    // Тот же маршрут + источник (ModelSource.*) для показа «Сейчас пойдёт». Маршрут может быть
    // маркером tier:*/preset:*; источник описывает, какой шаг приоритета сработал (явная модель /
    // назначение админа / слот дефолта места — личный или глобальный). tier:* с пустым слотом у
    // явной модели не отдаём — падаем на место (как раньше).
    private (string? Route, string? Source) ResolveRawRouteWithSource(
        string usageKey, string? explicitModel, string? ownerId, LocalAction? action)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            var v = explicitModel.Trim();
            // Явный tier:* с пустым слотом — не отдаём маркер наружу, идём на место (как раньше).
            // С заполненным слотом (в т.ч. preset) — возвращаем маркер, развернётся в Expand.
            if (LocalActionOverridesStore.ParseTierRoute(v) is { } et)
            {
                var slot = userTiers?.ModelFor(et, ownerId) ?? appSettings.TierModel(et);
                if (!string.IsNullOrWhiteSpace(slot)) return (v, ModelSource.ExplicitModel);
                // пустой слот — падаем на место (ниже)
            }
            else
            {
                return (v, ModelSource.ExplicitModel); // модель или preset — как есть
            }
        }

        // Назначение админа: local/direct агентному месту непригодны — игнор, падаем на дефолт.
        if (store?.TryGet(usageKey) is { } assigned && !string.IsNullOrWhiteSpace(assigned))
        {
            var v = assigned.Trim();
            if (action?.Agentic == true
                && (v == LocalActionOverridesStore.LocalRoute || CloudCheapClient.IsDirectRoute(v)))
            {
                // непригодно — падаем на дефолт каталога ниже
            }
            else
            {
                return (v, ModelSource.PlaceAssignment);
            }
        }

        // Дефолт места — слот каталога (маркер tier:*, развернётся в Expand). Источник —
        // личный слот владельца либо глобальный слот инстанса (для строки-итога).
        var tier = action is null ? ModelTier.Medium : LocalActionCatalog.EffectiveDefaultTier(action);
        var cell = userTiers?.ModelForWithSource(tier, ownerId, [])
            ?? new TierCellResult(appSettings.TierModel(tier), TierCellOrigin.InstanceSlot, -1);
        var defSource = cell.Origin == TierCellOrigin.OwnerSlot ? ModelSource.OwnerSlot : ModelSource.InstanceSlot;
        return (LocalActionOverridesStore.TierRoute(tier), defSource);
    }

    // Узкие матрицы для разворота уровня персоны (ADR-007 §2): матрица персоны, затем
    // матрицы специальности (запись → DefaultSpecialty). Пустую матрицу персоны не кладём —
    // разворачиватель (UserModelTierResolver) и так пропускает пустые ячейки, но так список
    // короче и понятнее в отладке.
    private List<TierMatrix> PersonaMatrices(Persona persona, string? ownerId)
    {
        var matrices = new List<TierMatrix>();
        var pm = new TierMatrix(persona.TierStrong, persona.TierMedium, persona.TierWeak);
        if (!pm.IsEmpty) matrices.Add(pm);
        if (persona.Specialty != PersonaSpecialty.None && specialty is not null)
            matrices.AddRange(specialty.SpecialtyMatrices(ownerId ?? "", persona.Specialty));
        return matrices;
    }

    // Значение ячейки/модели → конкретная модель хода. preset:{id} → первый пригодный
    // шаг цепочки (агентное место: local/direct пропускаются); модель — как есть; пусто — null.
    private string? PrimaryOf(string? value, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (LocalActionOverridesStore.IsPresetRoute(value))
            return ExpandRouteToModels(value, ownerId, agentic: true).FirstOrDefault();
        return value;
    }

    // Разворот маршрута в упорядоченный список конкретных моделей. См. ResolveChain.
    private List<string> ExpandRouteToModels(string? route, string? ownerId, bool agentic)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(route)) return result;
        ExpandInto(route, ownerId, agentic, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private void ExpandInto(string route, string? ownerId, bool agentic,
        List<string> result, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        // Пресет → шаги (без рекурсии: вложенность запрещена валидацией). Защита от цикла
        // «слот ↔ пресет»: повторная ссылка — отбрасываем шаг с предупреждением в лог (через ExpandChain).
        if (LocalActionOverridesStore.IsPresetRoute(route))
        {
            if (!visited.Add(route.Trim())) return;
            foreach (var step in specialty?.ExpandChain(route, ownerId) ?? [])
                ExpandStep(step, ownerId, agentic, result, visited);
            return;
        }
        ExpandStep(route, ownerId, agentic, result, visited);
    }

    private void ExpandStep(string step, string? ownerId, bool agentic,
        List<string> result, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(step)) return;
        var s = step.Trim();
        // tier:* — слот владельца (БЕЗ матриц: пресет/слот не знают о персоне, §1). Слот может
        // сам быть preset (User.ModelTierStrong = "preset:…") — тогда разворачиваем его, защищаясь
        // от цикла; ProtectionKey — само значение слота в visited.
        if (LocalActionOverridesStore.ParseTierRoute(s) is { } tier)
        {
            var slot = userTiers?.ModelFor(tier, ownerId) ?? appSettings.TierModel(tier);
            if (string.IsNullOrWhiteSpace(slot)) return;
            if (LocalActionOverridesStore.IsPresetRoute(slot)) { ExpandInto(slot, ownerId, agentic, result, visited); return; }
            AddIfNew(result, slot);
            return;
        }
        // Легаси pseudo-значения «модель по умолчанию» (v1): оба означали средний слот
        if (s is LocalActionOverridesStore.ClaudeRoute or LocalActionOverridesStore.DefaultRoute)
        {
            var m = userTiers?.ModelFor(ModelTier.Medium, ownerId) ?? appSettings.TierModel(ModelTier.Medium);
            if (!string.IsNullOrWhiteSpace(m)) AddIfNew(result, m);
            return;
        }
        // local — агентному месту непригоден (нужны инструменты CLI); direct:-модель тоже
        if (agentic && (s == LocalActionOverridesStore.LocalRoute || CloudCheapClient.IsDirectRoute(s))) return;
        AddIfNew(result, s);
    }

    private static void AddIfNew(List<string> result, string model)
    {
        if (!result.Contains(model, StringComparer.OrdinalIgnoreCase)) result.Add(model);
    }

    // --- Эффективный резолв для показа «Сейчас пойдёт» (ADR-007 §5 п.5, спека блок 4) ---
    // Та же кодовая дорога, что у боевого резолва (PersonaModel/Resolve), но с фиксацией
    // источника модели и цепочки пресета. Второй точки истины НЕТ: PersonaModel и Resolve
    // делегируют в *Detailed-варианты и берут .Model.

    // Wire-имена источников модели (для API /models/preview). Стабильны — фронт пишет по ним.
    public static class ModelSource
    {
        public const string PersonaModel = "persona-model";        // явная Persona.Model
        public const string PersonaCell = "persona-cell";          // ячейка матрицы персоны
        public const string SpecialtyCell = "specialty-cell";      // ячейка матрицы специальности
        public const string OwnerSlot = "owner-slot";              // личный слот тира владельца
        public const string InstanceSlot = "instance-slot";        // глобальный слот инстанса
        public const string PlaceAssignment = "place-assignment";  // назначение админа («Кто что выполняет»)
        public const string ExplicitModel = "explicit";            // явная модель из параметров
    }

    // Раскрытие пресета для показа: имя и шаги цепочки, broken=true если ссылка битая
    // (пресет удалён) — тогда Model=null, но это «ответ» превью, а не «падаем дальше».
    public sealed record ModelPresetPreview(string Id, string? Name, IReadOnlyList<string> Steps, bool Broken);

    // Результат эффективного резолва для показа. Model — первая модель хода (развёрнутая,
    // не маркер); null при пустом резолве или битой ссылке пресета. Source — откуда взялась
    // модель (ModelSource.*); TierOrigin — кто задал уровень ("task"|"persona"|"specialty"|"place"|null);
    // Preset — раскрытие, если значение ячейки/слота было preset:{id}; Chain — план фолбэка
    // (развёрнутые модели, для подсказки наведения «Дальше в цепочке: …»).
    public sealed record ModelSourceDetail(
        string? Model,
        string? Source,
        ModelTier? EffectiveTier,
        string? TierOrigin,
        ModelPresetPreview? Preset,
        IReadOnlyList<string> Chain)
    {
        public static readonly ModelSourceDetail Empty = new(null, null, null, null, null, []);
        public bool PresetBroken => Preset?.Broken ?? false;
    }

    /// <summary>
    /// Эффективный резолв модели по контексту места выбора (ADR-007 §5 п.5, спека блок 4):
    /// «Сейчас пойдёт: {модель} · {откуда}». Тот же расчёт, что на границе запуска хода, —
    /// персона (её модель → уровень с матрицами) при отсутствии ответа падает на место
    /// (назначение админа → дефолт места). overrideTier — уровень сверху (задача), сильнее
    /// уровня персоны/специальности/места. Возвращает источник, эффективный уровень и
    /// раскрытие пресета — фронт не дублирует логику узости.
    /// </summary>
    public ModelSourceDetail Preview(string? place, Persona? persona, PersonaSpecialty personaSpecialty,
        string? ownerId, ModelTier? overrideTier)
    {
        // Резолв по матрицам идёт, если задана персона ИЛИ специальность (превью карточки
        // специальности без персоны). Для specialty-only берём заглушку-персону с пустыми
        // ячейками и Specialty = personaSpecialty (образец — draftOwner в PersonasController:215):
        // её матрица пуста, personaMatrixCount=0, и выигравшая ячейка трактуется как specialty-cell.
        Persona? effective = persona;
        if (effective is null && personaSpecialty != PersonaSpecialty.None)
            effective = new Persona { Specialty = personaSpecialty };

        if (effective is not null)
        {
            var placeTier = place is null ? null : LocalActionCatalog.DefaultTierOf(place);
            var d = PersonaModelDetailed(effective, personaSpecialty, ownerId, placeTier, overrideTier);
            // Резолв дал ответ — в т.ч. битую ссылку пресета (показываем «пресет удалён»,
            // а не падаем на место): у Preview это валидный исход строки-итога.
            if (d.Model is not null || d.PresetBroken) return d;
            // Иначе — матрицы ничего не задали, падаем на место.
        }
        if (!string.IsNullOrWhiteSpace(place))
            return ResolveDetailed(place!, null, ownerId);
        return ModelSourceDetail.Empty;
    }

    // Полный резолв персоны с источником (те же шаги, что PersonaModel). personaSpecialty передаётся
    // снаружи: для превью карточки специальности матрицы строятся по ней даже без персоны.
    private ModelSourceDetail PersonaModelDetailed(Persona persona, PersonaSpecialty personaSpecialty,
        string? ownerId, ModelTier? placeDefaultTier, ModelTier? overrideTier)
    {
        // 1. Явная модель персоны (в т.ч. preset) — сильнее всего
        if (!string.IsNullOrWhiteSpace(persona.Model))
            return PrimaryDetailed(persona.Model.Trim(), ownerId, ModelSource.PersonaModel, null, null);

        // 2. Уровень: задача → персона → специальность → дефолт места
        ModelTier level;
        string origin;
        if (overrideTier is { } ot) { level = ot; origin = "task"; }
        else if (persona.ModelTier is { } pmt) { level = pmt; origin = "persona"; }
        else if (personaSpecialty != PersonaSpecialty.None && specialty is not null
            && specialty.SpecialtyDefaultTier(ownerId ?? "", personaSpecialty) is { } sdt)
        { level = sdt; origin = "specialty"; }
        else if (placeDefaultTier is { } pdt) { level = pdt; origin = "place"; }
        else return ModelSourceDetail.Empty;

        var (matrices, personaMatrixCount) = PersonaMatricesFor(personaSpecialty, persona, ownerId);
        var cell = userTiers?.ModelForWithSource(level, ownerId, matrices)
            ?? new TierCellResult(appSettings.TierModel(level), TierCellOrigin.InstanceSlot, -1);
        var source = cell.Origin switch
        {
            TierCellOrigin.NarrowMatrix when cell.MatrixIndex < personaMatrixCount => ModelSource.PersonaCell,
            TierCellOrigin.NarrowMatrix => ModelSource.SpecialtyCell,
            TierCellOrigin.OwnerSlot => ModelSource.OwnerSlot,
            TierCellOrigin.InstanceSlot => ModelSource.InstanceSlot,
            _ => null,
        };
        return PrimaryDetailed(cell.Value, ownerId, source, level, origin);
    }

    // Узкие матрицы для разворота уровня с подсчётом, сколько матриц в начале относятся к персоне
    // (0 или 1) — чтобы источник выигравшей ячейки назвать «ячейка персоны» vs «ячейка специальности».
    // То же содержимое, что у PersonaMatrices, но по переданной specialty (а не persona.Specialty).
    private (List<TierMatrix> Matrices, int PersonaMatrixCount) PersonaMatricesFor(
        PersonaSpecialty personaSpecialty, Persona persona, string? ownerId)
    {
        var matrices = new List<TierMatrix>();
        var pm = new TierMatrix(persona.TierStrong, persona.TierMedium, persona.TierWeak);
        var personaCount = 0;
        if (!pm.IsEmpty) { matrices.Add(pm); personaCount = 1; }
        if (personaSpecialty != PersonaSpecialty.None && specialty is not null)
            matrices.AddRange(specialty.SpecialtyMatrices(ownerId ?? "", personaSpecialty));
        return (matrices, personaCount);
    }

    // Значение ячейки/модели → ModelSourceDetail. preset:{id} разворачивается в цепочку
    // (первый шаг = Model); битая ссылка → Model=null, Preset.Broken=true. Пустое значение —
    // Empty (нет ответа, вызывающий падает дальше). baseSource описывает, ГДЕ выбрано значение
    // (ячейка персоны/специальности, слот, явная модель) — он ортогонален раскрытию пресета.
    private ModelSourceDetail PrimaryDetailed(string? value, string? ownerId,
        string? baseSource, ModelTier? tier, string? origin)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new ModelSourceDetail(null, baseSource, tier, origin, null, []);
        if (LocalActionOverridesStore.IsPresetRoute(value))
        {
            var id = LocalActionOverridesStore.ParsePresetRoute(value)!;
            var preset = specialty?.FindPreset(ownerId ?? "", id);
            if (preset is null)
                return new ModelSourceDetail(null, baseSource, tier, origin,
                    new ModelPresetPreview(id, null, [], true), []);
            var chain = ExpandRouteToModels(value, ownerId, agentic: true);
            return new ModelSourceDetail(chain.FirstOrDefault(), baseSource, tier, origin,
                new ModelPresetPreview(preset.Id, preset.Name, preset.Steps, false), chain);
        }
        return new ModelSourceDetail(value, baseSource, tier, origin, null, [value]);
    }

    // Полный резолв места с источником (те же шаги, что Resolve).
    private ModelSourceDetail ResolveDetailed(string usageKey, string? explicitModel, string? ownerId)
    {
        var action = LocalActionCatalog.Find(usageKey);
        var agentic = action?.Agentic ?? false;
        var (route, source) = ResolveRawRouteWithSource(usageKey, explicitModel, ownerId, action);
        if (string.IsNullOrWhiteSpace(route))
            return new ModelSourceDetail(null, source, null, null, null, []);

        var chain = ExpandRouteToModels(route, ownerId, agentic);
        ModelPresetPreview? preset = null;
        if (LocalActionOverridesStore.IsPresetRoute(route))
        {
            var id = LocalActionOverridesStore.ParsePresetRoute(route)!;
            var p = specialty?.FindPreset(ownerId ?? "", id);
            preset = p is null
                ? new ModelPresetPreview(id, null, [], true)
                : new ModelPresetPreview(p.Id, p.Name, p.Steps, false);
        }

        // Эффективный уровень и его источник — только когда шли через слот дефолта места
        // (explicit/assignment уровня не имеют; preset говорит сам за себя).
        ModelTier? effTier = null;
        string? tierOrigin = null;
        if (preset is null && source is ModelSource.OwnerSlot or ModelSource.InstanceSlot)
        {
            effTier = action is null ? ModelTier.Medium : LocalActionCatalog.EffectiveDefaultTier(action);
            tierOrigin = "place";
        }
        var model = preset?.Broken == true ? null : chain.FirstOrDefault();
        return new ModelSourceDetail(model, source, effTier, tierOrigin, preset, chain);
    }
}
