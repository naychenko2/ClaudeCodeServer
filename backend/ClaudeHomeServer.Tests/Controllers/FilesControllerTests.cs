using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class FilesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public FilesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
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

    // ─── W4-5B2: GET /api/projects/{id}/files/content?featureId= — чтение из дерева эпика ───

    // Реальный git-worktree проекта (git init + worktree add -b) и эпик фичи с этим деревом
    // (тот же приём, что merge-тесты TasksControllerTests). Возвращает (epicId, worktree,
    // projectId, mainRoot).
    private async Task<(string EpicId, string Worktree, string ProjectId, string MainRoot)>
        CreateEpicWithVerifiedWorktreeAsync(string tag)
    {
        var mainRoot = Path.Combine(_tempDir, "feat_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "FeatProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var worktree = Path.Combine(_tempDir, "feat_wt_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(worktree);
        var branch = "feat/" + tag + "-" + Guid.NewGuid().ToString("N")[..6];
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);
        await git.WorktreeAddAsync(null, mainRoot, worktree, branch);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = "epic " + tag, role = "feature",
            worktreePath = worktree, worktreeBranch = branch,
        });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (epicId, worktree, projectId, mainRoot);
    }

    // Файл с одинаковым относительным путём есть и в дереве эпика, и в main: с featureId —
    // читается дерево эпика (проверенный worktree), без него — main (прежнее поведение).
    [Fact]
    public async Task GetContent_FeatureId_ЧитаетИзПроверенногоДереваЭпика()
    {
        var (epicId, worktree, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("read");
        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from feature tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var withFeature = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}");
        withFeature.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyFeature = JsonSerializer.Deserialize<JsonElement>(await withFeature.Content.ReadAsStringAsync());
        bodyFeature.GetProperty("content").GetString().Should().Be("from feature tree", "featureId — читает проверенное дерево эпика");

        var withoutFeature = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md");
        withoutFeature.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyMain = JsonSerializer.Deserialize<JsonElement>(await withoutFeature.Content.ReadAsStringAsync());
        bodyMain.GetProperty("content").GetString().Should().Be("from main tree", "без параметра — корень проекта, как раньше");
    }

    // Путь эпика есть на диске, но НЕ числится в `git worktree list` проекта (чужая папка) —
    // отказ 404, без тихого фолбэка на main (пользователь смотрит не то).
    // Мутация: в GitService.VerifiedFeatureTreeAsync заменить `list.Any(...)` на unconditional
    // return path — этот тест краснеет (чужая папка принялась бы как дерево эпика).
    [Fact]
    public async Task GetContent_FeatureId_ПутьНеВWorktreeList_404()
    {
        var (epicId, _, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("alien");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");
        var alien = Path.Combine(_tempDir, "feat_alien_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(alien);
        File.WriteAllText(Path.Combine(alien, "draft.md"), "from alien dir");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.WorktreePath = alien;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "непроверенный путь — 404, не фолбэк на main");
    }

    // Эпик принадлежит другому проекту — для этого проекта фичи нет: 404.
    [Fact]
    public async Task GetContent_FeatureId_ЭпикДругогоПроекта_404()
    {
        var (epicId, _, _, _) = await CreateEpicWithVerifiedWorktreeAsync("otherproj");
        var (otherId, _) = await SetupProjectWithFileAsync("draft.md", "from main tree");

        var response = await _client.GetAsync($"/api/projects/{otherId}/files/content?path=draft.md&featureId={epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Запись через этот маршрут невозможна: PUT featureId не принимает — пишет в корень
    // проекта, дерево эпика остаётся нетронутым.
    [Fact]
    public async Task SaveContent_FeatureId_ИгнорируетПараметр_ПишетВMain()
    {
        var (epicId, worktree, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("write");
        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from feature tree");

        var put = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}",
            new { content = "written to main" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        File.ReadAllText(Path.Combine(mainRoot, "draft.md")).Should().Be("written to main");
        File.ReadAllText(Path.Combine(worktree, "draft.md")).Should().Be("from feature tree", "запись мимо дерева: featureId игнорируется");
    }

    // ─── W4-LINK-B (п.14): GET /api/projects/{id}/files/content?sessionId= — чтение из дерева задачи, к которой привязан чат ───

    // Тот же приём, что CreateEpicWithVerifiedWorktreeAsync: реальный git-worktree проекта,
    // задача с этим деревом и чат, привязанный к задаче (Session.TaskId). Возвращает
    // (sessionId, worktree, projectId, mainRoot).
    private async Task<(string SessionId, string Worktree, string ProjectId, string MainRoot)>
        CreateChatWithTaskWorktreeAsync(string tag)
    {
        var mainRoot = Path.Combine(_tempDir, "link_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "LinkProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var worktree = Path.Combine(_tempDir, "link_wt_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(worktree);
        var branch = "feat/" + tag + "-" + Guid.NewGuid().ToString("N")[..6];
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);
        await git.WorktreeAddAsync(null, mainRoot, worktree, branch);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = "task " + tag, role = "feature",
            worktreePath = worktree, worktreeBranch = branch,
        });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var taskId = JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.Auto, name: "chat " + tag, taskId: taskId);
        return (session.Id, worktree, projectId, mainRoot);
    }

    // Файл с одинаковым относительным путём есть и в дереве задачи, и в main: с sessionId —
    // читается дерево задачи (тот же предикат, что featureId); без параметра — main (прежнее
    // поведение).
    [Fact]
    public async Task GetContent_SessionId_ЧитаетИзДереваЗадачи()
    {
        var (sessionId, worktree, projectId, mainRoot) = await CreateChatWithTaskWorktreeAsync("read");
        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from task tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var withChat = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={sessionId}");
        withChat.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyChat = JsonSerializer.Deserialize<JsonElement>(await withChat.Content.ReadAsStringAsync());
        bodyChat.GetProperty("content").GetString().Should().Be("from task tree", "sessionId — читает проверенное дерево задачи чата");

        var withoutChat = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md");
        withoutChat.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyMain = JsonSerializer.Deserialize<JsonElement>(await withoutChat.Content.ReadAsStringAsync());
        bodyMain.GetProperty("content").GetString().Should().Be("from main tree", "без параметра — корень проекта, как раньше");
    }

    // Путь задачи есть на диске, но НЕ числится в `git worktree list` проекта (чужое дерево) —
    // отказ 404, без тихого фолбэка на main. Мутация: в FilesController.TaskTreeRootForAsync
    // заменить `return await git.VerifiedFeatureTreeAsync(...)` на `return task.WorktreePath;` —
    // этот тест краснеет (чужая папка принялась бы как дерево задачи).
    [Fact]
    public async Task GetContent_SessionId_ЗадачаНеВWorktreeList_404()
    {
        var (sessionId, _, projectId, mainRoot) = await CreateChatWithTaskWorktreeAsync("alien");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");
        var alien = Path.Combine(_tempDir, "link_alien_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(alien);
        File.WriteAllText(Path.Combine(alien, "draft.md"), "from alien dir");
        var taskId = _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId!;
        _factory.Services.GetRequiredService<TaskManager>().GetById(taskId)!.WorktreePath = alien;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "непроверенное дерево — 404, не фолбэк на main");
    }

    // Чат без задачи — дерева задачи нет, его дерево корень проекта: читается main, как раньше.
    [Fact]
    public async Task GetContent_SessionId_ЧатБезЗадачи_ЧитаетMain()
    {
        var (id, dir) = await SetupProjectWithFileAsync("draft.md", "from main tree");
        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(id, ClaudeMode.Auto, name: "chat without task");

        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree");
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
}
