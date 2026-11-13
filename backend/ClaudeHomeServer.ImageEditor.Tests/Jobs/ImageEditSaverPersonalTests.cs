using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// ADR-040, Ф2-3: сохранение картинки в «Личное» — тот же ImageEditSaver, корень — папка «Личного», папка —
// Personal/images/. Перезаписи нет, занятое имя уходит в -2 / .v2, выход из корня и ссылка наружу — отказ
public class ImageEditSaverPersonalTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Old = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9];

    private readonly string _base = Path.Combine(Path.GetTempPath(), "ie-personal-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly ImageEditSaver _saver = new(new VersionedImageStore());

    public ImageEditSaverPersonalTests()
    {
        _root = Path.Combine(_base, "Personal");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static EditedImage Image(byte[] bytes) => new(bytes, "image/png");

    [Fact]
    public void Новая_картинка_ложится_в_images_а_занятое_имя_получает_номер()
    {
        var request = new ImageEditSaveRequest(null, 1, null, "images", null);

        var first = _saver.Save(_root, request, Image(Png));
        var second = _saver.Save(_root, request, Image(Png));

        first.Value!.Path.Should().Be("images/image.png");
        second.Value!.Path.Should().Be("images/image-2.png", "перезаписи нет");
    }

    [Fact]
    public void Старая_личная_версия_копируется_новым_файлом_а_повтор_получает_v2()
    {
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Old);
        var request = new ImageEditSaveRequest(null, 1, "images/hero.png", "images", null);

        var saved = _saver.Save(_root, request, Image(Png));

        saved.Value!.Path.Should().Be("images/hero.v2.png");
        File.ReadAllBytes(Path.Combine(_root, "images", "hero.png")).Should().Equal(Old, "исходник не тронут");
        File.ReadAllBytes(Path.Combine(_root, "images", "hero.v2.png")).Should().Equal(Png);
    }

    [Fact]
    public void Сохранить_как_с_занятым_именем_name_taken_и_подсказка()
    {
        _saver.SaveAs(_root, "images", "sunset", Image(Png));

        var taken = _saver.SaveAs(_root, "images", "sunset", Image(Png));

        taken.ErrorCode.Should().Be(ImageEditErrorCodes.NameTaken);
        _saver.Check(_root, "images", "sunset", ".png").Value!.Suggestion.Should().Be("images/sunset.v2.png");
    }

    [Fact]
    public void Папка_вне_корня_Личного_отказ_и_ничего_не_пишется()
    {
        var saved = _saver.Save(_root, new ImageEditSaveRequest(null, 1, null, "../outside", null), Image(Png));
        var savedAs = _saver.SaveAs(_root, "../outside", "x", Image(Png));

        saved.Value.Should().BeNull();
        savedAs.Value.Should().BeNull();
        Directory.Exists(Path.Combine(_base, "outside")).Should().BeFalse();
    }

    [Fact]
    public void Папка_images_как_символическая_ссылка_наружу_отказ()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(Path.Combine(_root, "images"));
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "images"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        var saved = _saver.Save(_root, new ImageEditSaveRequest(null, 1, null, "images", null), Image(Png));
        var savedAs = _saver.SaveAs(_root, "images", "x", Image(Png));

        saved.Value.Should().BeNull();
        savedAs.Value.Should().BeNull();
        Directory.GetFiles(outside).Should().BeEmpty("ссылка не выпускает запись из корня «Личного»");
    }
}
