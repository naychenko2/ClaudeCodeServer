using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// «Сохранить как…» (ADR-018 §5): ровно выбранное имя, перезаписи нет никогда, занятое имя —
// name_taken, подсказка — следующий свободный номер; проверка на лету ничего не пишет.
public class ImageEditSaverAsTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];
    private static readonly byte[] Old = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9];

    private readonly string _base = Path.Combine(Path.GetTempPath(), "ie-saveas-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;
    private readonly ImageEditSaver _saver = new(new VersionedImageStore());

    public ImageEditSaverAsTests()
    {
        _root = Path.Combine(_base, "project");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static EditedImage Image(byte[] bytes) => new(bytes, "image/png");

    private string[] AllFiles() => Directory.GetFiles(_base, "*", SearchOption.AllDirectories);

    [Fact]
    public void Занятое_имя_409_с_подсказкой_и_байты_существующего_не_меняются()
    {
        // Мутационный сторож инварианта «перезаписи нет»: FileMode.CreateNew → Create затёр бы hero.png
        var existing = Path.Combine(_root, "images", "hero.png");
        File.WriteAllBytes(existing, Old);
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.v2.png"), Old);

        var saved = _saver.SaveAs(_root, "images", "hero", Image(Png));
        var check = _saver.Check(_root, "images", "hero", ".png");

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.NameTaken);
        File.ReadAllBytes(existing).Should().Equal(Old);
        check.Value.Should().Be(new SaveCheckResponse("images/hero.png", true, "images/hero.v3.png"));
    }

    [Fact]
    public void Свободное_имя_пишется_ровно_им_с_расширением_по_формату()
    {
        var saved = _saver.SaveAs(_root, "images", "sunset", Image(Jpeg));

        saved.Value!.Path.Should().Be("images/sunset.jpg");
        File.ReadAllBytes(Path.Combine(_root, "images", "sunset.jpg")).Should().Equal(Jpeg);
    }

    [Theory]
    [InlineData("hero.png")]
    [InlineData("hero.PNG")]
    [InlineData("hero.png.png")]
    [InlineData("hero.jpg")]
    public void Вписанное_расширение_срезается_а_не_удваивается(string name)
    {
        var saved = _saver.SaveAs(_root, "", name, Image(Png));

        saved.Value!.Path.Should().Be("hero.png");
        File.Exists(Path.Combine(_root, "hero.png.png")).Should().BeFalse();
    }

    [Fact]
    public void Версия_в_имени_сохраняется_как_часть_имени()
    {
        _saver.SaveAs(_root, "images", "hero.v2", Image(Png)).Value!.Path.Should().Be("images/hero.v2.png");
    }

    [Theory]
    [InlineData("a/b.png")]
    [InlineData("a\\b.png")]
    [InlineData("../x.png")]
    [InlineData("/etc/x.png")]
    [InlineData(".hidden")]
    [InlineData(".png")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a\u0001b")]
    [InlineData("dot.")]
    public void Недопустимое_имя_400_и_ничего_не_пишется(string name)
    {
        var saved = _saver.SaveAs(_root, "images", name, Image(Png));
        var check = _saver.Check(_root, "images", name, ".png");

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        check.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        AllFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("images/../../outside")]
    [InlineData("/etc")]
    [InlineData("missing")]
    public void Папка_вне_проекта_или_несуществующая_400_и_папки_не_создаются(string folder)
    {
        var saved = _saver.SaveAs(_root, folder, "cat", Image(Png));

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        AllFiles().Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "missing")).Should().BeFalse("папки из диалога не создаём");
    }

    [Fact]
    public void Папка_ссылка_наружу_400_и_наружу_не_пишет()
    {
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "out"), _outside); }
        // Windows без прав на ссылки — проверка идёт в CI на Linux
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }

        var saved = _saver.SaveAs(_root, "out", "cat", Image(Png));
        var check = _saver.Check(_root, "out", "cat", ".png");

        saved.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        saved.Error.Should().Contain("символическую ссылку");
        check.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        Directory.EnumerateFiles(_outside).Should().BeEmpty();
    }

    [Fact]
    public void Check_ничего_не_пишет_и_говорит_что_имя_свободно()
    {
        var check = _saver.Check(_root, "images", "hero.png", ".webp");

        check.Value.Should().Be(new SaveCheckResponse("images/hero.webp", false, null));
        AllFiles().Should().BeEmpty();
    }

    [Fact]
    public void Корень_проекта_пустой_папкой()
    {
        _saver.SaveAs(_root, null, "cat", Image(Png)).Value!.Path.Should().Be("cat.png");
        _saver.Check(_root, "", "cat", ".png").Value!.Taken.Should().BeTrue();
    }

    [Fact]
    public async Task Гонка_двух_сохранений_одно_имя_один_файл_второе_409()
    {
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return _saver.SaveAs(_root, "images", "race", Image(Png));
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Value is not null).Should().Be(1);
        results.Where(r => r.Value is null).Should().OnlyContain(r => r.ErrorCode == ImageEditErrorCodes.NameTaken);
    }
}
