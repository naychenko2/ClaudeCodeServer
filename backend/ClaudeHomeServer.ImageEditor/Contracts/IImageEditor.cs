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

// EnhanceFaces — быстрое действие «Улучшить лица» (локальные модели: FaceDetailer)
public enum ImageEditOp { Generate, Edit, Inpaint, Outpaint, RemoveBackground, Upscale, EnhanceFaces }

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

// Возможности МОДЕЛИ, а не поставщика: недоступные операции UI показывает серыми.
// SeparateMaskPass — кисть вместе со стрелками, рамками и подписями модель в одном запросе не
// отрабатывает (закрашенное остаётся), поэтому правка идёт в два запроса: сначала по маске,
// затем по пометкам поверх результата (ImageEditRequest.MaskPass). Драйвер такой модели
// обязан исполнить MaskPass, а котировка — посчитать лишнюю картинку.
public record ImageEditCaps(
    IReadOnlyList<ImageEditOp> Ops,
    MaskSupport Mask,
    int MaxReferences,
    int MaxCount,
    bool FaceByReferences,
    bool SeparateMaskPass = false,
    // Лимиты входа модели для автоуменьшения (ADR-018 §9): длинная сторона в px, площадь в Мп,
    // вес файла в МБ. null — лимита нет или он не курирован
    int? MaxInputSide = null,
    double? MaxInputMegapixels = null,
    double? MaxInputMb = null,
    // Сколько фото персонажа модель берёт как ОДНОГО человека; null — все. Qwen-Image рисует по
    // человеку на каждое фото, и текст запроса это не лечит (живой прогон 2026-09-26)
    int? MaxCharacterPhotos = null);

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
public record OutpaintSpec(int Left, int Top, int Right, int Bottom)
{
    // Та же пропорция уже есть (или она не задана) — холст растёт в полтора раза по обеим сторонам
    private const double SameAspectTolerance = 0.01;

    // Поля под пропорцию «W:H» поровну с двух сторон: холст растёт только по недостающей стороне.
    // null — пропорция не читается
    public static OutpaintSpec? ForAspect(int width, int height, string? aspect)
    {
        if (width < 1 || height < 1) return null;
        double target;
        if (string.IsNullOrWhiteSpace(aspect))
        {
            target = (double)width / height;
        }
        else
        {
            var parts = aspect.Split(':');
            if (parts.Length != 2
                || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aw)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ah)
                || !double.IsFinite(aw) || !double.IsFinite(ah) || aw <= 0 || ah <= 0)
                return null;
            target = aw / ah;
        }

        var current = (double)width / height;
        if (Math.Abs(current / target - 1) <= SameAspectTolerance)
            return Split(width / 2, height / 2);
        return current < target
            ? Split((int)Math.Round(height * target, MidpointRounding.AwayFromZero) - width, 0)
            : Split(0, (int)Math.Round(width / target, MidpointRounding.AwayFromZero) - height);
    }

    // Нечётный пиксель — правой и нижней стороне
    private static OutpaintSpec Split(int dx, int dy) => new(dx / 2, dy / 2, dx - dx / 2, dy - dy / 2);
}

public record CharacterRef(string Slug, string Name, string? Description);

// MaskPass — предварительная правка по маске одним вариантом (ImageEditCaps.SeparateMaskPass):
// её результат становится исходником этого запроса. Instruction — просьба человека как есть, без
// ролей картинок и текста пометок: по ней драйвер без канала маски узнаёт стирание (EditIntent)
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
    CharacterRef? Character,
    ImageEditRequest? MaskPass = null,
    string? Instruction = null);

public record EditedImage(byte[] Bytes, string ContentType);

// Сумма в единицах поставщика (ImageEditPriceUnits.*)
public record EditCost(double Amount, string Unit);

// Run/Runs — номер текущего прогона (с 1) и их число, EtaSeconds — ожидаемое время прогона;
// заполняет только драйвер, который это знает (local), остальные оставляют null.
// Ready — варианты, готовые прямо сейчас (скачан прогон): исполнитель отдаёт их в нить, не дожидаясь
// остальных. Итоговый ImageEditResult.Images всё равно несёт ВСЕ варианты по порядку
public record EditProgress(EditStage Stage, int? QueuePosition = null, int? Run = null, int? Runs = null,
    int? EtaSeconds = null, IReadOnlyList<EditedImage>? Ready = null);

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
    // Локальные модели: денег нет, вместо цены — время и очередь
    public const string Free = "free";
}
