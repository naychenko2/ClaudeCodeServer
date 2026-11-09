namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/install</c> (ADR-031 §2.3, шаг 1.4, C1/C2).
/// <see cref="Source"/> — НЕ произвольный путь хоста, а метка из белого списка:
/// <c>draft:&lt;slug&gt;</c>, <c>upload:&lt;id&gt;</c>, <c>catalog:&lt;ref&gt;</c>.
/// Каталог для чтения резолвится из метки через <c>PluginStore.DraftDir</c>/<c>UploadDir</c>
/// (оба через <c>SafePath.Join</c>); та же метка пишется в <c>installed.json</c> как <c>source</c>.
/// Запрошенные права (потолок) берутся только из манифеста, не из тела (C2) —
/// поэтому поля <c>requestedPermissions</c> здесь больше нет.
/// </summary>
public class PluginInstallRequest
{
    /// <summary>Метка источника из белого списка: <c>draft:&lt;slug&gt;</c> | <c>upload:&lt;id&gt;</c> | <c>catalog:&lt;ref&gt;</c>.</summary>
    public string Source { get; set; } = "";
}
