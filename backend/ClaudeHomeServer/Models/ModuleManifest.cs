using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Манифест внешнего модуля (module.json) по контракту
/// docs/module-platform-integration-contract.md §2. Читается ModuleRegistry на старте.
/// Поля контракта мажора 1; неизвестные поля манифеста игнорируются (forward-compat).
/// </summary>
public sealed class ModuleManifest
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("backend")] public ModuleBackend? Backend { get; set; }
    [JsonPropertyName("frontend")] public ModuleFrontend? Frontend { get; set; }
    [JsonPropertyName("mcp")] public List<ModuleMcp>? Mcp { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    [JsonPropertyName("auth")] public ModuleAuth? Auth { get; set; }
}

public sealed class ModuleBackend
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("healthPath")] public string HealthPath { get; set; } = "/health";
    [JsonPropertyName("routePrefix")] public string RoutePrefix { get; set; } = "";
}

public sealed class ModuleFrontend
{
    [JsonPropertyName("remoteEntry")] public string? RemoteEntry { get; set; }
    [JsonPropertyName("exposedModule")] public string ExposedModule { get; set; } = "./Tab";
    [JsonPropertyName("tab")] public ModuleTab? Tab { get; set; }
}

public sealed class ModuleTab
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; } = 100;
}

public sealed class ModuleMcp
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public List<string>? Args { get; set; }
}

public sealed class ModuleAuth
{
    [JsonPropertyName("audience")] public string? Audience { get; set; }
}

/// <summary>
/// Загруженный модуль: валидный манифест + каталог, откуда он прочитан
/// (базовый путь для резолва относительных args MCP-серверов).
/// </summary>
public sealed record LoadedModule(ModuleManifest Manifest, string ModuleDir)
{
    public string Id => Manifest.Id;
    /// <summary>aud модульного токена: из манифеста либо константа-шаблон контракта §5.1.</summary>
    public string Audience => Manifest.Auth?.Audience ?? $"aihome-module:{Manifest.Id}";
    /// <summary>scope-строка токена (RFC 8693): все возможности модуля через пробел.</summary>
    public string ScopeString => string.Join(' ', Manifest.Scopes ?? []);
    /// <summary>Ключ фич-флага видимости модуля (R8).</summary>
    public string FeatureFlagKey => $"module-{Manifest.Id}";
}
