using System.Text.Json;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/{id}/call</c> (ADR-031 §3.4): <see cref="Capability"/> —
/// операция моста {область}.{глагол} (например notes.read), <see cref="Args"/> — аргументы
/// операции (сырой JSON-объект из моста, M8).
/// </summary>
public class PluginCallRequest
{
    /// <summary>Операция моста: {область}.{глагол} (например notes.read, tasks.list, net.fetch).</summary>
    public string Capability { get; set; } = "";

    /// <summary>Аргументы операции — сырой JSON-объект (M8: JsonElement, не object?).</summary>
    public JsonElement? Args { get; set; }
}
