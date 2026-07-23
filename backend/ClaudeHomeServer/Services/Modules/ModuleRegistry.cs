using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Реестр внешних модулей (контракт docs/module-platform-integration-contract.md, ТЗ R1).
/// На старте читает манифесты module.json из каталога модулей (Modules:Path, дефолт
/// {data}/modules/*/module.json) и явного списка путей Modules:Manifests. Ядро обязано
/// стартовать при любом содержимом манифестов: битый JSON / несовместимый мажор /
/// невалидные поля → модуль пропускается с предупреждением в лог.
/// Hot-plug вне scope v1 — состав фиксируется до рестарта.
/// </summary>
public sealed partial class ModuleRegistry
{
    // Мажор контракта, который реализует это ядро (§8: несовместимый мажор → пропуск)
    public const int SupportedSchemaMajor = 1;
    public const int SupportedSchemaMinor = 0;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,63}$")]
    private static partial Regex SlugRegex();
    [GeneratedRegex(@"^(\d+)\.(\d+)$")]
    private static partial Regex SchemaVersionRegex();

    private readonly List<LoadedModule> _modules = [];
    private readonly ILogger<ModuleRegistry> _log;

    public ModuleRegistry(IConfiguration config, ILogger<ModuleRegistry> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        var modulesDir = config["Modules:Path"];
        if (string.IsNullOrWhiteSpace(modulesDir))
            modulesDir = Path.Combine(dataDir, "modules");

        var manifestPaths = new List<string>();
        if (Directory.Exists(modulesDir))
            foreach (var dir in Directory.EnumerateDirectories(modulesDir))
            {
                var manifest = Path.Combine(dir, "module.json");
                if (File.Exists(manifest)) manifestPaths.Add(manifest);
            }

        // Явные пути манифестов из конфига — для модулей, живущих вне общего каталога
        foreach (var p in config.GetSection("Modules:Manifests").Get<string[]>() ?? [])
            if (!string.IsNullOrWhiteSpace(p))
                manifestPaths.Add(Path.GetFullPath(p));

        foreach (var path in manifestPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            TryLoad(path);

        _log.LogInformation("Реестр модулей: загружено {Count} (каталог {Dir})", _modules.Count, modulesDir);
    }

    /// <summary>Все успешно загруженные модули (без учёта фич-флагов — это R8, слой выше).</summary>
    public IReadOnlyList<LoadedModule> All => _modules;

    public LoadedModule? Get(string id) =>
        _modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    private void TryLoad(string manifestPath)
    {
        ModuleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Манифест модуля {Path} пропущен: не читается ({Error})", manifestPath, ex.Message);
            return;
        }
        if (manifest is null)
        {
            _log.LogWarning("Манифест модуля {Path} пропущен: пустой документ", manifestPath);
            return;
        }

        var error = Validate(manifest);
        if (error is not null)
        {
            _log.LogWarning("Модуль из {Path} пропущен: {Error}", manifestPath, error);
            return;
        }

        if (_modules.Any(m => m.Id == manifest.Id))
        {
            _log.LogWarning("Модуль «{Id}» из {Path} пропущен: id уже занят другим манифестом", manifest.Id, manifestPath);
            return;
        }

        var minor = int.Parse(SchemaVersionRegex().Match(manifest.SchemaVersion).Groups[2].Value);
        if (minor > SupportedSchemaMinor)
            _log.LogWarning("Модуль «{Id}»: schemaVersion {Version} новее поддерживаемой {Major}.{Minor} — работа на общих полях",
                manifest.Id, manifest.SchemaVersion, SupportedSchemaMajor, SupportedSchemaMinor);

        _modules.Add(new LoadedModule(manifest, Path.GetDirectoryName(manifestPath)!));
        _log.LogInformation("Модуль загружен: «{Id}» v{Version} ({Name}) → {BaseUrl}",
            manifest.Id, manifest.Version, manifest.DisplayName, manifest.Backend!.BaseUrl);
    }

    // Валидация обязательных полей и якорей мажора 1 (§2, §8). null — манифест валиден.
    private static string? Validate(ModuleManifest m)
    {
        var sv = SchemaVersionRegex().Match(m.SchemaVersion ?? "");
        if (!sv.Success) return $"schemaVersion «{m.SchemaVersion}» не в формате мажор.минор";
        if (int.Parse(sv.Groups[1].Value) != SupportedSchemaMajor)
            return $"несовместимый мажор schemaVersion {m.SchemaVersion} (поддерживается {SupportedSchemaMajor}.x)";

        if (string.IsNullOrWhiteSpace(m.Id)) return "отсутствует id";
        if (!SlugRegex().IsMatch(m.Id)) return $"id «{m.Id}» не slug (^[a-z][a-z0-9-]+$)";
        if (string.IsNullOrWhiteSpace(m.Version)) return "отсутствует version";
        if (string.IsNullOrWhiteSpace(m.DisplayName)) return "отсутствует displayName";
        if (m.Backend is null) return "отсутствует секция backend";
        if (string.IsNullOrWhiteSpace(m.Backend.BaseUrl)
            || !Uri.TryCreate(m.Backend.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            return $"backend.baseUrl «{m.Backend.BaseUrl}» не абсолютный http(s)-адрес";
        if (string.IsNullOrWhiteSpace(m.Backend.HealthPath) || !m.Backend.HealthPath.StartsWith('/'))
            return $"backend.healthPath «{m.Backend.HealthPath}» должен начинаться с /";

        // Якорь мажора 1 (§8) и защита gateway: чужой префикс дал бы модулю перехват путей ядра
        var expectedPrefix = $"/api/modules/{m.Id}";
        if (!string.Equals(m.Backend.RoutePrefix, expectedPrefix, StringComparison.Ordinal))
            return $"backend.routePrefix «{m.Backend.RoutePrefix}» не равен якорю «{expectedPrefix}»";

        foreach (var mcp in m.Mcp ?? [])
        {
            if (string.IsNullOrWhiteSpace(mcp.Key) || !SlugRegex().IsMatch(mcp.Key))
                return $"mcp.key «{mcp.Key}» не slug";
            if (string.IsNullOrWhiteSpace(mcp.Command)) return $"mcp «{mcp.Key}»: отсутствует command";
        }

        foreach (var scope in m.Scopes ?? [])
            if (!Regex.IsMatch(scope, "^[a-z][a-z0-9._-]{1,63}$"))
                return $"scope «{scope}» не соответствует формату §5.1";

        return null;
    }
}
