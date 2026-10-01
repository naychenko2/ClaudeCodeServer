using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.AudioEditor.Schema;

// Проверка params по схеме модели (ADR-021 §2/§5): в поставщика уходят только ключи из схемы. Отказ —
// текст для человека и агента с именем поля: «неизвестный параметр», «задаётся общим полем», «пока не
// передаётся», тип и границы. null — params годятся
public static class AudioParamValidator
{
    public static string? Validate(AudioParamSchema schema, JsonObject? parameters)
    {
        if (parameters is null || parameters.Count == 0) return null;
        foreach (var (key, value) in parameters)
        {
            if (schema.Reserved.Contains(key, StringComparer.Ordinal))
                return $"Параметр «{key}» задаётся общим полем запроса, а не в params";
            if (schema.Fields.FirstOrDefault(f => f.Key == key) is not { } field)
                return $"Неизвестный параметр «{key}» у модели {schema.Model}";
            if (!field.Passed)
                return $"Параметр «{key}» пока не передаётся в модель {schema.Model}"
                       + (field.NotPassed is { Length: > 0 } why ? ": " + why : "");
            if (Check(field, value, key) is { } error) return error;
        }
        return null;
    }

    private static string? Check(AudioParamField field, JsonNode? value, string path)
    {
        if (value is null)
            return field.Nullable ? null : $"Параметр «{path}» не может быть пустым";

        switch (field.Type)
        {
            case AudioParamTypes.Any:
                return null;
            case AudioParamTypes.Boolean:
                return Kind(value) is JsonValueKind.True or JsonValueKind.False ? null : $"Параметр «{path}» — да или нет";
            case AudioParamTypes.Number:
            case AudioParamTypes.Integer:
            {
                // Через текст JSON: значение из кода (int, decimal) TryGetValue<double> не отдаёт
                if (Kind(value) != JsonValueKind.Number
                    || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return $"Параметр «{path}» — число";
                if (field.Type == AudioParamTypes.Integer && Math.Abs(number % 1) > 0)
                    return $"Параметр «{path}» — целое число";
                if (field.Min is { } min && number < min || field.Max is { } max && number > max)
                    return $"Параметр «{path}» — от {Num(field.Min)} до {Num(field.Max)}";
                return InEnum(field, value, path);
            }
            case AudioParamTypes.String:
            {
                if (Kind(value) != JsonValueKind.String) return $"Параметр «{path}» — строка";
                if (field.MaxLength is { } maxLength && value.GetValue<string>().Length > maxLength)
                    return $"Параметр «{path}» — не длиннее {maxLength} символов";
                return InEnum(field, value, path);
            }
            case AudioParamTypes.Array:
            {
                if (value is not JsonArray array) return $"Параметр «{path}» — список";
                if (field.Items is null) return null;
                for (var i = 0; i < array.Count; i++)
                    if (Check(field.Items, array[i], $"{path}[{i}]") is { } error) return error;
                return null;
            }
            case AudioParamTypes.Object:
            {
                if (value is not JsonObject obj) return $"Параметр «{path}» — объект";
                if (field.Fields is null) return null;
                foreach (var (key, inner) in obj)
                {
                    if (field.Fields.FirstOrDefault(f => f.Key == key) is not { } innerField)
                        return $"Неизвестный параметр «{path}.{key}»";
                    if (Check(innerField, inner, $"{path}.{key}") is { } error) return error;
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static string? InEnum(AudioParamField field, JsonNode value, string path) =>
        field.Enum is not { Count: > 0 } values || values.Any(v => JsonNode.DeepEquals(v, value))
            ? null
            : $"Параметр «{path}» — одно из: {string.Join(", ", values.Select(v => v.ToJsonString().Trim('"')))}";

    private static JsonValueKind Kind(JsonNode node) => node is JsonValue v ? v.GetValueKind() : node.GetValueKind();

    private static string Num(double? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "…";
}
