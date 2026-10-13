using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Faces;

// Маска лица на сервере (ADR-028, этап 2): эллипс по bbox целевого лица (полуоси 0.75·w ×
// 0.8·h, формула разведки), вычитание bbox всех остальных лиц (расширенных на 30 px), контроль
// остатка (меньше 60 % — «слишком близко»). Фейк растра записывает аргументы RenderMask.
public class FaceMaskBuilderTests
{
    private static LocalDetectedFace Face(int n, int x1, int y1, int x2, int y2) =>
        new(n, x1, y1, x2, y2, 0.9, false);

    private static (FaceMaskBuilder Builder, FakeRaster Raster) Create()
    {
        var raster = new FakeRaster();
        return (new FaceMaskBuilder(raster), raster);
    }

    // Одно лицо: эллипс — центр bbox, полуоси 0.75·w × 0.8·h, прямоугольников нет, PNG непустой
    [Fact]
    public void Одно_лицо_эллипс_по_bbox_без_прямоугольников()
    {
        var (builder, raster) = Create();
        var result = builder.Build(100, 100, [Face(1, 10, 10, 50, 50)], 1);

        result.Error.Should().BeNull();
        result.Png.Should().NotBeEmpty();
        raster.RenderMaskCalls.Should().Be(1);
        var e = raster.LastEllipses.Should().ContainSingle().Subject;
        e.Cx.Should().Be(30.0);
        e.Cy.Should().Be(30.0);
        e.Rx.Should().Be(30.0, "0.75 · (50-10)");
        e.Ry.Should().Be(32.0, "0.8 · (50-10)");
        raster.LastExcludeRects.Should().BeEmpty();
    }

    // Два лица далеко: bbox соседа (лицо 2, расширенный на 30) уходит в excludeRects; целевое
    // лицо (1) из себя не вычитается
    [Fact]
    public void Два_лица_далеко_сосед_вычитается_целевое_нет()
    {
        var (builder, raster) = Create();
        var result = builder.Build(200, 100, [Face(1, 10, 10, 50, 50), Face(2, 120, 10, 160, 50)], 1);

        result.Error.Should().BeNull();
        result.Png.Should().NotBeEmpty();
        var rect = raster.LastExcludeRects.Should().ContainSingle().Subject;
        rect.X1.Should().Be(90, "120 - 30");
        rect.Y1.Should().Be(-20, "10 - 30");
        rect.X2.Should().Be(190, "160 + 30");
        rect.Y2.Should().Be(80, "50 + 30");
    }

    // Сосед рядом, но остаток ≥ 60 %: маска строится, RenderMask вызван. Сосед (2, 75..115)
    // закрывает правую четверть эллипса (x ≥ 45), остаток ~75 %
    [Fact]
    public void Сосед_рядом_остаток_достаточен_маска_строится()
    {
        var (builder, raster) = Create();
        var result = builder.Build(200, 100, [Face(1, 10, 10, 50, 50), Face(2, 75, 10, 115, 50)], 1);

        result.Error.Should().BeNull();
        result.Png.Should().NotBeEmpty();
        raster.RenderMaskCalls.Should().Be(1);
    }

    // Сосед вплотную: остаток < 60 % — «слишком близко», RenderMask не вызывался
    [Fact]
    public void Сосед_вплотную_остаток_мал_слишком_близко()
    {
        var (builder, raster) = Create();
        var result = builder.Build(200, 100, [Face(1, 10, 10, 50, 50), Face(2, 30, 10, 70, 50)], 1);

        result.Error.Should().Be(FaceMaskBuilder.FacesTooClose);
        result.Png.Should().BeNull();
        raster.RenderMaskCalls.Should().Be(0);
    }

    // Эллипс частично за краем холста: считается по видимой части, не падает
    [Fact]
    public void Эллипс_частично_за_краем_видимая_часть_маска_строится()
    {
        var (builder, raster) = Create();
        // bbox (10,10)-(50,50): эллипс cx=30 cy=30 rx=30 ry=32 уходит за верх/лево холста
        var result = builder.Build(100, 100, [Face(1, 10, 10, 50, 50)], 1);

        result.Error.Should().BeNull();
        result.Png.Should().NotBeEmpty();
        raster.RenderMaskCalls.Should().Be(1);
    }

