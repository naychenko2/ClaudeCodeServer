using System.Diagnostics;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Единая точка для всех Activity (traces). Имя = source identifier для OTel SDK.
///
/// Границы спанов — на логических событиях (tool_use, permission, mcp),
/// НЕ на token deltas. Token deltas — атрибуты текущего спана или поля в SpendStore,
/// но не отдельные спаны (иначе trace раздуется до тысяч записей на ход).
/// </summary>
public static class ServerActivitySource
{
    public const string Name = "ClaudeHomeServer.Execution";
    public const string Version = "1.0";

    public static readonly ActivitySource Instance = new(Name, Version);

    /// <summary>
    /// Имена спанов — константы, чтобы не плодить опечатки и магические строки
    /// в местах вызова <c>ServerActivitySource.Instance.StartActivity(...)</c>.
    /// </summary>
    public static class SpanNames
    {
        public const string ChatTurn = "chat.turn";
        public const string ProcessStart = "process.start";
        public const string ToolUse = "tool.use";
        public const string PermissionRequest = "permission.request";
        public const string McpCall = "mcp.call";
        public const string DifySync = "dify.sync";
    }
}
