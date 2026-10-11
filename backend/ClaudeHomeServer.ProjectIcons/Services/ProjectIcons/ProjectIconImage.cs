using System.Xml;

namespace ClaudeHomeServer.Services.ProjectIcons;

/// <summary>Распознанный формат загруженной картинки иконки.</summary>
public sealed record ProjectIconImageFormat(string Extension, string ContentType);

/// <summary>
/// Проверка картинки, которую владелец загружает иконкой проекта (ревизия ADR-009 от
/// 01.10.2026). Формат определяется по байтам, а не по имени файла: расширение и
/// Content-Type из запроса — слова клиента. SVG принимается только как XML с корнем
/// &lt;svg&gt; и без DTD (billion laughs, внешние сущности). Скрипты внутри SVG не
/// вычищаются: картинка показывается только через &lt;img&gt;, а прямую отдачу
/// закрывает CSP default-src 'none' — разметку в DOM не вставляет никто.
/// </summary>
public static class ProjectIconImage
{
    // Иконка — это десятки килобайт; запас под подробный SVG-логотип
    public const int MaxBytes = 512 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["webp"] = "image/webp",
        ["ico"] = "image/x-icon",
        ["svg"] = "image/svg+xml",
    };

    /// <summary>Content-Type отдачи по расширению сохранённого файла; null — чужое расширение.</summary>
    public static string? ContentTypeOf(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName).TrimStart('.'));

    /// <summary>Формат картинки либо null, если байты не похожи ни на один допустимый.</summary>
    public static ProjectIconImageFormat? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxBytes) return null;

        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return Format("png");
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return Format("jpg");
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return Format("webp");
        if (data.Length >= 6 && data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
            return Format("ico");
        return IsSvg(data) ? Format("svg") : null;
    }

    private static ProjectIconImageFormat Format(string ext) => new(ext, ContentTypes[ext]);

    // Корневой элемент — svg, DTD запрещён, внешние ресурсы не резолвятся. Читаем документ
    // до конца: битый XML после корня — тоже отказ, а не «вроде svg».
    private static bool IsSvg(ReadOnlySpan<byte> data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg") return false;
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
