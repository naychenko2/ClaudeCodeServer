using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Faces;

// Маска лица на сервере (ADR-027, §4, этап 2): эллипс по bbox целевого лица (полуоси 0.75·w ×
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
