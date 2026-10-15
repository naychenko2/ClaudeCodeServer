using System.IO.Compression;
using ClaudeHomeServer.HandsBridge.Scanning.Imaging;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты PNG-кодировщика: (е) корректная сигнатура/IHDR/IDAT/IEND/CRC и round-trip через
/// тестовый декодер.
/// </summary>
public class PngEncoderTests
{
    private static readonly byte[] Signature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    /// <summary>Детерминированное тестовое изображение (градиент).</summary>
    private static byte[] MakeImage(int w, int h, int channels)
    {
        var img = new byte[w * h * channels];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w + x) * channels;
                for (var c = 0; c < channels; c++)
                    img[idx + c] = (byte)((x * (c + 1) + y * (c + 2)) % 256);
            }

        return img;
    }

    [Fact]
    public void Signature_is_correct()
    {
        var img = MakeImage(8, 8, 3);
        var png = PngEncoder.Encode(img, 8, 8);

        for (var i = 0; i < 8; i++)
            Assert.Equal(Signature[i], png[i]);
    }

    [Fact]
    public void Ihdr_has_correct_dimensions_and_color_type()
    {
        // RGB (3 канала) → colorType 2; RGBA (4) → colorType 6.
        var rgb = PngEncoder.Encode(MakeImage(16, 12, 3), 16, 12);
        var rgba = PngEncoder.Encode(MakeImage(16, 12, 4), 16, 12);

        Assert.Equal(16, ReadIhdrWidth(rgb));
        Assert.Equal(12, ReadIhdrHeight(rgb));
        Assert.Equal(2, ReadIhdrColorType(rgb));

        Assert.Equal(6, ReadIhdrColorType(rgba));
    }

    [Fact]
    public void Chunk_order_is_ihdr_idat_iend()
    {
        var png = PngEncoder.Encode(MakeImage(10, 10, 3), 10, 10);

        var types = ReadChunkTypes(png);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
    }

    [Fact]
    public void Every_chunk_crc_is_valid()
    {
        var png = PngEncoder.Encode(MakeImage(20, 15, 4), 20, 15);

        // Декодер проверяет CRC каждого чанка и бросит исключение при несовпадении.
        var (pixels, w, h, ch) = PngTestDecoder.Decode(png);
        Assert.Equal(20, w);
        Assert.Equal(15, h);
        Assert.Equal(4, ch);
        Assert.Equal(20 * 15 * 4, pixels.Length);
    }

    [Fact]
    public void Round_trip_rgb_matches_original()
    {
        const int W = 32, H = 24;
        var original = MakeImage(W, H, 3);

        var png = PngEncoder.Encode(original, W, H);
        var (decoded, w, h, ch) = PngTestDecoder.Decode(png);

        Assert.Equal(W, w);
        Assert.Equal(H, h);
        Assert.Equal(3, ch);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Round_trip_rgba_matches_original()
    {
        const int W = 28, H = 20;
        var original = MakeImage(W, H, 4);

        var png = PngEncoder.Encode(original, W, H);
        var (decoded, w, h, ch) = PngTestDecoder.Decode(png);

        Assert.Equal(W, w);
        Assert.Equal(H, h);
        Assert.Equal(4, ch);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Single_pixel_round_trip()
    {
        var original = new byte[] { 1, 2, 3 }; // 1×1 RGB
        var png = PngEncoder.Encode(original, 1, 1);
        var (decoded, w, h, _) = PngTestDecoder.Decode(png);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Size_above_ceiling_throws()
    {
        // 10000×10000 = 10^8 > 64 Мп.
        var img = new byte[10000 * 10000 * 3];
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(img, 10000, 10000));
    }

    [Fact]
    public void Bad_channel_count_throws()
    {
        // 5 каналов на пиксель — не RGB и не RGBA.
        var img = new byte[4 * 4 * 5];
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(img, 4, 4));
    }

    [Fact]
    public void Non_divisible_length_throws()
    {
        // 10 байт не делится на 3 (RGB) для 2×2 = 4 пикселей.
        var img = new byte[10];
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(img, 2, 2));
    }

    // --- Вспомогательные: чтение IHDR и типов чанков ---

    private static int ReadIhdrWidth(byte[] png)
    {
        // IHDR — первый чанк после сигнатуры: offset 8 = length(4), 12 = type "IHDR",
        // 16 = data: width(4).
        return (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
    }

    private static int ReadIhdrHeight(byte[] png)
    {
        return (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
    }

    private static int ReadIhdrColorType(byte[] png)
    {
        // IHDR data: width(4) height(4) bitDepth(1) colorType(1) ... → colorType на offset 16+8+1=25.
        return png[25];
    }

    private static List<string> ReadChunkTypes(byte[] png)
    {
        var types = new List<string>();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = new string(new[]
            {
                (char)png[pos + 4], (char)png[pos + 5], (char)png[pos + 6], (char)png[pos + 7],
            });
            types.Add(type);
            pos += 12 + length; // length(4) + type(4) + data + crc(4)
        }

        return types;
    }
}

/// <summary>
/// Минимальный проверочный PNG-декодер (8 бит, RGB/RGBA, фильтры None/Sub): валидирует
/// сигнатуру, CRC каждого чанка, декомпрессирует IDAT (zlib) и разворачивает фильтры.
/// Используется только в тестах для round-trip.
/// </summary>
internal static class PngTestDecoder
{
    public static (byte[] Pixels, int Width, int Height, int Channels) Decode(byte[] png)
    {
        var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (var i = 0; i < 8; i++)
            if (png[i] != sig[i])
                throw new InvalidDataException($"Сигнатура PNG: байт {i} = {png[i]:X2}, ожидается {sig[i]:X2}.");

        var pos = 8;
        var width = 0;
        var height = 0;
        var colorType = -1;
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
                width = (int)((uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3]);
                height = (int)((uint)data[4] << 24 | (uint)data[5] << 16 | (uint)data[6] << 8 | data[7]);
                colorType = data[9];
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

        var channels = colorType == 2 ? 3 : colorType == 6 ? 4 : throw new InvalidDataException($"Тип цвета {colorType} не поддерживается.");

        var raw = Decompress(idat.ToArray());
        var rowLen = width * channels;
        if (raw.Length != height * (1 + rowLen))
            throw new InvalidDataException($"Размер raw {raw.Length} ≠ {height * (1 + rowLen)}.");

        var pixels = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (1 + rowLen)];
            var row = new byte[rowLen];
            Array.Copy(raw, y * (1 + rowLen) + 1, row, 0, rowLen);

            switch (filter)
            {
                case 0: // None
                    break;
                case 1: // Sub: row[i] += row[i - channels]
                    for (var i = 0; i < rowLen; i++)
                    {
                        var left = i >= channels ? row[i - channels] : 0;
                        row[i] = (byte)(row[i] + left);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Фильтр {filter} не поддерживается декодером.");
            }

            Array.Copy(row, 0, pixels, y * rowLen, rowLen);
        }

        return (pixels, width, height, channels);
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
