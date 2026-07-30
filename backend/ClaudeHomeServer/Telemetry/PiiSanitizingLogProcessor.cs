using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Санитайзер PII для ЛОГОВ перед экспортом в OTLP. Парный к <see cref="PiiSanitizingProcessor"/>
/// (тот чистит спаны), правила общие — <see cref="PiiRules"/>.
///
/// Зачем: <see cref="PiiSanitizingProcessor"/> объявлен как <c>BaseProcessor&lt;Activity&gt;</c>,
/// то есть логов он не касается вообще. Логи же экспортируются с включёнными
/// <c>IncludeFormattedMessage</c> и <c>ParseStateValues</c>, поэтому в SigNoz уезжали
/// готовые строки со значениями: имена чатов, идентификаторы пользователей, пути к файлам
/// секретов, имена персон. Получался перекос — имя персоны в спане дропалось, а то же имя
/// в логе уходило целиком.
///
/// Что делает:
/// 1. <b>Тело сообщения</b> заменяется ШАБЛОНОМ (<c>{OriginalFormat}</c>): вместо
///    «Временный чат abc «Отчёт по клиенту» удалён» уезжает
///    «Временный чат {SessionId} «{Name}» удалён». Событие остаётся понятным,
///    пользовательских данных в нём нет.
/// 2. <b>Атрибуты</b> фильтруются по <see cref="PiiRules"/>: <c>{SessionId}</c> остаётся
///    (opaque-идентификатор), <c>{Name}</c> дропается, <c>{Path}</c> хэшируется.
///
/// Остаточный риск: если сообщение записано интерполяцией (<c>$"...{value}"</c>), шаблона
/// не существует — подставленная строка И ЕСТЬ шаблон, вычистить из неё значения нечем.
/// Такие сообщения уезжают как есть. Правильный способ логировать — структурный:
/// <c>logger.LogInformation("Чат {SessionId} удалён", id)</c>, а не интерполяция.
/// </summary>
public sealed class PiiSanitizingLogProcessor : BaseProcessor<LogRecord>
{
    /// <summary>Ключ, под которым ILogger кладёт исходный шаблон сообщения.</summary>
    private const string OriginalFormatKey = "{OriginalFormat}";

    public override void OnEnd(LogRecord record)
    {
        var attributes = record.Attributes;
        if (attributes is null)
        {
            // Атрибутов нет — значит нет и шаблона; развёрнутое сообщение оставить нельзя
            // (в него уже подставлены значения), но и заменить не на что.
            return;
        }

        string? template = null;
        var cleaned = new List<KeyValuePair<string, object?>>(attributes.Count);

        foreach (var attribute in attributes)
        {
            if (attribute.Key == OriginalFormatKey)
            {
                template = attribute.Value?.ToString();
                cleaned.Add(attribute); // шаблон без значений — безопасен
                continue;
            }

            switch (PiiRules.Classify(attribute.Key))
            {
                case PiiAction.Hash:
                    cleaned.Add(new(attribute.Key, PiiRules.ComputeHash(attribute.Value?.ToString() ?? string.Empty)));
                    break;
                case PiiAction.Keep:
                    cleaned.Add(attribute);
                    break;
                // Drop — просто не переносим в очищенный список
            }
        }

        // Исключение: экспортёр OTLP разворачивает LogRecord.Exception в атрибуты
        // exception.type / exception.message / exception.stacktrace УЖЕ ПОСЛЕ процессоров,
        // поэтому через record.Attributes их не отфильтровать — до них allowlist не достаёт.
        // В stacktrace уезжают абсолютные пути сборки, в message — текст с URL и параметрами.
        // Оставляем только тип исключения: для диагностики его достаточно, PII в нём нет.
        if (record.Exception is not null)
        {
            cleaned.Add(new("exception.type", record.Exception.GetType().FullName));
            record.Exception = null;
        }

        record.Attributes = cleaned;

        // Тело: шаблон вместо развёрнутой строки. Если шаблона нет (интерполяция) —
        // оставляем как есть, см. «остаточный риск» в докблоке класса.
        if (template is not null)
            record.FormattedMessage = template;
    }
}
