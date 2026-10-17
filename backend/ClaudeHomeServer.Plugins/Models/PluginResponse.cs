using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Ответ контроллера плагинов (ADR-031 §2.3, §4, шаг 1.4, C5): DTO без ЗНАЧЕНИЙ секретов.
// Хранилище (installed.json) хранит value локально; наружу — только имя + флаг «заполнен».
// Так GET /api/plugins и все ответы несут `secrets: [{name, set}]`, а не `value`.
public class PluginResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("requested")] public List<string> Requested { get; set; } = [];
    [JsonPropertyName("granted")] public List<string> Granted { get; set; } = [];
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("secrets")] public List<PluginSecretView> Secrets { get; set; } = [];

    // Из записи установки: значения секретов не несутся, только имя + признак «заполнен».
    public static PluginResponse From(PluginInstall record) => new()
    {
        Id = record.Id,
        Version = record.Version,
        Sha256 = record.Sha256,
        Requested = record.Requested,
        Granted = record.Granted,
        Enabled = record.Enabled,
        Source = record.Source,
        Secrets = record.Secrets
            .Select(s => new PluginSecretView { Name = s.Name, Set = s.Set })
            .ToList(),
    };
}

/// <summary>Секрет в ответе: имя (из манифеста) + признак «значение введено» (без самого значения).</summary>
public class PluginSecretView
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("set")] public bool Set { get; set; }
}
