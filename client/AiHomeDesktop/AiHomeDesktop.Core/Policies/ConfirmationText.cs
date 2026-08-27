using System.Text.Json;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Core.Policies;

/// <summary>Что показать человеку в тосте подтверждения.</summary>
/// <param name="CallId">Вызов, о котором спрашиваем: по нему тост гасится, если вызов отменили.</param>
/// <param name="Title">Заголовок — что именно просят сделать.</param>
/// <param name="ChatLine">Имя чата. Присутствует ВСЕГДА (ADR-008: «имя чата в каждом тосте»).</param>
/// <param name="Lines">Подробности, собранные из фактических аргументов вызова.</param>
public sealed record ConfirmationPrompt(string CallId, string Title, string ChatLine, IReadOnlyList<string> Lines)
{
    public string Text => string.Join("\n", new[] { Title, ChatLine }.Concat(Lines));
}

/// <summary>
/// Текст подтверждения строит КЛИЕНТ из фактических аргументов вызова (ADR-008, §8):
/// заголовок целевого окна, литеральный текст, командная строка, cwd.
///
/// Модельное резюме в подтверждении не показывается НИКОГДА — поэтому аргументы читаются
/// по белому списку полей, а не печатаются целиком: любое «зачем» и «почему», дописанное
/// моделью в args, до человека не доезжает. Человек подтверждает действие, а не рассказ
/// о действии.
/// </summary>
public static class ConfirmationText
{
    public static ConfirmationPrompt For(DesktopCallCommand command) =>
        For(command.CallId, command.Kind, command.Args, command.ChatName);

    public static ConfirmationPrompt For(string callId, string kind, JsonElement? args, string? chatName)
    {
        var chat = string.IsNullOrWhiteSpace(chatName)
            ? "Чат: без названия"
            : $"Чат: «{chatName!.Trim()}»";

        return kind switch
        {
            DesktopCallKinds.Screen => new ConfirmationPrompt(callId, "Снять кадр экрана", chat, ScreenLines(args)),
            DesktopCallKinds.Open => new ConfirmationPrompt(callId, "Открыть на этом компьютере", chat, OpenLines(args)),
            DesktopCallKinds.Ui => new ConfirmationPrompt(callId, "Снять снапшот окна", chat, WindowLine(args)),
            DesktopCallKinds.Act => new ConfirmationPrompt(callId, "Действия в окне", chat, ActLines(args)),
            DesktopCallKinds.Run => new ConfirmationPrompt(callId, "Выполнить команду", chat, RunLines(args)),
            _ => new ConfirmationPrompt(callId, $"Вызов «{kind}»", chat, [])
        };
    }

    private static List<string> ScreenLines(JsonElement? args)
    {
        var lines = new List<string>();
        var scope = Str(args, "scope") ?? "window";
        switch (scope)
        {
            case "screen":
                var screen = Num(args, "screen");
                lines.Add(screen is null ? "Экран целиком" : $"Экран {screen} целиком");
                break;
            case "region":
                var region = Obj(args, "region");
                var w = Num(region, "width");
                var h = Num(region, "height");
                var x = Num(region, "x");
                var y = Num(region, "y");
                lines.Add(w is null || h is null
                    ? "Область экрана"
                    : $"Область {w}×{h} в точке ({x ?? 0}, {y ?? 0})");
                break;
            default:
                var window = Str(args, "window");
                lines.Add(string.IsNullOrWhiteSpace(window)
                    ? "Активное окно"
                    : $"Окно «{window}»");
                break;
        }
        return lines;
    }

    private static List<string> OpenLines(JsonElement? args)
    {
        var lines = new List<string> { $"Цель: {Str(args, "target") ?? "не указана"}" };
        var extra = Str(args, "args");
        if (!string.IsNullOrWhiteSpace(extra)) lines.Add($"Аргументы: {extra}");
        return lines;
    }

    private static List<string> WindowLine(JsonElement? args)
    {
        var window = Str(args, "window");
        return [string.IsNullOrWhiteSpace(window) ? "Активное окно" : $"Окно «{window}»"];
    }

    private static List<string> ActLines(JsonElement? args)
    {
        // Шаги печатаются по фактическому содержимому: тип, адрес элемента и ЛИТЕРАЛЬНЫЙ
        // вводимый текст. Без литерала человек подтверждал бы «ввод текста» вслепую.
        var lines = new List<string>();
        var steps = Arr(args, "steps");
        if (steps is null) return lines;

        var index = 0;
        foreach (var step in steps.Value.EnumerateArray())
        {
            if (++index > DesktopProtocol.MaxBatchSteps) break;
            var type = Str(step, "type") ?? "шаг";
            var target = Str(step, "ref") ?? Str(step, "target");
            var text = Str(step, "text");
            var suffix = text is null ? "" : $" — «{text}»";
            lines.Add($"{index}. {type}{(target is null ? "" : $" {target}")}{suffix}");
        }
        return lines;
    }

    private static List<string> RunLines(JsonElement? args)
    {
        var lines = new List<string> { $"Команда: {Str(args, "command") ?? "не указана"}" };
        var cwd = Str(args, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd)) lines.Add($"Рабочая папка: {cwd}");
        return lines;
    }

    // --- аккуратное чтение args: чужой JSON приходит любой формы ---

    private static string? Str(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Str(JsonElement element, string name) => Str((JsonElement?)element, name);

    private static double? Num(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static JsonElement? Obj(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? Arr(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : null;
}
