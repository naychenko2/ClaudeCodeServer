using System.Text.Json;

namespace AiHomeDesktop.Windows.Execution;

/// <summary>Прямоугольник области в пикселях виртуального рабочего стола.</summary>
public sealed record RegionRect(int X, int Y, int Width, int Height);

/// <summary>Разобранные аргументы desktop_screen.</summary>
/// <param name="Scope">window (по умолчанию) | screen | region.</param>
/// <param name="Window">Часть заголовка целевого окна; пусто — активное окно.</param>
/// <param name="Screen">Номер экрана для scope=screen, нумерация с единицы.</param>
/// <param name="Region">Область для scope=region.</param>
/// <param name="SnapshotId">Снапшот, к которому привязан кадр (снапшотов эта версия не делает).</param>
public sealed record ScreenRequest(
    string Scope,
    string? Window,
    int? Screen,
    RegionRect? Region,
    string? SnapshotId);

/// <summary>Разобранные аргументы desktop_open.</summary>
public sealed record OpenRequest(string Target, string? Arguments);

/// <summary>
/// Разбор аргументов вызова. Чистые функции без WinAPI: чужой JSON приходит любой формы,
/// и «не разобрали» обязано быть честным исходом с текстом, а не исключением в канале.
/// </summary>
public static class CallArgs
{
    public static readonly string[] Scopes = ["window", "screen", "region"];

    public static bool TryScreen(JsonElement? args, out ScreenRequest request, out string? error)
    {
        request = new ScreenRequest("window", null, null, null, null);
        error = null;

        var scope = (Str(args, "scope") ?? "window").Trim().ToLowerInvariant();
        if (scope.Length == 0) scope = "window";
        if (!Scopes.Contains(scope))
        {
            error = $"Неизвестная область съёмки «{scope}»: бывает window, screen или region.";
            return false;
        }

        RegionRect? region = null;
        if (scope == "region")
        {
            var obj = Obj(args, "region");
            var width = Int(obj, "width");
            var height = Int(obj, "height");
            if (obj is null || width is null or <= 0 || height is null or <= 0)
            {
                error = "Для scope=region нужен region с положительными width и height "
                        + "в пикселях экрана (плюс x и y — левый верхний угол).";
                return false;
            }

            region = new RegionRect(Int(obj, "x") ?? 0, Int(obj, "y") ?? 0, width.Value, height.Value);
        }

        var screen = scope == "screen" ? Int(args, "screen") : null;
        if (screen is <= 0)
        {
            error = "Номер экрана считается с единицы — список экранов отдаёт desktop_devices.";
            return false;
        }

        request = new ScreenRequest(scope, Trimmed(Str(args, "window")), screen, region, Trimmed(Str(args, "snapshotId")));
        return true;
    }

    public static bool TryOpen(JsonElement? args, out OpenRequest request, out string? error)
    {
        request = new OpenRequest("", null);
        var target = Trimmed(Str(args, "target"));
        if (target is null)
        {
            error = "Не сказано, что открывать: нужен target — имя приложения из списка, путь к файлу или ссылка.";
            return false;
        }

        error = null;
        request = new OpenRequest(target, Trimmed(Str(args, "args")));
        return true;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Str(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Obj(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>Число из JSON: модель присылает и 100, и 100.0 — обе формы читаем одинаково.</summary>
    private static int? Int(JsonElement? args, string name) =>
        args is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? (int)Math.Round(number)
            : null;
}
