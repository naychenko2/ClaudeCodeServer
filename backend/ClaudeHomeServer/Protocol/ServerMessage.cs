using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Protocol;

// Сообщения от сервера к клиенту (через SignalR)
public abstract record ServerMessage(string Type)
{
    // Заполняется при броадкасте в SessionManager — позволяет клиенту роутить по сессии
    public string SessionId { get; init; } = "";
}

public record McpServerInfo(string Name, string Status);

// ClaudeSessionId — id сессии у провайдера (у Claude — транскрипт CLI, у DeepSeek — GUID истории);
// имя поля историческое, не меняем ради обратной совместимости фронта.
// Provider/Capabilities — хвостовые optional-поля, старый фронт их игнорирует.
public record SessionStartedMessage(string ClaudeSessionId, bool IsResume, string Model, string Mode,
    string? Cwd = null, int ToolCount = 0, IReadOnlyList<McpServerInfo>? McpServers = null,
    string Provider = "claude", Services.Llm.LlmCapabilities? Capabilities = null)
    : ServerMessage("session_started");

public record TextDeltaMessage(string Text)
    : ServerMessage("text_delta");

// Текст пользовательского сообщения для сервер-инициированных отправок (автоматизация/задача):
// клиент не добавлял его оптимистично — бродкастим, чтобы промпт появился в чате сразу,
// а не по перезагрузке истории. Только для auto && !systemDirective (ввод пользователя уже
// виден на клиенте, внутренние директивы цикла «до готово» показывать не нужно).
public record UserMessageMessage(string Text, IReadOnlyList<string>? AttachedPaths, string? SenderPersonaId, bool Auto)
    : ServerMessage("user_message");

public record ThinkingDeltaMessage(string Text)
    : ServerMessage("thinking_delta");

// Текст/размышление сабагента (Task/Agent): CLI не шлёт для вложенных сообщений дельт —
// блоки приходят целиком в assistant-сообщениях с parent_tool_use_id. Рендерятся внутри
// карточки сабагента (секция «Активность»), в основную ленту не попадают.
public record AgentTextMessage(string ParentToolUseId, string Text)
    : ServerMessage("agent_text");

public record AgentThinkingMessage(string ParentToolUseId, string Text)
    : ServerMessage("agent_thinking");

public record ToolUseMessage(string Id, string Name, object Input, string? ParentToolUseId = null)
    : ServerMessage("tool_use");

// Стриминг аргументов инструмента (input_json_delta) — накопленный частичный JSON
public record ToolInputDeltaMessage(string ToolUseId, string PartialJson)
    : ServerMessage("tool_input_delta");

public record ToolResultMessage(string ToolUseId, string Content, bool IsError)
    : ServerMessage("tool_result");

public record PermissionRequestMessage(string RequestId, string ToolName, object ToolInput)
    : ServerMessage("permission_request");

// AskUserQuestion: в режиме stdio приходит как обычный tool_use, ответ — tool_result в stdin
public record AskQuestionMessage(string ToolUseId, object Input)
    : ServerMessage("ask_question");

// ExitPlanMode в режиме «План»: Claude представляет готовый план и ждёт решения пользователя
// (одобрить → продолжить выполнение; отклонить → остаться в планировании)
public record PlanReviewMessage(string RequestId, string Plan)
    : ServerMessage("plan_review");

public record FileChangedMessage(string Path, int Added, int Removed)
    : ServerMessage("file_changed");

public record ResultMessage(string Subtype, long DurationMs, int NumTurns, UsageInfo? Usage, double? TotalCostUsd, string? ApiErrorStatus = null, IReadOnlyList<string>? PermissionDenials = null)
    : ServerMessage("result");

// Фактически списанная стоимость генерации fal.ai. Приходит асинхронно после tool_result:
// сервер опрашивает fal.ai billing-events по request_id (см. FalCostService).
public record FalCostMessage(string RequestId, string? EndpointId, double CostUsd, double? OutputUnits = null, double? UnitPrice = null)
    : ServerMessage("fal_cost");

