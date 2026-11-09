using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class FilesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public FilesControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "file_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var dir = Path.Combine(_tempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    private async Task<(string projectId, string projectDir)> SetupProjectWithFileAsync(
        string fileName = "test.txt", string content = "hello content")
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "FileProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return (json.GetProperty("id").GetString()!, dir);
    }

    private async Task<string> SetupProjectWithBinaryAsync(string fileName, byte[] bytes)
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), bytes);

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DocProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    private async Task AssertDocumentAsync(string fileName, byte[] bytes, string expectedKind, string expectedMime)
    {
        var id = await SetupProjectWithBinaryAsync(fileName, bytes);
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path={fileName}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("isDocument").GetBoolean().Should().BeTrue();
        body.GetProperty("docKind").GetString().Should().Be(expectedKind);
        body.GetProperty("isBinary").GetBoolean().Should().BeTrue();
        body.GetProperty("isImage").GetBoolean().Should().BeFalse();
        body.GetProperty("mimeType").GetString().Should().Be(expectedMime);
        // base64 декодируется обратно в исходные байты
        Convert.FromBase64String(body.GetProperty("base64").GetString()!).Should().Equal(bytes);
    }

    [Fact]
    public async Task List_NonExistentProject_Returns404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent/files");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_EmptyProject_Returns200WithVirtualNotes()
    {
        var id = await CreateProjectAsync("EmptyList");
        var response = await _client.GetAsync($"/api/projects/{id}/files");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        // Пустой проект содержит только виртуальную папку заметок «notes»
        body.GetArrayLength().Should().Be(1);
        body[0].GetProperty("name").GetString().Should().Be("notes");
        body[0].GetProperty("isDirectory").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task List_ProjectWithFiles_ReturnsFiles()
    {
        var (id, dir) = await SetupProjectWithFileAsync("file.txt", "content");
        var response = await _client.GetAsync($"/api/projects/{id}/files");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Tree_ShowHiddenQueryParam_OverridesProjectSetting()
    {
        // Дефолт проекта — ShowHiddenFiles: false; без query-параметра дерево прежнее
        var (id, dir) = await SetupProjectWithFileAsync("visible.txt");
        Directory.CreateDirectory(Path.Combine(dir, ".omc"));
        File.WriteAllText(Path.Combine(dir, ".omc", "spec.md"), "");

        var withoutParam = await _client.GetAsync($"/api/projects/{id}/files/tree");
        withoutParam.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyWithout = JsonSerializer.Deserialize<JsonElement>(await withoutParam.Content.ReadAsStringAsync());
        bodyWithout.EnumerateArray().Should().NotContain(e => e.GetProperty("path").GetString() == ".omc/spec.md");

        // showHidden=true в query — виден скрытый файл независимо от настройки проекта
        var withParam = await _client.GetAsync($"/api/projects/{id}/files/tree?showHidden=true");
        withParam.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyWith = JsonSerializer.Deserialize<JsonElement>(await withParam.Content.ReadAsStringAsync());
        bodyWith.EnumerateArray().Should().Contain(e => e.GetProperty("path").GetString() == ".omc/spec.md");
    }

    [Fact]
    public async Task Tree_ClaudeWorktrees_NeverIncluded()
    {
        var (id, dir) = await SetupProjectWithFileAsync("visible.txt");
        var wt = Path.Combine(dir, ".claude", "worktrees", "feature-x");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, "Program.cs"), "");

        var response = await _client.GetAsync($"/api/projects/{id}/files/tree?showHidden=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.EnumerateArray().Should()
            .NotContain(e => e.GetProperty("path").GetString()!.StartsWith(".claude/worktrees", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_NonExistentProject_Returns404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent/files/search?q=test");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_WithMatchingFile_ReturnsResults()
    {
        var (id, _) = await SetupProjectWithFileAsync("searchable.txt");
        var response = await _client.GetAsync($"/api/projects/{id}/files/search?q=searchable");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetContent_TextFile_Returns200WithContent()
    {
        var (id, _) = await SetupProjectWithFileAsync("read.txt", "file content here");
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=read.txt");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("file content here");
        body.GetProperty("isBinary").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetContent_PdfFile_ReturnsDocumentBase64()
    {
        await AssertDocumentAsync("doc.pdf", System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 fake"),
            "pdf", "application/pdf");
    }

    [Fact]
    public async Task GetContent_DocxFile_ReturnsDocumentBase64()
    {
        // PK — сигнатура zip-контейнера OOXML
        await AssertDocumentAsync("doc.docx", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3],
            "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    [Fact]
    public async Task GetContent_XlsxFile_ReturnsDocumentBase64()
    {
        await AssertDocumentAsync("book.xlsx", [0x50, 0x4B, 0x03, 0x04, 9, 8, 7],
            "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [Fact]
    public async Task GetContent_VsdxFile_ReturnsDocumentBase64()
    {
        await AssertDocumentAsync("diagram.vsdx", [0x50, 0x4B, 0x03, 0x04, 4, 5, 6],
            "visio", "application/vnd.ms-visio.drawing");
    }

    // Путь папки (в т.ч. с завершающим слешем, как его отдаёт `git status` для новой папки)
    // не должен ронять чтение: раньше File.ReadAllText кидал DirectoryNotFoundException → 500
    [Theory]
    [InlineData(".claude")]
    [InlineData(".claude/")]
    public async Task GetContent_DirectoryPath_Returns404(string path)
    {
        var (id, dir) = await SetupProjectWithFileAsync();
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));

        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path={Uri.EscapeDataString(path)}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetContent_NonExistentFile_Returns404()
    {
        var id = await CreateProjectAsync("GetContentMissing");
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=missing.txt");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetContent_PathTraversal_Returns403()
    {
        var id = await CreateProjectAsync("PathTraversalTest");
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=../../etc/passwd");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SaveContent_TextFile_Returns200()
    {
        var (id, _) = await SetupProjectWithFileAsync("save.txt", "original");
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=save.txt",
            new { content = "updated content" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SaveContent_ThenGetContent_ReturnsUpdated()
    {
        var (id, _) = await SetupProjectWithFileAsync("roundtrip.txt", "original");
        await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=roundtrip.txt",
            new { content = "new content" });

        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=roundtrip.txt");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("new content");
    }

    [Fact]
    public async Task GetDiff_NonGitRepo_Returns200WithNullDiff()
    {
        var (id, _) = await SetupProjectWithFileAsync("diff.txt");
        var response = await _client.GetAsync($"/api/projects/{id}/files/diff?path=diff.txt");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("diff").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateFile_ValidPath_Returns200()
    {
        var id = await CreateProjectAsync("CreateFileTest");
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/files/create",
            new { path = "created.txt" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateFile_ExistingPath_Returns409AndKeepsContent()
    {
        var (id, _) = await SetupProjectWithFileAsync("clash.txt", "старое содержимое");
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/files/create",
            new { path = "clash.txt", content = "новое" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Коллизия не трогает существующий файл — тихая перезапись это data loss
        var get = await _client.GetAsync($"/api/projects/{id}/files/content?path=clash.txt");
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("старое содержимое");
    }

    [Fact]
    public async Task CreateFile_WithContent_WritesContent()
    {
        var id = await CreateProjectAsync("CreateFileContentTest");
        var template = """{"type":"excalidraw","version":2,"source":"ccs","elements":[],"appState":{},"files":{}}""";
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/files/create",
            new { path = "diagram.excalidraw", content = template });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var get = await _client.GetAsync($"/api/projects/{id}/files/content?path=diagram.excalidraw");
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be(template);
    }

    [Fact]
    public async Task CreateDir_ValidPath_Returns200()
    {
        var id = await CreateProjectAsync("MkdirTest");
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/files/mkdir",
            new { path = "newdir" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rename_ExistingFile_Returns200()
    {
        var (id, _) = await SetupProjectWithFileAsync("rename_me.txt");
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/files/rename",
            new { oldPath = "rename_me.txt", newPath = "renamed.txt" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_ExistingFile_Returns204()
    {
        var (id, _) = await SetupProjectWithFileAsync("to_delete.txt");
        var response = await _client.DeleteAsync($"/api/projects/{id}/files?path=to_delete.txt");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_NonExistentFile_Returns404()
    {
        var id = await CreateProjectAsync("DeleteMissing");
        var response = await _client.DeleteAsync($"/api/projects/{id}/files?path=ghost.txt");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- zip папки ----------

    [Fact]
    public async Task Zip_Folder_ContainsFilesAndSkipsServiceDirs()
    {
        var (id, dir) = await SetupProjectWithFileAsync("root.txt", "root");
        Directory.CreateDirectory(Path.Combine(dir, "src", "deep"));
        File.WriteAllText(Path.Combine(dir, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(dir, "src", "deep", "b.txt"), "bbb");
        Directory.CreateDirectory(Path.Combine(dir, "src", "node_modules"));
        File.WriteAllText(Path.Combine(dir, "src", "node_modules", "junk.js"), "x");

        var response = await _client.GetAsync($"/api/projects/{id}/files/zip?path=src");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("src.zip");
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("a.cs", "deep/b.txt");
        using var reader = new StreamReader(zip.GetEntry("deep/b.txt")!.Open());
        (await reader.ReadToEndAsync()).Should().Be("bbb");
    }

    [Fact]
    public async Task Zip_ProjectRoot_NamedAfterProject()
    {
        var (id, _) = await SetupProjectWithFileAsync("root.txt", "root");
        var response = await _client.GetAsync($"/api/projects/{id}/files/zip");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("FileProject.zip");
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        zip.Entries.Select(e => e.FullName).Should().Contain("root.txt");
    }

    [Fact]
    public async Task Zip_FileOlderThan1980_DoesNotBreakArchive()
    {
        var (id, dir) = await SetupProjectWithFileAsync("root.txt", "root");
        var old = Path.Combine(dir, "epoch.txt");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, new DateTime(1970, 1, 1));

        var response = await _client.GetAsync($"/api/projects/{id}/files/zip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        zip.GetEntry("epoch.txt")!.LastWriteTime.Year.Should().Be(1980);
    }

    [Fact]
    public async Task Zip_KeepsBuildDir_SkipsGitAndBinObj()
    {
        var (id, dir) = await SetupProjectWithFileAsync("root.txt", "root");
        foreach (var d in new[] { "build", ".git", "bin", "obj" })
        {
            Directory.CreateDirectory(Path.Combine(dir, d));
            File.WriteAllText(Path.Combine(dir, d, "f.txt"), d);
        }

        var response = await _client.GetAsync($"/api/projects/{id}/files/zip");

        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToList();
        names.Should().Contain("build/f.txt");
        names.Should().NotContain(n => n.StartsWith(".git/") || n.StartsWith("bin/") || n.StartsWith("obj/"));
    }

    [Fact]
    public async Task Zip_SymlinkedFolder_Returns403()
    {
        var (id, dir) = await SetupProjectWithFileAsync("root.txt", "root");
        var outside = Path.Combine(_tempDir, "outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        try { Directory.CreateSymbolicLink(Path.Combine(dir, "host"), outside); }
        // Windows без прав разработчика ссылок не создаёт — проверять нечего
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }

        var response = await _client.GetAsync($"/api/projects/{id}/files/zip?path=host");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Zip_MissingFolder_Returns404()
    {
        var id = await CreateProjectAsync("ZipMissing");
        var response = await _client.GetAsync($"/api/projects/{id}/files/zip?path=ghost");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Zip_PathTraversal_Returns403()
    {
        var id = await CreateProjectAsync("ZipTraversal");
        var response = await _client.GetAsync($"/api/projects/{id}/files/zip?path=..");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
