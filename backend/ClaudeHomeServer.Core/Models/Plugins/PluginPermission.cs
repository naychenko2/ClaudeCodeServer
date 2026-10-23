using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Известные права пакета (ADR-031 §4): крупные, называют поверхности хоста.
/// <c>net:{exact-host}</c> и <c>secret:{name}</c> — с параметром; <c>notes:read</c> и
/// <c>tasks:read</c> — точные. Вынесено из <see cref="PluginManifest"/> в отдельный файл:
/// грамматика точного хоста (<see cref="IsHost"/>) и разбор (<see cref="Parse"/>) —
/// самостоятельная логика, не модель манифеста.
/// </summary>
public static class PluginPermission
{
    public const string NotesRead = "notes:read";
    public const string TasksRead = "tasks:read";
    // Право хостового хранилища панели (шаг B1): сервер знает право, обработчик операций
    // storage.get/set/remove/keys живёт на фронте (шаг F1) — в мосте его нет.
    public const string Storage = "storage";

    // Грамматика точного хоста (rework C3, minor): [a-z0-9.-]+ и опционально :port (1..5 цифр).
    // До фикса IsKnown принимал ЛЮБОЙ непустой параметр net:* — «принимает что угодно».
    private static readonly Regex HostPattern =
        new("^[a-z0-9.-]+(:[0-9]{1,5})?$", RegexOptions.Compiled);

    /// <summary>
    /// Право из известного набора? <c>net:*</c> — только при корректном хосте
    /// (<see cref="IsHost"/>), <c>secret:*</c> — при непустом имени. Всё остальное —
    /// неизвестное право.
    /// </summary>
    public static bool IsKnown(string? permission)
    {
        if (string.IsNullOrEmpty(permission)) return false;
        if (permission is NotesRead or TasksRead or Storage) return true;
        if (permission.StartsWith("net:", StringComparison.Ordinal))
            return IsHost(permission["net:".Length..]);
        if (permission.StartsWith("secret:", StringComparison.Ordinal))
            return permission["secret:".Length..].Length > 0;
        return false;
    }

    /// <summary>
    /// Точный хост <c>net:{host}</c> (опционально <c>:port</c>)? Грамматика: <c>[a-z0-9.-]+</c>
    /// для host, порт — <c>1..65535</c>. <c>net:bad/host</c> → false (слэш вне класса).
    /// </summary>
    public static bool IsHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (!HostPattern.IsMatch(host)) return false;
        // Порт (если есть) — в диапазоне 1..65535: regex допускает до 5 цифр, сверяем значение.
        var portIdx = host.LastIndexOf(':');
        if (portIdx >= 0)
        {
            var port = int.Parse(host[(portIdx + 1)..]);
            if (port < 1 || port > 65535) return false;
        }
        return true;
    }

    /// <summary>
    /// Разобрать право: (IsValid, Permission, Reason). <c>net:bad/host</c> → invalid;
    /// <c>notes:read</c>/<c>tasks:read</c>/<c>storage</c>/<c>net:{host}</c>/<c>secret:{name}</c> → valid.
    /// Набор разбираемых совпадает с <see cref="IsKnown"/>: точные права — дословно,
    /// параметрические — по той же грамматике.
    /// </summary>
    public static (bool IsValid, string? Permission, string? Reason) Parse(string? permission)
    {
        if (string.IsNullOrEmpty(permission))
            return (false, null, "Пустое право");
        if (permission is NotesRead or TasksRead or Storage)
            return (true, permission, null);
        if (permission.StartsWith("net:", StringComparison.Ordinal))
        {
            var host = permission["net:".Length..];
            return IsHost(host)
                ? (true, permission, null)
                : (false, null, $"Некорректный хост: {host}");
        }
        if (permission.StartsWith("secret:", StringComparison.Ordinal))
        {
            var name = permission["secret:".Length..];
            return name.Length > 0
                ? (true, permission, null)
                : (false, null, "Пустое имя секрета");
        }
        return (false, null, $"Неизвестное право: {permission}");
    }
}