// Ответ оборван по лимиту токенов (assistant stop_reason == max_tokens)
public record TruncatedMessage() : ServerMessage("truncated");

// Скрытое (зашифрованное) размышление — блок redacted_thinking
public record RedactedThinkingMessage() : ServerMessage("redacted_thinking");

public record ErrorMessage(string Text)
    : ServerMessage("error");

// Телеметрия лимитов подписки (rate_limit_event, ~каждый ход). Utilization (0..1) — доля
// использования окна; LimitType — five_hour/seven_day/weekly; Status — allowed/allowed_warning/
// rejected. Используется и для непрерывного индикатора, и для баннера (при warning/rejected).
public record RateLimitMessage(string LimitType, string? ResetsAt, string? Status = null,
    double? Utilization = null, bool IsUsingOverage = false,
    string? OverageStatus = null, string? OverageResetsAt = null)
    : ServerMessage("rate_limit");

// Граница компакции контекста: Claude свернул часть истории (system/compact_boundary).
// PostTokens — размер свернутой истории после компакции (из compact_metadata.post_tokens)
public record CompactBoundaryMessage(string Trigger, int? PreTokens, int? PostTokens = null)
    : ServerMessage("compact_boundary");

// Ход компакции (system/status): Status == "compacting" — началась;
// CompactResult == "success"/"failed" (+ CompactError) — завершилась
public record CompactStatusMessage(string? Status, string? CompactResult = null, string? CompactError = null)
    : ServerMessage("compact_status");

public record ExitedMessage()
    : ServerMessage("exited");

public record StatusChangedMessage(string Status, string? LastMessage = null, int MessageCount = 0)
    : ServerMessage("status_changed");

// Чат удалён (вручную или авто-удалением временного чата) — клиенты убирают его из списков
// и закрывают, если он открыт. SessionId — в базовом поле.
public record ChatDeletedMessage()
    : ServerMessage("chat_deleted");

public record UsageInfo(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreationTokens);

// Прогресс фоновых агентов Workflow (шлётся через SignalR по мере завершения)
public record WorkflowToolDto(string Name, int Count);

public record WorkflowAgentDto(string Id, string Prompt, string? Summary,
    IReadOnlyList<WorkflowToolDto>? Tools, IReadOnlyList<string>? Files, bool IsDone = false,
    string? AgentType = null);

public record WorkflowProgressMessage(string ToolUseId, IReadOnlyList<WorkflowAgentDto> Agents, bool IsDone)
    : ServerMessage("workflow_progress");

// Блок таймлайна workflow-агента (полный поток из его транскрипта):
// text | thinking | tool_use | structured (итог StructuredOutput, Text = pretty-json).
// Отдаётся лениво по REST при раскрытии карточки — в workflow_progress не входит (тяжёлый).
// tool_use несёт полный input и результат — фронт рендерит тем же ToolUseView, что и чат.
public record WorkflowAgentBlockDto(string Kind, string? Text = null,
    string? ToolName = null, string? ToolId = null, object? ToolInput = null,
    string? ToolResult = null, bool? IsError = null);

// Изменение задачи (created/updated/deleted) — шлётся в группу user_{userId},
// чтобы все устройства пользователя обновили списки и календарь
public record TaskChangedMessage(string Action, Models.TaskItem Task)
    : ServerMessage("task_changed");

// Изменение заметок (Claude создал/обновил/удалил заметку через MCP или пользователь
// с другого устройства) — шлётся в группу user_{userId}, чтобы обновить список и граф.
public record NotesChangedMessage(string Action, string? NoteId = null)
    : ServerMessage("notes_changed");

// Изменение баз знаний раздела «Знания» (created/deleted/doc_changed) — в группу
// user_{userId}, чтобы все устройства обновили список и состав базы. DatasetId — id
// датасета Dify, к которому относится изменение (для точечного рефреша на фронте).
public record KnowledgeChangedMessage(string Action, string? DatasetId = null)
    : ServerMessage("knowledge_changed");

