using System.IO.Compression;

namespace ClaudeHomeServer.HandsBridge.Scanning.Imaging;

/// <summary>
/// PNG-декодер (C1 «руки без UIA», C4): единственный проверенный декодер проекта — им пользуются
/// и инструмент <c>scan_canvas</c> (снимок GDI+ → пиксели), и тесты (<c>PngEncoderTests</c>).
/// Чистая логика без WinAPI: Linux CI.
/// <para>
/// Поддерживает: 8 бит, типы цвета 2 (RGB, 3 канала) и 6 (RGBA, 4 канала), ВСЕ ПЯТЬ фильтров
/// строк (None, Sub, Up, Average, Paeth) по спецификации. Чанки: сигнатура, CRC32 каждого,
/// IDAT (zlib), IEND обязателен. Отказ с понятной ошибкой: чересстрочность (interlace ≠ 0),
/// битовая глубина ≠ 8, тип цвета вне {2, 6}.
/// </para>
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    /// <summary>
    /// Декодирует PNG в пиксели. Возвращает (пиксели, ширина, высота, каналов): пиксели — сырые
    /// байты в порядке строк, по <c>width * channels</c> байт на строку; <c>channels</c> — 3 (RGB)
    /// или 4 (RGBA), как в исходном файле (альфа НЕ композитится — это делает вызывающий, если нужно).
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int Channels) Decode(byte[] png)
    {
        if (png is null)
            throw new ArgumentNullException(nameof(png));

        for (var i = 0; i < 8; i++)
            if (png[i] != Signature[i])
                throw new InvalidDataException($"Сигнатура PNG: байт {i} = {png[i]:X2}, ожидается {Signature[i]:X2}.");

        var pos = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = -1;
        var interlace = -1;
        var idat = new List<byte>();
        var iendSeen = false;

        while (pos < png.Length)
        {
            if (pos + 8 > png.Length)
                throw new InvalidDataException("Обрезанный чанк (нет length+type).");

            var length = (int)((uint)png[pos] << 24 | (uint)png[pos + 1] << 16 | (uint)png[pos + 2] << 8 | png[pos + 3]);
            pos += 4;
            var type = new string(new[]
            {
                (char)png[pos], (char)png[pos + 1], (char)png[pos + 2], (char)png[pos + 3],
            });
            pos += 4;

            if (pos + length + 4 > png.Length)
                throw new InvalidDataException($"Чанк {type}: не хватает данных (length={length}).");

            var data = new byte[length];
            Array.Copy(png, pos, data, 0, length);
            pos += length;

            var crc = (uint)png[pos] << 24 | (uint)png[pos + 1] << 16 | (uint)png[pos + 2] << 8 | png[pos + 3];
            pos += 4;

            var typeBytes = new byte[4];
            for (var i = 0; i < 4; i++)
                typeBytes[i] = (byte)type[i];
            if (crc != Crc32(typeBytes, data))
                throw new InvalidDataException($"CRC32 чанка {type}: {crc:X8} ≠ {Crc32(typeBytes, data):X8}.");

            if (type == "IHDR")
            {
                if (length != 13)
                    throw new InvalidDataException($"IHDR: длина {length} ≠ 13.");
                width = (int)((uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3]);
                height = (int)((uint)data[4] << 24 | (uint)data[5] << 16 | (uint)data[6] << 8 | data[7]);
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
            }
            else if (type == "IDAT")
            {
                idat.AddRange(data);
            }
            else if (type == "IEND")
            {
                iendSeen = true;
            }
        }

        if (!iendSeen)
            throw new InvalidDataException("Нет чанка IEND.");

        if (bitDepth != 8)
            throw new InvalidDataException($"Битовая глубина {bitDepth}: декодер поддерживает только 8 бит.");
        if (interlace != 0)
            throw new InvalidDataException("Чересстрочность (Adam7, interlace ≠ 0) не поддерживается декодером.");

        var channels = colorType switch
        {
            2 => 3,   // RGB
            6 => 4,   // RGBA
            _ => throw new InvalidDataException($"Тип цвета {colorType} не поддерживается (нужен 2=RGB или 6=RGBA)."),
        };

        var raw = Decompress(idat.ToArray());
        var rowLen = width * channels;
        if (raw.Length != height * (1 + rowLen))
            throw new InvalidDataException($"Размер raw {raw.Length} ≠ {height * (1 + rowLen)}.");

        var pixels = new byte[width * height * channels];
        // Предыдущая восстановленная строка (для фильтров Up/Average/Paeth) и текущая (для Sub).
        var prev = new byte[rowLen];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + rowLen);
            var filter = raw[rowStart];
            var row = new byte[rowLen];
            Array.Copy(raw, rowStart + 1, row, 0, rowLen);

            switch (filter)
            {
                case 0: // None
                    break;
                case 1: // Sub: x = D + x[left]
                    for (var i = 0; i < rowLen; i++)
                    {
                        var left = i >= channels ? row[i - channels] : (byte)0;
                        row[i] = (byte)(row[i] + left);
                    }
                    break;
                case 2: // Up: x = D + x[up]
                    for (var i = 0; i < rowLen; i++)
                        row[i] = (byte)(row[i] + prev[i]);
                    break;
                case 3: // Average: x = D + floor((x[left] + x[up]) / 2)
                    for (var i = 0; i < rowLen; i++)
                    {
                        var left = i >= channels ? row[i - channels] : (byte)0;
                        row[i] = (byte)(row[i] + (left + prev[i]) / 2);
                    }
                    break;
                case 4: // Paeth: x = D + PaethPredictor(x[left], x[up], x[up-left])
                    for (var i = 0; i < rowLen; i++)
                    {
                        var a = i >= channels ? row[i - channels] : (byte)0;
                        var b = prev[i];
                        var c = i >= channels ? prev[i - channels] : (byte)0;
                        row[i] = (byte)(row[i] + Paeth(a, b, c));
                    }
                    break;
                default:
                    throw new InvalidDataException($"Фильтр {filter} не поддерживается (0..4).");
            }

            Array.Copy(row, 0, pixels, y * rowLen, rowLen);
            Array.Copy(row, 0, prev, 0, rowLen);
        }

        return (pixels, width, height, channels);
    }

    /// <summary>
    /// Паэт-предсказатель по спецификации PNG: ближайший из left/up/up-left, при равенстве
    /// расстояний приоритет left → up → up-left.
    /// </summary>
    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        if (pb <= pc)
            return b;
        return c;
    }

    private static byte[] Decompress(byte[] zlibData)
    {
        using var input = new MemoryStream(zlibData);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

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

    private static uint Crc32(params byte[][] blocks)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var block in blocks)
            foreach (var b in block)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }
}
