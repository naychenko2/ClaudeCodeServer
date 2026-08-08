using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Protocol;

// Сообщения от сервера к клиенту (через SignalR)
public abstract record ServerMessage(string Type)
{
    // Заполняется при броадкасте в SessionManager — позволяет клиенту роутить по сессии
    public string SessionId { get; init; } = "";
}

public record McpServerInfo(string Name, string Status);

// Ход ушёл в дерево, отличное от того, куда его отправил сервер (агент вызвал встроенный
// EnterWorktree в обход тумблера чата) — Path/Name фактического cwd для короткой подписи в UI.
// null у SessionStartedMessage.TurnWorktree — обычный случай, ход идёт в ожидаемой папке.
public sealed record TurnWorktreeInfo(string Path, string Name);

// ClaudeSessionId — id сессии у провайдера (у Claude — транскрипт CLI, у DeepSeek — GUID истории);
// имя поля историческое, не меняем ради обратной совместимости фронта.
// Provider/Capabilities/TurnWorktree — хвостовые optional-поля, старый фронт их игнорирует.
public record SessionStartedMessage(string ClaudeSessionId, bool IsResume, string Model, string Mode,
    string? Cwd = null, int ToolCount = 0, IReadOnlyList<McpServerInfo>? McpServers = null,
    string Provider = "claude", Services.Llm.LlmCapabilities? Capabilities = null,
    TurnWorktreeInfo? TurnWorktree = null)
    : ServerMessage("session_started");

public record TextDeltaMessage(string Text)
    : ServerMessage("text_delta");

// Текст пользовательского сообщения для сервер-инициированных отправок (автоматизация/задача):
// клиент не добавлял его оптимистично — бродкастим, чтобы промпт появился в чате сразу,
// а не по перезагрузке истории. Только для auto && !systemDirective (ввод пользователя уже
// виден на клиенте, внутренние директивы цикла «до готово» показывать не нужно).
// Timestamp (Unix-мс UTC) — время отправки для подписи в панели действий поста; тем же
// числом оно уходит в историю, поэтому живая лента и перезагрузка показывают одно и то же.
public record UserMessageMessage(string Text, IReadOnlyList<string>? AttachedPaths, string? SenderPersonaId, bool Auto,
    string? SenderOrigin = null, string? SenderChatName = null, string? StaffNote = null,
    long? Timestamp = null)
    : ServerMessage("user_message");

// Очередь сообщений занятой сессии — полный снимок при каждом изменении (постановка,
// отмена, доставка). Список, а не дельта: клиент может подключиться в любой момент,
// а очередь короткая (потолок 10).
public record PendingMessagesMessage(IReadOnlyList<PendingMessageDto> Items)
    : ServerMessage("pending_messages");

// Ожидающее доставки сообщение для клиента. SenderOrigin — чип «откуда пришло»,
// заполнен только когда источник в другом проекте либо вне проектов.
// SenderChatName — подпись, когда у отправителя нет персоны: имя его чата.
// Kind — "user" (сообщение человека, ждёт в серверной очереди) или "agent" (chats_send);
// AttachedPaths/Mode заполнены только у пользовательских — клиент рисует их на карточке
// и (Mode) применяет при возврате в композер.
public record PendingMessageDto(string Id, string Text, string? SenderPersonaId,
    string? SenderOrigin, DateTime EnqueuedAt, string? SenderChatName = null,
    string Kind = "agent", IReadOnlyList<string>? AttachedPaths = null, string? Mode = null);

// «Стоп» вернул текст в композер (фича «честная очередь»). Payload null — восстанавливать
// нечего (прерван авто/агентский ход, пользовательских в очереди не было): клиент просто
// отмечает, что очередь заморожена. Иначе — последнее пользовательское сообщение:
// изъятое из очереди либо копия прерванного хода (из ленты оно НЕ убирается).
public record ComposerRestoreMessage(string? Text, IReadOnlyList<string>? AttachedPaths, string? Mode)
    : ServerMessage("composer_restore");

// Гостевая реплика персоны, вставленная в историю без агентского хода (0 токенов) — доклад
// о завершении делегированной задачи (модель Z, TaskExecutionService.ReportToDelegatorAsync).
// Live-аналог StoredTextMessage: клиент не получал текст через text_delta, бродкастим целиком,
// как UserMessageMessage для сервер-инициированной отправки. PersonaId — автор реплики (её лицо).
public record GuestTextMessage(string Text, string PersonaId, long? Timestamp = null)
    : ServerMessage("guest_text");

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

