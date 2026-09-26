namespace ClaudeHomeServer.Services.ImageEditor;

// REST-контракт редактора картинок (ADR-017, разделы 2, 4, 5, 7). Маршруты —
// api/projects/{projectId}/image-editor/*, см. ImageEditorController. Enum'ы уходят
// строками в camelCase (глобальный JsonStringEnumConverter хоста).

// ── Каталог: GET …/catalog ─────────────────────────────────────────────────────

// Provider = null — ни одного доступного поставщика: экран «Рисование не настроено»
public record ImageEditDefaultDto(string? Provider, string Model);

// У пункта «Авто» есть Modes и нет Caps; у конкретной модели наоборот
public record ImageEditModelDto(
    string Id,
    string Label,
    IReadOnlyList<EditMode>? Modes,
    ImageEditCaps? Caps,
    ImageEditPriceHint? PriceHint);

public record ImageEditProviderDto(
    string Key,
    string Label,
    string PriceUnit,
    IReadOnlyList<ImageEditModelDto> Models);

// HeavyFileMb — порог «тяжёлого файла» для диалога сохранения (ImageEditor:HeavyFileMb,
// ADR-018 §9): больше — предложить сжатие, но не блокировать
public record ImageEditLimitsDto(int MaxFileMb, int MaxReferences, int MaxCount, int HeavyFileMb = 5);

// Reason — почему список поставщиков пуст (ImageEditCatalogReasons); null, если есть хоть один
public record ImageEditCatalogDto(
    ImageEditDefaultDto Default,
    IReadOnlyList<ImageEditProviderDto> Providers,
    ImageEditLimitsDto Limits,
    string? Reason = null);

public static class ImageEditCatalogReasons
{
    // Ни fal, ни Higgsfield не настроены
    public const string NoProviderConfigured = "no_provider_configured";
    // Подсистема картинок выключена тумблером Subsystems:images:Enabled
    public const string SubsystemDisabled = "subsystem_disabled";
}

// ── Котировка: POST …/quote ────────────────────────────────────────────────────

// Выбор человека (поставщик + модель или «auto» с режимом) и признаки запроса.
// HasAnnotations — стрелки, рамки, подписи на холсте (кисть сюда не входит — это HasMask);
// Removal — запрос просит стереть отмеченное (фронт считает так же, как EditIntent.IsRemoval).
// Ничего не тратит.
public record ImageEditQuoteRequest(
    string Provider,
    string Model,
    EditMode Mode,
    ImageEditOp Op,
    int Count,
    bool HasMask,
    int References,
    bool HasCharacter,
    int? Width,
    int? Height,
    bool HasAnnotations = false,
    bool Removal = false);

// Source: ImageEditEstimateSources.*; Amount = null — «цена станет известна после запуска»
public record ImageEditEstimateDto(double? Amount, string Unit, bool Approx, string Source);

// Model — уже развёрнутая модель (не «auto»): запуск исполняет ровно эту пару.
// ExpectedSeconds — для процентов на фронте: у поставщиков процентов нет.
public record ImageEditQuoteDto(
    string QuoteId,
    string Provider,
    string Model,
    ImageEditEstimateDto Estimate,
    DateTime ExpiresAt,
    int? ExpectedSeconds);

public static class ImageEditEstimateSources
{
    public const string Provider = "provider";
    public const string Catalog = "catalog";
    public const string History = "history";
    public const string Unknown = "unknown";
}

// ── Задача: POST …/jobs, GET/DELETE …/jobs/{jobId} ─────────────────────────────

// То, что контроллер собрал из multipart: байты уже прочитаны, пути образцов из проекта
// уже разрешены через SafePath.Join. Вертикаль ProjectManager не видит.
// Character — подключённый персонаж; его фото уже лежат в References с ролью Character.
public record ImageEditJobInput(
    string QuoteId,
    string Prompt,
    string? MarksJson,
    ImageBytes? Source,
    ImageBytes? Mask,
    ImageBytes? Annotated,
    IReadOnlyList<ReferenceImage> References,
    string? SourcePath,
    CharacterRef? Character = null,
    // Возврат размера оригинала после скачивания (ADR-018 §9, п. 4)
    bool MatchSourceSize = true,
    // Чат картинки, из которого запущена задача (ADR-018 §2); null — запуск вне чата
    string? ChatSessionId = null,
    ImageEditInitiator Initiator = ImageEditInitiator.Human,
    // Шаг истории, с которого запущена правка: родитель шагов из её вариантов (ADR-018 §9)
    string? BaseStepId = null,
    // Пропорции результата («Дорисовать за края»): 1:1, 16:9, 9:16; null — на усмотрение драйвера
    string? AspectRatio = null);

