using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Tests.Services;

// Эталоны стенда, скопированные в тесты: ComfyReference/*.json — графы из
// ~/ComfyUI-h3-tools/workflows (прошли validate_prompt ComfyUI), object_info_subset.json — схема
// тех классов нод, что вообще бывают в наших графах (офлайн-снимок dump_object_info.py)
public static class ComfyReferenceFiles
{
    private static readonly Lazy<JsonObject> _objectInfo = new(() => Load("object_info_subset.json"));

    public static JsonObject ObjectInfo() => _objectInfo.Value;

    // Граф эталона без _meta (подписи нод для UI в API-граф не входят)
    public static JsonObject Graph(string name)
    {
        var graph = Load(name + ".json");
        foreach (var (_, node) in graph)
            node!.AsObject().Remove("_meta");
        return graph;
    }

    private static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Images", "ComfyReference", file)))!
            .AsObject();
}

// Поузловая сверка графа с эталоном: список расхождений «нода.вход: наше ≠ эталон». Числа
// сравниваются по значению (1 и 1.0 — одно и то же), порядок ключей не важен
public static class ComfyGraphDiff
{
    public static IReadOnlyList<string> Compare(JsonObject actual, JsonObject expected)
    {
        var diffs = new List<string>();
        foreach (var (id, _) in expected)
            if (!actual.ContainsKey(id)) diffs.Add($"{id}: нет ноды");
        foreach (var (id, node) in actual)
        {
            if (!expected.TryGetPropertyValue(id, out var reference)) { diffs.Add($"{id}: лишняя нода"); continue; }
            if (node!["class_type"]!.GetValue<string>() != reference!["class_type"]!.GetValue<string>())
                diffs.Add($"{id}: класс {node["class_type"]} ≠ {reference["class_type"]}");
            var ours = node["inputs"]!.AsObject();
            var theirs = reference["inputs"]!.AsObject();
            foreach (var key in ours.Select(p => p.Key).Union(theirs.Select(p => p.Key)))
                if (!Same(ours[key], theirs[key]))
                    diffs.Add($"{id}.{key}: {ours[key]?.ToJsonString() ?? "нет"} ≠ {theirs[key]?.ToJsonString() ?? "нет"}");
        }
        return diffs;
    }

    private static bool Same(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
        (JsonValue x, JsonValue y) when IsNumber(x) && IsNumber(y) => Number(x) == Number(y),
        _ => JsonNode.DeepEquals(a, b),
    };

    private static bool IsNumber(JsonValue v) => v.GetValueKind() == System.Text.Json.JsonValueKind.Number;

    private static decimal Number(JsonValue v) => decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

// Проверка API-графа по схеме нод (/object_info): то же, что делает validate_prompt ComfyUI до
// постановки, без живой очереди. Проверяет класс ноды, имена входов (включая вложенные входы
// dynamic combo «format.codec» и autogrow «ref_images.ref_image_0»), обязательные входы, тип
// связи против типа выхода источника, значения списков и числовые диапазоны.
//
// Не проверяет значения, которые зависят от машины: списки файлов у загрузчиков (input
// ComfyUI) и списки устройств (снимок снят на CPU)
public static class ComfyGraphValidator
{
    private static readonly HashSet<string> FileListInputs =
        ["LoadImage.image", "LoadImageMask.image", "LoadVideo.file", "LoadAudio.audio", "LoadLatent.latent"];

    public static IReadOnlyList<string> Validate(JsonObject graph, JsonObject objectInfo)
    {
        var errors = new List<string>();
        foreach (var (id, node) in graph)
        {
            var cls = node!["class_type"]!.GetValue<string>();
            if (objectInfo[cls] is not JsonObject info) { errors.Add($"{id}: класса {cls} нет у стенда"); continue; }
            var inputs = node["inputs"]!.AsObject();

            var allowed = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
            var required = new List<string>();
            Collect(info["input"]?["required"] as JsonObject, "", true, inputs, allowed, required);
            Collect(info["input"]?["optional"] as JsonObject, "", false, inputs, allowed, required);

            foreach (var name in required)
                if (!inputs.ContainsKey(name)) errors.Add($"{id}.{name}: обязательный вход не задан");

            foreach (var (name, value) in inputs)
            {
                if (!allowed.TryGetValue(name, out var spec)) { errors.Add($"{id}.{name}: у {cls} нет такого входа"); continue; }
                var error = value is JsonArray link
                    ? CheckLink(graph, objectInfo, link, spec)
                    : CheckValue($"{cls}.{name}", value, spec);
                if (error is not null) errors.Add($"{id}.{name}: {error}");
            }
        }
        return errors;
    }

