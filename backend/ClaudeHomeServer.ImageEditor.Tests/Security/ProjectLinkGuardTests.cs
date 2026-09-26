using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Security;

// ADR-017 §8: символическая ссылка внутри проекта, ведущая наружу, не пропускает ни чтение
// образца (ReferencePaths), ни запись результата (save: папка «Нарисовать картинку» и
// папка исходника при версионном сохранении). SafePath.Join такую ссылку не видит.
public class ProjectLinkGuardTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _base;
    private readonly string _root;
    private readonly string _outside;

    public ProjectLinkGuardTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "plg_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "project");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Png);
        File.WriteAllBytes(Path.Combine(_outside, "secret.png"), Png);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* временная папка, не критично */ }
        GC.SuppressFinalize(this);
    }

    // Windows без прав на ссылки — проверка идёт в CI на Linux
    private static bool TryLinkDir(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool TryLinkFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    private static ImageEditSaver Saver() => new(new VersionedImageStore());

    private static EditedImage Image() => new(Png, "image/png");

    // ── ReferencePaths: чтение образца из проекта ─────────────────────────────────

    [Fact]
    public void Обычный_путь_образца_разрешается_в_файл_проекта()
    {
        ProjectLinkGuard.ResolveInside(_root, "images/hero.png")
            .Should().Be(Path.Combine(_root, "images", "hero.png"));
        ProjectLinkGuard.ResolveInside(_root, "images\\hero.png").Should().NotBeNull();
    }

    [Theory]
    [InlineData("../outside/secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("\\etc\\passwd")]
    [InlineData("images/../../outside/secret.png")]
    [InlineData("")]
    public void Путь_образца_вне_проекта_отклоняется(string path)
    {
        ProjectLinkGuard.ResolveInside(_root, path).Should().BeNull();
    }

    [Fact]
    public void Образец_через_папку_ссылку_наружу_отклоняется()
    {
        if (!TryLinkDir(Path.Combine(_root, "refs"), _outside)) return;

        File.Exists(Path.Combine(_root, "refs", "secret.png")).Should().BeTrue("ссылка рабочая");
        ProjectLinkGuard.ResolveInside(_root, "refs/secret.png").Should().BeNull();
    }

    [Fact]
    public void Образец_файлом_ссылкой_наружу_отклоняется()
    {
        if (!TryLinkFile(Path.Combine(_root, "images", "leak.png"), Path.Combine(_outside, "secret.png"))) return;

        ProjectLinkGuard.ResolveInside(_root, "images/leak.png").Should().BeNull();
    }

    [Fact]
    public void Висячая_ссылка_тоже_отклоняется()
    {
        if (!TryLinkFile(Path.Combine(_root, "images", "dangling.png"), Path.Combine(_outside, "nope.png"))) return;

        ProjectLinkGuard.ResolveInside(_root, "images/dangling.png").Should().BeNull();
    }

    [Fact]
    public void Корень_проекта_под_ссылкой_не_мешает()
    {
        var linkedRoot = Path.Combine(_base, "linked-project");
        if (!TryLinkDir(linkedRoot, _root)) return;

        ProjectLinkGuard.ResolveInside(linkedRoot, "images/hero.png").Should().NotBeNull();
    }

    // ── save: «Нарисовать картинку» в папку ───────────────────────────────────────

    [Fact]
    public void Сохранение_в_обычную_папку_создаёт_файл_и_следующий_номер()
    {
        var first = Saver().Save(_root, new ImageEditSaveRequest("j", 1, null, "images", "cat"), Image());
        var second = Saver().Save(_root, new ImageEditSaveRequest("j", 1, null, "images", "cat"), Image());

        first.Value!.Path.Should().Be("images/cat.png");
        second.Value!.Path.Should().Be("images/cat-2.png");
        File.ReadAllBytes(Path.Combine(_root, "images", "cat-2.png")).Should().Equal(Png);
    }

    [Fact]
    public void Сохранение_в_папку_ссылку_наружу_отклоняется_и_наружу_не_пишет()
    {
        if (!TryLinkDir(Path.Combine(_root, "out"), _outside)) return;

        var saved = Saver().Save(_root, new ImageEditSaveRequest("j", 1, null, "out", "cat"), Image());

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        saved.Error.Should().Contain("вне папки проекта");
        Directory.EnumerateFiles(_outside).Should().ContainSingle("наружу ничего не записано");
    }

    [Fact]
    public void Сохранение_во_вложенную_папку_под_ссылкой_отклоняется()
    {
        Directory.CreateDirectory(Path.Combine(_outside, "deep"));
        if (!TryLinkDir(Path.Combine(_root, "out"), _outside)) return;

        var saved = Saver().Save(_root, new ImageEditSaveRequest("j", 1, null, "out/deep", "cat"), Image());

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        Directory.EnumerateFiles(Path.Combine(_outside, "deep")).Should().BeEmpty();
    }

    // ── save: версионное сохранение рядом с исходником ───────────────────────────

    [Fact]
    public void Версия_рядом_с_исходником_в_папке_ссылке_наружу_отклоняется()
    {
        if (!TryLinkDir(Path.Combine(_root, "out"), _outside)) return;

        var saved = Saver().Save(_root, new ImageEditSaveRequest("j", 1, "out/secret.png", null, null), Image());

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        File.Exists(Path.Combine(_outside, "secret.v2.png")).Should().BeFalse("наружу ничего не записано");
    }

    [Fact]
    public void Версия_рядом_с_обычным_исходником_сохраняется()
    {
        var saved = Saver().Save(_root, new ImageEditSaveRequest("j", 1, "images/hero.png", null, null), Image());

        saved.Value!.Path.Should().Be("images/hero.v2.png");
    }

    [Fact]
    public void Хранилище_версий_само_отказывает_на_ссылке()
    {
        if (!TryLinkDir(Path.Combine(_root, "out"), _outside)) return;

        var act = () => new VersionedImageStore().SaveNextVersion(_root, "out/secret.png", Png);

        act.Should().Throw<UnauthorizedAccessException>();
        File.Exists(Path.Combine(_outside, "secret.v2.png")).Should().BeFalse();
    }
}
