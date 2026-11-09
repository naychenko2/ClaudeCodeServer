using System.Text.Json;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/{id}/call</c> (ADR-031 §3.4): <see cref="Capability"/> —
/// операция моста {область}.{глагол} (например notes.read), <see cref="Args"/> — аргументы
/// операции (сырой JSON-объект из моста, M8), <see cref="ProjectId"/> — проект слота, в
/// котором открыта панель (QA-фикс L1).
/// </summary>
public class PluginCallRequest
{
    /// <summary>Операция моста: {область}.{глагол} (например notes.read, tasks.list, net.fetch).</summary>
    public string Capability { get; set; } = "";

    /// <summary>Аргументы операции — сырой JSON-объект (M8: JsonElement, не object?).</summary>
    public JsonElement? Args { get; set; }

    /// <summary>
    /// Проект слота (ADR-031 §3.4, QA-фикс L1): id проекта, в котором открыта панель;
    /// null — личные данные владельца. Хост (доверенный) подставляет из контекста слота;
    /// панель (недоверенная) сама тело не формирует. Контроллер проверяет существование
    /// проекта и его владельца (GetById не фильтрует по владельцу).
    /// </summary>
    public string? ProjectId { get; set; }
}
