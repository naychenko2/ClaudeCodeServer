using System.Text.Json;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Чистый разбор аргументов шага <c>hands_batch</c> (D2f): те же функции, которыми
/// <see cref="HandsBatchTool"/> читает аргументы <c>keyboard_control</c>/<c>mouse_control</c>
/// из шага. Без <c>WindowsToolsBase</c> и WinAPI, поэтому исполняется на Linux
/// (тесты <c>HandsBridge.Policy.Tests</c>): разбор числа, строки, массива и «число как строка»
/// не зависит от платформы.
/// </summary>
internal static class HandsBatchStepArgs
{
    /// <summary>
    /// Свойство объекта по имени (регистронезависимо). <c>false</c> — свойства нет или оно
    /// JSON-<c>null</c>; <paramref name="value"/> тогда <c>default</c>.
    /// </summary>
    public static bool TryGet(JsonElement args, string name, out JsonElement value)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return value.ValueKind != JsonValueKind.Null;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Строковый аргумент: строка — как есть, любое другое значение (число, <c>windowHandle</c>
    /// числом) — его «сырой» текст. <c>null</c> — аргумента нет.
    /// </summary>
    public static string? Str(JsonElement args, string name) =>
        TryGet(args, name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    /// <summary>
    /// Целочисленный аргумент: только JSON-число, влезшее в <c>int32</c>. Нецелое (1.5),
    /// строка и отсутствие — <c>null</c>.
    /// </summary>
    public static int? Int(JsonElement args, string name) =>
        TryGet(args, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>
    /// <c>sequence</c> клавиатуры: строка с JSON-массивом — как есть, сам JSON-массив — его
    /// «сырой» текст. <c>null</c> — аргумента нет.
    /// </summary>
    public static string? Sequence(JsonElement args) =>
        TryGet(args, "sequence", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
}