// Завершение фоновых агентов (Agent run_in_background / Workflow): toolUseId их карточек.
// Единственный достоверный сигнал «агент закончил» для UI — по <task-notification> CLI;
// Aborted=true — агенты умерли вместе с процессом (финализация прогона), не доработав
public record BgAgentDoneMessage(IReadOnlyList<string> ToolUseIds, bool Aborted = false)
    : ServerMessage("bg_agent_done");

public record PermissionRequestMessage(string RequestId, string ToolName, object ToolInput)
    : ServerMessage("permission_request");

// AskUserQuestion: в режиме stdio приходит как обычный tool_use, ответ — tool_result в stdin
public record AskQuestionMessage(string ToolUseId, object Input)
    : ServerMessage("ask_question");

// ExitPlanMode в режиме «План»: Claude представляет готовый план и ждёт решения пользователя
// (одобрить → продолжить выполнение; отклонить → остаться в планировании)
public record PlanReviewMessage(string RequestId, string Plan)
    : ServerMessage("plan_review");

// External — правка пришла не от модели этого чата (нет заявки FileChangeAttributor):
// человек в IDE, форматтер, Bash-команда без Edit/Write. Фронт снимает кнопку «Откатить»
// и показывает пометку «Изменение вне чата» — см. FileChangeAttributor.
public record FileChangedMessage(string Path, int Added, int Removed, bool External = false)
    : ServerMessage("file_changed");

// ContextTokens — размер контекста ПОСЛЕДНЕГО запроса к API за ход (input + cache_read +
// cache_creation из usage последнего assistant-сообщения основного агента). Именно он, а не
// Usage: тот суммирует ВСЕ запросы хода (каждый шаг tool-лупа плюс сабагенты), поэтому годится
// для стоимости, но как оценка заполнения окна завышает её кратно числу тул-вызовов.
public record ResultMessage(string Subtype, long DurationMs, int NumTurns, UsageInfo? Usage, double? TotalCostUsd, string? ApiErrorStatus = null, IReadOnlyList<string>? PermissionDenials = null, int? ContextTokens = null, string? UsageModel = null)
    : ServerMessage("result");

// Фактически списанная стоимость генерации fal.ai. Приходит асинхронно после tool_result:
// сервер опрашивает fal.ai billing-events по request_id (см. FalCostService).
public record FalCostMessage(string RequestId, string? EndpointId, double CostUsd, double? OutputUnits = null, double? UnitPrice = null)
    : ServerMessage("fal_cost");

// Учёт завершённой glif-генерации: приходит синхронно из tool_result (кредиты есть в _meta.glif).
// Контракт с фронтом: camelCase (jobId, outputType, mediaCount, credits, model).
public record GlifCostMessage(string JobId, string? OutputType, int MediaCount, double? Credits = null, string? Model = null)
    : ServerMessage("glif_cost");

// Ответ оборван по лимиту токенов (assistant stop_reason == max_tokens)
public record TruncatedMessage() : ServerMessage("truncated");

// Скрытое (зашифрованное) размышление — блок redacted_thinking
public record RedactedThinkingMessage() : ServerMessage("redacted_thinking");

// ExpectResultFollows — синтетический ErrorMessage из ветки is_error (ClaudeSession):
// следом БЕЗУСЛОВНО идёт ResultMessage того же хода, поэтому терминальные обработчики
// «конец хода» (штаб, цикл «до готово») не должны дёргаться на нём дважды — см.
// SessionManager.OnMessageAsync, SessionEntry.SkipNextTeamTurnEnd.
public record ErrorMessage(string Text, bool ExpectResultFollows = false)
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

// Terminal PTY: вывод от сервера к клиенту (Data = фрагмент текста)
public record TerminalOutputMessage(string Data, bool IsError = false, string? TerminalId = null)
    : ServerMessage("terminal_output");

// Terminal PTY: смена статуса (starting/running/stopped/error)
public record TerminalStatusMessage(string Status, int? ExitCode = null, string? TerminalId = null)
    : ServerMessage("terminal_status");

// Terminal PTY: терминал переименован
public record TerminalRenamedMessage(string TerminalId, string Name)
    : ServerMessage("terminal_renamed");

