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

public record ImageEditLimitsDto(int MaxFileMb, int MaxReferences, int MaxCount);

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
    CharacterRef? Character = null);

public record ImageEditJobCreatedDto(string JobId);

public enum ImageEditJobStatus { Queued, Running, Downloading, Completed, Failed, Cancelled, Interrupted }

// Variants — номера готовых вариантов (для …/jobs/{jobId}/variants/{n})
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
    DateTime CreatedAt);

// ── Сохранение: POST …/save ────────────────────────────────────────────────────

// Флага перезаписи нет по построению: результат всегда новым файлом (hero.v2.png).
// SourcePath — правка файла проекта; Folder + FileName — «Нарисовать картинку».
public record ImageEditSaveRequest(
    string JobId,
    int Variant,
    string? SourcePath,
    string? Folder,
    string? FileName);

// Path — относительный путь созданного файла от корня проекта
public record ImageEditSaveResultDto(string Path);

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
    // 404: персонажа нет в проекте (или slug не проходит белый список)
    public const string CharacterNotFound = "character_not_found";
    // 429: потолок одновременных задач владельца или инстанса
    public const string TooManyJobs = "too_many_jobs";
    // 503: подсистема картинок выключена на этом сервере
    public const string Unavailable = "image_editor_unavailable";
}

// Результат вызова шва: Value при успехе, иначе код ImageEditErrorCodes.* и текст
public record ImageEditCallResult<T>(T? Value, string? ErrorCode, string? Error)
{
    public static ImageEditCallResult<T> Ok(T value) => new(value, null, null);
    public static ImageEditCallResult<T> Fail(string code, string error) => new(default, code, error);
}
