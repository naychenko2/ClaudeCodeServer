namespace ClaudeHomeServer.Services.Images.Editing;

// Размеры картинки по заголовку, без библиотеки изображений (ADR-016, раздел 3): PNG — IHDR,
// JPEG — маркер SOF, WebP — чанк VP8/VP8L/VP8X, GIF — логический экран. Нужны, чтобы
// маска совпадала с исходником до запуска (иначе модель отвечает 422 уже после списания),
// и драйверам, которым размер задаётся в пикселях (дорисовка за края, апскейл).
public static class ImageDimensions
{
    public static (int Width, int Height)? Read(byte[]? b)
    {
        if (b is null || b.Length < 10) return null;
        try
        {
            return ReadPng(b) ?? ReadJpeg(b) ?? ReadWebp(b) ?? ReadGif(b);
        }
        catch (IndexOutOfRangeException)
        {
            // оборванный заголовок — размер неизвестен
            return null;
        }
    }

    public static bool IsPng(byte[]? b) =>
        b is { Length: >= 8 } && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static (int, int)? ReadPng(byte[] b)
    {
        if (!IsPng(b) || b.Length < 24) return null;
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return null;
        return (BigEndian32(b, 16), BigEndian32(b, 20));
    }

    private static (int, int)? ReadJpeg(byte[] b)
    {
        if (b[0] != 0xFF || b[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            // Маркеры без длины: SOI, TEM, RST0–7
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var length = (b[i + 2] << 8) | b[i + 3];
            // SOF0–SOF15, кроме DHT (C4), JPG (C8) и DAC (CC)
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + length;
        }
        return null;
    }

    private static (int, int)? ReadWebp(byte[] b)
    {
        if (b.Length < 30 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F'
            || b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P')
            return null;
        var chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
        return chunk switch
        {
            "VP8 " => ((b[26] | (b[27] << 8)) & 0x3FFF, (b[28] | (b[29] << 8)) & 0x3FFF),
            "VP8L" => (1 + (((b[22] & 0x3F) << 8) | b[21]),
                       1 + (((b[24] & 0x0F) << 10) | (b[23] << 2) | ((b[22] & 0xC0) >> 6))),
            "VP8X" => (1 + (b[24] | (b[25] << 8) | (b[26] << 16)),
                       1 + (b[27] | (b[28] << 8) | (b[29] << 16))),
            _ => null,
        };
    }

    private static (int, int)? ReadGif(byte[] b)
    {
        if (b[0] != 'G' || b[1] != 'I' || b[2] != 'F') return null;
        return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
    }

    private static int BigEndian32(byte[] b, int at) =>
        (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
}
