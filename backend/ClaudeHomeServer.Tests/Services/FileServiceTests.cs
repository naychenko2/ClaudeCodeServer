using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class FileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileService _svc = new();

    public FileServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ─── SafeJoin ───────────────────────────────────────────────────────────

    [Fact]
    public void SafeJoin_ValidRelativePath_ReturnsPathUnderRoot()
    {
        var result = FileService.SafeJoin(_root, "sub/file.txt");
        result.Should().StartWith(_root);
    }

    [Fact]
    public void SafeJoin_DotDotTraversal_ThrowsUnauthorizedAccess()
    {
        var act = () => FileService.SafeJoin(_root, "../../etc/passwd");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void SafeJoin_AbsolutePathOutsideRoot_DoesNotEscapeRoot()
    {
        // Абсолютный путь чужого корня не должен давать доступ за пределы root.
        // Семантика зависит от платформы, поэтому берём абсолютный путь под текущую ОС:
        if (OperatingSystem.IsWindows())
        {
            // На Windows путь другого драйва отбрасывает root — SafeJoin отвергает его.
            var act = () => FileService.SafeJoin(_root, @"C:\Windows\System32\cmd.exe");
            act.Should().Throw<UnauthorizedAccessException>();
        }
        else
        {
            // На Unix ведущий «/» срезается, путь остаётся под root — доступа наружу нет.
            var result = FileService.SafeJoin(_root, "/etc/passwd");
            result.Should().StartWith(_root);
        }
    }

    [Fact]
    public void SafeJoin_SiblingWithCommonPrefix_ThrowsUnauthorizedAccess()
    {
        // root "...\proj" не должен пропускать "...\proj2\secret" (общий префикс имени)
        var act = () => FileService.SafeJoin(_root, ".." + Path.DirectorySeparatorChar +
            Path.GetFileName(_root) + "2" + Path.DirectorySeparatorChar + "secret.txt");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void SafeJoin_RootItself_ReturnsRoot()
    {
        var result = FileService.SafeJoin(_root, "");
        result.TrimEnd(Path.DirectorySeparatorChar).Should().Be(_root.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void SafeJoin_CaseInsensitive_ReEnterRootInDifferentCase_Allowed()
    {
        // Сравнение в SafeJoin регистронезависимое (OrdinalIgnoreCase):
        // выход из root и возврат в него же другим регистром — не traversal
        var upperName = Path.GetFileName(_root).ToUpperInvariant();
        var result = FileService.SafeJoin(_root,
            ".." + Path.DirectorySeparatorChar + upperName + Path.DirectorySeparatorChar + "file.txt");

        result.Should().EndWith("file.txt");
        result.Should().StartWithEquivalentOf(_root); // регистронезависимое сравнение префикса
    }

    [Fact]
    public void SafeJoin_AbsoluteSiblingWithCommonPrefix_DoesNotEscapeRoot()
    {
        // root "...\proj" не должен давать доступ к соседнему "...\proj2\secret" (общий префикс имени).
        // Семантика абсолютного пути зависит от платформы:
        var absoluteSibling = _root + "2" + Path.DirectorySeparatorChar + "secret.txt";
        if (OperatingSystem.IsWindows())
        {
            // На Windows это абсолютный путь другого каталога — SafeJoin его отвергает.
            var act = () => FileService.SafeJoin(_root, absoluteSibling);
            act.Should().Throw<UnauthorizedAccessException>();
        }
        else
        {
            // На Unix ведущий «/» срезается, путь оседает под root — до соседнего каталога не дотянуться.
            var result = FileService.SafeJoin(_root, absoluteSibling);
            result.Should().StartWith(_root);
        }
    }

    // Свойство безопасности: любой хитрый относительный путь либо отклоняется,
    // либо нормализуется внутрь root. Trailing dots/spaces и смесь слэшей
    // нормализуются по-разному на Windows/Linux — проверяем инвариант, а не точный вид.
    [Theory]
    [InlineData("sub/../file.txt. ")]     // trailing dot + space
    [InlineData("sub/..././file.txt")]    // сегмент "..." + "."
    [InlineData("sub\\..\\file.txt")]     // backslash-траверс внутри root
    [InlineData("sub/inner\\file.txt")]   // смесь разделителей
    [InlineData(".. \\.. \\secret")]      // ".. " с пробелом
    [InlineData("..\\../..\\secret")]     // смесь слэшей в траверсе наружу
    public void SafeJoin_TrickyPaths_EitherRejectedOrStayUnderRoot(string tricky)
    {
        var rootFull = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            var result = FileService.SafeJoin(_root, tricky);
            // Не бросило — результат обязан остаться внутри root
            (result.Equals(rootFull, StringComparison.OrdinalIgnoreCase) ||
             result.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue($"путь «{tricky}» нормализовался в «{result}» вне root");
        }
        catch (UnauthorizedAccessException)
        {
            // Отклонён — тоже корректный исход
        }
    }

    [Fact]
    public void SafeJoin_ForwardSlashes_NormalizedUnderRoot()
    {
        var result = FileService.SafeJoin(_root, "a/b/c.txt");
        result.Should().StartWith(_root);
        result.Should().EndWith("c.txt");
    }

    // ─── List ────────────────────────────────────────────────────────────────

    [Fact]
    public void List_EmptyDir_ReturnsOnlyVirtualNotes()
    {
        // Корень всегда содержит виртуальную папку заметок (vault проекта)
        _svc.List(_root).Should().ContainSingle(e => e.IsDirectory && e.Name == "notes");
    }

    [Fact]
    public void List_WithFiles_ReturnsFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var entries = _svc.List(_root).Where(e => e.Name != "notes").ToList();
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(e => e.IsDirectory.Should().BeFalse());
    }

    [Fact]
    public void List_WithSubDir_ReturnsDirFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subdir"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");

        var entries = _svc.List(_root).Where(e => e.Name != "notes").ToList();
        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(e => e.IsDirectory && e.Name == "subdir");
        entries.Should().ContainSingle(e => !e.IsDirectory && e.Name == "file.txt");
    }

    [Fact]
    public void List_NotesVirtual_ExpandsToEmptyAndNotDuplicated()
    {
        // Раскрытие несозданной notes/ — пустой список, не исключение
        _svc.List(_root, "notes").Should().BeEmpty();

        // Физическая notes/ не дублируется виртуальной записью
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        _svc.List(_root).Count(e => e.Name == "notes").Should().Be(1);
    }

    [Fact]
    public void List_SubPath_ListsSubdirContents()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "inner.txt"), "");

        var entries = _svc.List(_root, "sub").ToList();
        entries.Should().ContainSingle(e => e.Name == "inner.txt");
    }

    [Fact]
    public void List_NonExistentSubDir_ThrowsDirectoryNotFound()
    {
        var act = () => _svc.List(_root, "ghost").ToList();
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void List_FileEntry_HasCorrectRelativePath()
    {
        File.WriteAllText(Path.Combine(_root, "f.txt"), "");
        var entry = _svc.List(_root).Single(e => !e.IsDirectory);
        entry.Path.Should().Be("f.txt");
    }

    // ─── Search ──────────────────────────────────────────────────────────────

    [Fact]
    public void Search_ByName_ReturnsMatches()
    {
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "");
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "");

        _svc.Search(_root, "alpha").Should().HaveCount(1)
            .And.Contain(e => e.Name == "alpha.txt");
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "");
        _svc.Search(_root, "readme").Should().HaveCount(1);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "");
        _svc.Search(_root, "xyz").Should().BeEmpty();
    }

    [Fact]
    public void Search_InSubDirs_ReturnsNestedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "deep", "nested"));
        File.WriteAllText(Path.Combine(_root, "deep", "nested", "found.cs"), "");

        _svc.Search(_root, "found").Should().HaveCount(1);
    }

    // ─── ReadFile / WriteFile ────────────────────────────────────────────────

    [Fact]
    public void WriteFile_ThenReadFile_ReturnsSameContent()
    {
        _svc.WriteFile(_root, "test.txt", "hello world");
        _svc.ReadFile(_root, "test.txt").Should().Be("hello world");
    }

    [Fact]
    public void WriteFile_Overwrites_ExistingContent()
    {
        _svc.WriteFile(_root, "f.txt", "first");
        _svc.WriteFile(_root, "f.txt", "second");
        _svc.ReadFile(_root, "f.txt").Should().Be("second");
    }

    [Fact]
    public void ReadFile_PathTraversal_ThrowsUnauthorizedAccess()
    {
        var act = () => _svc.ReadFile(_root, "../../etc/passwd");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void WriteFile_PathTraversal_ThrowsUnauthorizedAccess()
    {
        var act = () => _svc.WriteFile(_root, "../../malicious", "bad");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    // ─── CreateFile ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateFile_CreatesEmptyFile()
    {
        _svc.CreateFile(_root, "new.txt");
        File.Exists(Path.Combine(_root, "new.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "new.txt")).Should().BeEmpty();
    }

    [Fact]
    public void CreateFile_CreatesParentDirectories()
    {
        _svc.CreateFile(_root, "sub/deep/new.txt");
        File.Exists(Path.Combine(_root, "sub", "deep", "new.txt")).Should().BeTrue();
    }

    // ─── CreateDirectory ─────────────────────────────────────────────────────

    [Fact]
    public void CreateDirectory_CreatesDir()
    {
        _svc.CreateDirectory(_root, "newdir");
        Directory.Exists(Path.Combine(_root, "newdir")).Should().BeTrue();
    }

    [Fact]
    public void CreateDirectory_NestedPath_CreatesAllDirs()
    {
        _svc.CreateDirectory(_root, "a/b/c");
        Directory.Exists(Path.Combine(_root, "a", "b", "c")).Should().BeTrue();
    }

    // ─── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_ExistingFile_DeletesFile()
    {
        File.WriteAllText(Path.Combine(_root, "del.txt"), "");
        _svc.Delete(_root, "del.txt");
        File.Exists(Path.Combine(_root, "del.txt")).Should().BeFalse();
    }

    [Fact]
    public void Delete_ExistingDirectory_DeletesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "todelete"));
        File.WriteAllText(Path.Combine(_root, "todelete", "inner.txt"), "");
        _svc.Delete(_root, "todelete");
        Directory.Exists(Path.Combine(_root, "todelete")).Should().BeFalse();
    }

    [Fact]
    public void Delete_NonExistent_ThrowsFileNotFound()
    {
        var act = () => _svc.Delete(_root, "ghost.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    // ─── Rename ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_File_RenamesSuccessfully()
    {
        File.WriteAllText(Path.Combine(_root, "old.txt"), "content");
        _svc.Rename(_root, "old.txt", "new.txt");
        File.Exists(Path.Combine(_root, "old.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "new.txt")).Should().Be("content");
    }

    [Fact]
    public void Rename_Directory_RenamesSuccessfully()
    {
        Directory.CreateDirectory(Path.Combine(_root, "olddir"));
        _svc.Rename(_root, "olddir", "newdir");
        Directory.Exists(Path.Combine(_root, "olddir")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "newdir")).Should().BeTrue();
    }

    // ─── IsBinaryFile ────────────────────────────────────────────────────────

    [Fact]
    public void IsBinaryFile_TextFile_ReturnsFalse()
    {
        File.WriteAllText(Path.Combine(_root, "t.txt"), "hello");
        _svc.IsBinaryFile(_root, "t.txt").Should().BeFalse();
    }

    [Fact]
    public void IsBinaryFile_PngExtension_ReturnsTrue()
    {
        File.WriteAllBytes(Path.Combine(_root, "img.png"), [1, 2, 3]);
        _svc.IsBinaryFile(_root, "img.png").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_ExeExtension_ReturnsTrue()
    {
        File.WriteAllBytes(Path.Combine(_root, "app.exe"), []);
        _svc.IsBinaryFile(_root, "app.exe").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_NonExistentFile_ReturnsFalse()
    {
        _svc.IsBinaryFile(_root, "ghost.exe").Should().BeFalse();
    }

    // ─── IsImageFile ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("icon.gif")]
    [InlineData("icon.svg")]
    [InlineData("icon.webp")]
    public void IsImageFile_ImageExtensions_ReturnsTrue(string filename)
    {
        _svc.IsImageFile(_root, filename).Should().BeTrue();
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData("app.exe")]
    [InlineData("doc.pdf")]
    [InlineData("archive.zip")]
    public void IsImageFile_NonImageExtensions_ReturnsFalse(string filename)
    {
        _svc.IsImageFile(_root, filename).Should().BeFalse();
    }

    // ─── GetFileBase64 ───────────────────────────────────────────────────────

    [Fact]
    public void GetFileBase64_ReturnsCorrectBase64()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        File.WriteAllBytes(Path.Combine(_root, "img.png"), bytes);
        _svc.GetFileBase64(_root, "img.png").Should().Be(Convert.ToBase64String(bytes));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
