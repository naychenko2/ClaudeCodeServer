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
    public async Task List_EmptyProject_Returns200EmptyArray()
    {
        var id = await CreateProjectAsync("EmptyList");
        var response = await _client.GetAsync($"/api/projects/{id}/files");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
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
}
