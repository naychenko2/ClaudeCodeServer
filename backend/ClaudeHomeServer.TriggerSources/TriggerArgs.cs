using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Чтение типизированных значений из гибкого Args-мешка триггера (Dictionary<string,JsonElement>).
// Args — JSON-объект произвольной формы; ключи зависят от Trigger.Type (см. комментарий к AutomationTrigger).
public static class TriggerArgs
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty =
        new Dictionary<string, JsonElement>();

    public static IReadOnlyDictionary<string, JsonElement> Of(AutomationTrigger? trigger) => trigger?.Args ?? Empty;

    public static string? GetString(this IReadOnlyDictionary<string, JsonElement> a, string key) =>
        a.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? GetInt(this IReadOnlyDictionary<string, JsonElement> a, string key) =>
        a.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static List<string>? GetStringList(this IReadOnlyDictionary<string, JsonElement> a, string key)
    {
        if (!a.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
        return list;
    }

    public static List<int>? GetIntList(this IReadOnlyDictionary<string, JsonElement> a, string key)
    {
        if (!a.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) list.Add(n);
        return list;
    }

    // Вложенный объект (напр. schedule у Timer) как словарь. null — нет такого объекта.
    public static IReadOnlyDictionary<string, JsonElement>? GetObject(
        this IReadOnlyDictionary<string, JsonElement> a, string key)
    {
        if (!a.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in v.EnumerateObject()) dict[p.Name] = p.Value;
        return dict;
    }
}