// Чат авто-переименован (локальная модель уточнила заголовок по первому сообщению).
// Topic — имя lucide-компонента (PascalCase), фронт рисует по нему иконку. Смена ТОЛЬКО
// значка шлёт это же сообщение с прежним Name — отдельного события не заводим.
public record ChatRenamedMessage(string Name, string? Topic = null)
    : ServerMessage("chat_renamed");

// Preview dev-server: смена статуса конкретного сервиса
public record PreviewStatusMessage(string Status, int? Port = null, string? Error = null, string? ServiceId = null)
    : ServerMessage("preview_status");

// Вывод дев-сервера (Data — накопленный за тик фрагмент текста с CRLF). Уходит только
// подписчикам группы конкретного сервиса, см. DevServerService.LogGroup.
// stdout и stderr склеены в порядке появления: разделять их флагом было бы враньём —
// в батче за тик перемешаны оба потока.
public record PreviewLogMessage(string ServiceId, string Data)
    : ServerMessage("preview_log");

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

// Изменение git-статуса проекта (commit/stage/unstage/checkout/discard) — в группу
// user_{userId}. Без payload: клиент сам перезапрашивает GET git/status (не спамим).
public record GitStatusChangedMessage(string ProjectId)
    : ServerMessage("git_status_changed");

// Авто-коммит хода Claude (документный режим) — в группу сессии: чат показывает плашку
// «Изменения сохранены» со ссылкой на просмотр коммита.
// SessionId — унаследованное свойство ServerMessage, задаётся отправителем через init:
// позиционным параметром его объявлять нельзя (одноимённое свойство базы не перекрывается,
// и значение молча терялось бы — CS8907).
public record GitTurnCommitMessage(string ProjectId, string Sha, string Subject)
    : ServerMessage("git_turn_commit");

// Изменение персон — created/updated/deleted — в группу user_{userId},
// чтобы все устройства обновили раздел «Персоны».
public record PersonasChangedMessage(string Action, string? PersonaId = null)
    : ServerMessage("personas_changed");

// Изменение общей памяти команды проекта (added/updated/removed) — в группу user_{userId},
// чтобы вкладка «Память» командного центра обновилась на всех устройствах.
public record TeamMemoryChangedMessage(string Action, string ProjectId, string? EntryId = null)
    : ServerMessage("team_memory_changed");

// Онбординг завершён (фича default-personas-onboarding): дефолт-персона назначена из
// онбординг-сессии. Kind — "user" | "project" (см. OnboardingKinds), PersonaId — назначенная
// дефолт-персона, ProjectId — проект онбординг-сессии (null у пользовательского).
// Эфемерное: в history не пишется; фронт снимает гейт по концу хода (result) или кнопке,
// а не по этому событию mid-turn.
public record OnboardingCompletedMessage(string Kind, string PersonaId, string? ProjectId = null)
    : ServerMessage("onboarding_completed");

// Смена активного спикера группового чата (@упоминание переключило персону-собеседника).
// Label — готовая подпись «Роль (Имя)» для разделителя «Теперь отвечает: …».
public record SpeakerChangedMessage(string PersonaId, string Label)
    : ServerMessage("speaker_changed");

// Состояние цикла «до готово» (флаг work-loop): активность, номер итерации, лимит,
// фаза (working/verifying) — для тумблера в композере и счётчика в шапке чата.
public record WorkLoopMessage(bool Active, int Iteration, int MaxIterations, string? Phase)
    : ServerMessage("work_loop");

// Явная остановка цикла «до готово» в ленту (B5, см. StoredWorkLoopStoppedMessage) — WorkLoopMessage
// гасит только бейдж, тут — человекочитаемый текст. Reason ∈ limit|error|manual (контракт с фронтом).
public record WorkLoopStoppedMessage(string Reason, string Text)
    : ServerMessage("work_loop_stopped");

// Карточка плана режима «Командная реализация» (Э2): структурный план в ленту штаба.
// Аналог plan_review, но план — объект (под-задачи, исполнители, обоснование, волны),
// а не текст. Ответ — SessionHub.RespondTeamPlan. Событие переиздаётся при смене
// исполнителя (Reassign), поэтому клиент сверяет карточку по PlanId.
// Автор карточки (шапка «аватар + имя») — Plan.PlannerPersonaId, уже вложенный в план
// с Э2; отдельного поля на верхнем уровне сообщения не нужно.
public record TeamPlanMessage(
        string PlanId,
        Models.TeamImplementPlan Plan,
        bool Resolved,
        bool? Approved,
        // Версия, заменившая эту карточку (перепланирование): фронт рисует «заменена
        // версией vN» вместо «план отменён». null — прочие исходы (запуск, отмена, открыта).
        int? SupersededBy = null)
    : ServerMessage("team_plan");

