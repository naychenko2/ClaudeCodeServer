using System.IO.Compression;

namespace ClaudeHomeServer.HandsBridge.Scanning.Imaging;

/// <summary>
/// Чистый кодировщик PNG: 8 бит, RGB (3 канала) или RGBA (4 канала), фильтры строк None/Sub
/// (на каждую строку выбирается более сжимаемый), zlib через <see cref="ZLibStream"/>, CRC32 —
/// собственный. Без System.Drawing/ImageSharp — чтобы тесты шли на Linux, а мост не тянул
/// лишних зависимостей.
/// </summary>
public static class PngEncoder
{
    /// <summary>Потолок размера результата, px (64 Мп).</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>Сигнатура PNG (8 байт).</summary>
    private static readonly byte[] Signature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    /// <summary>
    /// Кодирует изображение в PNG. <paramref name="pixels"/> — RGB (3 байта/пиксель) или RGBA
    /// (4 байта/пиксель) в row-major; число каналов определяется из длины массива.
    /// <paramref name="width"/>/<paramref name="height"/> — размеры. Размер ограничен
    /// <see cref="MaxPixels"/>. Возвращает готовые байты PNG-файла.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Ширина и высота должны быть положительными.", nameof(width));

        var pixelCount = (long)width * height;
        if (pixelCount > MaxPixels)
            throw new ArgumentException(
                $"Размер {width}×{height} = {pixelCount} px превышает потолок {MaxPixels} px.",
                nameof(pixels));

        if (pixels.Length % pixelCount != 0)
            throw new ArgumentException(
                $"Длина {pixels.Length} не делится на число пикселей {pixelCount}.",
                nameof(pixels));

        var channels = (int)(pixels.Length / pixelCount);
        if (channels != 3 && channels != 4)
            throw new ArgumentException(
                $"Каналов на пиксель: {channels}, ожидается 3 (RGB) или 4 (RGBA).",
                nameof(pixels));

        var colorType = channels == 3 ? 2 : 6; // 2 = RGB, 6 = RGBA
        var rowLen = width * channels;

        // Сырые сканлайны: на каждую строку [фильтр][данные]. Фильтр выбираем по строке:
        // None (0) или Sub (1) — тот, что даёт меньшую «энергию» (сумму модулей).
        var raw = new byte[height * (1 + rowLen)];
        for (var y = 0; y < height; y++)
        {
            var src = pixels.Slice(y * rowLen, rowLen);
            var dst = raw.AsSpan().Slice(y * (1 + rowLen) + 1, rowLen);

            // m4: суммы эвристики в long — на ширине выше ~8 млн px int переполняется.
            var noneScore = 0L;
            var subScore = 0L;
            for (var i = 0; i < rowLen; i++)
            {
                noneScore += src[i];
                var prev = i >= channels ? src[i - channels] : 0;
                subScore += Math.Abs(src[i] - prev);
            }

            var useSub = subScore < noneScore;
            raw[y * (1 + rowLen)] = (byte)(useSub ? 1 : 0);

            if (useSub)
            {
                for (var i = 0; i < rowLen; i++)
                {
                    var prev = i >= channels ? src[i - channels] : 0;
                    dst[i] = (byte)(src[i] - prev);
                }
            }
            else
            {
                src.CopyTo(dst);
            }
        }

        // Собираем файл: сигнатура + IHDR + IDAT (zlib) + IEND.
        using var result = new MemoryStream();
        result.Write(Signature);

        var ihdr = new byte[13];
        WriteUInt32(ihdr, 0, (uint)width);
        WriteUInt32(ihdr, 4, (uint)height);
        ihdr[8] = 8; // глубина 8 бит
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; // сжатие
        ihdr[11] = 0; // фильтр
        ihdr[12] = 0; // без интерлейса
        WriteChunk(result, "IHDR", ihdr);

        WriteChunk(result, "IDAT", Compress(raw));
        WriteChunk(result, "IEND", Array.Empty<byte>());

        return result.ToArray();
    }

    /// <summary>
    /// Чанк PNG: длина (4, big-endian) + тип (4) + данные + CRC32 (4) над тип+данные.
    /// </summary>
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++)
            typeBytes[i] = (byte)type[i];

        WriteUInt32(s, (uint)data.Length);
        s.Write(typeBytes);
        s.Write(data);
        WriteUInt32(s, Crc32(typeBytes, data));
    }

    /// <summary>Записывает uint32 big-endian в поток.</summary>
    private static void WriteUInt32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    /// <summary>Записывает uint32 big-endian в массив по смещению.</summary>
    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>
    /// Сжимает данные в zlib-контейнер (RFC 1950) через <see cref="ZLibStream"/> — именно
    /// этот формат ожидает IDAT.
    /// </summary>
    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return ms.ToArray();
    }

    /// <summary>CRC32 (полином 0xEDB88320, отражённый) — как в PNG. Таблица строится один раз.</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    /// <summary>CRC32 над последовательностью блоков (тип + данные чанка).</summary>
    private static uint Crc32(params byte[][] blocks)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var block in blocks)
            foreach (var b in block)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }
}
