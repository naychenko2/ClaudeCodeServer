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
    public void SafeJoin_AbsolutePathOutsideRoot_ThrowsUnauthorizedAccess()
    {
        var act = () => FileService.SafeJoin(_root, @"C:\Windows\System32\cmd.exe");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    // ─── List ────────────────────────────────────────────────────────────────

    [Fact]
    public void List_EmptyDir_ReturnsEmpty()
    {
        _svc.List(_root).Should().BeEmpty();
    }

    [Fact]
    public void List_WithFiles_ReturnsFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var entries = _svc.List(_root).ToList();
        entries.Should().HaveCount(2);
        entries.Should().AllSatisfy(e => e.IsDirectory.Should().BeFalse());
    }

    [Fact]
    public void List_WithSubDir_ReturnsDirFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subdir"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");

        var entries = _svc.List(_root).ToList();
        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(e => e.IsDirectory && e.Name == "subdir");
        entries.Should().ContainSingle(e => !e.IsDirectory && e.Name == "file.txt");
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
        var entry = _svc.List(_root).Single();
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