public record ImageEditJobCreatedDto(string JobId);

public enum ImageEditJobStatus { Queued, Running, Downloading, Completed, Failed, Cancelled, Interrupted }

// Кто запустил задачу или написал промпт: человек в редакторе или агент чата картинки
public enum ImageEditInitiator { Human, Agent }

// Variants — номера готовых вариантов (для …/jobs/{jobId}/variants/{n}).
// ChatSessionId — чат картинки, из которого запущена задача (ADR-018 §2); имя не SessionId,
// чтобы в событиях не столкнуться с ServerMessage.SessionId, по которому фронт роутит ленту.
public record ImageEditJobDto(
    string JobId,
    string ProjectId,
    ImageEditJobStatus Status,
    string Provider,
    string Model,
    IReadOnlyList<int> Variants,
    EditCost? Cost,
    EditOutcome? Outcome,
    bool? Charged,
    string? Error,
    int? QueuePosition,
    DateTime CreatedAt,
    string? ChatSessionId = null,
    ImageEditInitiator Initiator = ImageEditInitiator.Human,
    string? BaseStepId = null,
    // Пометка возврата размера (ImageEditSizeNotes.*); null — размер приведён или не требовался
    string? SizeNote = null);

public static class ImageEditSizeNotes
{
    public const string AspectMismatch = "Размер не приведён: другие пропорции";
}

// ── Сохранение: POST …/save, GET …/save/check ──────────────────────────────────

// Флага перезаписи нет по построению: результат всегда новым файлом (hero.v2.png).
// Mode = ImageEditSaveModes.*; null — прежнее поведение (NextVersion).
// NextVersion: SourcePath — правка файла проекта; Folder + FileName — «Нарисовать картинку».
// As («Сохранить как…», ADR-018 §5): Folder + FileName, расширение сервер ставит сам по
// формату; занятое имя — 409 name_taken с suggestion, а не тихий переход на номер.
// Источник — вариант задачи (JobId + Variant) ИЛИ шаг истории (StepId).
// ChatSessionId — чат картинки, который переезжает на новый файл вместе с редактором.
// Encode — перекодирование при записи; null — формат результата как есть.
public record ImageEditSaveRequest(
    string? JobId,
    int Variant,
    string? SourcePath,
    string? Folder,
    string? FileName,
    string? Mode = null,
    string? StepId = null,
    string? ChatSessionId = null,
    ImageEncodeSpec? Encode = null);

public static class ImageEditSaveModes
{
    public const string NextVersion = "next-version";
    public const string As = "as";
}

// Path — относительный путь созданного файла от корня проекта
public record ImageEditSaveResultDto(string Path);

// Проверка имени на лету. Path — итоговый путь с расширением по формату; Taken — имя занято;
// Suggestion — ближайшее свободное имя (hero.v3.png), null — если свободно
public record SaveCheckResponse(string Path, bool Taken, string? Suggestion);

// ── Правки без ИИ: POST …/transform[?dryRun=true] (ADR-018 §9) ─────────────────

// База правки — ровно одно из трёх: файл проекта (Path), шаг истории (StepId) или вариант
// задачи (JobId + Variant). Вариант с пустым Ops — «применить вариант»: он становится шагом
// той же ленты, родитель — BaseStepId задачи
public record ImageTransformBase(string? Path = null, string? StepId = null, string? JobId = null, int? Variant = null);

public record ImageTransformRequest(
    ImageTransformBase Base,
    IReadOnlyList<ImageTransformOp> Ops,
    ImageEncodeSpec? Encode = null);

