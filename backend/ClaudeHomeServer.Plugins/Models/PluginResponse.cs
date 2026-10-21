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
    [JsonPropertyName("contributes")] public PluginContributesView Contributes { get; set; } = new();

    // Из записи установки: значения секретов не несутся, только имя + признак «заполнен»
    // + время последнего изменения (шаг 3.3: {name, set, updatedAt}).
    // Contributes — из манифеста установленной версии (panels: {id, title, entry});
    // entry — путь относительно корня пакета (контракт assets, ADR-031 §3.1).
    public static PluginResponse From(PluginInstall record, PluginManifest? manifest) => new()
    {
        Id = record.Id,
        Version = record.Version,
        Sha256 = record.Sha256,
        Requested = record.Requested,
        Granted = record.Granted,
        Enabled = record.Enabled,
        Source = record.Source,
        Secrets = record.Secrets
            .Select(s => new PluginSecretView { Name = s.Name, Set = s.Set, UpdatedAt = s.UpdatedAt })
            .ToList(),
        Contributes = new PluginContributesView
        {
            Panels = manifest?.Contributes?.Panels
                ?.Select(p => new PluginPanelView { Id = p.Id, Title = p.Title, Entry = p.Entry })
                .ToList() ?? [],
        },
    };
}

/// <summary>
/// Секрет в ответе: имя (из манифеста) + признак «значение введено» + время последнего
/// изменения (шаг 3.3). Значение секрета наружу не уходит никогда.
/// </summary>
public class PluginSecretView
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("set")] public bool Set { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Вклады пакета, нужные UI (ADR-031 §2.1): панели. Значения секретов и хостовые пути
/// наружу не едут — только id/title/entry панели. <see cref="PluginPanelView.Entry"/> —
/// путь относительно корня пакета (как в <c>contributes.panels[].entry</c> манифеста),
/// совпадает с контрактом <c>GET /api/plugins/{id}/{ver}/assets/{entry}</c> (ADR-031 §3.1).
/// </summary>
public class PluginContributesView
{
    [JsonPropertyName("panels")] public List<PluginPanelView> Panels { get; set; } = [];
}

/// <summary>
/// Панель в ответе: id + title + entry. <see cref="Entry"/> — путь относительно корня
/// пакета (например <c>panel/index.html</c>), по которому фронт забирает статику панели.
/// </summary>
public class PluginPanelView
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";
}