// Карточка остановки режима «Командная реализация» (Э4): блокер исполнителя, провал задачи,
// исчерпанный бюджет, зависшая волна… Kind — wire-токен триггера, Actions — кнопки решения.
// Событие переиздаётся при ответе человека (Resolved=true), клиент сверяет по EscalationId.
public record TeamEscalationMessage(
        string EscalationId,
        string Kind,
        string Title,
        string Details,
        IReadOnlyList<Models.TeamEscalationAction> Actions,
        string? TaskId,
        int Wave,
        bool Resolved,
        string? ChosenActionId,
        // Автор карточки (Э8): координатор на момент публикации — карточка идёт от его лица
        string? PersonaId = null)
    : ServerMessage("team_escalation");

// Жизненный цикл планировщика «Командной реализации» (Э2). Транзитное событие для ленты
// штаба: показывает спиннер «Штаб планирует…» и снимает его по факту результата.
// В историю не пишется — карточка плана (TeamPlanMessage) или карточка отказа
// (TeamEscalationMessage) уже там, дублировать не надо; при рестарте сервера просто
// пропадает (карточка подтянется через /api/.../history). Событие НЕ переиздаётся по
// смене состояния — клиент по одному сообщению знает и факт, и результат.
//  • Start=true           — планировщик запущен. Прочие поля диагностические (для логов).
//  • Start=false, Success=true  — план собран, см. SubtaskCount/WaveCount/Route/ElapsedMs.
//  • Start=false, Success=false — отказ, см. Failure (тот же текст, что в карточке отказа).
// Failure == null у успеха; PromptChars/ResponseChars — диагностика для лога фронта
// (карточка их не показывает, но клик по «что случилось» в dev-режиме открывает подробности).
public record TeamPlanningMessage(
        bool Start,
        bool Success,
        int SubtaskCount,
        int WaveCount,
        long ElapsedMs,
        // Описание маршрута, как в логе: «model=nemotron:free», «tier=strong», «claude», «local».
        string? Route,
        // Причина отказа (текст для человека): «Планировщик не уложился во время»,
        // «Планировщик не уместил план в лимит вывода», «Планировщик вернул неразборчивый план».
        // null у Start=true и у Success=true.
        string? Failure,
        int PromptChars = 0,
        int ResponseChars = 0)
    : ServerMessage("team_planning");

// Состояние режима «Командная реализация»: для бейджа в композере
// и маркера в списке чатов. Stage — wire-токен стадии (planning/confirming/wave/…).
// PlannedWaves — плановое число волн текущей итерации (Э3): бейдж «волна N из M» берёт
// M отсюда; 0 — план ещё не запускался (тогда M показывать нечем).
public record TeamImplementMessage(
        bool Active,
        string? Stage,
        int WaveNumber,
        bool AutoWaves,
        string? CoordinatorPersonaId,
        string? PlannerPersonaId,
        IReadOnlyList<string>? ExecutorPersonaIds,
        Models.TeamImplementBudget? Budget,
        string? PlanCardId,
        int PlannedWaves = 0,
        bool CoordinatorNoCode = true,
        // Человек нажал «Остановить» (Э4): новые волны не стартуют, пока он не продолжит.
        // Отдельно от стадии: практика может ждать решения и без остановки (блокер, провал).
        bool Stopped = false,
        // Штаб держит чат в план-режиме (Э8): селектор режима в композере показывает «план»
        // и заблокирован до согласования плана. Отдельно от стадии: у провайдера без
        // поддержки плана режим не навязывается, и блокировать селектор не за что.
        bool ModeLocked = false,
        // Версия текущего плана итерации (Э8): карточка «План v2 · обновлён после уточнений».
        // 0 — планов ещё не было.
        int PlanVersion = 0)
    : ServerMessage("team_implement");

