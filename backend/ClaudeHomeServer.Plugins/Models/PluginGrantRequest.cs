namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Тело <c>POST /api/plugins/{id}/grant</c> (ADR-031 §4, шаг 1.4): выдать права и
/// ввести секреты. <see cref="Permissions"/> — подмножество запрошенных (requested) прав,
/// которые человек одобряет (заменяются ПОЛНОСТЬЮ — это сужение прав); <see cref="Secrets"/>
/// — секреты, на которые пакет ссылается (имя из манифеста <c>requires.secrets[].name</c>).
/// Семантика <see cref="Secrets"/> — «изменить», не «заменить» (QA-фикс P): присланное
/// значение устанавливает/обновляет, <c>null</c> — явно удаляет, а секрет, которого в теле
/// НЕТ, остаётся как есть.
/// </summary>
public class PluginGrantRequest
{
    /// <summary>Выдаваемые права — обязаны быть подмножеством requested записи установки.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// Секреты: имя (из манифеста) + значение. Значение <c>null</c> — явное удаление секрета
    /// (значение уходит из plugin-secrets.json, set=false); непустое — установить/обновить.
    /// Секрет, отсутствующий в этом списке, не трогается.
    /// </summary>
    public List<PluginGrantSecret> Secrets { get; set; } = [];
}

/// <summary>
/// Одно значение секрета: имя (из манифеста) + значение. <see cref="Value"/> = <c>null</c>
/// — явное удаление (QA-фикс P); непустое — установка/обновление значения.
/// </summary>
public class PluginGrantSecret
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
}
