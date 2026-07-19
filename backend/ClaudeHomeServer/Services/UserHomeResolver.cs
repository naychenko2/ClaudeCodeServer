using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Единая точка резолва ДОМАШНЕЙ ПАПКИ пользователя — базы, внутри которой лежат его проекты,
// папка чатов вне проекта (Chats) и корни файловых триггеров проактивности.
//
// По умолчанию это {база по среде}/{username}: база — DefaultProjectsPath у local-пользователей
// и Sandbox:ProjectsRoot у container-пользователей (в песочницу монтируется только он).
//
// Однопользовательскому инстансу прослойка {username} только мешает: чтобы работать прямо
// в общей папке (например C:\GIT), путь переопределяется машинно-специфичным конфигом
// (appsettings.Local.json, не коммитится):
//
//   "Projects": { "UserHomeOverrides": { "admin": "C:\\GIT" } }
//
// Ключ — логин пользователя (регистр не важен), значение — АБСОЛЮТНЫЙ путь.
// Для container-пользователей override принимается ТОЛЬКО строго ВНУТРИ Sandbox:ProjectsRoot:
// путь вне песочницы процессы пользователя всё равно не увидят, а сам корень песочницы —
// общий для всех изолированных пользователей (дом в нём снял бы границу между ними).
// Непригодный override игнорируется, работает обычная схема {база}/{username}.
public sealed class UserHomeResolver
{
    private readonly AppSettingsService _appSettings;
    private readonly Execution.SandboxManager? _sandbox;
    private readonly ILogger<UserHomeResolver>? _log;
    private readonly Dictionary<string, string> _overrides;

    public UserHomeResolver(IConfiguration? config, AppSettingsService appSettings,
        Execution.SandboxManager? sandbox = null, ILogger<UserHomeResolver>? log = null)
    {
        _appSettings = appSettings;
        _sandbox = sandbox;
        _log = log;
        _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in config?.GetSection("Projects:UserHomeOverrides").GetChildren() ?? [])
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                _overrides[item.Key] = item.Value;
    }

    // Резолвер без override — единый фолбэк для ручного создания сервисов (тесты).
    // В приложении все потребители получают общий инстанс из DI, с override'ами из конфига.
    public static UserHomeResolver WithoutOverrides(AppSettingsService appSettings,
        Execution.SandboxManager? sandbox = null) => new(null, appSettings, sandbox);

    public string? Resolve(User? user) =>
        user is null ? null : Resolve(user.Username, user.ExecutionEnvironment);

    public string? Resolve(string? username, string? executionEnvironment)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        var isContainer = executionEnvironment == ExecutionEnvironments.Container;
        var basePath = isContainer ? _sandbox?.Options.ProjectsRoot : _appSettings.Get().DefaultProjectsPath;
        if (string.IsNullOrWhiteSpace(basePath)) return null;

        if (_overrides.TryGetValue(username, out var custom))
        {
            var raw = custom.Trim();
            // Относительный путь резолвился бы от рабочей папки процесса (у службы и IDE она
            // разная) — молча уехавшая домашняя папка хуже явного игнора
            if (!Path.IsPathFullyQualified(raw))
                _log?.LogWarning(
                    "Projects:UserHomeOverrides для «{User}» ({Path}) не абсолютный путь — игнорируем",
                    username, raw);
            else if (!isContainer)
                return Path.GetFullPath(raw);
            else if (IsStrictlyInside(Path.GetFullPath(raw), basePath))
                return Path.GetFullPath(raw);
            else
                _log?.LogWarning(
                    "Projects:UserHomeOverrides для «{User}» ({Path}) вне Sandbox:ProjectsRoot "
                    + "(или совпадает с ним) — игнорируем",
                    username, raw);
        }

        return Path.Combine(basePath, username);
    }

    // Сообщение о ненастроенной базе — тексты исторические, зависят от среды пользователя
    public static string NotConfiguredMessage(string? executionEnvironment) =>
        executionEnvironment == ExecutionEnvironments.Container
            ? "Песочница не настроена: задайте Sandbox:ProjectsRoot в appsettings.Local.json"
            : "Не задана папка проектов по умолчанию";

    // Путь лежит внутри корня (или совпадает с ним). Сравнение — по нормализованным путям и
    // обязательно с разделителем: иначе «C:\Sandbox2» прошёл бы как вложенный в «C:\Sandbox».
    // Общая проверка вложенности для всех guard'ов путей (песочница, traversal).
    public static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseDir = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(baseDir, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Строго ВНУТРИ: сам корень не подходит (общая для всех папка не может быть домом одного)
    public static bool IsStrictlyInside(string path, string root) =>
        IsInside(path, root)
        && !Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
}