    // Эллипс целиком вне холста: S = 0 — «лицо вне картинки»
    [Fact]
    public void Эллипс_вне_холста_лицо_вне_картинки()
    {
        var (builder, raster) = Create();
        // bbox (200,200)-(240,240) на холсте 100×100: эллипс целиком за правым/нижним краем
        var result = builder.Build(100, 100, [Face(1, 200, 200, 240, 240)], 1);

        result.Error.Should().Be(FaceMaskBuilder.FaceOutsideImage);
        result.Png.Should().BeNull();
        raster.RenderMaskCalls.Should().Be(0);
    }

    // Номер не найден (включая пустой список): «такого лица нет»
    [Fact]
    public void Номер_не_найден_такого_лица_нет()
    {
        var (builder, raster) = Create();
        var result = builder.Build(100, 100, [Face(1, 10, 10, 50, 50)], 2);

        result.Error.Should().Be(FaceMaskBuilder.FaceNotFound);
        result.Png.Should().BeNull();
        raster.RenderMaskCalls.Should().Be(0);
    }

    [Fact]
    public void Пустой_список_лиц_такого_лица_нет()
    {
        var (builder, raster) = Create();
        var result = builder.Build(100, 100, [], 1);

        result.Error.Should().Be(FaceMaskBuilder.FaceNotFound);
        raster.RenderMaskCalls.Should().Be(0);
    }

