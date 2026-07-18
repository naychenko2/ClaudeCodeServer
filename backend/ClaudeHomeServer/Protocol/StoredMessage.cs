using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StoredUserMessage), "user_message")]
[JsonDerivedType(typeof(StoredSessionStartedMessage), "session_started")]
[JsonDerivedType(typeof(StoredTextMessage), "text")]
[JsonDerivedType(typeof(StoredThinkingMessage), "thinking")]
[JsonDerivedType(typeof(StoredToolUseMessage), "tool_use")]
[JsonDerivedType(typeof(StoredAskQuestionMessage), "ask_question")]
[JsonDerivedType(typeof(StoredPlanReviewMessage), "plan_review")]
[JsonDerivedType(typeof(StoredFileChangedMessage), "file_changed")]
[JsonDerivedType(typeof(StoredResultMessage), "result")]
[JsonDerivedType(typeof(StoredFalCostMessage), "fal_cost")]
[JsonDerivedType(typeof(StoredCompactBoundaryMessage), "compact_boundary")]
[JsonDerivedType(typeof(StoredErrorMessage), "error")]
[JsonDerivedType(typeof(StoredWorkflowProgressMessage), "workflow_progress")]
public abstract class StoredMessage { }

public class StoredUserMessage(string text, string[]? attachedPaths = null, bool? viaAgent = null,
    string? senderPersonaId = null, bool? systemDirective = null, bool? auto = null) : StoredMessage
{
    public string Text { get; init; } = text;
    public string[]? AttachedPaths { get; init; } = attachedPaths;
    // Сообщение прислано не человеком, а агентом из другой сессии (chats_send) — для пометки в UI
    public bool? ViaAgent { get; init; } = viaAgent;
    // Персона-отправитель (chats_send из чата персоны, авто-ход задачи) — для рендера
    // сообщения её лицом
    public string? SenderPersonaId { get; init; } = senderPersonaId;
    // Служебная директива механики цикла «до готово» (continuation/verification) — UI прячет
    // сырой текст за компактной плашкой вместо пузыря пользователя
    public bool? SystemDirective { get; init; } = systemDirective;
    // Сообщение опубликовано автоматически (не человеком), например промпт задачи.
    // UI показывает источник (персона или стандартный значок)
    public bool? Auto { get; init; } = auto;
}

public class StoredSessionStartedMessage(string model, string mode) : StoredMessage
{
    public string Model { get; init; } = model;
    public string Mode { get; init; } = mode;
}

public class StoredTextMessage(string text, string? personaId = null, string? parentToolUseId = null) : StoredMessage
{
    public string Text { get; init; } = text;
    // Персона, от лица которой написан ответ (на момент хода) — чтобы после смены
    // собеседника у старых реплик оставался прежний аватар. null — обычный ассистент.
    public string? PersonaId { get; init; } = personaId;
    // Текст сабагента (Task/Agent): ссылка на родительский tool_use — рендерится внутри
    // его карточки, а не в основной ленте. null — текст основного агента.
    public string? ParentToolUseId { get; init; } = parentToolUseId;
}

public class StoredThinkingMessage(string text, string? parentToolUseId = null) : StoredMessage
{
    public string Text { get; init; } = text;
    // См. StoredTextMessage.ParentToolUseId
    public string? ParentToolUseId { get; init; } = parentToolUseId;
}

public class StoredFileChangedMessage(string path, int added, int removed) : StoredMessage
{
    public string Path { get; init; } = path;
    public int Added { get; init; } = added;
    public int Removed { get; init; } = removed;
}

public class StoredResultMessage(string subtype, long durationMs, int numTurns,
    UsageInfo? usage = null, double? totalCostUsd = null, string? apiErrorStatus = null,
    IReadOnlyList<string>? permissionDenials = null, int? contextTokens = null) : StoredMessage
{
    public string Subtype { get; init; } = subtype;
    public long DurationMs { get; init; } = durationMs;
    public int NumTurns { get; init; } = numTurns;
    public UsageInfo? Usage { get; init; } = usage;
    public double? TotalCostUsd { get; init; } = totalCostUsd;
    public string? ApiErrorStatus { get; init; } = apiErrorStatus;
    public IReadOnlyList<string>? PermissionDenials { get; init; } = permissionDenials;
    // Размер контекста последнего запроса хода — см. ResultMessage.ContextTokens.
    // В историях до этого поля null: старый чат остаётся без оценки до первого нового хода.
    public int? ContextTokens { get; init; } = contextTokens;
}

public class StoredErrorMessage(string text) : StoredMessage
{
    public string Text { get; init; } = text;
}

// Граница компакции контекста — чтобы после перезагрузки страницы оценка заполнения не врала
public class StoredCompactBoundaryMessage(string trigger, int? preTokens, int? postTokens = null) : StoredMessage
{
    public string Trigger { get; init; } = trigger;
    public int? PreTokens { get; init; } = preTokens;
    public int? PostTokens { get; init; } = postTokens;
}

// Стоимость генерации fal.ai (фактически списанная), приходит вне хода — хранится отдельной записью
public class StoredFalCostMessage(string requestId, string? endpointId, double costUsd,
    double? outputUnits = null, double? unitPrice = null) : StoredMessage
{
    public string RequestId { get; init; } = requestId;
    public string? EndpointId { get; init; } = endpointId;
    public double CostUsd { get; init; } = costUsd;
    public double? OutputUnits { get; init; } = outputUnits;
    public double? UnitPrice { get; init; } = unitPrice;
}

// Result/IsError заполняются позже — при получении tool_result от Claude
public class StoredToolUseMessage : StoredMessage
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public object? Input { get; set; }
    public string? Result { get; set; }
    public bool IsError { get; set; }
    public string? ParentToolUseId { get; init; }
    // Фоновый агент (run_in_background/Workflow) реально завершился (bg_agent_done):
    // у него tool_result — лишь квитанция запуска, признак завершения — только этот.
    // null — не фоновый вызов либо старая история
    public bool? BgDone { get; set; }
}

// Последний снапшот workflow_progress (по ToolUseId вызова Workflow) — чтобы карточка
// workflow и вкладка «Агенты» переживали перезагрузку страницы и рестарт сервера.
// Aborted=true — прогресс восстановлен из истории после рестарта: ватчеров больше нет,
// незавершённые агенты уже не завершатся
public class StoredWorkflowProgressMessage : StoredMessage
{
    public string ToolUseId { get; init; } = "";
    public bool IsDone { get; set; }
    public bool? Aborted { get; set; }
    public IReadOnlyList<WorkflowAgentDto>? Agents { get; set; }
}

// AskUserQuestion: Resolved/Answers заполняются при ответе пользователя
public class StoredAskQuestionMessage : StoredMessage
{
    public string ToolUseId { get; init; } = "";
    public object? Input { get; init; }
    public bool Resolved { get; set; }
    public object? Answers { get; set; }
}

// ExitPlanMode (режим «План»): Resolved/Approved/Feedback заполняются при решении пользователя
public class StoredPlanReviewMessage : StoredMessage
{
    public string RequestId { get; init; } = "";
    public string Plan { get; init; } = "";
    public bool Resolved { get; set; }
    public bool? Approved { get; set; }
    public string? Feedback { get; set; }
}
