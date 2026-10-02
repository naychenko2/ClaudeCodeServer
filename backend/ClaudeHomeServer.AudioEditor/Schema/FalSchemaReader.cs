using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Catalog;

namespace ClaudeHomeServer.Services.AudioEditor.Schema;

// Схема входа эндпоинта fal из его OpenAPI 3.0 (Platform API, expand=openapi-3.0): тело POST-запроса
// очереди → поля автоформы. $ref, allOf из одной ссылки и anyOf с null разворачиваются; anyOf из разных
// типов — Any. Вложенные объекты — до MaxDepth уровней, глубже — объект без проверки ключей
internal static class FalSchemaReader
{
    private const int MaxDepth = 3;

    // Поля входа или null, если в OpenAPI нет схемы тела запроса
    public static IReadOnlyList<AudioParamField>? Read(JsonElement openapi)
    {
        if (InputSchema(openapi) is not { } input) return null;
        return ObjectFields(openapi, input, 0) ?? [];
    }

    // Поля, которыми владеют общие поля запроса и каталог, плюс поля-ссылки верхнего уровня схемы: в params
    // их не пускаем, в форме не рисуем
    public static IReadOnlyList<string> Reserved(AudioCatalog.FalFields f, string? linkTo = null,
        IReadOnlyList<AudioParamField>? fields = null)
    {
        var keys = new[] { f.Text, f.Prompt, f.Lyrics, f.Language, f.Duration, f.Start, f.End, f.Source, f.Reference, f.Seed, linkTo }
            .Where(k => k is not null).Select(k => k!)
            .Concat(f.Fixed?.Keys ?? [])
            .Concat(fields?.Where(x => IsLink(x.Key)).Select(x => x.Key) ?? []);
        return [.. keys.Distinct(StringComparer.Ordinal)];
    }

    // Поле, через которое fal пойдёт по адресу: вебхук, колбэк, любая ссылка. Адреса в запрос кладём только
    // мы сами из входов (Source/Reference каталога) — из params ни один не принимаем
    public static bool IsLink(string key)
    {
        var k = key.ToLowerInvariant();
        return k.Contains("webhook") || k.StartsWith("callback", StringComparison.Ordinal)
            || LinkNouns.Any(n => k == n || k.EndsWith("_" + n, StringComparison.Ordinal));
    }

    // Существительные адреса — сами по себе и хвостом через «_» (redirect_uri, upload_endpoints)
    private static readonly string[] LinkNouns = ["url", "urls", "uri", "uris", "endpoint", "endpoints"];

    private static JsonElement? InputSchema(JsonElement openapi)
    {
        if (openapi.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
            foreach (var path in paths.EnumerateObject())
                if (path.Value.TryGetProperty("post", out var post)
                    && post.TryGetProperty("requestBody", out var body)
                    && body.TryGetProperty("content", out var content)
                    && content.TryGetProperty("application/json", out var json)
                    && json.TryGetProperty("schema", out var schema))
                    return Resolve(openapi, schema);

        // Запасной путь: компонент «…Input»
        if (openapi.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemas) && schemas.ValueKind == JsonValueKind.Object)
            foreach (var s in schemas.EnumerateObject())
                if (s.Name.EndsWith("Input", StringComparison.Ordinal))
                    return s.Value;
        return null;
    }

    private static IReadOnlyList<AudioParamField>? ObjectFields(JsonElement root, JsonElement schema, int depth)
    {
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;
        // Вложенные поля-ссылки выкидываем сразу: валидатор откажет им как неизвестным. Верхний уровень
        // оставляем — его ссылки уходят в Reserved с внятной причиной отказа
        var fields = props.EnumerateObject()
            .Where(p => depth == 0 || !IsLink(p.Name))
            .Select(p => Field(root, p.Name, p.Value, depth)).ToList();

        // Порядок полей — как в плейграунде fal
        if (schema.TryGetProperty("x-fal-order-properties", out var order) && order.ValueKind == JsonValueKind.Array)
        {
            var rank = order.EnumerateArray().Select((e, i) => (Key: e.GetString() ?? "", i))
                .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
            fields = [.. fields.OrderBy(f => rank.TryGetValue(f.Key, out var r) ? r : int.MaxValue)];
        }
        return fields;
    }

    private static AudioParamField Field(JsonElement root, string key, JsonElement raw, int depth)
    {
        var title = Str(raw, "title");
        var description = Str(raw, "description");
        var defaultValue = raw.TryGetProperty("default", out var d) ? JsonNode.Parse(d.GetRawText()) : null;

        var schema = Resolve(root, raw);
        var nullable = false;
        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            var variants = anyOf.EnumerateArray().Select(v => Resolve(root, v)).ToList();
            var nonNull = variants.Where(v => Str(v, "type") != "null").ToList();
            nullable = nonNull.Count < variants.Count;
            if (nonNull.Count != 1)
                return new AudioParamField(key, AudioParamTypes.Any, title, description, defaultValue, Nullable: nullable);
            schema = nonNull[0];
        }

        title ??= Str(schema, "title");
        description ??= Str(schema, "description");
        if (defaultValue is null && schema.TryGetProperty("default", out var sd)) defaultValue = JsonNode.Parse(sd.GetRawText());

        IReadOnlyList<JsonNode>? values = schema.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
            ? [.. e.EnumerateArray().Select(v => JsonNode.Parse(v.GetRawText())).Where(v => v is not null).Select(v => v!)]
            : null;
        var type = Str(schema, "type") ?? (values is not null ? AudioParamTypes.String
            : schema.TryGetProperty("properties", out _) ? AudioParamTypes.Object : AudioParamTypes.Any);

        return type switch
        {
            "number" or "integer" => new AudioParamField(key, type, title, description, defaultValue,
                Min: Num(schema, "minimum") ?? Num(schema, "exclusiveMinimum"),
                Max: Num(schema, "maximum") ?? Num(schema, "exclusiveMaximum"),
                Enum: values, Nullable: nullable),
            "string" => new AudioParamField(key, AudioParamTypes.String, title, description, defaultValue,
                MaxLength: (int?)Num(schema, "maxLength"), Enum: values, Nullable: nullable, Format: Str(schema, "format")),
            "boolean" => new AudioParamField(key, AudioParamTypes.Boolean, title, description, defaultValue, Nullable: nullable),
            "array" => new AudioParamField(key, AudioParamTypes.Array, title, description, defaultValue, Nullable: nullable,
                Items: schema.TryGetProperty("items", out var items) && depth < MaxDepth
                    ? Field(root, "item", items, depth + 1)
                    : null),
            "object" => new AudioParamField(key, AudioParamTypes.Object, title, description, defaultValue, Nullable: nullable,
                Fields: depth < MaxDepth ? ObjectFields(root, schema, depth + 1) : null),
            _ => new AudioParamField(key, AudioParamTypes.Any, title, description, defaultValue, Nullable: nullable),
        };
    }

    // $ref на components и allOf из одной схемы (так pydantic оформляет поле-ссылку с описанием)
    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        for (var hops = 0; hops < 8; hops++)
        {
            if (Str(schema, "$ref") is { } reference && Pointer(root, reference) is { } target)
                schema = target;
            else if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array
                     && allOf.GetArrayLength() == 1)
                schema = allOf[0];
            else
                break;
        }
        return schema;
    }

    private static JsonElement? Pointer(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var node = root;
        foreach (var part in reference[2..].Split('/'))
        {
            var name = part.Replace("~1", "/").Replace("~0", "~");
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)) return null;
        }
        return node;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
