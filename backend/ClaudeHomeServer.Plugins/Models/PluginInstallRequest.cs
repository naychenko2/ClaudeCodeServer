namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/install</c> (ADR-031 §2.3, шаг 1.4): принять пакет
/// из указанной папки на диске. <see cref="Source"/> — путь к папке пакета (от неё
/// читается манифест <c>ccs-plugin.json</c> — id/version — и валидируются файлы);
/// <see cref="RequestedPermissions"/> — запрошенные права (потолок) из манифеста,
/// которые человек подтверждает при установке.
/// </summary>
public class PluginInstallRequest
{
    /// <summary>Путь к папке пакета на диске (source, из которого копируем в packages/).</summary>
    public string Source { get; set; } = "";

    /// <summary>Запрошенные права (потолок) — копия permissions.requested манифеста.</summary>
    public List<string> RequestedPermissions { get; set; } = [];
}
