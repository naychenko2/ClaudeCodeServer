using System.IO.Compression;
using ClaudeHomeServer.HandsBridge.Scanning.Imaging;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты PNG-кодировщика: (е) корректная сигнатура/IHDR/IDAT/IEND/CRC и round-trip через
/// общий декодер <see cref="PngDecoder"/> (тот же, что использует инструмент <c>scan_canvas</c>).
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
        var (pixels, w, h, ch) = PngDecoder.Decode(png);
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
        var (decoded, w, h, ch) = PngDecoder.Decode(png);

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
        var (decoded, w, h, ch) = PngDecoder.Decode(png);

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
        var (decoded, w, h, _) = PngDecoder.Decode(png);

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
/// Тесты PNG-декодера (C4): ВСЕ ПЯТЬ фильтров строк (None, Sub, Up, Average, Paeth) по
/// спецификации восстанавливают исходное изображение попиксельно; отказы — чересстрочность,
/// битовая глубина ≠ 8, тип цвета вне {2, 6}. PNG строится вручную (вручную применяется фильтр),
/// чтобы каждый фильтр проверялся независимо, а не через выбор кодировщика.
/// </summary>
public class PngDecoderFilterTests
{
    private static readonly byte[] Signature =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    /// <summary>Детерминированное изображение (градиент, не делится на период).</summary>
    private static byte[] MakeImage(int w, int h, int channels)
    {
        var img = new byte[w * h * channels];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w + x) * channels;
                for (var c = 0; c < channels; c++)
                    img[idx + c] = (byte)((x * (c + 3) + y * (c + 5) + c * 11) % 256);
            }
        return img;
    }

    /// <summary>
    /// PNG, где КАЖДАЯ строка использует один и тот же <paramref name="filter"/>: строится вручную
    /// (D = x − предсказание), чтобы декодер восстанавливал исходное изображение.
    /// </summary>
    private static byte[] BuildPng(byte[] image, int w, int h, int channels, int filter)
    {
        var ihdr = new byte[13];
        WriteInt32(ihdr, 0, w);
        WriteInt32(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = (byte)(channels == 4 ? 6 : 2);
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        var rowLen = w * channels;
        var raw = new byte[h * (1 + rowLen)];
        var prevOrig = new byte[rowLen];
        for (var y = 0; y < h; y++)
        {
            var rowStart = y * (1 + rowLen);
            raw[rowStart] = (byte)filter;
            var orig = new byte[rowLen];
            Array.Copy(image, y * rowLen, orig, 0, rowLen);
            for (var i = 0; i < rowLen; i++)
            {
                var left = i >= channels ? orig[i - channels] : (byte)0;
                var up = prevOrig[i];
                var upLeft = i >= channels ? prevOrig[i - channels] : (byte)0;
                var pred = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (byte)((left + up) / 2),
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidOperationException($"фильтр {filter}"),
                };
                raw[rowStart + 1 + i] = (byte)(orig[i] - pred);
            }
            Array.Copy(orig, 0, prevOrig, 0, rowLen);
        }

        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        var idat = ms.ToArray();

        var result = new List<byte>();
        result.AddRange(Signature);
        result.AddRange(Chunk("IHDR", ihdr));
        result.AddRange(Chunk("IDAT", idat));
        result.AddRange(Chunk("IEND", Array.Empty<byte>()));
        return result.ToArray();
    }

    /// <summary>Round-trip через каждый из пяти фильтров: декодер восстанавливает исходное изображение.</summary>
    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // Sub
    [InlineData(2)] // Up
    [InlineData(3)] // Average
    [InlineData(4)] // Paeth
    public void Every_filter_reconstructs_the_original(int filter)
    {
        const int W = 16, H = 12;
        var original = MakeImage(W, H, 3);

        var png = BuildPng(original, W, H, 3, filter);
        var (decoded, w, h, ch) = PngDecoder.Decode(png);

        Assert.Equal(W, w);
        Assert.Equal(H, h);
        Assert.Equal(3, ch);
        Assert.Equal(original, decoded);
    }

    /// <summary>RGBA (4 канала) с фильтром Paeth: альфа-канал тоже восстанавливается.</summary>
    [Fact]
    public void Rgba_with_paeth_reconstructs_all_four_channels()
    {
        const int W = 10, H = 8;
        var original = MakeImage(W, H, 4);

        var png = BuildPng(original, W, H, 4, 4);
        var (decoded, w, h, ch) = PngDecoder.Decode(png);

        Assert.Equal(4, ch);
        Assert.Equal(original, decoded);
    }

    /// <summary>Чересстрочность (interlace = 1, Adam7) — отказ с понятной ошибкой.</summary>
    [Fact]
    public void Interlace_is_rejected_with_a_clear_error()
    {
        var img = MakeImage(4, 4, 3);
        var png = BuildPng(img, 4, 4, 3, 0);
        // interlace — 13-й байт IHDR-data (offset 16+12=28 в файле).
        png[28] = 1;
        // CRC IHDR теперь неверен — пересчитаем, чтобы упал именно на interlace, а не на CRC.
        Array.Copy(FixIhdrCrc(png), 0, png, 0, png.Length);

        var ex = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));
        Assert.Contains("Adam7", ex.Message);
    }

    /// <summary>Битовая глубина ≠ 8 — отказ.</summary>
    [Fact]
    public void Bit_depth_not_8_is_rejected()
    {
        var img = MakeImage(4, 4, 3);
        var png = BuildPng(img, 4, 4, 3, 0);
        png[24] = 16; // bit depth (offset 16+8=24)
        Array.Copy(FixIhdrCrc(png), 0, png, 0, png.Length);

        var ex = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));
        Assert.Contains("8 бит", ex.Message);
    }

    /// <summary>Тип цвета вне {2, 6} (например 0=grayscale) — отказ.</summary>
    [Fact]
    public void Color_type_outside_rgb_rgba_is_rejected()
    {
        var img = MakeImage(4, 4, 3);
        var png = BuildPng(img, 4, 4, 3, 0);
        png[25] = 0; // color type (offset 16+9=25)
        Array.Copy(FixIhdrCrc(png), 0, png, 0, png.Length);

        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png));
    }

    /// <summary>Пересчёт CRC IHDR после ручной правки полей (чтобы тест упал на нужном поле).</summary>
    private static byte[] FixIhdrCrc(byte[] png)
    {
        // IHDR: length(4) на 8, type(4) на 12, data(13) на 16, crc(4) на 29.
        var type = new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        var data = new byte[13];
        Array.Copy(png, 16, data, 0, 13);
        var crc = Crc32(type, data);
        png[29] = (byte)(crc >> 24);
        png[30] = (byte)(crc >> 16);
        png[31] = (byte)(crc >> 8);
        png[32] = (byte)crc;
        return png;
    }

    private static void WriteInt32(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)(value >> 24);
        dst[offset + 1] = (byte)(value >> 16);
        dst[offset + 2] = (byte)(value >> 8);
        dst[offset + 3] = (byte)value;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var result = new byte[4 + 4 + data.Length + 4];
        WriteInt32(result, 0, data.Length);
        for (var i = 0; i < 4; i++)
            result[4 + i] = (byte)type[i];
        Array.Copy(data, 0, result, 8, data.Length);
        var crc = Crc32(result[4..8], data);
        result[8 + data.Length] = (byte)(crc >> 24);
        result[8 + data.Length + 1] = (byte)(crc >> 16);
        result[8 + data.Length + 2] = (byte)(crc >> 8);
        result[8 + data.Length + 3] = (byte)crc;
        return result;
    }

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