    private static void Collect(JsonObject? section, string prefix, bool isRequired, JsonObject values,
        Dictionary<string, JsonArray> allowed, List<string> required)
    {
        if (section is null) return;
        foreach (var (rawName, specNode) in section)
        {
            if (specNode is not JsonArray spec) continue;
            var name = prefix + rawName;
            var type = spec[0] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            var options = spec.Count > 1 ? spec[1] as JsonObject : null;

            if (type == "COMFY_AUTOGROW_V3")
            {
                // Имена входов — явным списком names либо prefix + номер с нуля до max
                var template = options?["template"];
                var names = template?["names"] is JsonArray list
                    ? list.Select(n => n!.GetValue<string>())
                    : Enumerable.Range(0, template?["max"]?.GetValue<int>() ?? 0)
                        .Select(i => (template?["prefix"]?.GetValue<string>() ?? "") + i);
                if (template?["input"]?["required"] is JsonObject item && item.FirstOrDefault().Value is JsonArray itemSpec)
                    foreach (var itemName in names)
                        allowed[$"{name}.{itemName}"] = itemSpec;
                continue;
            }

            allowed[name] = spec;
            if (isRequired) required.Add(name);

            // Dynamic combo: вложенные входы выбранного варианта называются «вход.подвход»
            if (type == "COMFY_DYNAMICCOMBO_V3" && values[name] is JsonValue chosen
                && options?["options"] is JsonArray variants)
            {
                var key = chosen.ToString();
                var variant = variants.OfType<JsonObject>().FirstOrDefault(v => v["key"]?.GetValue<string>() == key);
                Collect(variant?["inputs"]?["required"] as JsonObject, name + ".", isRequired, values, allowed, required);
                Collect(variant?["inputs"]?["optional"] as JsonObject, name + ".", false, values, allowed, required);
            }
        }
    }

    private static string? CheckLink(JsonObject graph, JsonObject objectInfo, JsonArray link, JsonArray spec)
    {
        if (link.Count != 2 || link[0] is not JsonValue src || link[1] is not JsonValue slot) return "битая связь";
        var source = src.GetValue<string>();
        if (graph[source] is not JsonObject sourceNode) return $"связь на несуществующую ноду {source}";
        var outputs = objectInfo[sourceNode["class_type"]!.GetValue<string>()]?["output"] as JsonArray;
        var index = slot.GetValue<int>();
        if (outputs is null || index < 0 || index >= outputs.Count) return $"у {source} нет выхода {index}";

        var produced = outputs[index]!.ToString();
        var expected = spec[0] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "COMBO";
        if (expected == "*" || produced == "*" || expected == produced) return null;
        // Список значений (COMBO) может прийти связью от ноды, отдающей COMBO
        return expected == "COMBO" && produced == "COMBO" ? null : $"тип {produced} от {source}, а ждём {expected}";
    }

    private static string? CheckValue(string key, JsonNode? value, JsonArray spec)
    {
        var options = spec.Count > 1 ? spec[1] as JsonObject : null;
        JsonArray? list = spec[0] as JsonArray;
        var type = spec[0] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
        if (type is "COMBO" or "COMFY_DYNAMICCOMBO_V3")
            list = options?["options"] is JsonArray o
                ? new JsonArray([.. o.Select(x => (JsonNode?)(x is JsonObject v ? v["key"]!.DeepClone() : x!.DeepClone()))])
                : null;

        if (list is not null)
        {
            if (FileListInputs.Contains(key) || key.EndsWith(".device", StringComparison.Ordinal)) return null;
            return list.Any(x => x?.ToString() == value?.ToString()) ? null : $"«{value}» нет в списке допустимых";
        }

        switch (type)
        {
            case "INT" or "FLOAT":
                if (value is not JsonValue number || number.GetValueKind() != System.Text.Json.JsonValueKind.Number)
                    return "ждём число";
                var n = decimal.Parse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (type == "INT" && n != decimal.Truncate(n)) return "ждём целое";
                if (options?["min"] is JsonValue min && n < min.GetValue<decimal>()) return $"{n} меньше минимума {min}";
                if (options?["max"] is JsonValue max && n > max.GetValue<decimal>()) return $"{n} больше максимума {max}";
                return null;
            case "BOOLEAN":
                return value is JsonValue b && b.GetValueKind() is System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False ? null : "ждём true/false";
            case "STRING":
                return value is JsonValue str && str.GetValueKind() == System.Text.Json.JsonValueKind.String ? null : "ждём строку";
            default:
                // Входы-типы (MODEL, IMAGE, …) принимают только связь
                return $"вход {type} принимает только связь";
        }
    }
}