    // width/height <= 0 — баг вызывающего: ArgumentOutOfRangeException
    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    [InlineData(100, -5)]
    public void Неположительный_размер_исключение(int width, int height)
    {
        var (builder, _) = Create();
        var act = () => builder.Build(width, height, [Face(1, 10, 10, 50, 50)], 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Кроп вокруг цели (ADR-028, решение 12): рамка ненулевой части маски (эллипс) + 64 px,
    // обрезанная холстом; X, Y кратны 8; Width, Height кратны 8 внутри холста, а прижатые к
    // правому/нижнему краю — до самого края (адаптер проверяет то же ДО очереди); кроп покрывает
    // всю маску и ⊇ рамка + 30 (обрезанная холстом); Scaled — вверх до ≈1 Мп, кратно 16.
    // Параметризовано на реалистичных bbox: центр, углы, края, холсты с шириной/высотой,
    // не кратной 8 (1313×777, 439×640). Живой прогон 13.10: округление вниз у края срезало
    // маску — полоса старого лица 7 px при ширине 439 и 1 строка при высоте 777
    [Theory]
    // (canvasW, canvasH, x1, y1, x2, y2)
    [InlineData(1024, 768, 120, 90, 380, 350)]    // центр, пример из ревью: cropRight=383 → 383%8=7
    [InlineData(1024, 768, 10, 10, 100, 100)]     // левый верхний угол
    [InlineData(1024, 768, 924, 668, 1014, 758)]  // правый нижний угол
    [InlineData(1024, 768, 400, 10, 600, 100)]     // верхняя кромка
    [InlineData(1024, 768, 400, 668, 600, 758)]   // нижняя кромка
    [InlineData(1313, 777, 120, 90, 380, 350)]    // холст 1313×777 (1313%8=5, 777%8=1)
    [InlineData(1313, 777, 10, 10, 100, 100)]     // холст некратный 8, левый верх
    [InlineData(1313, 777, 1213, 677, 1303, 767)] // холст некратный 8, правый низ
    [InlineData(800, 600, 300, 200, 500, 400)]    // обычный
    [InlineData(800, 600, 0, 0, 50, 50)]          // лицо в самом углу (0,0)
    [InlineData(800, 600, 750, 550, 800, 600)]   // лицо в правом нижнем углу
    [InlineData(100, 100, 10, 10, 50, 50)]        // очень маленький холст
    [InlineData(439, 640, 340, 250, 435, 380)]    // холст 439 (439%8=7), лицо у правого края
    [InlineData(439, 640, 300, 520, 420, 636)]    // холст 439×640, правый нижний угол
    [InlineData(1313, 777, 600, 690, 700, 775)]   // холст 1313×777, лицо у нижнего края
    [InlineData(1313, 777, 1220, 300, 1310, 420)] // холст 1313×777, лицо у правого края
    public void Кроп_Геометрия_ВнутриХолста_Кратно8_ПокрываетМаску_СкалитсяДоМп(
        int canvasW, int canvasH, int x1, int y1, int x2, int y2)
    {
        var (builder, _) = Create();
        // С лимитами модели: мелкий кроп увеличивается до ≈1 Мп как раньше, лимит (1,8 Мп) не мешает
        var result = builder.Build(canvasW, canvasH, [Face(1, x1, y1, x2, y2)], 1, QwenLimits);

        result.Error.Should().BeNull();
        result.Crop.Should().NotBeNull();
        var crop = result.Crop!;

        // X, Y кратны 8; Width/Height кратны 8, если кроп не прижат к правому/нижнему краю
        // (то же проверяет адаптер LocalImageMediaAdapter, раздел «Кроп вокруг цели»)
        (crop.X % 8).Should().Be(0, $"X={crop.X}");
        (crop.Y % 8).Should().Be(0, $"Y={crop.Y}");
        if (crop.X + crop.Width != canvasW) (crop.Width % 8).Should().Be(0, $"Width={crop.Width}");
        if (crop.Y + crop.Height != canvasH) (crop.Height % 8).Should().Be(0, $"Height={crop.Height}");

        // Кроп внутри холста
        crop.X.Should().BeGreaterOrEqualTo(0);
        crop.Y.Should().BeGreaterOrEqualTo(0);
        (crop.X + crop.Width).Should().BeLessOrEqualTo(canvasW, $"кроп не выходит за холст ({canvasW}×{canvasH})");
        (crop.Y + crop.Height).Should().BeLessOrEqualTo(canvasH, $"кроп не выходит за холст ({canvasW}×{canvasH})");

        // Кроп ⊇ рамка маски + 30 (обрезанная холстом): рамка эллипса, обрезанная холстом
        var w = x2 - x1;
        var h = y2 - y1;
        var cx = (x1 + x2) / 2.0;
        var cy = (y1 + y2) / 2.0;
        var rx = FaceMaskBuilder.EllipseWidthShare * w;
        var ry = FaceMaskBuilder.EllipseHeightShare * h;
        var left = Math.Max(0, (int)Math.Floor(cx - rx));
        var top = Math.Max(0, (int)Math.Floor(cy - ry));
        var right = Math.Min(canvasW, (int)Math.Ceiling(cx + rx));
        var bottom = Math.Min(canvasH, (int)Math.Ceiling(cy + ry));

        // Левая/верхняя граница кропа ≤ рамка − 30 (обрезанная холстом)
        crop.X.Should().BeLessOrEqualTo(Math.Max(0, left - FaceMaskBuilder.NeighborMarginPx),
            $"кроп ⊇ рамка({left},{top})−30");
        crop.Y.Should().BeLessOrEqualTo(Math.Max(0, top - FaceMaskBuilder.NeighborMarginPx),
            $"кроп ⊇ рамка({left},{top})−30");
        // Ни одного пикселя маски вне кропа: рамка маски (обрезанная холстом) целиком в кропе
        crop.X.Should().BeLessOrEqualTo(left, "маска не срезана слева");
        crop.Y.Should().BeLessOrEqualTo(top, "маска не срезана сверху");
        (crop.X + crop.Width).Should().BeGreaterOrEqualTo(right, $"маска не срезана справа (рамка до {right})");
        (crop.Y + crop.Height).Should().BeGreaterOrEqualTo(bottom, $"маска не срезана снизу (рамка до {bottom})");
        // Правая/нижняя граница кропа ≥ рамка + 30, обрезанная холстом (у края — до самого края)
        (crop.X + crop.Width).Should().BeGreaterOrEqualTo(
            Math.Min(canvasW, right + FaceMaskBuilder.NeighborMarginPx), $"кроп ⊇ рамка+30 до {right}");
        (crop.Y + crop.Height).Should().BeGreaterOrEqualTo(
            Math.Min(canvasH, bottom + FaceMaskBuilder.NeighborMarginPx), $"кроп ⊇ рамка+30 до {bottom}");

        // Scaled ≥ кропа, кратно 16; для мелкого кропа ≈1 Мп (ScaleUp округляет каждую ось
        // отдельно до 16, поэтому площадь может быть чуть ниже цели — проверяем ≥ 99.9 % цели)
        crop.ScaledWidth.Should().BeGreaterOrEqualTo(crop.Width);
        crop.ScaledHeight.Should().BeGreaterOrEqualTo(crop.Height);
        (crop.ScaledWidth % 16).Should().Be(0);
        (crop.ScaledHeight % 16).Should().Be(0);
        ((long)crop.ScaledWidth * crop.ScaledHeight).Should().BeGreaterOrEqualTo(
            (long)(FaceMaskBuilder.CropTargetPixels * 0.999), "≈1 Мп (округление осей до 16)");
    }

    // Лимиты входа боевой модели подстановки на лицо: 1664 px по стороне, 1,8 Мп
    private static readonly ImageEditCaps QwenLimits = ImageEditCatalog.WithInputLimits(
        new LocalImageEditor(null).Models.Single(m => m.Id == LocalImageEditor.QwenImage)).Caps;

    // Кроп ≥ 1 Мп, но в пределах лимита модели — Scaled = размер кропа (округлённый вверх до 16),
    // не уменьшаем. До Д5 тест звучал «крупный кроп не уменьшается» без лимита: исходник ужимался
    // фиттером, и кроп больше лимита не встречался. Теперь исходник при кропе не ужимается, и кроп
    // больше лимита уменьшается (тест ниже) — здесь держим, что ниже лимита уменьшения нет.
    // Параметризовано: (canvasW, canvasH, x1, y1, x2, y2) — включая холст с шириной, не кратной 16
    [Theory]
    [InlineData(1200, 1200, 100, 100, 1100, 1100)]  // кроп = весь холст 1200×1200 = 1.44 Мп
    [InlineData(1100, 1100, 100, 100, 1000, 1000)]  // холст 1100 (1100%16=4): кроп ~1100 → Scaled = RoundUp16
    public void Кроп_Крупный_в_пределах_лимита_модели_Скалится_Равен_Кропу(int canvasW, int canvasH, int x1, int y1, int x2, int y2)
    {
        var (builder, _) = Create();
        var result = builder.Build(canvasW, canvasH, [Face(1, x1, y1, x2, y2)], 1, QwenLimits);

        result.Error.Should().BeNull();
        result.Crop.Should().NotBeNull();
        var crop = result.Crop!;
        var area = (long)crop.Width * crop.Height;
        area.Should().BeGreaterOrEqualTo(FaceMaskBuilder.CropTargetPixels, "кроп крупный");
        // Крупный кроп не уменьшается: Scaled ≥ кропа (RoundUp16 не даёт меньше)
        crop.ScaledWidth.Should().BeGreaterOrEqualTo(crop.Width, "крупный кроп не уменьшается");
        crop.ScaledHeight.Should().BeGreaterOrEqualTo(crop.Height, "крупный кроп не уменьшается");
        (crop.ScaledWidth % 16).Should().Be(0);
        (crop.ScaledHeight % 16).Should().Be(0);
    }

    // Д5: кроп больше лимита модели (лицо крупным планом на 4000×3000 — кроп ≈1,6×2 тыс. px, ≈3,3 Мп)
    // уменьшается до лимита: площадь ≤ 1,8 Мп, сторона ≤ 1664, кратно 16, пропорции ≈ кропа.
    // Без лимита (null) — как раньше, не уменьшается. Мутация «не уменьшать вниз» → красный
    [Fact]
    public void Кроп_Больше_лимита_модели_уменьшается_до_лимита_кратно_16()
    {
        var (builder, _) = Create();
        var faces = new[] { Face(1, 1500, 1000, 2500, 2200) };

        var crop = builder.Build(4000, 3000, faces, 1, QwenLimits).Crop!;

        ((long)crop.Width * crop.Height).Should().BeGreaterThan(1_800_000, "кроп больше лимита модели");
        ((long)crop.ScaledWidth * crop.ScaledHeight).Should().BeLessOrEqualTo(1_800_000, "площадь в лимите");
        Math.Max(crop.ScaledWidth, crop.ScaledHeight).Should().BeLessOrEqualTo(1664, "сторона в лимите");
        crop.ScaledWidth.Should().BeLessThan(crop.Width, "кроп уменьшен");
        (crop.ScaledWidth % 16).Should().Be(0);
        (crop.ScaledHeight % 16).Should().Be(0);
        ((double)crop.ScaledWidth / crop.ScaledHeight).Should().BeApproximately((double)crop.Width / crop.Height, 0.02);

        var unlimited = builder.Build(4000, 3000, faces, 1).Crop!;
        unlimited.ScaledWidth.Should().BeGreaterOrEqualTo(unlimited.Width, "без лимита кроп не уменьшается");
        (unlimited with { ScaledWidth = 0, ScaledHeight = 0 }).Should().Be(crop with { ScaledWidth = 0, ScaledHeight = 0 },
            "лимит меняет только размер увеличения, не прямоугольник кропа");
    }

    // Граница: Scaled ровно на лимите стороны — не уменьшается; лимит на 1 px меньше — уменьшается
    // до кратного 16, не выше лимита
    [Fact]
    public void Кроп_На_границе_лимита_не_уменьшается_за_ней_уменьшается()
    {
        var (builder, _) = Create();
        var faces = new[] { Face(1, 100, 100, 1100, 1100) };
        var free = builder.Build(1200, 1200, faces, 1).Crop!;
        var side = Math.Max(free.ScaledWidth, free.ScaledHeight);

        var atLimit = builder.Build(1200, 1200, faces, 1, QwenLimits with { MaxInputSide = side, MaxInputMegapixels = null }).Crop!;
        var overLimit = builder.Build(1200, 1200, faces, 1, QwenLimits with { MaxInputSide = side - 1, MaxInputMegapixels = null }).Crop!;

        atLimit.Should().Be(free, "ровно на лимите — без изменений");
        Math.Max(overLimit.ScaledWidth, overLimit.ScaledHeight).Should().BeLessOrEqualTo(side - 1);
        Math.Max(overLimit.ScaledWidth, overLimit.ScaledHeight).Should().Be(side - 16, "вниз до ближайшего кратного 16");
        (overLimit.ScaledWidth % 16).Should().Be(0);
        (overLimit.ScaledHeight % 16).Should().Be(0);
    }

    // Сквозной тест (ADR-028, решение 12): реалистичный bbox из ревью → FaceMaskBuilder.Build →
    // EditRequestComposer.Compose → кроп в ImageEditRequest проходит валидацию адаптера
    // (проверка кратности кропа в адаптере LocalImageMediaAdapter: X/Y/Width/Height кратны 8). Именно этот сценарий падал:
    // bbox (120,90)-(380,350) на 1024×768 давал cropRight=383 (383%8=7) → адаптер отказывал
    [Fact]
    public void Сквозной_РеалистичныйBbox_КропПроходитВалидациюАдаптера()
    {
        var (builder, _) = Create();
        // Пример из ревью: bbox (120,90)-(380,350) на холсте 1024×768
        var result = builder.Build(1024, 768, [Face(1, 120, 90, 380, 350)], 1);

        result.Error.Should().BeNull();
        result.Crop.Should().NotBeNull();
        var crop = result.Crop!;

        // Проверка кратности кропа в адаптере LocalImageMediaAdapter: X/Y/Width/Height кратны 8
        (crop.X % 8).Should().Be(0, $"X={crop.X}");
        (crop.Y % 8).Should().Be(0, $"Y={crop.Y}");
        (crop.Width % 8).Should().Be(0, $"Width={crop.Width}");
        (crop.Height % 8).Should().Be(0, $"Height={crop.Height}");
        // Scaled кратно 16 (та же проверка кратности кропа в адаптере)
        (crop.ScaledWidth % 16).Should().Be(0);
        (crop.ScaledHeight % 16).Should().Be(0);

        // Сквозной путь: кроп из Build → EditRequestComposer.Compose → ImageEditRequest.FaceCrop
        var png = ClaudeHomeServer.Tests.ImageEditor.Fakes.TestImages.Png(1024, 768);
        var source = new ImageBytes(png, "image/png");
        var mask = new ImageBytes(png, "image/png");
        var model = new ImageEditModelInfo("qwen", "Qwen",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.Veil, 15, 4, true));
        var input = new ImageEditJobInput("", "заменить лицо", null, source, mask, null, [], "images/hero.png",
            FaceCrop: crop, KeepMask: mask);
        var request = EditRequestComposer.Compose(input, ImageEditOp.Edit, model, 1).Value!;

        // Кроп доехал в запрос (record — без трансформации) и маска — явным каналом KeepMask
        request.FaceCrop.Should().Be(crop);
        request.KeepMask.Should().NotBeNull("маска — явным каналом KeepMask, не образцом");
    }

    // Фейк растра: записывает аргументы RenderMask, возвращает заглушку; прочие методы не
    // используются (Build зовёт только RenderMask)
    private sealed class FakeRaster : IImageRaster
    {
        public int RenderMaskCalls;
        public List<MaskEllipse> LastEllipses = [];
        public List<MaskRect> LastExcludeRects = [];

        public RasterProbe? Probe(byte[] data) => throw new NotImplementedException();
        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null) =>
            throw new NotImplementedException();
        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => throw new NotImplementedException();
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => throw new NotImplementedException();
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => throw new NotImplementedException();

        public byte[] RenderMask(int width, int height, IReadOnlyList<MaskEllipse> ellipses, IReadOnlyList<MaskRect> excludeRects)
        {
            RenderMaskCalls++;
            LastEllipses = [.. ellipses];
            LastExcludeRects = [.. excludeRects];
            return [1, 2, 3];
        }
    }
}