// StepId = null при dryRun: шаг не записан, посчитан только вес
public record ImageTransformResponse(string? StepId, int Width, int Height, long Bytes);

// ── Чат картинки: …/image-editor/chats (ADR-018 §1) ────────────────────────────

// POST …/chats — создать чат картинки (и «Новый чат по этой картинке»); ответ — Session
public record ImageChatCreateRequest(string SourcePath, string? PersonaId = null);

// GET …/chats?path= — Current: чат с CurrentPath == path (null — нет);
// Continued: чаты, у которых path в Lineage (разговор ушёл на новую версию файла)
public record ImageChatLookupResponse(Models.Session? Current, IReadOnlyList<Models.Session> Continued);

// PUT …/chats/{sessionId}/path — привязать чат к другому файлу
public record ImageChatPathRequest(string Path);

// Образец в состоянии чата: путь проекта (или копия в рабочей папке для загруженных с
// компьютера) и роль
public record ImageChatReference(string Path, ReferenceRole Role, string? Label = null);

// Запись журнала «с прошлого хода»: Kind — ImageChatEventKinds.*, Text — строка для блока
// состояния хода; JobId — задача, к которой относится запись
public record ImageChatEvent(DateTime At, string Kind, string Text, string? JobId = null);

public static class ImageChatEventKinds
{
    public const string Launched = "launched";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Saved = "saved";
}

// Состояние редактора чата картинки на сервере (ImageChatStateStore, волна 2):
// PUT/GET …/chats/{sessionId}/state. Revision растёт с каждой записью; запись со старой
// Revision — 409. Marks — marks.json как есть. CanvasRevision — хеш CurrentPath, шага и
// пометок; LastSentRevision — ревизия, снимок которой уже ушёл в чат.
public record ImageChatState(
    string Prompt,
    ImageEditInitiator PromptAuthor,
    string? Provider,
    string? Model,
    EditMode Mode,
    int Count,
    IReadOnlyList<ImageChatReference> References,
    string? CharacterSlug,
    System.Text.Json.JsonElement? Marks,
    string? CanvasRevision,
    string? LastSentRevision,
    string? CurrentStepId,
    bool MatchSourceSize,
    IReadOnlyList<ImageChatEvent> Events,
    long Revision);

// ── Ошибки ─────────────────────────────────────────────────────────────────────

// Тело ошибки ручек: { error: "текст для человека", code: ImageEditErrorCodes.* }
public static class ImageEditErrorCodes
{
    // 409: поставщик не настроен или отключён админом — фронт перечитывает каталог
    public const string ProviderUnavailable = "provider_unavailable";
    // 400: модель не из каталога поставщика, число вариантов вне 1–4, битые входы
    public const string InvalidRequest = "invalid_request";
    // 404: котировка не найдена или истекла — фронт пересчитывает котировку
    public const string QuoteNotFound = "quote_not_found";
    // 404: задача не найдена или чужая (чужая неотличима от несуществующей)
    public const string JobNotFound = "job_not_found";
    // 404: шага истории нет, он истёк по TTL или чужой
    public const string StepNotFound = "step_not_found";
    // 404: персонажа нет в проекте (или slug не проходит белый список)
    public const string CharacterNotFound = "character_not_found";
    // 409: «Сохранить как…» на занятое имя; в теле ответа ещё suggestion — ближайшее свободное
    public const string NameTaken = "name_taken";
    // 429: потолок одновременных задач владельца или инстанса
    public const string TooManyJobs = "too_many_jobs";
    // 503: подсистема картинок выключена на этом сервере
    public const string Unavailable = "image_editor_unavailable";
    // 503: растра нет — подсистема картинок выключена, transform и запуск задач недоступны
    public const string RasterUnavailable = "raster_unavailable";
}

// Результат вызова шва: Value при успехе, иначе код ImageEditErrorCodes.* и текст
public record ImageEditCallResult<T>(T? Value, string? ErrorCode, string? Error)
{
    public static ImageEditCallResult<T> Ok(T value) => new(value, null, null);
    public static ImageEditCallResult<T> Fail(string code, string error) => new(default, code, error);
}
