namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Конверт сообщения моста панели v1 (ADR-031 §3.3). Общий для всех сообщений:
/// <see cref="Source"/> (ccs-plugin — фрейм, ccs-host — хост), <see cref="V"/> (версия
/// протокола, = 1), <see cref="Type"/> (ready/call/resize — фрейм→хост; init/result/theme/
/// context — хост→фрейм). Невалидное сообщение (не тот source, не та версия) — игнор,
/// без 500 (ADR-031 §3.2).
/// </summary>
public sealed record PluginBridgeMessage
{
    /// <summary>Метка источника: 'ccs-plugin' (фрейм) или 'ccs-host' (хост).</summary>
    public string Source { get; init; } = "";

    /// <summary>Версия протокола моста. v1 = 1; внутри v1 — только аддитивные изменения (ADR-031 §3.5).</summary>
    public int V { get; init; }

    /// <summary>Тип сообщения: ready/call/resize (фрейм→хост), init/result/theme/context (хост→фрейм).</summary>
    public string Type { get; init; } = "";

    // --- Валидация конверта (ADR-031 §3.2) ---

    /// <summary>
    /// Валидно ли сообщение ФРЕЙМА (фрейм→хост): source = 'ccs-plugin' и v = 1.
    /// false — невалидное (хост игнорирует, без 500).
    /// </summary>
    public static bool IsValidFrameMessage(PluginBridgeMessage msg) =>
        msg.Source == "ccs-plugin" && msg.V == 1;

    /// <summary>
    /// Валидно ли сообщение ХОСТА (хост→фрейм): source = 'ccs-host' и v = 1.
    /// </summary>
    public static bool IsValidHostMessage(PluginBridgeMessage msg) =>
        msg.Source == "ccs-host" && msg.V == 1;
}
