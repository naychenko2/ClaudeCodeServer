using System.Diagnostics;
using OpenTelemetry;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Санитайзер PII для СПАНОВ перед экспортом в OTLP. Сидит в начале pipeline
/// (CompositeProcessor), так что ОБА бэкенда (Aspire + SigNoz) получают очищенные данные.
///
/// Правила общие с логами — см. <see cref="PiiRules"/> (allowlist + drop-by-default).
/// Парный процессор для логов — <see cref="PiiSanitizingLogProcessor"/>.
/// </summary>
public sealed class PiiSanitizingProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        // Собираем изменения в списки, применяем ПОСЛЕ итерации (безопасная мутация)
        var replacements = new List<KeyValuePair<string, object?>>();
        var removals = new List<string>();

        foreach (var tag in activity.TagObjects)
        {
            switch (PiiRules.Classify(tag.Key))
            {
                case PiiAction.Hash:
                    replacements.Add(new(tag.Key, PiiRules.ComputeHash(tag.Value?.ToString() ?? string.Empty)));
                    break;
                case PiiAction.Keep:
                    break;
                default:
                    removals.Add(tag.Key);
                    break;
            }
        }

        foreach (var key in removals)
            activity.SetTag(key, null);

        foreach (var replacement in replacements)
            activity.SetTag(replacement.Key, replacement.Value);

        // StatusDescription инструментация заполняет текстом исключения, а там —
        // URL с query-строкой (в ней бывают API-ключи) и абсолютные пути сборки.
        // Сам факт и код ошибки сохраняются в activity.Status и теге error.type.
        //
        // Событий (activity.Events с exception.message/stacktrace) здесь нет:
        // коллекция неизменяема, поэтому их не создают вовсе — RecordException
        // выключен в ObservabilityExtensions. Если кто-то включит его обратно,
        // стектрейсы поедут в SigNoz В ОБХОД этого allowlist'а.
        if (!string.IsNullOrEmpty(activity.StatusDescription))
            activity.SetStatus(activity.Status, null);
    }
}
