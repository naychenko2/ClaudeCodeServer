using System.Text;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Allow-list типов вложения ребёнка (ADR-041 «Контракт», ручка №8): тип определяется ТОЛЬКО
/// по сигнатуре байтов — расширение и Content-Type не смотрятся вовсе (переименованный .exe
/// в .png остаётся .exe). Состав ровно attachmentTypes ADR: PNG/JPEG/WEBP, PDF (%PDF-),
/// UTF-8-текст без нулевых байтов. Allow-list — константа в коде, не конфигурация
/// (тот же принцип, что у ChildLimitsCalculator.AttachmentTypesAllowList).
/// </summary>
public static class ChildAttachmentPolicy
{
    public enum Kind { Image, Pdf, Text, Unsupported }

    // Текст одного вложения не может быть больше инлайна: AttachmentInliner.MaxInlineBytes
    // (256 КБ) читает целиком, большего всё равно не отдаёт модели.
    public const int MaxTextBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D]; // "%PDF-"
    private const string Riff = "RIFF", Webp = "WEBP";

    /// <summary>
    /// Тип по сигнатуре байтов. Пустой вход — Unsupported (отказ empty_file решает вызывающий
    /// раньше: пустое — не тип, а отсутствие файла).
    /// </summary>
    public static Kind Detect(byte[] bytes)
    {
        if (bytes.Length == 0) return Kind.Unsupported;
        if (ImageExtOf(bytes) is not null) return Kind.Image;
        if (StartsWith(bytes, Pdf)) return Kind.Pdf;
        return IsUsableText(bytes) ? Kind.Text : Kind.Unsupported;
    }

    // Формат картинки по сигнатуре: расширение для сохранения (NormalizeExtension) или null,
    // если это не картинка. PNG / JPEG / WEBP (RIFF????WEBP — четыре байта длины между
    // маркерами, их не смотрим) — тот же порядок проверок, что был в Detect.
    private static string? ImageExtOf(byte[] bytes)
    {
        if (StartsWith(bytes, Png)) return ".png";
        if (StartsWith(bytes, Jpeg)) return ".jpg";
        if (bytes.Length >= 12 &&
            System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == Riff &&
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == Webp)
            return ".webp";
        return null;
    }

    // Допустимые расширения картинки (написание того же формата, что сигнатуры выше):
    // своё оставшееся расширение не противоречит kind=image.
    private static readonly HashSet<string> AllowedImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>
    /// Расширение сохраняемого имени по определённому типу (возврат ревью R-KM-B6e): тип
    /// определяется по сигнатуре, но downstream читает его снова по расширению —
    /// AttachmentInliner.SplitImagePaths относит файл к картинкам по расширению,
    /// MarkitdownService выбирает конвертер по нему же. Расширение не должно расходиться
    /// с kind: Image оставляет допустимое расширение из allow-list, иначе ставит по сигнатуре;
    /// Text ставит .txt; Pdf ставит .pdf. Меняется только расширение — имя остаётся читаемым.
    /// Unsupported не трогается: такой файл отказан вызывающим до сохранения.
    /// </summary>
    public static string NormalizeExtension(string fileName, Kind kind, byte[] bytes) => kind switch
    {
        Kind.Image when AllowedImageExts.Contains(Path.GetExtension(fileName)) => fileName,
        Kind.Image => Path.ChangeExtension(fileName, ImageExtOf(bytes) ?? ".png"),
        Kind.Pdf => Path.ChangeExtension(fileName, ".pdf"),
        Kind.Text => Path.ChangeExtension(fileName, ".txt"),
        _ => fileName,
    };

    /// <summary>
    /// Тот же критерий text/plain, что у Detect: валидный UTF-8 без нулевых байтов.
    /// Применяется и к результату PDF-конверсии: нулевые байты или невалидный UTF-8 —
    /// не текст (означает «скан/битый», отказ pdf_no_text), а не битая конверсия.
    /// </summary>
    public static bool IsUsableText(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        try { _ = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>
    /// Ограничить текст бюджетом MaxTextBytes с явной пометкой на месте обрыва.
    /// Граница режется по байтам целой UTF-8-последовательности (полунера не остаётся);
    /// итог не больше бюджета вместе с пометкой.
    /// </summary>
    public static string TruncateToTextBudget(string text, out bool truncated)
    {
        var note = "…(текст обрезан по размеру)";
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.Length <= MaxTextBytes)
        {
            truncated = false;
            return text;
        }
        truncated = true;
        var budget = MaxTextBytes - StrictUtf8.GetByteCount(note);
        var cut = 0;
        while (cut < bytes.Length)
        {
            // шаг по началу символа; следующий символ должен уместиться в бюджет целиком
            var size = bytes[cut] < 0x80 ? 1 : bytes[cut] < 0xE0 ? 2 : bytes[cut] < 0xF0 ? 3 : 4;
            if (cut + size > budget) break;
            cut += size;
        }
        return StrictUtf8.GetString(bytes, 0, cut) + note;
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
