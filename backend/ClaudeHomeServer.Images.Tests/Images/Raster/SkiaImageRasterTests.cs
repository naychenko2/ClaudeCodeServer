using System.IO.Compression;
using System.Text;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.Services.Images.Raster;

// Сторожа ADR-018 §9 для растра на SkiaSharp: потолок мегапикселей по заголовку, геометка не
// утекает, ICC-профиль сохраняется, EXIF-ориентация применяется к пикселям, маска бинарна,
// занятость → 429. Картинки собираются в памяти, файлов на диске нет.
public class SkiaImageRasterTests
{
    private static readonly SKColorSpace DisplayP3 =
        SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);

    private readonly SkiaImageRaster _raster = new();

    [Fact]
    public void Вход_больше_100_Мп_отказ_400_по_заголовку()
    {
        var png = HugeBlankPng(12_000, 9_000); // 108 Мп, в файле — десятки килобайт

        var probe = _raster.Probe(png);
        probe.Should().NotBeNull();
        probe!.Width.Should().Be(12_000);
        probe.Height.Should().Be(9_000);

        var outcome = _raster.Apply(png, [new RotateOp(90)]);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Be(RasterError.TooLarge);
        outcome.StatusCode.Should().Be(400);
        outcome.Message.Should().Contain("слишком большая");
    }

    [Fact]
    public void Мусор_вместо_картинки_отказ_400_без_исключения()
    {
        var outcome = _raster.Apply(Encoding.ASCII.GetBytes("definitely not an image"), []);

        outcome.Error.Should().Be(RasterError.Unsupported);
        outcome.StatusCode.Should().Be(400);
        _raster.Probe([1, 2, 3]).Should().BeNull();
    }

    [Fact]
    public void JPEG_с_GPS_в_EXIF_в_результате_EXIF_нет()
    {
        var source = WithExif(Jpeg(Solid(64, 48, SKColors.Gray)), orientation: 1, withGps: true);
        IndexOf(source, ExifHeader).Should().BeGreaterThan(0, "исходник обязан нести EXIF, иначе тест пустой");
        IndexOf(source, Encoding.ASCII.GetBytes("N\0")).Should().BeGreaterThan(0);

        foreach (var format in new[] { ImageEncodeFormat.Jpeg, ImageEncodeFormat.Png, ImageEncodeFormat.Webp })
        {
            var outcome = _raster.Apply(source, [], new ImageEncodeSpec(format));

            outcome.Ok.Should().BeTrue(outcome.Message);
            IndexOf(outcome.Image!.Bytes, ExifHeader).Should().Be(-1, $"в {format} EXIF (и геометка) не пишется");
            IndexOf(outcome.Image.Bytes, Encoding.ASCII.GetBytes("EXIF")).Should().Be(-1, $"в {format} нет чанка EXIF");
        }
    }

    [Theory]
    [InlineData(ImageEncodeFormat.Png)]
    [InlineData(ImageEncodeFormat.Jpeg)]
    [InlineData(ImageEncodeFormat.Webp)]
    public void Фото_Display_P3_профиль_в_результате_сохранён(ImageEncodeFormat format)
    {
        var source = Png(Solid(32, 32, new SKColor(200, 40, 40), DisplayP3));
        using (var codec = SKCodec.Create(SKData.CreateCopy(source)))
            IsDisplayP3(codec.Info.ColorSpace).Should().BeTrue("исходник обязан быть в P3, иначе тест пустой");

        var outcome = _raster.Apply(source, [new ResizeOp(Width: 16)], new ImageEncodeSpec(format));

        outcome.Ok.Should().BeTrue(outcome.Message);
        using var result = SKCodec.Create(SKData.CreateCopy(outcome.Image!.Bytes));
        result.Info.ColorSpace.Should().NotBeNull();
        result.Info.ColorSpace.IsSrgb.Should().BeFalse($"{format} потерял профиль и стал sRGB");
        IsDisplayP3(result.Info.ColorSpace).Should().BeTrue($"в {format} должен остаться Display P3");
    }

    [Fact]
    public void EXIF_orientation_6_пиксели_повёрнуты_метки_нет()
    {
        // В файле 40×20: слева красное, справа синее. Orientation 6 = показать с поворотом на 90°
        // по часовой: левая половина уезжает наверх, картинка становится 20×40
        var stored = Split(40, 20, SKColors.Red, SKColors.Blue);
        var source = WithExif(Jpeg(stored, quality: 100), orientation: 6, withGps: false);

        var probe = _raster.Probe(source)!;
        probe.Orientation.Should().Be(6);
        (probe.DisplayWidth, probe.DisplayHeight).Should().Be((20, 40));

        var outcome = _raster.Apply(source, [], new ImageEncodeSpec(ImageEncodeFormat.Png));

        outcome.Ok.Should().BeTrue(outcome.Message);
        (outcome.Image!.Width, outcome.Image.Height).Should().Be((20, 40));
        using var decoded = SKBitmap.Decode(outcome.Image.Bytes);
        IsNear(decoded.GetPixel(10, 5), SKColors.Red).Should().BeTrue($"сверху должно быть красное, а там {decoded.GetPixel(10, 5)}");
        IsNear(decoded.GetPixel(10, 35), SKColors.Blue).Should().BeTrue($"снизу должно быть синее, а там {decoded.GetPixel(10, 35)}");
        using var codec = SKCodec.Create(SKData.CreateCopy(outcome.Image.Bytes));
        codec.EncodedOrigin.Should().Be(SKEncodedOrigin.TopLeft);
        IndexOf(outcome.Image.Bytes, ExifHeader).Should().Be(-1);
    }

    // Все восемь EXIF-ориентаций. В файле 40×20 четыре четверти: R — левая верхняя, G — правая
    // верхняя, B — левая нижняя, Y — правая нижняя. Ожидание — какие цвета окажутся в углах
    // показанной картинки в порядке «левый верх, правый верх, левый низ, правый низ».
    // 5 и 7 — транспонирование: одним поворотом их не получить, поэтому их ловит только эта таблица
    [Theory]
    [InlineData(1, "RGBY", 40, 20)]
    [InlineData(2, "GRYB", 40, 20)]
    [InlineData(3, "YBGR", 40, 20)]
    [InlineData(4, "BYRG", 40, 20)]
    [InlineData(5, "RBGY", 20, 40)]
    [InlineData(6, "BRYG", 20, 40)]
    [InlineData(7, "YGBR", 20, 40)]
    [InlineData(8, "GYRB", 20, 40)]
    public void EXIF_ориентация_применяется_к_пикселям(int orientation, string corners, int w, int h)
    {
        var colors = new Dictionary<char, SKColor>
        {
            ['R'] = SKColors.Red, ['G'] = SKColors.Lime, ['B'] = SKColors.Blue, ['Y'] = SKColors.Yellow,
        };
        var stored = Solid(40, 20, colors['R']);
        using (var canvas = new SKCanvas(stored))
        {
            void Fill(SKColor c, float x, float y) { using var p = new SKPaint { Color = c, IsAntialias = false }; canvas.DrawRect(x, y, 20, 10, p); }
            Fill(colors['G'], 20, 0);
            Fill(colors['B'], 0, 10);
            Fill(colors['Y'], 20, 10);
        }
        var source = WithExif(Jpeg(stored, quality: 100), orientation, withGps: false);

        var outcome = _raster.Apply(source, [], new ImageEncodeSpec(ImageEncodeFormat.Png));

        outcome.Ok.Should().BeTrue(outcome.Message);
        (outcome.Image!.Width, outcome.Image.Height).Should().Be((w, h));
        using var decoded = SKBitmap.Decode(outcome.Image.Bytes);
        var probes = new[] { (4, 4), (w - 5, 4), (4, h - 5), (w - 5, h - 5) };
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = probes[i];
            var actual = decoded.GetPixel(x, y);
            IsNear(actual, colors[corners[i]]).Should().BeTrue(
                $"ориентация {orientation}: в углу {i} ждали {corners[i]}, а там {actual}");
        }
    }

    [Theory]
    [InlineData(600, 400, 205, 137)]
    [InlineData(100, 50, 603, 301)]
    public void Маска_nearest_остаётся_бинарной_и_нужного_размера(int w, int h, int tw, int th)
    {
        using var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
            canvas.DrawRect(w / 3f, h / 3f, w / 3f, h / 3f, paint);
            canvas.DrawRect(1, 1, 1, h - 2, paint); // тонкая линия: у сглаживания она поплыла бы первой
        }

        var outcome = _raster.ResizeMask(Png(mask), tw, th);

        outcome.Ok.Should().BeTrue(outcome.Message);
        outcome.Image!.Format.Should().Be(ImageEncodeFormat.Png);
        using var result = SKBitmap.Decode(outcome.Image.Bytes);
        (result.Width, result.Height).Should().Be((tw, th));
        var values = result.Pixels.SelectMany(p => new[] { p.Red, p.Green, p.Blue }).Distinct().OrderBy(v => v).ToArray();
        values.Should().Equal([0, 255], "в маске после масштабирования только 0 и 255");
    }

    // Стирание кистью для локальной Qwen-Image: белое на маске — серое на картинке, остальное как было
    [Fact]
    public void Стирание_по_маске_закрашивает_серым_только_отмеченное()
    {
        var image = Png(Solid(8, 4, SKColors.Red));
        var mask = Png(Split(8, 4, SKColors.White, SKColors.Black));

        var outcome = _raster.EraseMasked(image, mask);

        outcome.Ok.Should().BeTrue(outcome.Message);
        using var result = SKBitmap.Decode(outcome.Image!.Bytes);
        result.GetPixel(1, 1).Should().Be(new SKColor(128, 128, 128));
        result.GetPixel(6, 2).Should().Be(SKColors.Red);
        _raster.EraseMasked(image, Png(Solid(4, 4, SKColors.White))).Error.Should().Be(RasterError.InvalidOp,
            "маска другого размера — отказ, а не сдвиг заливки");
    }

    [Fact]
    public void Ресайз_ступенями_размеры_и_пропорции()
    {
        var source = Jpeg(Split(4000, 3000, SKColors.Green, SKColors.White));

        var byWidth = _raster.Apply(source, [new ResizeOp(Width: 1000)]);
        byWidth.Ok.Should().BeTrue(byWidth.Message);
        (byWidth.Image!.Width, byWidth.Image.Height).Should().Be((1000, 750));
        byWidth.Image.Format.Should().Be(ImageEncodeFormat.Jpeg, "без Encode формат исходника сохраняется");

        var byPercent = _raster.Apply(source, [new ResizeOp(Percent: 10)], new ImageEncodeSpec(ImageEncodeFormat.Webp, 70));
        (byPercent.Image!.Width, byPercent.Image.Height).Should().Be((400, 300));
        _raster.Probe(byPercent.Image.Bytes)!.Format.Should().Be(ImageEncodeFormat.Webp);

        var free = _raster.Apply(source, [new ResizeOp(Height: 100, LockAspect: false)]);
        (free.Image!.Width, free.Image.Height).Should().Be((4000, 100));
    }

    [Fact]
    public void Обрезка_поворот_отражение_цепочкой()
    {
        // 40×20: левая половина красная, правая синяя
        var source = Png(Split(40, 20, SKColors.Red, SKColors.Blue));

        // Обрезка правой половины → 20×20 синего; отражение по горизонтали исходника → синее слева
        var cropped = _raster.Apply(source, [new CropOp(new ImageFractionRect(0.5, 0, 0.5, 1))]);
        (cropped.Image!.Width, cropped.Image.Height).Should().Be((20, 20));
        using (var b = SKBitmap.Decode(cropped.Image.Bytes)) b.GetPixel(2, 2).Should().Be(SKColors.Blue);

        var flipped = _raster.Apply(source, [new FlipOp(ImageFlipAxis.Horizontal)]);
        using (var b = SKBitmap.Decode(flipped.Image!.Bytes)) b.GetPixel(2, 10).Should().Be(SKColors.Blue);

        // 270° по часовой: левая (красная) половина уезжает вниз
        var rotated = _raster.Apply(source, [new RotateOp(270)]);
        (rotated.Image!.Width, rotated.Image.Height).Should().Be((20, 40));
        using (var b = SKBitmap.Decode(rotated.Image.Bytes))
        {
            b.GetPixel(10, 35).Should().Be(SKColors.Red);
            b.GetPixel(10, 5).Should().Be(SKColors.Blue);
        }
    }

    [Fact]
    public void Недопустимые_операции_отказ_400()
    {
        var source = Png(Solid(10, 10, SKColors.Gray));

        _raster.Apply(source, [new RotateOp(45)]).Error.Should().Be(RasterError.InvalidOp);
        _raster.Apply(source, [new CropOp(new ImageFractionRect(0.5, 0.5, 0.8, 0.2))]).Error.Should().Be(RasterError.InvalidOp);
        _raster.Apply(source, [new ResizeOp(Width: 100_000)]).Error.Should().Be(RasterError.InvalidOp);
        _raster.Apply(source, [], new ImageEncodeSpec(ImageEncodeFormat.Jpeg, 20)).Error.Should().Be(RasterError.InvalidOp);
        _raster.Apply(source, [new RotateOp(45)]).StatusCode.Should().Be(400);
    }

    [Fact]
    public void Сверх_двух_одновременных_операций_отказ_429()
    {
        var gate = new SemaphoreSlim(SkiaImageRaster.DefaultMaxConcurrent);
        var raster = new SkiaImageRaster(SkiaImageRaster.DefaultMaxMegapixels, gate);
        var source = Png(Solid(10, 10, SKColors.Gray));
        // Две операции «в полёте» занимают оба слота
        gate.Wait(0).Should().BeTrue();
        gate.Wait(0).Should().BeTrue();

        var busy = raster.Apply(source, []);
        busy.Error.Should().Be(RasterError.Busy);
        busy.StatusCode.Should().Be(429);

        gate.Release();
        raster.Apply(source, []).Ok.Should().BeTrue("освободившийся слот снова принимает работу");
        gate.CurrentCount.Should().Be(1, "операция обязана вернуть слот");
    }

    // ── Сборка картинок ─────────────────────────────────────────────────────────

    private static readonly byte[] ExifHeader = Encoding.ASCII.GetBytes("Exif\0\0");

    private static SKBitmap Solid(int w, int h, SKColor color, SKColorSpace? colorSpace = null)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
        bitmap.Erase(color);
        return bitmap;
    }

    private static SKBitmap Split(int w, int h, SKColor left, SKColor right)
    {
        var bitmap = Solid(w, h, left);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = right, IsAntialias = false };
        canvas.DrawRect(w / 2f, 0, w / 2f, h, paint);
        return bitmap;
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using (bitmap)
        using (var pixmap = bitmap.PeekPixels())
        using (var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)))
            return data!.ToArray();
    }

    private static byte[] Jpeg(SKBitmap bitmap, int quality = 90)
    {
        using (bitmap)
        using (var pixmap = bitmap.PeekPixels())
        using (var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore)))
            return data!.ToArray();
    }

    // Вставляет сегмент APP1/EXIF сразу после SOI: IFD0 с Orientation и, по желанию, GPS IFD с
    // GPSLatitudeRef = "N". Little-endian TIFF
    private static byte[] WithExif(byte[] jpeg, int orientation, bool withGps)
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
        var entries = withGps ? 2 : 1;
        Le16(tiff, entries);
        Entry(tiff, 0x0112, 3, 1, (uint)orientation);
        var gpsOffset = 8 + 2 + entries * 12 + 4;
        if (withGps) Entry(tiff, 0x8825, 4, 1, (uint)gpsOffset);
        Le32(tiff, 0);
        if (withGps)
        {
            Le16(tiff, 1);
            Entry(tiff, 0x0001, 2, 2, 'N'); // "N\0" помещается в поле значения
            Le32(tiff, 0);
        }

        var payload = ExifHeader.Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();

        static void Entry(List<byte> b, int tag, int type, int count, uint value)
        {
            Le16(b, tag); Le16(b, type); Le32(b, (uint)count); Le32(b, value);
        }
        static void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        static void Le32(List<byte> b, uint v) { for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
    }

    // Настоящий PNG гигантских размеров: серый 1 бит, все строки нулевые — сжимается в килобайты
    private static byte[] HugeBlankPng(int w, int h)
    {
        var rowBytes = 1 + (w + 7) / 8;
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[rowBytes];
            for (var y = 0; y < h; y++) z.Write(row);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        Be32(ihdr, 0, w); Be32(ihdr, 4, h);
        ihdr[8] = 1; // глубина 1 бит, тип 0 — оттенки серого
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be32(len, 0, data.Length); s.Write(len);
            var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(typeAndData);
            var crc = new byte[4]; Be32(crc, 0, (int)Crc32(typeAndData)); s.Write(crc);
        }
        static void Be32(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    // Профиль после записи в ICC и обратного чтения совпадает с эталоном до квантования
    // s15Fixed16, поэтому сравнение гамута — с допуском, а не SKColorSpace.Equal
    private static bool IsDisplayP3(SKColorSpace? cs)
    {
        if (cs is null || cs.IsSrgb) return false;
        var actual = cs.ToColorSpaceXyz().Values;
        var expected = SKColorSpaceXyz.DisplayP3.Values;
        return actual.Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-3);
    }

    private static bool IsNear(SKColor a, SKColor b, int tolerance = 40) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static int IndexOf(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle);
}
