using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Компактный вид карточки задачи в ответах tasks_* (контекст агента дороже байтов).
///
/// Что убираем: null, пустые строки/массивы/объекты (на любой глубине) и служебные поля платформы,
/// которые не читает ни одна роль конвейера (реестр потребителей — в итоге задачи 85fbaee8).
/// Что НЕ убираем никогда: сигналы конвейера и всё, что читает фронт (id/title/status/projectId/
/// personaId/assignee/priority/dueDate/labels/subtasks/featureId/stepRef/lane — TaskCreatedView и
/// useSessionArtifacts), поля awaitingHuman*, stage, routeRequired, workflowProposal, workflow, hold,
/// note, pregate, executorStoppedAt/executorStopReason/droppedByHumanAt/deviceWait* (состояние
/// исполнителя, по которому оркестратор решает, что делать дальше).
///
/// Режим переключается ключом <see cref="ConfigKey"/> (живой — appsettings.Local.json перечитывается),
/// «полный» = прежний ответ байт в байт.
/// </summary>
internal static class TasksCompactView
{
    public const string ConfigKey = "Mcp:TasksCompactResponses";

    // Служебное: платформа пишет и читает сама, агенту знание не нужно. Имена — как в JSON (camelCase).
    private static readonly string[] ServiceKeys =
    [
        "ownerId", "reminderSentAt", "createdByUserId", "seriesId", "sourceNoteLine",
        "executionExpiresAfterMinutes", "claudeStartedAt", "holdResumeDeliveredAt",
        "executorNudgedAt", "executorStaleAlertedAt", "completionDelivered",
        "executorLaunchedBySessionId", "executorLaunchedByPersonaId", "executorLaunchedByChat",
        "createdByPersonaId", "delegationDepth", "order", "initialEtaMinutes", "baselineSteps",
    ];

    // Тексты, которые вызывающий только что сам прислал (или который ему не нужен в ответе на запись):
    // эхо большого поля — главная доля веса ответа изменяющего вызова. Свидетельства — отдельно (ниже).
    private static readonly string[] MutationNeverEcho = ["description", "resultMarkdown", "claudeResult"];

    // Тяжёлые списки: в ответе записи — только если ключ был в аргументах вызова (серверные id
    // критериев и подзадач вызывающему нужны, а повторять неизменённое незачем).
    private static readonly string[] MutationEchoWhenSent = ["acceptance", "planSteps", "subtasks", "linkedFiles"];

    /// <summary>Компактная карточка для чтения (tasks_get): без null, пустого и служебного.</summary>
    public static JsonObject ForRead(JsonObject card)
    {
        foreach (var key in ServiceKeys) card.Remove(key);
        Prune(card);
        return card;
    }

    /// <summary>
    /// Ответ изменяющего вызова: компактная карточка без эха присланных текстов; тяжёлые списки —
    /// только если ключ был в <paramref name="sentKeys"/>.
    /// </summary>
    public static JsonObject ForMutation(JsonObject card, IEnumerable<string> sentKeys)
    {
        var sent = sentKeys.ToHashSet(StringComparer.Ordinal);
        ForRead(card);
        foreach (var key in MutationNeverEcho) card.Remove(key);
        foreach (var key in MutationEchoWhenSent)
            if (!sent.Contains(key)) card.Remove(key);
        // Свидетельства: смена критериев может сбросить чужое свидетельство, а закрытие их требует —
        // вызывающему нужно видеть, какие критерии покрыты. Тексты how/result он прислал сам: остаётся
        // только criterionId. Если ни свидетельства, ни критерии не менялись — поле не едет вовсе.
        if (card["evidence"] is JsonArray evidence && (sent.Contains("evidence") || sent.Contains("acceptance")))
        {
            var covered = new JsonArray();
            foreach (var item in evidence)
                if (item is JsonObject o && o["criterionId"] is { } id)
                    covered.Add(new JsonObject { ["criterionId"] = id.DeepClone() });
            card["evidence"] = covered;
        }
        else card.Remove("evidence");
        return card;
    }

    private static void Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    Prune(child);
                    if (IsEmpty(child)) obj.Remove(key);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr) Prune(child);
                break;
        }
    }

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonObject o => o.Count == 0,
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0,
        _ => false,
    };
}
