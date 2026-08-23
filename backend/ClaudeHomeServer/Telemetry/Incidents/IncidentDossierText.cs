using System.Globalization;
using System.Text;

namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Одно текстовое представление досье на все три действия карточки: описание заводимой
/// задачи, черновик сообщения в чат и промпт кнопки «Объяснить».
///
/// Одно, а не три: состав досье зафиксирован таблицей приватности
/// (docs/observability/incident-queries.md), и три расходящихся рендера означали бы, что
/// в промпт модели однажды уедет то, чего в таблице нет. Здесь же видно ровно то, что
/// уходит наружу: разрез, времена, id чатов, тексты логов. Реплик чатов, промптов, имён
/// персон и путей в досье НЕТ.
/// </summary>
public static class IncidentDossierText
{
    public static string Render(IncidentDossier dossier)
    {
        var sb = new StringBuilder();
        var incident = dossier.Incident;

        sb.Append("## Инцидент: ").AppendLine(incident.Title);
        if (!string.IsNullOrWhiteSpace(incident.Description))
            sb.AppendLine(incident.Description);
        sb.AppendLine();

        sb.Append("- Состояние: ").AppendLine(incident.IsFiring ? "горит" : "погас");
        if (incident.Severity is { } severity) sb.Append("- Важность: ").AppendLine(severity);
        if (incident.Environment is { } env) sb.Append("- Контур: ").AppendLine(env);
        sb.Append("- Окно разбора: ").Append(Time(dossier.From)).Append(" — ").AppendLine(Time(dossier.To));
        if (dossier.IsForeignEnvironment)
            sb.AppendLine("- Инцидент ЧУЖОГО контура: локальных чатов и расхода по нему на этом инстансе нет.");
        sb.AppendLine();

        if (dossier.Status != IncidentStatus.Ok)
        {
            sb.AppendLine(dossier.Status == IncidentStatus.NotConfigured
                ? "Телеметрия не настроена — данных для разбора нет."
                : "SigNoz не ответил — данные за окно собрать не удалось.");
            return sb.ToString();
        }

        if (dossier.Breakdown.Count > 0)
        {
            sb.Append("### Разрез по `").Append(dossier.BreakdownTag).AppendLine("`");
            foreach (var row in dossier.Breakdown)
                sb.Append("- ").Append(row.Label).Append(": ")
                  .AppendLine(row.Count.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        if (dossier.Turns.Count > 0)
        {
            sb.AppendLine("### Упавшие ходы");
            foreach (var turn in dossier.Turns)
            {
                sb.Append("- ").Append(Time(turn.At))
                  .Append(" · ").Append(turn.Provider ?? "провайдер неизвестен")
                  .Append(" · ").Append(turn.Model ?? "модель неизвестна")
                  .Append(" · ").Append(turn.ErrorType ?? "тип ошибки неизвестен");
                if (turn.DurationMs > 0) sb.Append(" · ").Append(turn.DurationMs).Append(" мс");
                sb.AppendLine();
            }
            if (dossier.TurnsTotal > dossier.Turns.Count)
                sb.Append("- показаны ").Append(dossier.Turns.Count).Append(" из ")
                  .Append(dossier.TurnsTotal).AppendLine();
            sb.AppendLine();
        }

        if (dossier.Chats.Count > 0)
        {
            sb.AppendLine("### Затронутые чаты");
            foreach (var chat in dossier.Chats)
            {
                sb.Append("- ").Append(chat.Title ?? "без названия")
                  .Append(" (").Append(chat.ChatId).Append(')');
                // «Указан алертом» вместо «падений 0»: у правил с разрезом по чату ходы
                // успешные, просто долгие, и ноль падений здесь ничего не опровергает.
                if (chat.FromAlert) sb.Append(" · указан алертом");
                if (chat.Failures > 0) sb.Append(" · падений ").Append(chat.Failures);
                if (chat.TotalTokens > 0) sb.Append(" · токенов ").Append(chat.TotalTokens);
                if (chat.McpFailures.Count > 0)
                    sb.Append(" · отказы MCP: ").Append(string.Join(", ", chat.McpFailures));
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (dossier.Logs.Count > 0)
        {
            sb.AppendLine("### Логи окна");
            foreach (var line in dossier.Logs)
                sb.Append("- ").Append(Time(line.At)).Append(" [").Append(line.Severity).Append("] ")
                  .AppendLine(line.Message);
            if (dossier.LogsTotal > dossier.Logs.Count)
                sb.Append("- показаны ").Append(dossier.Logs.Count).Append(" из ")
                  .Append(dossier.LogsTotal).AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Логи связаны с ходами только по времени: у логов пустой trace_id.");
        }

        return sb.ToString();
    }

    /// <summary>Промпт кнопки «Объяснить»: то же досье плюс просьба разобрать его по делу.</summary>
    public static string ExplainPrompt(IncidentDossier dossier) =>
        $"""
        Ты разбираешь инцидент телеметрии в сервере ClaudeCodeServer (ASP.NET Core + claude CLI).
        Ниже — досье, собранное детерминированно из SigNoz и локальных сторов.

        {Render(dossier)}

        Ответь по-русски, коротко и по делу, тремя частями:
        1. Что произошло — одним абзацем, без пересказа цифр.
        2. Наиболее вероятная причина — и почему именно она следует из данных.
        3. Что проверить первым — два-три конкретных шага.

        Не выдумывай данных, которых нет в досье. Если данных не хватает — так и скажи.
        """;

    private static string Time(DateTimeOffset? at)
        => at is { } value
            ? value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
            : "время неизвестно";
}
