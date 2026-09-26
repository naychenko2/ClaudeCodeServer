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
[JsonDerivedType(typeof(StoredTeamPlanMessage), "team_plan")]
[JsonDerivedType(typeof(StoredTeamEscalationMessage), "team_escalation")]
[JsonDerivedType(typeof(StoredFileChangedMessage), "file_changed")]
[JsonDerivedType(typeof(StoredResultMessage), "result")]
[JsonDerivedType(typeof(StoredFalCostMessage), "fal_cost")]
[JsonDerivedType(typeof(StoredGlifCostMessage), "glif_cost")]
[JsonDerivedType(typeof(StoredCompactBoundaryMessage), "compact_boundary")]
[JsonDerivedType(typeof(StoredContextPrunedMessage), "context_pruned")]
[JsonDerivedType(typeof(StoredErrorMessage), "error")]
[JsonDerivedType(typeof(StoredWorkflowProgressMessage), "workflow_progress")]
[JsonDerivedType(typeof(StoredWorkLoopStoppedMessage), "work_loop_stopped")]
[JsonDerivedType(typeof(StoredModelSwitchedMessage), "model_switched")]
[JsonDerivedType(typeof(StoredBranchedFromMessage), "branched_from")]
[JsonDerivedType(typeof(StoredInterruptedMessage), "interrupted")]
[JsonDerivedType(typeof(StoredImageLaunchMessage), "image_launch")]
[JsonDerivedType(typeof(StoredImageFileMovedMessage), "image_file_moved")]
public abstract class StoredMessage { }

public class StoredUserMessage(string text, string[]? attachedPaths = null, bool? viaAgent = null,
    string? senderPersonaId = null, bool? systemDirective = null, bool? auto = null,
    string? senderOrigin = null, string? senderChatName = null, string? staffNote = null,
    long? timestamp = null, string? delegationTaskId = null) : StoredMessage
{
    // Когда отправлено сообщение (Unix-мс UTC) — см. StoredTextMessage.Timestamp
    public long? Timestamp { get; init; } = timestamp;

    // Снимок промпта, собранного для этого хода (кнопка «какой промпт ушёл» под постом).
    // Заполняется НЕ при создании: снимок пишется позже, уже при запуске хода — поэтому set,
    // а не init (см. TurnAccumulator.SetPromptSnapshot). null — история до этого поля,
    // ход без нового сообщения (продолжение цикла «до готово») либо сбой записи снимка.
    public string? PromptSnapshotId { get; set; }

    // Источник входящего сообщения, когда оно пришло ИЗ ДРУГОГО места: имя чужого проекта
    // либо «Вне проектов». Заполняет сервер, сравнив проекты отправителя и получателя;
    // null — источник тот же, чип в UI не нужен
    public string? SenderOrigin { get; init; } = senderOrigin;
    // Имя чата-отправителя — подпись карточки, когда персоны у него нет: «Задача: починить
    // билд» отвечает на вопрос «кто пишет» лучше, чем безликое «Агент»
    public string? SenderChatName { get; init; } = senderChatName;
    // Служебный ход механики штаба (ответ на карточку, возврат в интервью, сводка волны):
    // UI рисует компактную плашку-разделитель с этой подписью вместо пузыря «Автоматически»
    // с сырым текстом директивы. null — обычное сообщение
    public string? StaffNote { get; init; } = staffNote;

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
    // Задача, о завершении которой докладывает это сообщение — см.
    // StoredTextMessage.DelegationTaskId (доклад из чата-исполнителя без персоны
    // приходит пользовательским сообщением)
    public string? DelegationTaskId { get; init; } = delegationTaskId;
    // Снимок холста чата картинки (ADR-018 §3): приложен ли он к этому сообщению и на какой
    // ревизии холста. По нему лента пишет «холст не менялся — снимок не приложен».
    // null — обычный чат либо история до этого поля.
    public StoredImageSnapshot? ImageSnapshot { get; init; }
}

public record StoredImageSnapshot(string Revision, bool Attached);

public class StoredSessionStartedMessage(string model, string mode, TurnWorktreeInfo? turnWorktree = null) : StoredMessage
{
    public string Model { get; init; } = model;
    public string Mode { get; init; } = mode;
    // Ход шёл в чужом дереве (см. TurnWorktreeInfo) — null у записей до этого поля
    // десериализуется штатно (System.Text.Json подставляет default для отсутствующих свойств)
    public TurnWorktreeInfo? TurnWorktree { get; init; } = turnWorktree;
}

