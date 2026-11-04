using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Единственная точка чтения общих проектов: рубильник инстанса <c>SharedProjects:Enabled</c>
/// (appsettings.json, по умолчанию false) И пользовательский флаг <c>shared-projects</c>.
/// Инлайновых <c>GetValue</c> этих ключей не заводим (план shared-projects, §3).
/// </summary>
public sealed class SharedProjectsGate(IConfiguration config, FeatureFlagService flags)
{
    public const string SwitchKey = "SharedProjects:Enabled";

    /// <summary>Рубильник инстанса: false — общие проекты выключены для всех.</summary>
    public bool InstanceEnabled => config.GetValue<bool>(SwitchKey);

    /// <summary>Флаг включён у пользователя (без учёта рубильника).</summary>
    public bool FlagOn(string? userId) =>
        !string.IsNullOrEmpty(userId) && flags.IsEnabled(userId, FeatureFlagKeys.SharedProjects);

    /// <summary>Общие проекты доступны пользователю: рубильник включён И флаг включён.</summary>
    public bool IsOn(string? userId) => InstanceEnabled && FlagOn(userId);
}
