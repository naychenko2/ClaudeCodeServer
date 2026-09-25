using ClaudeHomeServer.Services.ImageEditor.Versioning;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.ImageEditor.Versioning;

public class VersionedImageStoreTests : IDisposable
{
    private readonly string _root;
    private readonly VersionedImageStore _store = new();

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    public VersionedImageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vis_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* дев-стенд, не критично */ }
    }

    [Fact]
    public void SaveNextVersion_OriginalHasNoVersionYet_CreatesV2()
    {
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), [0]);

        var rel = _store.SaveNextVersion(_root, "images/hero.png", PngBytes);

        rel.Should().Be("images/hero.v2.png");
        File.ReadAllBytes(Path.Combine(_root, rel)).Should().Equal(PngBytes);
    }

    [Fact]
    public void SaveNextVersion_SecondSaveFromOriginal_CreatesV3AndKeepsV2Intact()
    {
        // Мутационный тест инварианта «оригинал (в т.ч. уже сохранённая версия) не
        // перезаписывается»: подмена FileMode.CreateNew на Create тихо затёрла бы hero.v2.png
        // вторым сохранением вместо перехода на hero.v3.png — обе проверки ниже это ловят.
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), [0]);
        byte[] v2Bytes = [.. PngBytes, 9];
        byte[] v3Bytes = [.. PngBytes, 7, 7];

        var firstRel = _store.SaveNextVersion(_root, "images/hero.png", v2Bytes);
        var secondRel = _store.SaveNextVersion(_root, "images/hero.png", v3Bytes);

        firstRel.Should().Be("images/hero.v2.png");
        secondRel.Should().Be("images/hero.v3.png");
        File.ReadAllBytes(Path.Combine(_root, firstRel)).Should().Equal(v2Bytes);
        File.ReadAllBytes(Path.Combine(_root, secondRel)).Should().Equal(v3Bytes);
    }

    [Fact]
    public void SaveNextVersion_FromAlreadyVersionedName_DoesNotDoubleSuffix()
    {
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.v2.png"), [0]);

        var rel = _store.SaveNextVersion(_root, "images/hero.v2.png", PngBytes);

        rel.Should().Be("images/hero.v3.png");
    }

    [Fact]
    public void SaveNextVersion_PngBytesUnderJpgName_SavesWithPngExtension()
    {
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.jpg"), [0]);

        var rel = _store.SaveNextVersion(_root, "images/hero.jpg", PngBytes);

        rel.Should().Be("images/hero.v2.png");
    }

    [Fact]
    public void SaveNextVersion_PathTraversalInOriginalPath_Throws()
    {
        var act = () => _store.SaveNextVersion(_root, "../../etc/hero.png", PngBytes);

        act.Should().Throw<UnauthorizedAccessException>();
    }
}
