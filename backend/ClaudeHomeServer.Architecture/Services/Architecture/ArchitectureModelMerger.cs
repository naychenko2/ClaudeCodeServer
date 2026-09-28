using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Итог слияния: новая persist-обёртка, обновлённая карта происхождения (блок
/// <c>elements</c> файла метаданных) и счётчики.
/// </summary>
public sealed record ArchitectureMergeResult(
    JsonObject Document,
    int Added,
    int Matched,
    int ConnectionsAdded,
    JsonObject Elements,
    int Candidates = 0,
    int MarkedMissing = 0,
    int Unmarked = 0,
    int SkippedDeleted = 0,
    IReadOnlyList<string>? Missing = null);

/// <summary>
/// Слияние сгенерированной модели с файлом Viaduct (persist-обёртка zustand:
/// <c>{ state: { model: FlatC4Model }, version }</c>).
///
/// Инварианты повторной сборки:
/// <list type="bullet">
/// <item>Существующий элемент ищется по id по всему уровню (генерируемые id стабильны;
///   перенесённый руками к другому родителю остаётся там), затем по имени
///   внутри того же родителя (без учёта регистра); найденный НЕ перезаписывается —
///   сохраняются описание, позиция, url, docs/sequence/flows и любые чужие поля.
///   Дописываются только пустые description/technology.</item>
/// <item>Ручные элементы и связи, которых нет в коде, не удаляются никогда.</item>
/// <item>Связи только добавляются: цель, к которой связь уже есть, пропускается.</item>
/// <item>Происхождение — в карте <c>elements</c> метаданных (не в модели: редактор Viaduct
///   чужие поля затирает): <c>origin=code</c> у всего, что создал генератор, <c>agent</c> —
///   у созданного тулсетом, нет записи — вручную. Старые модели мигрируют лениво: id с
///   префиксом <c>gen-</c> считаются <c>code</c>.</item>
/// <item>«Нет в коде»: <c>code</c>-элемент, которого нет в свежей генерации, получает тег
///   <see cref="MissingTag"/> и <c>missingSince</c>; вернулся — тег и отметка снимаются.
///   Теги трогаются ТОЛЬКО у <c>code</c>-элементов и только этот; при трёх чужих тегах тег
///   не ставится, факт остаётся в сводке.</item>
/// <item>Удалённый человеком <c>code</c>-элемент (запись есть, в модели нет) не
///   пересоздаётся: ставится <c>userDeletedAt</c>, вложенное в него тоже не создаётся.</item>
/// </list>
/// </summary>
public static class ArchitectureModelMerger
{
    // Сетка новых элементов почти квадратная (колонок ≈ √N соседей, не больше потолка):
    // в одну строку по пять четыре контейнера вставали в линию, и после «вписать в
    // экран» схема мельчала, а стрелки уходили за края (смоук 26.09)
    public const int MaxGridColumns = 6;
    public const int GridStepX = 360;
    public const int GridStepY = 240;

    /// <summary>Тег элемента из кода, которого в коде больше нет.</summary>
    public const string MissingTag = "нет в коде";

    public const string OriginCode = "code";
    public const string OriginAgent = "agent";

    /// <summary>Число колонок сетки под N соседей одного родителя.</summary>
    public static int GridColumnsFor(int count) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(count, 1))), 1, MaxGridColumns);

    /// <summary>Стабильный id элемента по уровню и ключу-пути (не зависит от порядка и часов).</summary>
    public static string StableId(string level, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(level + "|" + key.ToLowerInvariant()));
        return $"gen-{level}-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}";
    }

    /// <summary>
    /// Доводит persist-обёртку до формы, которую открывает редактор Viaduct: <c>state.model</c>
    /// с четырьмя массивами уровней, <c>viewLevel</c> и <c>version</c> обёртки. Недостающее
    /// добавляется, имеющееся не трогается. Единая точка формы для генератора и для
    /// первой модели, заведённой агентом (arch_create_element в пустом проекте).
    /// </summary>
    public static JsonObject EnsureShape(JsonObject doc)
    {
        var state = EnsureObject(doc, "state");
        var model = EnsureObject(state, "model");
        if (!doc.ContainsKey("version")) doc["version"] = 0;
        EnsureArray(model, "systems");
        EnsureArray(model, "containers");
        EnsureArray(model, "components");
        EnsureArray(model, "codeElements");
        if (model["viewLevel"] is null) model["viewLevel"] = "system";
        return model;
    }

    /// <param name="elementsMeta">Блок <c>elements</c> метаданных (не меняется — в итоге копия).</param>
    /// <param name="now">Время прогона для missingSince/userDeletedAt (в тестах — фиксированное).</param>
    public static ArchitectureMergeResult Merge(JsonNode? existing, GeneratedModel generated,
        JsonObject? elementsMeta = null, DateTimeOffset? now = null)
    {
        var doc = existing?.DeepClone() as JsonObject ?? new JsonObject();
        var model = EnsureShape(doc);
        var systems = (JsonArray)model["systems"]!;
        var containers = (JsonArray)model["containers"]!;
        var components = (JsonArray)model["components"]!;
        var elements = elementsMeta?.DeepClone() as JsonObject ?? new JsonObject();
        var stamp = (now ?? DateTimeOffset.UtcNow).ToString("O");

        // Сгенерированное, но удалённое человеком: запись code есть, элемента в модели нет.
        // Модели нет или в ней ни одного элемента — это не «человек удалил всё», а потерянный
        // файл модели: создаём заново всё, иначе сборка молча выдала бы пустую схему
        var modelIds = AllElements(model).Select(o => o["id"].Str()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var deletedByUser = modelIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : elements
                .Where(p => OriginOf(p.Value) == OriginCode && !modelIds.Contains(p.Key))
                .Select(p => p.Key)
                .ToHashSet(StringComparer.Ordinal);

        var idByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objByKey = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var addedIds = new List<string>();
        int added = 0, matched = 0, skippedDeleted = 0, candidates = 0;

        // Система: id → имя → единственная система модели (переименованная руками).
        var sysGen = generated.System;
        var sysObj = FindById(systems, StableId("system", sysGen.Key))
                     ?? FindByName(systems, sysGen.Name)
                     ?? (systems.Count == 1 ? systems[0] as JsonObject : null);
        Place(sysGen, "system", sysObj, systems, parentFields: [], columns: 1);

        var containerColumns = GridColumnsFor(generated.Containers.Count);
        foreach (var c in generated.Containers)
        {
            var sysId = idByKey[c.ParentKey!];
            var siblings = Children(containers, "systemId", sysId);
            // id ищется по всему уровню: перенесённый руками к другому родителю — тот же
            // элемент, перенос уважаем (не двигаем и не плодим дубль id)
            var obj = FindById(containers, StableId("container", c.Key)) ?? FindByName(siblings, c.Name);
            Place(c, "container", obj, containers, [("systemId", sysId)], containerColumns);
        }

        var componentsPerParent = generated.Components
            .GroupBy(c => c.ParentKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in generated.Components)
        {
            // Контейнер удалён человеком — его компоненты не воскрешаем (и считаем пропущенными)
            if (!idByKey.TryGetValue(c.ParentKey!, out var ctrId))
            {
                skippedDeleted++;
                continue;
            }
            var sysId = objByKey[c.ParentKey!]["systemId"].Str() ?? idByKey[ArchitectureModelBuilder.SystemKey];
            var siblings = Children(components, "containerId", ctrId);
            var obj = FindById(components, StableId("component", c.Key)) ?? FindByName(siblings, c.Name);
            Place(c, "component", obj, components, [("containerId", ctrId), ("systemId", sysId)],
                GridColumnsFor(componentsPerParent[c.ParentKey!]));
        }

        // Внешние системы-кандидаты — соседи системы проекта на уровне systems.
        var externals = generated.Externals ?? [];
        var projectSystemId = idByKey[sysGen.Key];
        var externalKeys = externals.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var createdExternals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in externals)
        {
            var others = systems.OfType<JsonObject>().Where(o => o["id"].Str() != projectSystemId).ToList();
            var obj = FindById(others, StableId("system", e.Key)) ?? FindByName(others, e.Name);
            var addedBefore = added;
            if (Place(e, "system", obj, systems, parentFields: [], GridColumnsFor(externals.Count + 1)))
            {
                candidates++;
                if (added > addedBefore) createdExternals.Add(e.Key);
            }
        }

        // Связи — после того как все ключи получили id.
        var connectionsAdded = 0;
        foreach (var el in new[] { generated.System }.Concat(generated.Containers).Concat(generated.Components))
        {
            if (!objByKey.TryGetValue(el.Key, out var owner)) continue;
            var connections = EnsureArray(owner, "connections");
            foreach (var conn in el.Connections)
            {
                if (!idByKey.TryGetValue(conn.TargetKey, out var targetId)) continue;
                // Стрелка к кандидату — только вместе с самим кандидатом: удалённую человеком
                // при оставшемся кандидате не возвращаем
                if (externalKeys.Contains(conn.TargetKey) && !createdExternals.Contains(conn.TargetKey)) continue;
                if (connections.OfType<JsonObject>().Any(x => x["targetId"].Str() == targetId)) continue;
                connections.Add(new JsonObject { ["targetId"] = targetId, ["label"] = conn.Label });
                connectionsAdded++;
            }
        }

        // Происхождение: созданное сейчас и (ленивая миграция) старые gen-элементы — code
        foreach (var id in addedIds) Record(id)[OriginKey] ??= OriginCode;
        foreach (var o in AllElements(model))
            if (o["id"].Str() is { } id && id.StartsWith("gen-", StringComparison.Ordinal) && elements[id] is null)
                Record(id)[OriginKey] = OriginCode;

        // «Нет в коде»: сверка code-элементов модели со свежей генерацией
        var fresh = idByKey.Values.ToHashSet(StringComparer.Ordinal);
        int markedMissing = 0, unmarked = 0;
        var missing = new List<string>();
        foreach (var o in AllElements(model))
        {
            if (o["id"].Str() is not { } id || elements[id] is not JsonObject rec || OriginOf(rec) != OriginCode) continue;
            // Элемент в модели — значит, не удалён (например, вернули из git)
            rec.Remove("userDeletedAt");
            var tags = o["tags"] as JsonArray;
            var hasTag = tags?.Any(t => t.Str() == MissingTag) == true;
            if (fresh.Contains(id))
            {
                if (!hasTag && rec["missingSince"] is null) continue;
                if (hasTag && tags is not null)
                {
                    foreach (var t in tags.Where(t => t.Str() == MissingTag).ToList()) tags.Remove(t);
                    if (tags.Count == 0) o.Remove("tags");
                }
                rec.Remove("missingSince");
                unmarked++;
                continue;
            }

            missing.Add(o["name"].Str() ?? id);
            if (rec["missingSince"] is null)
            {
                rec["missingSince"] = stamp;
                markedMissing++;
            }
            // Слотов у Viaduct три: занятые чужими — тег не ставим, факт остаётся в сводке
            if (!hasTag && (tags?.Count ?? 0) < ArchitectureModelEditor.MaxTags)
                EnsureArray(o, "tags").Add(MissingTag);
        }

        return new ArchitectureMergeResult(doc, added, matched, connectionsAdded, elements,
            candidates, markedMissing, unmarked, skippedDeleted, missing);

        JsonObject Record(string id)
        {
            if (elements[id] is JsonObject r) return r;
            var created = new JsonObject();
            elements[id] = created;
            return created;
        }

        bool Place(GeneratedElement g, string level, JsonObject? found, JsonArray list,
            (string Name, string Value)[] parentFields, int columns)
        {
            // Удалён человеком — не пересоздаём (корень-система не пропускается: без неё
            // некуда вкладывать остальное)
            if (found is null && !ReferenceEquals(g, sysGen)
                && deletedByUser.Contains(StableId(level, g.Key)))
            {
                Record(StableId(level, g.Key))["userDeletedAt"] ??= stamp;
                skippedDeleted++;
                return false;
            }
            if (found is not null)
            {
                FillIfEmpty(found, "description", g.Description);
                FillIfEmpty(found, "technology", g.Technology);
                EnsureArray(found, "connections");
                matched++;
            }
            else
            {
                // Новый элемент — в первую свободную клетку сетки: уже стоящих соседей
                // (в том числе разложенных руками) не накрываем
                var siblings = parentFields.Length == 0
                    ? list.OfType<JsonObject>().ToList()
                    : Children(list, parentFields[0].Name, parentFields[0].Value);
                var (x, y) = FreeCell(siblings, columns);
                found = new JsonObject
                {
                    ["id"] = StableId(level, g.Key),
                    ["name"] = g.Name,
                    ["type"] = level,
                    ["position"] = new JsonObject { ["x"] = x, ["y"] = y },
                    ["connections"] = new JsonArray(),
                };
                if (!string.IsNullOrWhiteSpace(g.Description)) found["description"] = g.Description;
                if (!string.IsNullOrWhiteSpace(g.Technology)) found["technology"] = g.Technology;
                if (g.External)
                {
                    found["external"] = true;
                    found["tags"] = new JsonArray(ArchitectureModelBuilder.CandidateTag);
                }
                foreach (var (name, value) in parentFields) found[name] = value;
                list.Add(found);
                addedIds.Add(StableId(level, g.Key));
                added++;
            }
            if (found["id"].Str() is null) found["id"] = StableId(level, g.Key);
            idByKey[g.Key] = found["id"].Str()!;
            objByKey[g.Key] = found;
            return true;
        }
    }

    private const string OriginKey = "origin";

    private static string? OriginOf(JsonNode? record) => record?[OriginKey].Str();

    /// <summary>
    /// Имена элементов, заведённых человеком: нет записи происхождения в метаданных и id не
    /// генераторный (<c>gen-</c> — ленивая миграция в <c>code</c>). Нужны постановке агента
    /// прохода 2: сам тулсет происхождения не показывает, а ручное агенту трогать нельзя.
    /// </summary>
    public static List<string> ManualElementNames(JsonNode? doc, JsonObject? elementsMeta)
    {
        if (doc?["state"]?["model"] is not JsonObject model) return [];
        return AllElements(model)
            .Where(o => o["id"].Str() is { } id
                        && !id.StartsWith("gen-", StringComparison.Ordinal)
                        && OriginOf(elementsMeta?[id]) is null)
            .Select(o => o["name"].Str() ?? o["id"].Str()!)
            .ToList();
    }

    private static IEnumerable<JsonObject> AllElements(JsonObject model) =>
        new[] { "systems", "containers", "components", "codeElements" }
            .SelectMany(level => model[level] as JsonArray ?? [])
            .OfType<JsonObject>();

    /// <summary>
    /// Первая по строкам клетка сетки, рядом с которой (ближе полушага) не стоит ни один
    /// сосед. Соседи без позиции места не занимают.
    /// </summary>
    public static (int X, int Y) FreeCell(IReadOnlyList<JsonObject> siblings, int columns)
    {
        var taken = siblings
            .Select(o => (X: Num(o["position"]?["x"]), Y: Num(o["position"]?["y"])))
            .Where(p => p.X is not null && p.Y is not null)
            .Select(p => (X: p.X!.Value, Y: p.Y!.Value))
            .ToList();
        for (var i = 0; ; i++)
        {
            int x = i % columns * GridStepX, y = i / columns * GridStepY;
            if (!taken.Any(p => Math.Abs(p.X - x) < GridStepX / 2.0 && Math.Abs(p.Y - y) < GridStepY / 2.0))
                return (x, y);
        }
    }

    // Число из JSON: файл Viaduct хранит координаты и целыми, и дробными
    private static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static List<JsonObject> Children(JsonArray list, string parentField, string parentId) =>
        list.OfType<JsonObject>().Where(o => o[parentField].Str() == parentId).ToList();

    private static JsonObject? FindById(IEnumerable<JsonNode?> list, string id) =>
        list.OfType<JsonObject>().FirstOrDefault(o => o["id"].Str() == id);

    private static JsonObject? FindByName(IEnumerable<JsonNode?> list, string name) =>
        list.OfType<JsonObject>().FirstOrDefault(o =>
            string.Equals(o["name"].Str()?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void FillIfEmpty(JsonObject obj, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (obj[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return;
        obj[field] = value;
    }

    /// <summary>Строковое значение узла или null (число/объект/отсутствие — не исключение).</summary>
    private static string? Str(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o) return o;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray EnsureArray(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray a) return a;
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }
}
