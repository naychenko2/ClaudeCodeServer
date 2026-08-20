using System.Globalization;
using System.Text.RegularExpressions;
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
/// 1. <b>Тело сообщения</b> пересобирается из ШАБЛОНА (<c>{OriginalFormat}</c>) и уже
///    ОЧИЩЕННЫХ атрибутов: «Временный чат abc «Отчёт по клиенту» удалён» уезжает как
///    «Временный чат 7f3a… «{Name}» удалён» — разрешённый идентификатор виден, имя чата
///    осталось плейсхолдером. Подставляется ровно то, что пропустил allowlist пункта 2,
///    поэтому новых утечек рендер не создаёт по построению.
///
///    Раньше тело возвращалось к голому шаблону целиком, и в ленте SigNoz строка читалась
///    как «cheap-runner: действие {Action} — {Reason}»: значения лежали в атрибутах записи,
///    но глазами список был бесполезен — приходилось раскрывать каждую строку.
/// 2. <b>Атрибуты</b> фильтруются по <see cref="PiiRules"/>: <c>{SessionId}</c> остаётся
///    (opaque-идентификатор), <c>{Name}</c> дропается, <c>{Path}</c> хэшируется.
///
/// Остаточный риск: если сообщение записано интерполяцией (<c>$"...{value}"</c>), шаблона
/// не существует — подставленная строка И ЕСТЬ шаблон, вычистить из неё значения нечем.
/// Такие сообщения уезжают как есть. Правильный способ логировать — структурный:
/// <c>logger.LogInformation("Чат {SessionId} удалён", id)</c>, а не интерполяция.
/// </summary>
public sealed partial class PiiSanitizingLogProcessor : BaseProcessor<LogRecord>
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

        // Тело: шаблон, в который возвращены значения ПРОШЕДШИХ санитайзер атрибутов.
        // Если шаблона нет (интерполяция) — оставляем как есть, см. «остаточный риск».
        if (template is not null)
            record.FormattedMessage = Render(template, cleaned);
    }

    /// <summary>
    /// Подставить в шаблон значения очищенных атрибутов. Плейсхолдер, которому не нашлось
    /// разрешённого атрибута, остаётся в тексте как есть: именно так дропнутое значение
    /// и должно выглядеть — «{Name}» вместо названия чата.
    /// </summary>
    private static string Render(string template, List<KeyValuePair<string, object?>> cleaned)
        => Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            foreach (var attribute in cleaned)
            {
                if (!string.Equals(attribute.Key, name, StringComparison.Ordinal)) continue;
                // Формат из шаблона ({Idle:0} мин) применяем, но ВСЕГДА инвариантной
                // культурой: на ru-RU «0.#» даёт запятую, и одинаковые события перестают
                // грепаться по общей подстроке (та же грабля, что в OneShotClaudeRunner).
                if (match.Groups[2].Success && attribute.Value is IFormattable formattable)
                    return formattable.ToString(match.Groups[2].Value, CultureInfo.InvariantCulture);
                return attribute.Value?.ToString() ?? string.Empty;
            }
            return match.Value;
        });

    /// <summary>
    /// Плейсхолдер структурного шаблона: <c>{Name}</c> либо <c>{Name:формат}</c>.
    /// Двойная скобка <c>{{</c> — экранирование ILogger, её не трогаем.
    /// </summary>
    [GeneratedRegex(@"(?<!\{)\{([A-Za-z_][A-Za-z0-9_.]*)(?::([^}]*))?\}")]
    private static partial Regex Placeholder();
}
