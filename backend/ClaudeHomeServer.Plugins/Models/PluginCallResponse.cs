namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Ответ <c>POST /api/plugins/{id}/call</c> (ADR-031 §3.3, result): <see cref="Ok"/> +
/// <see cref="Result"/> (успех) либо <see cref="Error"/> (сбой: код + текст).
/// Хост пересылает это во фрейм как сообщение <c>result</c> моста.
/// </summary>
public class PluginCallResponse
{
    /// <summary>true — операция выполнена, <see cref="Result"/> — данные результата.</summary>
    public bool Ok { get; set; }

    /// <summary>Данные результата (успех): произвольный JSON-объект.</summary>
    public object? Result { get; set; }

    /// <summary>Ошибка (сбой): код (denied/not_found/invalid/unavailable/...) + текст.</summary>
    public PluginCallErrorDto? Error { get; set; }
}

/// <summary>Ошибка в ответе call: код (ADR-031 §3.4) + человекочитаемый текст.</summary>
public class PluginCallErrorDto
{
    /// <summary>Код ошибки: denied / not_found / invalid / unavailable / upstream / timeout / limit.</summary>
    public string Code { get; set; } = "";

    /// <summary>Человекочитаемое описание ошибки.</summary>
    public string Message { get; set; } = "";
}