// Изменение персон — created/updated/deleted — в группу user_{userId},
// чтобы все устройства обновили раздел «Персоны».
public record PersonasChangedMessage(string Action, string? PersonaId = null)
    : ServerMessage("personas_changed");

// Изменение общей памяти команды проекта (added/updated/removed) — в группу user_{userId},
// чтобы вкладка «Память» командного центра обновилась на всех устройствах.
public record TeamMemoryChangedMessage(string Action, string ProjectId, string? EntryId = null)
    : ServerMessage("team_memory_changed");

// Смена активного спикера группового чата (@упоминание переключило персону-собеседника).
// Label — готовая подпись «Роль (Имя)» для разделителя «Теперь отвечает: …».
public record SpeakerChangedMessage(string PersonaId, string Label)
    : ServerMessage("speaker_changed");

// Состояние цикла «до готово» (флаг work-loop): активность, номер итерации, лимит,
// фаза (working/verifying) — для тумблера в композере и счётчика в шапке чата.
public record WorkLoopMessage(bool Active, int Iteration, int MaxIterations, string? Phase)
    : ServerMessage("work_loop");

// Live-прогресс совещания персон (P7). Phase: independent | attack | synthesis —
// с PersonaId и Status (running/done/error) построчно по персонам либо без PersonaId
// со Status="done" (фаза завершена); финал — Phase "done" или "error" (+ Error).
public record MeetingProgressMessage(string MeetingId, string Phase, string? PersonaId = null,
    string? Status = null, string? Error = null)
    : ServerMessage("meeting_progress");

// Завершённая фаза совещания с содержимым (broadcast-пара StoredMeetingPhaseMessage) —
// live-клиенты получают тексты позиций без перечитывания истории.
public record MeetingPhaseMessage(string MeetingId, string Phase, string Question,
    IReadOnlyList<MeetingEntry> Entries)
    : ServerMessage("meeting_phase");

// Live-прогресс конвейера пантеона (флаг persona-pipeline). Phase: analysis | plan |
// review | execute — со Status (running/done/error); финал — Phase "done"/"error" (+ Error).
public record PipelineProgressMessage(string PipelineId, string Phase,
    string? Status = null, string? Error = null)
    : ServerMessage("pipeline_progress");

// Завершённая фаза конвейера с содержимым (broadcast-пара StoredPipelinePhaseMessage).
// Round — номер круга доработки плана (1 = первый проход).
public record PipelinePhaseMessage(string PipelineId, string Phase, string Task,
    string PersonaId, string Text, int Round = 1)
    : ServerMessage("pipeline_phase");

// Пользовательское уведомление (напоминание о задаче, событие Claude-исполнителя и т.п.) —
// в группу user_{userId}: открытое приложение показывает тост + сохраняет в центр уведомлений.
// Kind — семантика для иконки/цвета: reminder | claude | info | success | meeting
// NotificationId — id в NotificationStore (для mark-read/delete через тост).
// Type — подтип: task_reminder | execution_started | execution_completed | briefing | summary | ...
public record NotificationMessage(string Title, string Body, string? Url = null,
    string Kind = "info", string? NotificationId = null, string? Type = null,
    string? ProjectId = null, string? SessionId = null, string? TaskId = null,
    string? Source = null, string? Tag = null)
    : ServerMessage("notification");

// Манифест recall (F3): что персона подтянула в ход из памяти/заметок/базы/команды — для
// атрибуции «опирается на…» / «использовано сейчас». Kind ∈ memory|note|knowledge|team.
public record RecallItemDto(string Kind, string? Ref, string Title, string? Snippet);
public record RecallManifestMessage(IReadOnlyList<RecallItemDto> Items)
    : ServerMessage("recall_manifest");

// Сообщения от клиента к серверу
public record ClientMessage([property: JsonPropertyName("type")] string Type);

public record SendMessageRequest(string Text, string[]? AttachedPaths = null) : ClientMessage("send_message");

public record PermissionDecisionRequest(string RequestId, string Behavior) : ClientMessage("permission_decision");

public record InterruptRequest() : ClientMessage("interrupt");
