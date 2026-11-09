namespace ClaudeHomeServer.Services.Media;

// Content-type медиа-файла по сигнатуре байтов (ADR-030 §3): имя/расширение файла недоверенны
// (поставщик может вернуть PNG там, где было .jpg, а человек — переименовать). Читаем первые
// байты и определяем тип. SVG и HTML помечаем отдельно: их никогда не отдаём (XSS/скрипт).
// Неизвестная сигнатура — null (файл не медиа, что мы умеем).
public static class MediaContentTypeSniffer
{
    // Типы, которые никогда не отдаём: SVG (скрипт) и HTML (скрипт). Фронт покажет «недоступен»
    public const string Svg = "image/svg+xml";
    public const string Html = "text/html";

    public static bool IsNeverServed(string? contentType) =>
        contentType is Svg or Html;

    // head — первые ~64 байта файла. null — не распознано
    public static string? Detect(byte[] head)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return "image/png";
        // JPEG: FF D8 FF
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        // WebP: RIFF....WEBP
        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            return "image/webp";
        // GIF: GIF87a / GIF89a
        if (head.Length >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'8'
            && (head[4] == (byte)'7' || head[4] == (byte)'9') && head[5] == (byte)'a')
            return "image/gif";
        // MP4: ....ftyp (brand на offset 4)
        if (head.Length >= 12 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
            return "video/mp4";
        // WebM/Matroska: EBML 1A 45 DF A3
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
            return "video/webm";
        // WAV: RIFF....WAVE
        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'A' && head[10] == (byte)'V' && head[11] == (byte)'E')
            return "audio/wav";
        // FLAC: fLaC
        if (head.Length >= 4 && head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C')
            return "audio/flac";
        // OGG: OggS
        if (head.Length >= 4 && head[0] == (byte)'O' && head[1] == (byte)'g' && head[2] == (byte)'g' && head[3] == (byte)'S')
            return "audio/ogg";
        // MP3: ID3 (тег) или frame sync FF Ex
        if (head.Length >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
            return "audio/mpeg";
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
            return "audio/mpeg";
        // SVG: <?xml ... <svg  (XML с тегом svg)
        if (StartsWithXml(head) && ContainsAscii(head, "<svg"))
            return Svg;
        // HTML: <!DOCTYPE html / <html
        if (ContainsAscii(head, "<!doctype html") || ContainsAscii(head, "<html"))
            return Html;
        return null;
    }

    private static bool StartsWithXml(byte[] head)
    {
        var span = head.AsSpan();
        // Пропустить BOM и пробелы, искать "<?xml"
        var i = 0;
        if (i + 3 < span.Length && span[i] == 0xEF && span[i + 1] == 0xBB && span[i + 2] == 0xBF) i += 3;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t' || span[i] == (byte)'\n' || span[i] == (byte)'\r')) i++;
        return span.Length - i >= 5 && span[i] == (byte)'<' && span[i + 1] == (byte)'?' && span[i + 2] == (byte)'x'
            && span[i + 3] == (byte)'m' && span[i + 4] == (byte)'l';
    }

    private static bool ContainsAscii(byte[] head, string token)
    {
        var t = (byte[])System.Text.Encoding.ASCII.GetBytes(token);
        for (var i = 0; i + t.Length <= head.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < t.Length; j++)
                if (head[i + j] != t[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}