public class StoredTextMessage(string text, string? personaId = null, string? parentToolUseId = null,
    long? timestamp = null, string? delegationTaskId = null) : StoredMessage
{
    public string Text { get; init; } = text;
    // Задача, о завершении которой докладывает эта реплика (доклад по делегированной
    // задаче, модель Z): связь структурная, а не вытащенная из текста маркера — карточка
    // доклада по ней открывает задачу. null — обычная реплика либо история до этого поля.
    public string? DelegationTaskId { get; init; } = delegationTaskId;
    // Персона, от лица которой написан ответ (на момент хода) — чтобы после смены
    // собеседника у старых реплик оставался прежний аватар. null — обычный ассистент.
    public string? PersonaId { get; init; } = personaId;
    // Текст сабагента (Task/Agent): ссылка на родительский tool_use — рендерится внутри
    // его карточки, а не в основной ленте. null — текст основного агента.
    public string? ParentToolUseId { get; init; } = parentToolUseId;
    // Когда написан пост (Unix-мс UTC) — подпись времени в панели действий поста.
    // Именно число, а не DateTimeOffset: живая лента ставит время на фронте (Date.now()),
    // и разнотипица «строка из истории vs число из ленты» ломала бы форматтер.
    // null — история до этого поля.
    public long? Timestamp { get; init; } = timestamp;
    // Модель, которой написан пост. Заполняется НЕ при создании (в середине хода
    // фактическая модель ещё не известна), а backfill'ом в OnResultAsync по
    // ResultMessage.UsageModel — поэтому set, а не init. null — история до поля,
    // текст сабагента либо ход без модели в usage.
    public string? Model { get; set; }
}

public class StoredThinkingMessage(string text, string? parentToolUseId = null) : StoredMessage
{
    public string Text { get; init; } = text;
    // См. StoredTextMessage.ParentToolUseId
    public string? ParentToolUseId { get; init; } = parentToolUseId;
}

public class StoredFileChangedMessage(string path, int added, int removed, bool external = false) : StoredMessage
{
    public string Path { get; init; } = path;
    public int Added { get; init; } = added;
    public int Removed { get; init; } = removed;
    // См. FileChangedMessage.External — сохраняется, чтобы после перезагрузки ленты
    // (F5, повторная загрузка истории) пометка «вне чата» и снятая кнопка «Откатить» не терялись
    public bool External { get; init; } = external;
}

public class StoredResultMessage(string subtype, long durationMs, int numTurns,
    UsageInfo? usage = null, double? totalCostUsd = null, string? apiErrorStatus = null,
    IReadOnlyList<string>? permissionDenials = null, int? contextTokens = null,
    long? durationApiMs = null) : StoredMessage
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
    // Время запросов к API за ход — см. ResultMessage.DurationApiMs. В историях до этого
    // поля null: скорость у старых ходов считается по полному времени хода.
    public long? DurationApiMs { get; init; } = durationApiMs;
    // Точный якорь границы хода для ветвления чата (фича chat-branch, §4 документа-основания):
    // uuid последней записи транскрипта CLI на момент конца этого хода
    // (TranscriptProbe.LastRecordUuid). Пока его нет, границу приходится искать текстовым
    // сопоставлением сообщений истории с промптами транскрипта — оно честно отказывает
    // примерно на каждом десятом шаге. null — история до этого поля (исторические чаты
    // так и ветвятся текстовым путём), транскрипт не найден либо хвост не прочитался.
    public string? TranscriptTailUuid { get; init; }
}

public class StoredErrorMessage(string text) : StoredMessage
{
    public string Text { get; init; } = text;
    // Сырой технический текст сбоя (ErrorMessage.Details): в карточке живёт под
    // «Подробностями» и переживает F5. null — деталей нет (старые записи в том числе).
    public string? Details { get; init; }
    // Когда случился сбой (Unix-мс UTC) — см. StoredTextMessage.Timestamp. По нему фронт
    // отделяет ошибки прошлых дней (склеиваются в кат «Завершённые с ошибкой») от
    // сегодняшних (полноразмерный баннер). null — история до этого поля: дату фронт
    // подставляет от предыдущего сообщения хода (см. normalizeHistory).
    public long? Timestamp { get; init; }
}

