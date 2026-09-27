using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>Итог слияния: новая persist-обёртка и счётчики.</summary>
public sealed record ArchitectureMergeResult(
    JsonObject Document,
    int Added,
    int Matched,
    int ConnectionsAdded);

/// <summary>
/// Слияние сгенерированной модели с файлом Viaduct (persist-обёртка zustand:
/// <c>{ state: { model: FlatC4Model }, version }</c>).
///
/// Инварианты повторной сборки:
/// <list type="bullet">
/// <item>Существующий элемент ищется по id (генерируемые id стабильны), затем по имени
///   внутри того же родителя (без учёта регистра); найденный НЕ перезаписывается —
///   сохраняются описание, позиция, url, docs/sequence/flows и любые чужие поля.
///   Дописываются только пустые description/technology.</item>
/// <item>Ручные элементы и связи, которых нет в коде, не удаляются никогда.</item>
/// <item>Связи только добавляются: цель, к которой связь уже есть, пропускается.</item>
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

    public static ArchitectureMergeResult Merge(JsonNode? existing, GeneratedModel generated)
    {
        var doc = existing?.DeepClone() as JsonObject ?? new JsonObject();
        var model = EnsureShape(doc);
        var systems = (JsonArray)model["systems"]!;
        var containers = (JsonArray)model["containers"]!;
        var components = (JsonArray)model["components"]!;

        var idByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objByKey = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        int added = 0, matched = 0;

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
            var obj = FindById(siblings, StableId("container", c.Key)) ?? FindByName(siblings, c.Name);
            Place(c, "container", obj, containers, [("systemId", sysId)], containerColumns);
        }

        var componentsPerParent = generated.Components
            .GroupBy(c => c.ParentKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in generated.Components)
        {
            var ctrId = idByKey[c.ParentKey!];
            var sysId = objByKey[c.ParentKey!]["systemId"].Str() ?? idByKey[ArchitectureModelBuilder.SystemKey];
            var siblings = Children(components, "containerId", ctrId);
            var obj = FindById(siblings, StableId("component", c.Key)) ?? FindByName(siblings, c.Name);
            Place(c, "component", obj, components, [("containerId", ctrId), ("systemId", sysId)],
                GridColumnsFor(componentsPerParent[c.ParentKey!]));
        }

        // Связи — после того как все ключи получили id.
        var connectionsAdded = 0;
        foreach (var el in generated.Containers.Concat(generated.Components))
        {
            var connections = EnsureArray(objByKey[el.Key], "connections");
            foreach (var conn in el.Connections)
            {
                if (!idByKey.TryGetValue(conn.TargetKey, out var targetId)) continue;
                if (connections.OfType<JsonObject>().Any(x => x["targetId"].Str() == targetId)) continue;
                connections.Add(new JsonObject { ["targetId"] = targetId, ["label"] = conn.Label });
                connectionsAdded++;
            }
        }

        return new ArchitectureMergeResult(doc, added, matched, connectionsAdded);

        void Place(GeneratedElement g, string level, JsonObject? found, JsonArray list,
            (string Name, string Value)[] parentFields, int columns)
        {
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
                foreach (var (name, value) in parentFields) found[name] = value;
                list.Add(found);
                added++;
            }
            if (found["id"].Str() is null) found["id"] = StableId(level, g.Key);
            idByKey[g.Key] = found["id"].Str()!;
            objByKey[g.Key] = found;
        }
    }

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
