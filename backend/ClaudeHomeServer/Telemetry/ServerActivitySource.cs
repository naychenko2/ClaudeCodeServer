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
    ///
    /// Здесь только то, что действительно создаётся. Раньше рядом лежали ещё
    /// <c>tool.use</c>, <c>permission.request</c>, <c>mcp.call</c> и <c>dify.sync</c> —
    /// ни один из них не создавался нигде, и список читался как перечень имеющихся
    /// спанов, хотя был перечнем намерений. Понадобится спан — константа заводится
    /// вместе с местом вызова.
    /// </summary>
    public static class SpanNames
    {
        public const string ChatTurn = "chat.turn";
        public const string ProcessStart = "process.start";
    }
}
