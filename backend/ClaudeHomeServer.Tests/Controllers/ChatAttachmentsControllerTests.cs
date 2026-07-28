using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Единый путь загрузки вложений сообщения: POST /api/chats/{id}/files/upload работает
// и для чата вне проекта, и для проектной сессии (рабочая папка — корень проекта).
public class ChatAttachmentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public ChatAttachmentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "chat_attach_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<(string sessionId, string projectDir)> CreateProjectSessionAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "AttachProject", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var project = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync());
        var projectId = project.GetProperty("id").GetString()!;

        var sessionResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        sessionResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = JsonSerializer.Deserialize<JsonElement>(await sessionResp.Content.ReadAsStringAsync());
        return (session.GetProperty("id").GetString()!, dir);
    }

    private static MultipartFormDataContent UploadForm(string fileName, string content)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName);
        return form;
    }

    [Fact]
    public async Task Upload_ПроектныйЧат_КладётФайлВПроектСОригинальнымИменем()
    {
        var (sessionId, projectDir) = await CreateProjectSessionAsync();

        var response = await _client.PostAsync($"/api/chats/{sessionId}/files/upload",
            UploadForm("ТЗ.pdf", "содержимое"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var rel = body.GetProperty("path").GetString()!;
        rel.Should().StartWith(FileService.AttachmentsDir + "/");
        // Имя файла не переименовывается: на плашке и у Claude — оригинальное
        rel.Should().EndWith("/ТЗ.pdf");
        File.Exists(Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
    }

    [Fact]
    public async Task Upload_ЧужойЧат_404()
    {
        var (sessionId, _) = await CreateProjectSessionAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.PostAsync($"/api/chats/{sessionId}/files/upload",
            UploadForm("secret.txt", "x"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_ПроектСоСвоимGitignore_ПишетИгнорВInfoExclude()
    {
        var (sessionId, projectDir) = await CreateProjectSessionAsync();
        // Реальный кодовый проект: git-репозиторий со своим .gitignore (дефолтный ему не пишется).
        // Настоящий git CLI не нужен — правило кладётся по структуре папки .git
        Directory.CreateDirectory(Path.Combine(projectDir, ".git"));
        var gitignore = Path.Combine(projectDir, ".gitignore");
        await File.WriteAllTextAsync(gitignore, "node_modules/\n");

        (await _client.PostAsync($"/api/chats/{sessionId}/files/upload", UploadForm("shot.png", "png")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var exclude = await File.ReadAllTextAsync(Path.Combine(projectDir, ".git", "info", "exclude"));
        exclude.Should().Contain(FileService.AttachmentsDir + "/");
        (await File.ReadAllTextAsync(gitignore)).Should().Be("node_modules/\n");
    }

    [Fact]
    public async Task Upload_ПапкаВложений_НеПопадаетВДеревоФайловПроекта()
    {
        var (sessionId, projectDir) = await CreateProjectSessionAsync();

        (await _client.PostAsync($"/api/chats/{sessionId}/files/upload", UploadForm("shot.png", "png")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var files = new FileService();
        files.List(projectDir, showHidden: true).Should()
            .NotContain(e => e.Name == FileService.AttachmentsDir);
    }
}