// Чат переключён на другой аккаунт/провайдер. Auto=true — тихий фейловер внутри пула
// подписок Claude (та же модель и эндпоинт, в ленту не попадает); иначе — явная миграция
// на стороннего провайдера, Label — подпись разделителя «Продолжено на …».
// Reason — структурированная причина автоподмены (wire-значения FallbackErrorClass:
// rate_limit | usage_limit | provider_error | unreachable). Label остаётся текстом
// разделителя, а по Reason фронт показывает каноническую формулировку подсказки
// («Исчерпан лимит», «Провайдер выключен», «Эндпоинт недоступен») вместо сырого
// текста маркера. null — подмена не автоматическая либо причина неизвестна.
public record ProviderSwitchedMessage(string Provider, string? Model = null, string? Label = null,
    bool Auto = false, string? Reason = null)
    : ServerMessage("provider_switched");

// Лимит подписки исчерпан: предложение продолжить чат карточкой с кнопками в ленте —
// либо на другом здоровом аккаунте того же пула подписок (Kind="subscription", та же
// модель и эндпоинт, но своя предоплата), либо на стороннем провайдере (Kind="provider",
// дефолт — старое поведение, TierLabel/Utilization не заполняются). Providers — доступные
// варианты: Key — ключ подписки пула ИЛИ ключ стороннего провайдера (различается Kind),
// DisplayName — имя для кнопки, Model — модель, с которой пойдёт продолжение (у аккаунтов
// пула это модель текущего чата — она не меняется, меняется только аккаунт).
public record ProviderFallbackOption(
    string Key,
    string DisplayName,
    string Model,
    string Kind = "provider",
    string? TierLabel = null,
    double? Utilization = null);
public record ProviderLimitMessage(string? ResetsAt, IReadOnlyList<ProviderFallbackOption> Providers)
    : ServerMessage("provider_limit");

// Пользовательское уведомление (напоминание о задаче, событие Claude-исполнителя и т.п.) —
// в группу user_{userId}: открытое приложение показывает тост + сохраняет в центр уведомлений.
// Kind — семантика для иконки/цвета: reminder | claude | info | success
// NotificationId — id в NotificationStore (для mark-read/delete через тост).
// NotifType — подтип: task_reminder | execution_started | execution_completed | briefing | summary | ...
// Именно NotifType, а не Type: одноимённый с базовым ServerMessage.Type позиционный параметр
// свойство не создаёт, значение молча терялось (CS8907) — фронт читает поле notifType.
// SessionId — унаследованное свойство базы, задаётся отправителем через init (та же причина).
public record NotificationMessage(string Title, string Body, string? Url = null,
    string Kind = "info", string? NotificationId = null, string? NotifType = null,
    string? ProjectId = null, string? TaskId = null,
    string? Source = null, string? Tag = null,
    // Атрибуция персоны (для аватара/лица в тосте, центре и web-push) и имя проекта.
    // Денормализуются в NotificationService по PersonaId/ProjectId — отправители шлют id.
    string? PersonaId = null, string? PersonaName = null, string? PersonaRole = null,
    string? PersonaColor = null, bool PersonaHasAvatar = false, string? ProjectName = null)
    : ServerMessage("notification");

// Манифест recall (F3): что персона подтянула в ход из памяти/заметок/базы/команды — для
// атрибуции «опирается на…» / «использовано сейчас». Kind ∈ memory|note|knowledge|team.
public record RecallItemDto(string Kind, string? Ref, string Title, string? Snippet);
public record RecallManifestMessage(IReadOnlyList<RecallItemDto> Items)
    : ServerMessage("recall_manifest");

// Снимок промпта хода записан: id для кнопки «какой промпт ушёл» под постом.
// Текст по SignalR не гоняем — фронт забирает его отдельным REST-запросом при открытии.
// Applied=false — ход доигрывался в живом процессе, и этот промпт модели не уходил;
// действует снимок старта прогона (InheritedFromId).
public record PromptSnapshotMessage(string SnapshotId, bool Applied, string? InheritedFromId = null)
    : ServerMessage("prompt_snapshot");

// Подсказка следующего сообщения: текст от claude CLI после хода.
// Эфемерное событие — в history.json не пишется (нет case в OnMessageAsync и StoredMessage).
public record PromptSuggestionMessage(string Text)
    : ServerMessage("prompt_suggestion");

// Сообщения от клиента к серверу
public record ClientMessage([property: JsonPropertyName("type")] string Type);

public record SendMessageRequest(string Text, string[]? AttachedPaths = null) : ClientMessage("send_message");

public record PermissionDecisionRequest(string RequestId, string Behavior) : ClientMessage("permission_decision");

public record InterruptRequest() : ClientMessage("interrupt");
