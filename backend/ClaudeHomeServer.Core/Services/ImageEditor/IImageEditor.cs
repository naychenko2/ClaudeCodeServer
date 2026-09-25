namespace ClaudeHomeServer.Services.ImageEditor;

// Контракт драйвера правки картинок (ADR-017, раздел 2). Живёт рядом со старым
// IImageGenerator, а не вместо него: на старом держатся аватары и очередь догоняющей
// генерации. Отказ — результат с причиной (EditOutcome), а не пустой список: экрану
// ошибки нужно различать «сервис не ответил» и «не хватает кредитов». Исключение наружу
// летит только при отмене снаружи.
public interface IImageEditor
{
    // Ключ поставщика: "fal", "higgsfield" (тот же, что у IImageGenerator)
    string Key { get; }

    // Подпись поставщика в списке «Поставщик ▾»
    string Label { get; }

    // Единица цены поставщика: ImageEditPriceUnits.*. Своей валюты у AI Home нет.
    string PriceUnit { get; }

    // Поставщик доступен СЕЙЧАС: у fal задан ключ, у Higgsfield IHiggsfieldAccess.AccessToken()
    // != null. Недоступный в каталог не попадает вовсе — это не ошибка и не 500.
    bool Enabled { get; }

    // Курируемый список моделей с возможностями. Пункт «Авто» сюда не входит — его
    // добавляет каталог (ImageEditCatalog.AutoModelId).
    IReadOnlyList<ImageEditModelInfo> Models { get; }

    // Разворот «Авто» в модель ВНУТРИ этого поставщика; null — поставщик так не умеет
    ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits);

    Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct);

    // Отмена у поставщика — лучшее усилие: у Higgsfield инструмента отмены нет, там false
    Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct);
}

public enum ImageEditOp { Generate, Edit, Inpaint, Outpaint, RemoveBackground, Upscale }

// «Авто / Быстрая / Точная правка / Фотореализм»; режим имеет смысл только при модели «Авто»
public enum EditMode { Auto, Fast, Precise, Photoreal }

// Канал маски у модели: Native — настоящий инпейнт, AsReference — маска уходит образцом
// с подписью «Точность границ ниже», None — модель маску не принимает
public enum MaskSupport { None, Native, AsReference }

public enum ReferenceRole { Character, Style, Object }

public enum EditOutcome { Ok, Failed, InsufficientCredits, Rejected, Cancelled, Unavailable }

public enum EditStage { Queued, Running, Downloading }

// HasAnnotations — на холсте есть стрелка, рамка или подпись: их видно только на размеченной
// копии, поэтому модели нужен канал образцов. Removal — запрос просит стереть отмеченное:
// чистый инпейнт по маске дорисовывает, а не стирает
public record EditTraits(bool HasMask, int References, bool HasCharacter, bool HasAnnotations = false, bool Removal = false);

// Возможности МОДЕЛИ, а не поставщика: недоступные операции UI показывает серыми
public record ImageEditCaps(
    IReadOnlyList<ImageEditOp> Ops,
    MaskSupport Mask,
    int MaxReferences,
    int MaxCount,
    bool FaceByReferences);

// Ориентир цены для каталога; точная сумма — только в котировке
public record ImageEditPriceHint(double Amount, string Unit, string Per);

public record ImageEditModelInfo(
    string Id,
    string Label,
    ImageEditCaps Caps,
    ImageEditPriceHint? PriceHint = null);

public record ImageBytes(byte[] Bytes, string ContentType);

public record ReferenceImage(byte[] Bytes, string ContentType, ReferenceRole Role, string? Label);

// Поля дорисовки за края в пикселях по сторонам
public record OutpaintSpec(int Left, int Top, int Right, int Bottom);

public record CharacterRef(string Slug, string Name, string? Description);

public record ImageEditRequest(
    ImageEditOp Op,
    string Prompt,
    ImageBytes? Source,
    ImageBytes? Mask,
    IReadOnlyList<ReferenceImage> References,
    int Count,
    string? AspectRatio,
    OutpaintSpec? Outpaint,
    string Model,
    CharacterRef? Character);

public record EditedImage(byte[] Bytes, string ContentType);

// Сумма в единицах поставщика (ImageEditPriceUnits.*)
public record EditCost(double Amount, string Unit);

public record EditProgress(EditStage Stage, int? QueuePosition = null);

// Charged: true — списано, false — точно не списано, null — неизвестно («Списание
// уточняется у сервиса»)
public record ImageEditResult(
    EditOutcome Outcome,
    IReadOnlyList<EditedImage> Images,
    EditCost? ActualCost,
    bool? Charged,
    string? RemoteId,
    string? Error);

public static class ImageEditPriceUnits
{
    public const string Usd = "usd";
    public const string Credits = "credits";
}
