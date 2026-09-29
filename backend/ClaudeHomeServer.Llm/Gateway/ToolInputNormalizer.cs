using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Костыль под чужой дефект (задача «MiniMax-M3: массивы в tool_use.input»): модель провайдера
// отдаёт массив аргумента инструмента объектом {"item": [...]} — след XML→JSON конвертера
// tool-call (<image_urls><item>…</item></image_urls>). Схема инструмента от этого не спасает:
// fal submit_job отвечает 422 «Input should be a valid list».
//
// Правило, рекурсивно по input: объект с ЕДИНСТВЕННЫМ ключом item разворачивается в массив
// (значение-массив — как есть, скаляр или объект — массивом из одного элемента). Где у поля
// есть input_schema инструмента из запроса, решает схема: разворачиваем только там, где она
// ждёт массив, и не трогаем объект, у которого item — законное поле. Схемы нет (свободный
// object, как input у fal submit_job) — действует эвристика.
//
// Включается флагом провайдера LlmProviderConfig.NormalizeToolInputArrays; каждое срабатывание
// шлюз пишет в лог — по нему видно, когда провайдер починился и костыль можно снять.
public static class ToolInputNormalizer
{
    public const string WrapperKey = "item";

    // Нормализует input на месте. Возвращает узел, который встаёт на место input: тот же
    // экземпляр, если корень не менялся (вложенные правки при этом уже внесены). changedPaths —
    // пути развёрнутых узлов («$.input.image_urls»), пусто — ничего не менялось.
    public static JsonNode? Normalize(JsonNode? input, JsonNode? schema, List<string> changedPaths) =>
        Visit(input, schema, "$", changedPaths);

    private static JsonNode? Visit(JsonNode? node, JsonNode? schema, string path, List<string> changed)
    {
        if (node is JsonObject obj)
        {
            if (IsWrapper(obj) && ShouldUnwrap(schema))
            {
                var value = obj[WrapperKey];
                obj.Remove(WrapperKey);
                var array = value as JsonArray ?? new JsonArray(value);
                changed.Add(path);
                // Внутри развёрнутого массива могут быть свои обёртки
                return Visit(array, schema, path, changed);
            }
            foreach (var name in obj.Select(p => p.Key).ToList())
            {
                var child = obj[name];
                var replaced = Visit(child, PropertySchema(schema, name), $"{path}.{name}", changed);
                // Замена через индексатор сохраняет порядок полей; развёрнутый массив уже
                // отцеплен от обёртки (Remove выше), родителя у него нет
                if (!ReferenceEquals(replaced, child)) obj[name] = replaced;
            }
            return obj;
        }
        if (node is JsonArray arr)
        {
            var items = ItemsSchema(schema);
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                var replaced = Visit(child, items, $"{path}[{i}]", changed);
                if (!ReferenceEquals(replaced, child)) arr[i] = replaced;
            }
            return arr;
        }
        return node;
    }

    private static bool IsWrapper(JsonObject obj) => obj.Count == 1 && obj.ContainsKey(WrapperKey);

    // Схема знает тип поля — верим ей; не знает — эвристика «обёртка = массив».
    private static bool ShouldUnwrap(JsonNode? schema)
    {
        var types = Types(schema);
        if (types.Count == 0) return true;
        return types.Contains("array") && !types.Contains("object");
    }

    // Типы узла схемы: type (строка или массив) плюс type ветвей anyOf/oneOf.
    private static HashSet<string> Types(JsonNode? schema)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (schema is not JsonObject s) return result;
        AddTypes(s["type"], result);
        foreach (var branch in Branches(s))
            if (branch is JsonObject b) AddTypes(b["type"], result);
        return result;
    }

    private static void AddTypes(JsonNode? type, HashSet<string> into)
    {
        if (type is JsonValue v && v.TryGetValue<string>(out var single)) into.Add(single);
        else if (type is JsonArray many)
            foreach (var t in many)
                if (t is JsonValue tv && tv.TryGetValue<string>(out var name)) into.Add(name);
    }

    private static IEnumerable<JsonNode?> Branches(JsonObject s) =>
        (s["anyOf"] as JsonArray ?? []).Concat(s["oneOf"] as JsonArray ?? []);

    // Схема свойства: properties[name], иначе additionalProperties-схема, иначе ветвь
    // anyOf/oneOf, которая это свойство знает. null — схема поля неизвестна.
    private static JsonNode? PropertySchema(JsonNode? schema, string name)
    {
        if (schema is not JsonObject s) return null;
        if (s["properties"] is JsonObject props && props[name] is { } prop) return prop;
        if (s["additionalProperties"] is JsonObject extra) return extra;
        foreach (var branch in Branches(s))
            if (PropertySchema(branch, name) is { } found) return found;
        return null;
    }

    private static JsonNode? ItemsSchema(JsonNode? schema)
    {
        if (schema is not JsonObject s) return null;
        if (s["items"] is JsonObject items) return items;
        foreach (var branch in Branches(s))
            if (ItemsSchema(branch) is { } found) return found;
        return null;
    }
}