// Явная остановка цикла «до готово» (B5): лимит итераций/ошибка хода/ручной стоп — иначе в
// ленте гаснет только бейдж, и не видно, доделана работа или брошена. Живёт в истории — как
// и остальные внеходовые записи (см. StoredTeamEscalationMessage), переживает перезагрузку.
// Reason ∈ limit|error|manual — контракт для фронта.
public class StoredWorkLoopStoppedMessage(string reason, string text) : StoredMessage
{
    public string Reason { get; init; } = reason;
    public string Text { get; init; } = text;
}

// Отметка «Ход остановлен пользователем»: ход оборвал ЧЕЛОВЕК («Стоп», «Прервать и
// отправить», сообщение в ход, ждавший его ответа). Убитый ход не присылает ни result, ни
// error — без этой записи после F5 и на другом устройстве в ленте висел бы вопрос без
// ответа, неотличимый от зависшего хода. Прочие обрывы (падение процесса, перезапуск хода
// штабом, снятие задачи) её не пишут — у них своя запись либо её нет вовсе.
// Timestamp — Unix-мс UTC, как у StoredErrorMessage.
public class StoredInterruptedMessage(long? timestamp = null) : StoredMessage
{
    public long? Timestamp { get; init; } = timestamp;
}

// Граница компакции контекста — чтобы после перезагрузки страницы оценка заполнения не врала
public class StoredCompactBoundaryMessage(string trigger, int? preTokens, int? postTokens = null) : StoredMessage
{
    public string Trigger { get; init; } = trigger;
    public int? PreTokens { get; init; } = preTokens;
    public int? PostTokens { get; init; } = postTokens;
}

