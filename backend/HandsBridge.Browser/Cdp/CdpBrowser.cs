using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

public sealed record CdpBrowserVersion(string Product, string ProtocolVersion, string UserAgent);

public sealed record CdpTargetInfo(string TargetId, string Type, string Title, string Url, bool Attached);

/// <summary>
/// Команды уровня браузера (без сессии): версия, загрузки, вкладки. Тонкая обёртка — политика
/// «что можно модели» живёт в гейте, а не здесь.
/// </summary>
public sealed class CdpBrowser(CdpConnection connection)
{
    public CdpConnection Connection { get; } = connection;

    public async Task<CdpBrowserVersion> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var r = await Connection.SendAsync("Browser.getVersion", cancellationToken: cancellationToken);
        return new CdpBrowserVersion(Str(r, "product"), Str(r, "protocolVersion"), Str(r, "userAgent"));
    }

    /// <summary>Запрет загрузок файлов (ADR-016 §7.1): рука не пишет на диск машины.</summary>
    public Task DenyDownloadsAsync(CancellationToken cancellationToken = default) =>
        Connection.SendAsync("Browser.setDownloadBehavior", new JsonObject { ["behavior"] = "deny" },
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<CdpTargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        var r = await Connection.SendAsync("Target.getTargets", cancellationToken: cancellationToken);
        if (!r.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array) return [];
        return [.. infos.EnumerateArray().Select(t => new CdpTargetInfo(
            Str(t, "targetId"), Str(t, "type"), Str(t, "title"), Str(t, "url"),
            t.TryGetProperty("attached", out var a) && a.ValueKind == JsonValueKind.True))];
    }

    /// <summary>Открыть вкладку; вернёт targetId.</summary>
    public async Task<string> CreateTargetAsync(string url, CancellationToken cancellationToken = default)
    {
        var r = await Connection.SendAsync("Target.createTarget", new JsonObject { ["url"] = url },
            cancellationToken: cancellationToken);
        return Str(r, "targetId");
    }

    /// <summary>Подключиться к вкладке в режиме flatten: команды и события идут по той же трубе с sessionId.</summary>
    public async Task<CdpPage> AttachAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var r = await Connection.SendAsync("Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, cancellationToken: cancellationToken);
        return new CdpPage(Connection, targetId, Str(r, "sessionId"));
    }

    public Task ActivateTargetAsync(string targetId, CancellationToken cancellationToken = default) =>
        Connection.SendAsync("Target.activateTarget", new JsonObject { ["targetId"] = targetId },
            cancellationToken: cancellationToken);

    public Task CloseTargetAsync(string targetId, CancellationToken cancellationToken = default) =>
        Connection.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = targetId },
            cancellationToken: cancellationToken);

    public Task CloseAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        Connection.SendAsync("Browser.close", timeout: timeout, cancellationToken: cancellationToken);

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";
}
