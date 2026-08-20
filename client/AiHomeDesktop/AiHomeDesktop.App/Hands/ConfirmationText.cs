using System.Text.Json;
using AiHomeDesktop.App.Execution;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Текст тоста подтверждения. Собирается ИЗ ФАКТИЧЕСКИХ АРГУМЕНТОВ вызова: что открываем,
/// с какими аргументами, какое окно снимаем. Модельного резюме («сейчас я аккуратно открою
/// отчёт») в подтверждении нет никогда — человек должен видеть то, что реально уедет в
/// исполнение, а не пересказ намерения.
///
/// Имя чата присутствует в каждом тосте: тост без чата не отвечает на вопрос «кто просит».
/// </summary>
public static class ConfirmationText
{
    /// <summary>
    /// Приписка внизу тоста. Обещаний безопасности в ней нет — их и не существует:
    /// единственный предохранитель здесь человек, и после первого одобрения он деградирует.
    /// </summary>
    public const string Footer = "После подтверждения действие выполнится на этом компьютере от вашего имени.";

    /// <summary>Собрать тост по команде канала.</summary>
    public static ConfirmationRequest For(DesktopCall call)
    {
        var lines = new List<ConfirmationLine>();
        string title;

        switch (call.Kind)
        {
            case DesktopCallKinds.Open:
                title = "Открыть на этом компьютере";
                var target = Str(call.Args, "target");
                lines.Add(new ConfirmationLine("Что открыть", target ?? "не указано"));
                if (Str(call.Args, "args") is { Length: > 0 } openArgs)
                    lines.Add(new ConfirmationLine("Аргументы", openArgs));
                break;

            case DesktopCallKinds.Screen:
                // Кадр внутри сеанса подтверждения не требует (гейт чтения — сам сеанс), но
                // если сервер всё же попросил — показываем ровно ту область, что снимаем.
                title = "Снять кадр экрана";
                lines.Add(new ConfirmationLine("Что снимаем", ScreenScope(call.Args)));
                break;

            case DesktopCallKinds.Run:
                title = "Выполнить команду на этом компьютере";
                lines.Add(new ConfirmationLine("Команда", Str(call.Args, "command") ?? "не указана"));
                if (Str(call.Args, "cwd") is { Length: > 0 } cwd)
                    lines.Add(new ConfirmationLine("Рабочая папка", cwd));
                break;

            case DesktopCallKinds.Act:
                title = "Действовать в окне на этом компьютере";
                lines.Add(new ConfirmationLine("Шаги", RawArgs(call.Args)));
                break;

            default:
                title = $"Вызов «{call.Kind}» на этом компьютере";
                lines.Add(new ConfirmationLine("Аргументы", RawArgs(call.Args)));
                break;
        }

        // Чат — последней строкой и всегда: он отвечает на вопрос «кто просит».
        lines.Add(new ConfirmationLine("Чат", call.ChatTitle));
        return new ConfirmationRequest(call.CallId, call.ChatTitle, title, lines);
    }

    /// <summary>Человеческое описание области кадра — им же подписывается лента «что ушло в модель».</summary>
    public static string ScreenScope(JsonElement? args)
    {
        var scope = Str(args, "scope") ?? "window";
        return scope switch
        {
            "screen" => Num(args, "screen") is { } n ? $"экран №{n} целиком" : "экран целиком",
            "region" => Region(args) is { } r ? $"область {r}" : "область экрана",
            _ => Str(args, "window") is { Length: > 0 } w ? $"окно «{w}»" : "активное окно"
        };
    }

    private static string? Region(JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } o
            || !o.TryGetProperty("region", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        var x = Num(r, "x"); var y = Num(r, "y");
        var w = Num(r, "width"); var h = Num(r, "height");
        return w is null || h is null ? null : $"{w}×{h} в точке {x ?? 0};{y ?? 0}";
    }

    private static string? Str(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.ToString()
            }
            : null;

    private static double? Num(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } o ? Num(o, name) : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    // Незнакомый вид вызова показываем сырыми аргументами: пересказывать их своими словами —
    // тот же грех, что показывать резюме модели.
    private static string RawArgs(JsonElement? args) =>
        args is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } a
            ? Trim(a.ToString())
            : "нет";

    private static string Trim(string s) => s.Length <= 600 ? s : s[..600] + "…";
}
