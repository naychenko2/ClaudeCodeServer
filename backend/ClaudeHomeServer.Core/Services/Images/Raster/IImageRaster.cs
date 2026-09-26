using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing.Raster;

// Растр редактора на сервере (ADR-018 §9): правки без ИИ, автоуменьшение перед моделью,
// возврат размера и маска. Ошибки — не исключения, а RasterError в ответе: вызывающий
// (ручка transform, InputFitter) переводит их в 400/429, наружу ничего не вылетает.
public interface IImageRaster
{
    // Заголовок файла без декодирования пикселей; null — формат не распознан
    RasterProbe? Probe(byte[] data);

    // AutoOrient на входе всегда, затем ops по порядку, затем кодирование. Encode = null —
    // формат исходника (не PNG/JPEG/WebP — PNG) с качеством по умолчанию
    RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null);

    // Только перекодирование (с тем же AutoOrient на входе)
    RasterOutcome Encode(byte[] data, ImageEncodeSpec encode);

    // Маска до заданных размеров методом nearest-neighbour: бинарность сохраняется. Результат — PNG
    RasterOutcome ResizeMask(byte[] mask, int width, int height);
}

// Width/Height — как лежат в файле; DisplayWidth/DisplayHeight — после учёта EXIF-ориентации.
// Orientation — значение EXIF 1..8 (1 — нормальная). Format = null — формат не PNG/JPEG/WebP
public sealed record RasterProbe(
    ImageEncodeFormat? Format,
    string FormatName,
    int Width,
    int Height,
    int Orientation,
    long Bytes)
{
    public bool SwapsSides => Orientation is >= 5 and <= 8;
    public int DisplayWidth => SwapsSides ? Height : Width;
    public int DisplayHeight => SwapsSides ? Width : Height;
    public double Megapixels => (double)Width * Height / 1_000_000d;
}

public enum RasterError
{
    None,
    // Формат не распознан или файл битый
    Unsupported,
    // Вход больше потолка мегапикселей — отказ по заголовку, до декодирования
    TooLarge,
    // Недопустимая операция: угол не 90/180/270, пустая рамка, качество вне 40–100 и т.п.
    InvalidOp,
    // Потолок одновременных операций на инстанс исчерпан
    Busy,
}

public sealed record RasterImage(byte[] Bytes, ImageEncodeFormat Format, int Width, int Height);

public sealed record RasterOutcome(RasterImage? Image, RasterError Error, string? Message)
{
    public bool Ok => Error == RasterError.None && Image is not null;

    // Код ответа ручки: 429 — только занятость, остальное — ошибка запроса
    public int StatusCode => Error switch
    {
        RasterError.None => 200,
        RasterError.Busy => 429,
        _ => 400,
    };

    public static RasterOutcome Success(RasterImage image) => new(image, RasterError.None, null);
    public static RasterOutcome Fail(RasterError error, string message) => new(null, error, message);
}