// Обрезка контекста прокси локальной модели — чтобы карточка пережила перезагрузку страницы.
// Поля — ровно как у ContextPrunedMessage (протокол): расхождение форм после перезагрузки
// давало бы карточку без цифр.
//
// Единственное расхождение вынужденное: вид карточки ("prune"/"compact_cloud") в ЛЕНТЕ
// приезжает полем `kind`, а в ИСТОРИИ — полем `pruneKind`. Имя `kind` здесь занято
// дискриминатором полиморфизма StoredMessage, и свойство с таким же именем роняет
// сериализацию ВСЕЙ истории целиком (InvalidOperationException при первом же сохранении),
// а не только этой записи.
//
// EventId — та же личность сдвига, что в ContextPrunedMessage: она едет и в историю, иначе
// после перезагрузки страницы дедуп ленты потерял бы точку сравнения. У карточек, записанных
// до её появления, поля нет — null, и дедуп по нему НЕ работает (две старые записи с
// одинаковым null схлопнулись бы в одну).
public class StoredContextPrunedMessage(string kind, int tokensBefore, int tokensAfter, int blocks,
    int resultBlocks, int inputBlocks, int thinkingBlocks, double? prefillSeconds = null,
    int? cacheReadTokens = null, int? promptTokens = null, string? eventId = null) : StoredMessage
{
    [JsonPropertyName("pruneKind")]
    public string Kind { get; init; } = kind;
    public string? EventId { get; init; } = eventId;
    public int TokensBefore { get; init; } = tokensBefore;
    public int TokensAfter { get; init; } = tokensAfter;
    public int Blocks { get; init; } = blocks;
    public int ResultBlocks { get; init; } = resultBlocks;
    public int InputBlocks { get; init; } = inputBlocks;
    public int ThinkingBlocks { get; init; } = thinkingBlocks;
    public double? PrefillSeconds { get; init; } = prefillSeconds;
    public int? CacheReadTokens { get; init; } = cacheReadTokens;
    public int? PromptTokens { get; init; } = promptTokens;
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

// Учёт завершённой glif-генерации (кредиты из _meta.glif), приходит вне хода — отдельная запись
public class StoredGlifCostMessage(string jobId, string? outputType, int mediaCount,
    double? credits = null, string? model = null) : StoredMessage
{
    public string JobId { get; init; } = jobId;
    public string? OutputType { get; init; } = outputType;
    public int MediaCount { get; init; } = mediaCount;
    public double? Credits { get; init; } = credits;
    public string? Model { get; init; } = model;
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

// Карточка плана режима «Командная реализация» (Э2). В отличие от plan_review план
// СТРУКТУРНЫЙ: под-задачи с исполнителями, обоснованием, файлами и волнами. Смена
// исполнителя до запуска перезаписывает Plan (карточка остаётся открытой),
// Resolved/Approved заполняются при «Запустить»/«Отменить».
public class StoredTeamPlanMessage : StoredMessage
{
    public string PlanId { get; init; } = "";
    public Models.TeamImplementPlan Plan { get; set; } = new();
    public bool Resolved { get; set; }
    public bool? Approved { get; set; }
    // Версия плана, заменившая эту карточку (перепланирование по правке человека или
    // clarify): карточка погашена НЕ решением человека, а выходом новой версии — фронт
    // рисует её «заменена версией vN», а не «план отменён». null — отмена человеком,
    // запуск либо карточка ещё открыта.
    public int? SupersededBy { get; set; }
    // Автор карточки (Э8): планировщик на момент публикации. В истории — чтобы после
    // рестарта и смены координатора карточка осталась речью того, кто её написал.
    public string? PersonaId { get; set; }
}

// Карточка остановки режима «Командная реализация» (Э4): причина и кнопки решения.
// Живёт в истории — иначе после перезагрузки страницы человек не увидел бы, чего от него
// ждёт вставшая практика (а молчаливых остановок в этом режиме быть не должно).
public class StoredTeamEscalationMessage : StoredMessage
{
    public string EscalationId { get; init; } = "";
    public Models.TeamEscalation Escalation { get; set; } = new();
}

// Пометка «Ответила …» при автоподмене модели в фолбэке (тот же логический смысл, что
// живая пилюля model_switched в ленте: провалившаяся попытка перед подменой уже прислала
// session_started с PreviousModel, фактически ответила новая модель). Без записи в
// историю после F5 / рестарта человек не увидит, что отвечала не та модель. PreviousModel
// — модель последнего session_started этого чата на момент подмены; Model — новая
// фактическая. Reason — канонический класс ошибки (rate_limit | usage_limit |
// provider_error | unreachable | context_overflow) для подсказки; null — на проводе отсутствует (старая запись
// либо подмена без Reason).
public class StoredModelSwitchedMessage : StoredMessage
{
    public string Model { get; init; } = "";
    public string PreviousModel { get; init; } = "";
    public string? Reason { get; init; }
    // Сырой текст промежуточной ошибки, погашенной этой подменой (ProviderSwitchedMessage.
    // ErrorDetails): без записи в историю после F5 «Подробности» маркера опустели бы.
    public string? Details { get; init; }
}

// Плашка «Ветка от {имя чата}» в ленте нового чата, созданного ветвлением (фича
// chat-branch). Это запись ИСТОРИИ, а не живое событие: она обязана переживать F5 и
// рестарт сервера (как model_switched, а не как provider_switched). SourceSessionId —
// id оригинального чата, SourceName — его имя (снимок на момент ветвления), Timestamp —
// Unix-мс UTC (см. StoredTextMessage.Timestamp).
public class StoredBranchedFromMessage : StoredMessage
{
    public string SourceSessionId { get; init; } = "";
    public string SourceName { get; init; } = "";
    public long? Timestamp { get; init; }
}

// Тихая строка «Вы запустили: «…» · FLUX Fill · ≈ $0.10 · 2 варианта» в чате картинки
// (ADR-018 §2). Пишется в history.json, а не в транскрипт CLI: модель её НЕ видит, о ручном
// запуске она узнаёт из блока состояния хода. By — ImageEditInitiator строкой ("human" |
// "agent"): у истории нет конвертера enum'ов. Estimate — котировка на момент запуска.
public class StoredImageLaunchMessage : StoredMessage
{
    public string By { get; init; } = "human";
    public string Prompt { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public int Count { get; init; }
    public Services.ImageEditor.ImageEditEstimateDto? Estimate { get; init; }
    public string JobId { get; init; } = "";
    public long? Timestamp { get; init; }
}

// Тихая строка «Сохранено как … Редактор перешёл на этот файл, чат — вместе с ним»
// (ADR-018 §1): чат картинки переехал на новый путь. Пути — от корня проекта.
public class StoredImageFileMovedMessage : StoredMessage
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public long? Timestamp { get; init; }
}
