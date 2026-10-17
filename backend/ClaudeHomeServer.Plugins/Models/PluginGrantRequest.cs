namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/{id}/grant</c> (ADR-031 §4, шаг 1.4): выдать права и
/// ввести секреты. <see cref="Permissions"/> — подмножество запрошенных (requested) прав,
/// которые человек одобряет; <see cref="Secrets"/> — значения секретов, на которые пакет
/// ссылается (имя из манифеста <c>requires.secrets[].name</c>).
/// </summary>
public class PluginGrantRequest
{
    /// <summary>Выдаваемые права — обязаны быть подмножеством requested записи установки.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Секреты: имя (из манифеста) + значение, введённое владельцем.</summary>
    public List<PluginGrantSecret> Secrets { get; set; } = [];
}

/// <summary>Одно значение секрета: имя (из манифеста) + значение.</summary>
public class PluginGrantSecret
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
