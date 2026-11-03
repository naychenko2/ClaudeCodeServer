using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

[Trait("Category", "Host")]
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

    // Шаг B2a: ?featureId= остаётся СТРОГИМ — поиск по деревьям (смена решения W4-LINK-B)
    // на него не распространяется. Файл есть в другом worktree проекта (не в дереве эпика и
    // не в main): featureId → 404. Мутация: пустить featureId-ветку через ProjectTreeLocator
    // — тест краснеет (файл прочитался бы из чужого дерева).
    [Fact]
    public async Task GetContent_FeatureId_НеИщетПоЧужимДеревьям_404()
    {
        var (epicId, _, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("strict");
        var git = _factory.Services.GetRequiredService<GitService>();
        var other = Path.Combine(_tempDir, "strict_other_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        await git.WorktreeAddAsync(null, mainRoot, other, "feat/other-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(other, "draft.md"), "from other tree");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "featureId не ищет по чужим деревьям");
    }

    // Дополнение к контракту (ревью F1a, п.7): при обоих параметрах сразу featureId
    // выигрывает, sessionId игнорируется. Файл есть и в дереве эпика, и в своём дереве чата:
    // с featureId+sessionId читается дерево эпика. Мутация: поставить ветку sessionId
    // раньше featureId в GetContent — тест краснеет (прочиталось дерево чата).
    [Fact]
    public async Task GetContent_FeatureIdИSessionId_Одновременно_FeatureIdВыигрывает()
    {
        var (epicId, epicTree, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("both");
        var git = _factory.Services.GetRequiredService<GitService>();
        var chatTree = Path.Combine(_tempDir, "both_chat_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(chatTree);
        await git.WorktreeAddAsync(null, mainRoot, chatTree, "feat/both-" + Guid.NewGuid().ToString("N")[..6]);

        File.WriteAllText(Path.Combine(epicTree, "draft.md"), "from feature tree");
        File.WriteAllText(Path.Combine(chatTree, "draft.md"), "from chat tree");

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat with own tree");
        (await sessions.AttachWorktreeAsync(session.Id, chatTree)).Should().BeTrue("дерево чата на диске и в worktree list");

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from feature tree", "featureId явный — он и решает; sessionId игнорируется");
    }

    // Хвост резолва W4-LINK-B3: асимметрия решена — правило «дерево == корень» единое. Дерево
    // эпика — корень проекта: эпик работает в корне, читается корень (как у дерева чата), а не
    // 404, как было до унификации. Мутация: убрать обход корня в FilesController.TreeRootForAsync
    // — тест краснеет (предикат VerifiedFeatureTreeAsync main деревом фичи не считает).
    [Fact]
    public async Task GetContent_FeatureId_ЭпикНаКорне_ЧитаетRoot()
    {
        var (epicId, _, projectId, mainRoot) = await CreateEpicWithVerifiedWorktreeAsync("rootepic");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.WorktreePath = mainRoot;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "дерево эпика — корень: читается корень, не 404");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree");
    }

    // Хвост резолва W4-LINK-B3: самый частый случай — эпик без заявленного дерева (пустой
    // WorktreePath — норма большинства задач). Правило «дерева нет → корень» закреплено тестом:
    // featureId читает корень проекта, а не 404. Мутация: убрать в FilesController.TreeRootForAsync
    // обход `string.IsNullOrWhiteSpace(worktreePath)` — тест краснеет (пустой путь больше не
    // попадает в корень: NormalizePath бросает, запрос роняется в 500).
    [Fact]
    public async Task GetContent_FeatureId_ЭпикБезДерева_ЧитаетRoot()
    {
        var (projectId, _) = await SetupProjectWithFileAsync("draft.md", "from main tree");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = "epic без дерева", role = "feature",
        });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&featureId={epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "дерева нет: читается корень, не 404");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree");
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
    // (sessionId, worktree, branch, projectId, mainRoot) — ветка нужна для проверки
    // branch/isAnchor в ответе content (шаг B2a).
    private async Task<(string SessionId, string Worktree, string Branch, string ProjectId, string MainRoot)>
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
        return (session.Id, worktree, branch, projectId, mainRoot);
    }

    // Файл с одинаковым относительным путём есть и в дереве задачи, и в main: с sessionId —
    // читается дерево задачи (тот же предикат, что featureId); без параметра — main (прежнее
    // поведение).
    [Fact]
    public async Task GetContent_SessionId_ЧитаетИзДереваЗадачи()
    {
        var (sessionId, worktree, branch, projectId, mainRoot) = await CreateChatWithTaskWorktreeAsync("read");
        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from task tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var withChat = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={sessionId}");
        withChat.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyChat = JsonSerializer.Deserialize<JsonElement>(await withChat.Content.ReadAsStringAsync());
        bodyChat.GetProperty("content").GetString().Should().Be("from task tree", "sessionId — читает проверенное дерево задачи чата");
        // Шаг B2a: чтение из заявленного дерева — isAnchor=true, чип «из дерева» не нужен;
        // ветка дерева несётся в ответе.
        bodyChat.GetProperty("isAnchor").GetBoolean().Should().BeTrue("прочитано из заявленного дерева");
        bodyChat.GetProperty("branch").GetString().Should().Be(branch, "ответ несёт ветку прочитанного дерева");

        var withoutChat = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md");
        withoutChat.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyMain = JsonSerializer.Deserialize<JsonElement>(await withoutChat.Content.ReadAsStringAsync());
        bodyMain.GetProperty("content").GetString().Should().Be("from main tree", "без параметра — корень проекта, как раньше");
    }

    // Смена решения W4-LINK-B (шаг B2a): путь задачи заявлен, но НЕ числится в
    // `git worktree list` проекта (чужое дерево, например worktree снесли после мержа) —
    // не 404, а поиск: читается main, ответ несёт isAnchor=false (чип «из дерева»).
    // Мутация: вернуть строгое правило (непроверенное дерево → 404) — тест краснеет.
    [Fact]
    public async Task GetContent_SessionId_ЗадачаНеВWorktreeList_ЧитаетMain_IsAnchorFalse()
    {
        var (sessionId, _, _, projectId, mainRoot) = await CreateChatWithTaskWorktreeAsync("alien");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");
        var alien = Path.Combine(_tempDir, "link_alien_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(alien);
        File.WriteAllText(Path.Combine(alien, "draft.md"), "from alien dir");
        var taskId = _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId!;
        _factory.Services.GetRequiredService<TaskManager>().GetById(taskId)!.WorktreePath = alien;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "заявленного дерева нет — поиск по main, а не 404");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree", "фолбэк есть, но не тихий");
        body.GetProperty("isAnchor").GetBoolean().Should().BeFalse("прочитано не из заявленного дерева");
        body.TryGetProperty("branch", out _).Should().BeFalse("у main ветки нет — поля в ответе нет");
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

    // ─── W4-LINK-B (рев.1): «дерева нет» ≠ «дерево не проверено»; своё дерево чата — первично ───

    // Задача без дерева (WorktreePath пуст — норма): её исполнитель работает в корне проекта,
    // поэтому чат с такой задачей читает main, а не 404. Мутация: убрать в FilesController.TreeRootForAsync
    // строку `if (string.IsNullOrWhiteSpace(worktreePath)) return project.RootPath;` —
    // этот тест краснеет (пустое дерево ушло бы в предикат и дал 404).
    [Fact]
    public async Task GetContent_SessionId_ЗадачаБезДерева_ЧитаетMain()
    {
        var (id, _) = await SetupProjectWithFileAsync("draft.md", "from main tree");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{id}/tasks", new { title = "task without tree" });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var taskId = JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(id, ClaudeMode.Auto, name: "chat with task without tree", taskId: taskId);

        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "пустой WorktreePath — не отказ: дерево задачи — main");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree");
    }

    // Своё дерево чата (тумблер «отдельное дерево», AttachWorktreeAsync — та же проверка
    // существования и числения в worktree list): с sessionId читается оно, а не main. Мутация:
    // убрать ветку `if (!string.IsNullOrEmpty(session.WorktreePath))` в ChatTreeRootForAsync —
    // тест краснеет (прочитался main вместо дерева чата).
    [Fact]
    public async Task GetContent_SessionId_СвоёДеревоЧата_Читается()
    {
        var mainRoot = Path.Combine(_tempDir, "ownwt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "OwnTreeProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var worktree = Path.Combine(_tempDir, "ownwt_wt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(worktree);
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);
        await git.WorktreeAddAsync(null, mainRoot, worktree, "feat/own-" + Guid.NewGuid().ToString("N")[..6]);

        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from chat tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat with own tree");
        (await sessions.AttachWorktreeAsync(session.Id, worktree)).Should().BeTrue("дерево на диске и в worktree list");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from chat tree", "своё дерево чата — первичный источник чтения");
    }

    // Смена решения W4-LINK-B (шаг B2a, переписан кейс cb7a1d3): своё дерево чата заявлено
    // (WorktreePath присян напрямую, минуя проверку AttachWorktreeAsync), но его нет в
    // `git worktree list` проекта (чужая папка — снесли после мержа): НЕ 404 — поиск
    // читает main, ответ несёт isAnchor=false. Мутация: вернуть строгое правило
    // (непроверённый анкер → 404) — этот тест краснеет.
    [Fact]
    public async Task GetContent_SessionId_СвоёДеревоЧатаНеВWorktreeList_ЧитаетMain_IsAnchorFalse()
    {
        var mainRoot = Path.Combine(_tempDir, "alienchat_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "AlienTreeProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // Реальный репозиторий: без него worktree list пуст — закрепляем предикат, а не отсутствие git.
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);

        var alien = Path.Combine(_tempDir, "alienchat_wt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(alien);
        File.WriteAllText(Path.Combine(alien, "draft.md"), "from alien tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.Auto, name: "chat with unverified tree");
        session.WorktreePath = alien;

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "заявленного дерева нет — поиск по main, а не 404");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree", "фолбэк есть, но с меткой");
        body.GetProperty("isAnchor").GetBoolean().Should().BeFalse("прочитано не из заявленного дерева");
        body.TryGetProperty("branch", out _).Should().BeFalse("у main ветки нет — поля в ответе нет");
    }

    // Хвост W4-LINK-B: приоритет двух деревьев. У чата своё дерево И задача, к которой он
    // привязан, с другим деревом — читается дерево чата (порядок веток в GetContent:
    // WorktreePath раньше TaskId). Мутация: поменять порядок веток (сначала проверять
    // TaskId) — тест краснеет (прочиталось дерево задачи).
    [Fact]
    public async Task GetContent_SessionId_СвоёДеревоЧата_ВажнееДереваЗадачи()
    {
        var mainRoot = Path.Combine(_tempDir, "prio_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "PriorityProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);

        var chatTree = Path.Combine(_tempDir, "prio_chat_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(chatTree);
        await git.WorktreeAddAsync(null, mainRoot, chatTree, "feat/chat-" + Guid.NewGuid().ToString("N")[..6]);
        var taskTree = Path.Combine(_tempDir, "prio_task_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(taskTree);
        var taskBranch = "feat/task-" + Guid.NewGuid().ToString("N")[..6];
        await git.WorktreeAddAsync(null, mainRoot, taskTree, taskBranch);

        File.WriteAllText(Path.Combine(chatTree, "draft.md"), "from chat tree");
        File.WriteAllText(Path.Combine(taskTree, "draft.md"), "from task tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = "task with its own tree", role = "feature",
            worktreePath = taskTree, worktreeBranch = taskBranch,
        });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var taskId = JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat with own tree and a task", taskId: taskId);
        (await sessions.AttachWorktreeAsync(session.Id, chatTree)).Should().BeTrue("дерево чата на диске и в worktree list");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from chat tree", "своё дерево чата важнее дерева задачи");
    }

    // Хвост W4-LINK-B: чат привязан к корню проекта. Главное дерево числится в
    // `git worktree list`, поэтому AttachWorktreeAsync его принимает, но предикат
    // VerifiedFeatureTreeAsync main не считает деревом фичи — без обхода был бы 404.
    // Мутация: убрать обход корня в FilesController.TreeRootForAsync — тест краснеет (404
    // вместо чтения main).
    [Fact]
    public async Task GetContent_SessionId_ДеревоЧатаРавноКорню_ЧитаетRoot()
    {
        var mainRoot = Path.Combine(_tempDir, "rootwt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "RootTreeProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat on the root");
        (await sessions.AttachWorktreeAsync(session.Id, mainRoot)).Should().BeTrue("главное дерево числится в worktree list");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=draft.md&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "корень как дерево чата — не отказ: читается корень проекта");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("from main tree");
    }

    // ─── Шаг B2a: GET /api/projects/{id}/files/stream?sessionId= — тот же резолв дерева ───

    // Чужой чат (привязан к другому проекту): stream отказывает 404 — не читает main и не
    // даёт чужой контекст. Мутация: убрать проверку GetOwnedSession в Stream (игнорировать
    // sessionId) — тест краснеет (прочитался бы main, 200).
    [Fact]
    public async Task Stream_SessionId_ЧужойЧат_404()
    {
        var (projectId, _) = await SetupProjectWithFileAsync("clip.mp4", "main bytes");
        var otherId = await CreateProjectAsync("OtherStreamProject");
        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(otherId, ClaudeMode.Auto, name: "chat in another project");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/stream?path=clip.mp4&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "чат другого проекта — отказ, не фолбэк на main");
    }

    // Своё дерево чата: stream отдаёт файл из него, а не одноимённый файл из main — резолв
    // тот же, что у content. Мутация: убрать ветку sessionId в Stream (читать корень) —
    // тест краснеет (вернулся бы контент main).
    [Fact]
    public async Task Stream_SessionId_ЧитаетДеревоЧата()
    {
        var mainRoot = Path.Combine(_tempDir, "streamwt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "StreamTreeProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);
        var worktree = Path.Combine(_tempDir, "streamwt_wt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(worktree);
        await git.WorktreeAddAsync(null, mainRoot, worktree, "feat/stream-" + Guid.NewGuid().ToString("N")[..6]);

        File.WriteAllText(Path.Combine(worktree, "clip.mp4"), "from chat tree");
        File.WriteAllText(Path.Combine(mainRoot, "clip.mp4"), "from main tree");

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat with stream tree");
        (await sessions.AttachWorktreeAsync(session.Id, worktree)).Should().BeTrue("дерево на диске и в worktree list");

        var response = await _client.GetAsync($"/api/projects/{projectId}/files/stream?path=clip.mp4&sessionId={session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("from chat tree", "stream — тот же резолв дерева, что content");
    }

    // ─── Шаг B2b: POST /api/projects/{id}/files/locate — пакетный резолв с alternatives ───

    // Лимит пакета — часть контракта (фронт батчит ≤50): 51 путь → 400, а не тихая обрезка.
    // Мутация: убрать проверку в Locate — тест краснеет (запрос обработался бы 200).
    [Fact]
    public async Task Locate_51Пути_400()
    {
        var id = await CreateProjectAsync("LocateLimit");
        var paths = Enumerable.Range(0, 51).Select(i => $"f{i}.md").ToList();

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/files/locate", new { paths });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "больше 50 путей — отказ, не обрезка");
    }

    // Чужой (другого проекта) и несуществующий sessionId → 404, а не молчаливый фолбэк без анкера.
    // Мутация: снять GetOwnedSession в Locate (anchor = null) — тест краснеет (200).
    [Fact]
    public async Task Locate_SessionId_ЧужойИлиНесуществующий_404()
    {
        var projectId = await CreateProjectAsync("LocateForeignSession");
        var otherId = await CreateProjectAsync("LocateOtherProject");
        var foreign = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(otherId, ClaudeMode.Auto, name: "chat in another project");

        var r1 = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/locate",
            new { paths = new[] { "a.md" }, sessionId = foreign.Id });
        r1.StatusCode.Should().Be(HttpStatusCode.NotFound, "чат другого проекта");

        var r2 = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/locate",
            new { paths = new[] { "a.md" }, sessionId = "no-such-session" });
        r2.StatusCode.Should().Be(HttpStatusCode.NotFound, "несуществующий чат");
    }

    // Путь есть в двух чужих деревьях (не в main), без sessionId: hit — свежайший из них,
    // isAnchor=false, branch — его ветка, alternatives — число других совпадений (2−1=1).
    // Ненайденный и абсолютный пути в ответе отсутствуют. Мутация: в ProjectTreeLocator.
    // LocateManyAsync заменить `total - 1` на `0` (возвращать только первое совпадение) —
    // assertions про alternatives краснеют.
    [Fact]
    public async Task Locate_ЧужоеДерево_МеткаВетки_Alternatives()
    {
        var mainRoot = Path.Combine(_tempDir, "locwt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mainRoot);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "LocateProject", rootPath = mainRoot });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, mainRoot);

        var treeA = Path.Combine(_tempDir, "locwt_a_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(treeA);
        var branchA = "feat/loc-a-" + Guid.NewGuid().ToString("N")[..6];
        await git.WorktreeAddAsync(null, mainRoot, treeA, branchA);
        var treeB = Path.Combine(_tempDir, "locwt_b_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(treeB);
        var branchB = "feat/loc-b-" + Guid.NewGuid().ToString("N")[..6];
        await git.WorktreeAddAsync(null, mainRoot, treeB, branchB);

        File.WriteAllText(Path.Combine(treeA, "report.md"), "from tree A");
        File.WriteAllText(Path.Combine(treeB, "report.md"), "from tree B");
        // Детерминированный mtime: B свежнее A — hit обязан быть из B
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(treeA, "report.md"), now - TimeSpan.FromSeconds(10));
        File.SetLastWriteTimeUtc(Path.Combine(treeB, "report.md"), now);

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/locate",
            new { paths = new[] { "report.md", "missing.md", "/etc/passwd" } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(1, "ненайденный и абсолютный пути — не в ответе");
        var hit = body[0];
        hit.GetProperty("path").GetString().Should().Be("report.md", "ключ ответа — ровно присланный путь");
        hit.GetProperty("branch").GetString().Should().Be(branchB, "hit — свежайшее из чужих деревьев, с его веткой");
        hit.GetProperty("isAnchor").GetBoolean().Should().BeFalse("анкера нет — hit не из заявленного дерева");
        hit.GetProperty("alternatives").GetInt32().Should().Be(1, "одно другое совпадение");
    }

    // Анкер побеждает остальные совпадения: файл есть в дереве задачи чата, в main и в третьем
    // дереве — hit из дерева чата (isAnchor=true, branch его ветка), alternatives = 3−1 = 2.
    // Мутация: убрать приоритет анкера в LocateManyAsync (считать только main/остальные) —
    // isAnchor и branch краснеют.
    [Fact]
    public async Task Locate_SessionId_АнкерПобеждает_AlternativesПоОстальным()
    {
        var (sessionId, worktree, branch, projectId, mainRoot) = await CreateChatWithTaskWorktreeAsync("locanchor");
        var git = _factory.Services.GetRequiredService<GitService>();
        var other = Path.Combine(_tempDir, "locanchor_other_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        await git.WorktreeAddAsync(null, mainRoot, other, "feat/loc-other-" + Guid.NewGuid().ToString("N")[..6]);

        File.WriteAllText(Path.Combine(worktree, "draft.md"), "from task tree");
        File.WriteAllText(Path.Combine(mainRoot, "draft.md"), "from main tree");
        File.WriteAllText(Path.Combine(other, "draft.md"), "from other tree");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/locate",
            new { paths = new[] { "draft.md" }, sessionId });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(1);
        var hit = body[0];
        hit.GetProperty("isAnchor").GetBoolean().Should().BeTrue("заявленное дерево чата — приоритет");
        hit.GetProperty("branch").GetString().Should().Be(branch, "ответ несёт ветку анкера");
        hit.GetProperty("alternatives").GetInt32().Should().Be(2, "ещё два совпадения — main и чужое дерево");
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

    // ---------- S2c-1 · ETag и 409 при сохранении (К8) ----------

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // GET content → ETag-заголовок: (сырое значение с кавычками — как его вернёт клиент,
    // хеш без кавычек). К8: `ETag: "<sha256>"`.
    private async Task<(string ETagRaw, string Sha256)> GetContentETagAsync(string projectId, string path)
    {
        var response = await _client.GetAsync($"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var values = response.Headers.GetValues("ETag").ToList();
        values.Should().HaveCount(1, "ETag по К8 — один сильный заголовок");
        var raw = values[0];
        raw.StartsWith('"').Should().BeTrue("сильный ETag отдаётся в кавычках");
        return (raw, raw.Trim('"'));
    }

    private async Task<HttpResponseMessage> PutWithIfMatchAsync(
        string projectId, string path, string content, string ifMatchRaw)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatchRaw);
        request.Content = new StringContent(JsonSerializer.Serialize(new { content }), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task GetContent_ReturnsETagSha256OfContent()
    {
        var (id, _) = await SetupProjectWithFileAsync("etag.txt", "hello content");
        var (_, sha) = await GetContentETagAsync(id, "etag.txt");
        // Хеш байтов файла на диске, а не «вида» текста: LOWERCASE hex SHA-256
        sha.Should().Be(Sha256Hex("hello content"));
    }

    [Fact]
    public async Task ETag_ChangesAfterSave()
    {
        var (id, _) = await SetupProjectWithFileAsync("round.txt", "v1 content");
        var (etag1, _) = await GetContentETagAsync(id, "round.txt");

        var put = await PutWithIfMatchAsync(id, "round.txt", "v2 content", etag1);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var (etag2, sha2) = await GetContentETagAsync(id, "round.txt");
        etag2.Should().NotBe(etag1, "после записи хеш обязан смениться");
        sha2.Should().Be(Sha256Hex("v2 content"));
    }

    [Fact]
    public async Task SaveContent_IfMatchMatches_WritesFile()
    {
        var (id, dir) = await SetupProjectWithFileAsync("match.txt", "original");
        var (etag, _) = await GetContentETagAsync(id, "match.txt");

        var put = await PutWithIfMatchAsync(id, "match.txt", "new content", etag);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(dir, "match.txt")).Should().Be("new content");
    }

    // Названный тест мутации (S2c-1): убрать сравнение хеша в SaveContent — этот тест красный.
    [Fact]
    public async Task SaveContent_IfMatchMismatch_Returns409FileChanged_FileUnchanged()
    {
        var (id, dir) = await SetupProjectWithFileAsync("stale.txt", "stale content");
        var put = await PutWithIfMatchAsync(id, "stale.txt", "my edit", "\"0000000000000000000000000000000000000000000000000000000000000000\"");
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        // Тело по К8 — актуальное состояние: хеш и содержимое на диске, а не присланное
        body.GetProperty("sha256").GetString().Should().Be(Sha256Hex("stale content"));
        body.GetProperty("content").GetString().Should().Be("stale content");
        body.GetProperty("changedAt").ValueKind.Should().NotBe(JsonValueKind.Null, "mtime — серверное состояние");
        // Автор — из серверного состояния (буфер коммиттера, S3-3); здесь его нет, и из тела
        // запроса он не принимается
        body.GetProperty("author").ValueKind.Should().Be(JsonValueKind.Null);

        File.ReadAllText(Path.Combine(dir, "stale.txt")).Should().Be("stale content", "отказ не трогает файл");
    }

    [Fact]
    public async Task SaveContent_Race_TwoParallelWithSameIfMatch_ExactlyOneWins()
    {
        var (id, dir) = await SetupProjectWithFileAsync("race.txt", "base");
        var (etag, _) = await GetContentETagAsync(id, "race.txt");
        var racePath = Path.Combine(dir, "race.txt");

        // Гонка без таймеров: обе записи стоят на одном gate и стартуют одновременно
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = Task.Run(async () => { await gate.Task; return await PutWithIfMatchAsync(id, "race.txt", "win A", etag); });
        var t2 = Task.Run(async() => { await gate.Task; return await PutWithIfMatchAsync(id, "race.txt", "win B", etag); });
        gate.SetResult();
        var responses = await Task.WhenAll(t1, t2);

        var statuses = responses.Select(r => r.StatusCode).ToList();
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1, "под замком проходит ровно одна запись");
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(1);

        var winner = responses[0].StatusCode == HttpStatusCode.OK ? "win A" : "win B";
        var loser = responses[0].StatusCode == HttpStatusCode.OK ? responses[1] : responses[0];
        File.ReadAllText(racePath).Should().Be(winner, "файл содержит запись победителя");
        // Проигравший получает актуальное состояние — уже запись победителя
        var body = JsonSerializer.Deserialize<JsonElement>(await loser.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        body.GetProperty("content").GetString().Should().Be(winner);
        body.GetProperty("sha256").GetString().Should().Be(Sha256Hex(winner));
    }

    // П.1 доработки S2c-1: атомарная замена (WriteAtomic) несёт права не цели, а временного
    // файла: без переноса 0600 с битом исполнения становился 0644. Явный Skip на Windows —
    // unix-битов там нет, тест не должен молчать на другой ОС.
    [SkippableFact]
    public async Task SaveContent_IfMatch_PreservesUnixModeAndExecBit()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix-права (0600 + бит исполнения) — вне Windows");
        var (id, dir) = await SetupProjectWithFileAsync("keep_mode.sh", "#!/bin/sh\necho old");
        var abs = Path.Combine(dir, "keep_mode.sh");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; // 0700
        File.SetUnixFileMode(abs, mode);

        var (etag, _) = await GetContentETagAsync(id, "keep_mode.sh");
        var put = await PutWithIfMatchAsync(id, "keep_mode.sh", "#!/bin/sh\necho new", etag);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        File.ReadAllText(abs).Should().Be("#!/bin/sh\necho new");
        File.GetUnixFileMode(abs).Should().Be(mode,
            "подмена через временный файл не меняет прав исходного файла");
    }

    // П.3 доработки S2c-1: видео/аудио/документы раньше не читались вовсе — полный хеш на
    // каждый GET стоил чтения всего файла. Тег для них не отдаётся, ответ не ломается.
    [Fact]
    public async Task GetContent_VideoFile_HasNoETag_AndResponseIsOk()
    {
        var id = await SetupProjectWithBinaryAsync("clip.mp4", new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 });
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=clip.mp4");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("isVideo").GetBoolean().Should().BeTrue("видео отдаётся как раньше");
        response.Headers.Contains("ETag").Should().BeFalse(
            "нередактируемый файл не перечитывается целиком ради тега");
    }

    // П.3: текст выше потолка (MaxEtagTextBytes) — без тега; PUT таких файлов остаётся
    // без If-Match, как до S2c-1.
    [Fact]
    public async Task GetContent_TextAboveEtagCap_HasNoETag_AndContentIsFull()
    {
        var big = new string('a', (int)FilesController.MaxEtagTextBytes + 1);
        var (id, _) = await SetupProjectWithFileAsync("big.txt", big);
        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=big.txt");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString()!.Length.Should().Be((int)FilesController.MaxEtagTextBytes + 1);
        response.Headers.Contains("ETag").Should().BeFalse(
            "файл сверх потолка не хешируется целиком на каждый GET");
    }

    // П.4 доработки S2c-1: хеш — по каноническим байтам (тем же, что отдаёт GET), а не по
    // сырым байтам диска. Файл с BOM: content из GET без BOM, ETag = его хеш, и PUT с этим
    // тегом проходит (раньше unchanged-файл отвечал 409 на собственный же тег).
    [Fact]
    public async Task GetContent_FileWithBom_ETagMatchesReturnedContent_PutWithThatETagPasses()
    {
        const string text = "содержимое с BOM";
        var (id, dir) = await SetupProjectWithFileAsync("bom.txt", text);
        var abs = Path.Combine(dir, "bom.txt");
        File.WriteAllText(abs, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var response = await _client.GetAsync($"/api/projects/{id}/files/content?path=bom.txt");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var content = body.GetProperty("content").GetString()!;
        content.Should().Be(text, "GET отдаёт декодированный текст без BOM");

        var etagRaw = response.Headers.GetValues("ETag").Single();
        etagRaw.Trim('"').Should().Be(Sha256Hex(text),
            "тег — по тем же байтам, что отдаёт GET, а не по сырым байтам с BOM");

        var put = await PutWithIfMatchAsync(id, "bom.txt", "правка по ETag из GET", etagRaw);
        put.StatusCode.Should().Be(HttpStatusCode.OK, "unchanged-файл принимает собственный тег");

        var after = File.ReadAllBytes(abs);
        (after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF)
            .Should().BeFalse("запись API пишет UTF-8 без BOM");
        var (_, sha2) = await GetContentETagAsync(id, "bom.txt");
        sha2.Should().Be(Sha256Hex("правка по ETag из GET"), "после записи тег — хеш нового содержимого");
    }

    // ---------- S2c-1 доживка (дефект найден на S2c-F2a): 200-ответ PUT несёт новый ETag ----------

    // Названный тест мутации (дозивка S2c-1): убрать SetContentETagAfterWrite в If-Match-ветке
    // SaveContent — этот тест красный. ETag ответа PUT = канонический хеш записанного
    // содержимого (файл с BOM) и равен ETag последующего GET.
    [Fact]
    public async Task SaveContent_IfMatch_ResponseETagEqualsSubsequentGet_FileWithBom()
    {
        const string text = "содержимое с BOM";
        var (id, dir) = await SetupProjectWithFileAsync("etag_put_bom.txt", text);
        File.WriteAllText(Path.Combine(dir, "etag_put_bom.txt"), text, new UTF8Encoding(true));

        var (etag1, _) = await GetContentETagAsync(id, "etag_put_bom.txt");
        var put = await PutWithIfMatchAsync(id, "etag_put_bom.txt", "правка по ETag", etag1);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var values = put.Headers.GetValues("ETag").ToList();
        values.Should().HaveCount(1, "ETag по К8 — один сильный заголовок");
        var raw2 = values[0];
        raw2.StartsWith('"').Should().BeTrue("сильный ETag отдаётся в кавычках");
        raw2.Trim('"').Should().Be(Sha256Hex("правка по ETag"),
            "тег ответа — хеш записанного содержимого, той же канонической функцией, что у GET");

        var (etag2, _) = await GetContentETagAsync(id, "etag_put_bom.txt");
        etag2.Should().Be(raw2, "тег ответа PUT равен ETag последующего GET");
    }

    // Конкурентность: цепочка клиента GET→PUT(If-Match=etag1)→etag2 из ответа→PUT(If-Match=etag2)
    // проходит 200 без ложного 409 на собственную правку.
    [Fact]
    public async Task SaveContent_IfMatch_ChainByResponseETag_NoFalse409()
    {
        var (id, _) = await SetupProjectWithFileAsync("chain.txt", "v1");
        var (etag1, _) = await GetContentETagAsync(id, "chain.txt");

        var put1 = await PutWithIfMatchAsync(id, "chain.txt", "v2", etag1);
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag2 = put1.Headers.GetValues("ETag").Single();
        etag2.Trim('"').Should().Be(Sha256Hex("v2"));

        var put2 = await PutWithIfMatchAsync(id, "chain.txt", "v3", etag2);
        put2.StatusCode.Should().Be(HttpStatusCode.OK,
            "следующая сохранка по возвращённому тегу — не 409 на собственную правку");
        var (etag3, sha3) = await GetContentETagAsync(id, "chain.txt");
        sha3.Should().Be(Sha256Hex("v3"));
    }

    // Чужая запись между сохранками: вторая сохранка с etag2 — настоящий конфликт, 409
    // (тег протух не из-за сервера, а из-за чужой правки).
    [Fact]
    public async Task SaveContent_IfMatch_ForeignWriteBetweenSaves_SecondSaveWithOldTag409()
    {
        var (id, _) = await SetupProjectWithFileAsync("interloper.txt", "v1");
        var (etag1, _) = await GetContentETagAsync(id, "interloper.txt");

        var put1 = await PutWithIfMatchAsync(id, "interloper.txt", "v2", etag1);
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag2 = put1.Headers.GetValues("ETag").Single();

        // Чужая запись между сохранками клиента (правка мимо If-Match)
        var foreign = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=interloper.txt", new { content = "чужая правка" });
        foreign.StatusCode.Should().Be(HttpStatusCode.OK);

        var put2 = await PutWithIfMatchAsync(id, "interloper.txt", "v3", etag2);
        put2.StatusCode.Should().Be(HttpStatusCode.Conflict, "тег протух из-за чужой записи — настоящий 409");
        var body = JsonSerializer.Deserialize<JsonElement>(await put2.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        body.GetProperty("content").GetString().Should().Be("чужая правка");
    }

    // Путь без If-Match тоже отдаёт ETag: клиент, начавший с тега, получает тег.
    [Fact]
    public async Task SaveContent_NoIfMatch_ResponseCarriesETagOfWrittenContent()
    {
        var (id, _) = await SetupProjectWithFileAsync("plain_etag.txt", "old");
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=plain_etag.txt", new { content = "новая запись" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = response.Headers.GetValues("ETag").Single();
        raw.Trim('"').Should().Be(Sha256Hex("новая запись"));
        var (etag2, _) = await GetContentETagAsync(id, "plain_etag.txt");
        etag2.Should().Be(raw, "тег ответа PUT равен ETag последующего GET");
    }

    // Не-etaggable: 200-ответ сохранения без заголовка — как GET такого файла (бинарный по
    // расширению остаётся бинарным и после записи).
    [Fact]
    public async Task SaveContent_BinaryFile_ResponseHasNoETag()
    {
        var id = await SetupProjectWithBinaryAsync("clip_put.mp4", new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 });
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=clip_put.mp4", new { content = "текст в mp4" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("ETag").Should().BeFalse(
            "бинарный файл не etaggable — заголовка нет, как у GET");
    }

    // ---------- доработка по ревью 00f9451e: тег ответа — хеш текста запроса, не файла ----------

    // П.1 (major, contract): замок на путь сериализует только SaveContent — писатель мимо замка
    // (revert/upload/офис/агент — реальные точки записи) может вклиниться между нашей записью
    // и ответом. Окно симулировано публичным швом: FileService.WriteFile и WriteAtomic зовут
    // NotifyMutated синхронно внутри записи (FileService.cs:427, FilesController.cs:504), подписчик пишет чужую версию
    // на диск до SetContentETagAfterWrite. Тег ответа — хеш ОТПРАВЛЕННОГО текста (не чужой
    // версии файла), и следующий PUT по нему — настоящий 409, а не тихая перезапись чужой правки.
    [Fact]
    public async Task SaveContent_ResponseETagIsOurText_ForeignWriteInWindow_NextPut409()
    {
        var (id, dir) = await SetupProjectWithFileAsync("window.txt", "v1");
        var filesSvc = _factory.Services.GetRequiredService<FileService>();
        Action<string, string, FileMutationKind, string?> interloper = (root, rel, _, _) =>
        {
            if (rel == "window.txt") File.WriteAllText(Path.Combine(root, rel), "чужая правка");
        };
        filesSvc.OnMutated += interloper;
        string etagRaw;
        try
        {
            var (etag1, _) = await GetContentETagAsync(id, "window.txt");
            var put = await PutWithIfMatchAsync(id, "window.txt", "v2", etag1);
            put.StatusCode.Should().Be(HttpStatusCode.OK);

            var values = put.Headers.GetValues("ETag").ToList();
            values.Should().HaveCount(1, "ETag по К8 — один сильный заголовок");
            etagRaw = values[0];
            etagRaw.Trim('"').Should().Be(Sha256Hex("v2"),
                "тег ответа — хеш отправленного контента, даже если на диске уже чужая запись");
        }
        finally { filesSvc.OnMutated -= interloper; }

        File.ReadAllText(Path.Combine(dir, "window.txt")).Should().Be("чужая правка",
            "окно настоящее: на момент ответа файл уже чужой");
        var put2 = await PutWithIfMatchAsync(id, "window.txt", "v3", etagRaw);
        put2.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "цепочка по нашему тегу не затирает чужую правку молча — настоящий 409");
        var body = JsonSerializer.Deserialize<JsonElement>(await put2.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("file_changed");
        body.GetProperty("content").GetString().Should().Be("чужая правка");
    }

    // П.1, граница BOM: контент, начинающийся с U+FEFF. Запись пишет его как есть (EF BB BF),
    // GET срезает преамбулу при декоде — тег ответа обязан быть хешем БЕЗ преамбулы и равен
    // ETag последующего GET, иначе цепочка дала бы ложный 409. Без снятия BOM у текста запроса
    // этот тест красный. U+FEFF — явным кодом точки: литерал тонет в диффе.
    [Fact]
    public async Task SaveContent_ContentStartsBom_ResponseETagEqualsSubsequentGet_NoFalse409()
    {
        var (id, _) = await SetupProjectWithFileAsync("bom_put.txt", "old");
        var content = (char)0xFEFF + "новая запись с BOM";
        var put = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/files/content?path=bom_put.txt", new { content });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = put.Headers.GetValues("ETag").Single();
        raw.Trim('"').Should().Be(Sha256Hex("новая запись с BOM"),
            "тег — хеш без BOM-преамбулы: та же канонизация, что у GET после декода");

        var (etag2, _) = await GetContentETagAsync(id, "bom_put.txt");
        etag2.Should().Be(raw, "тег ответа PUT равен ETag последующего GET");

        var put2 = await PutWithIfMatchAsync(id, "bom_put.txt", "v2", raw);
        put2.StatusCode.Should().Be(HttpStatusCode.OK,
            "цепочка по возвращённому тегу не даёт ложного 409 на BOM-преамбулу");
    }

    // П.2 доработки S2c-1: служебный файл атомарной записи («.имя.<guid>.tmp») не правка
    // пользователя — до filesChanged он не доходит (тот же накопленный список уходит в
    // QueueSync синка знаний, FilesWatcherService.cs:535). Проверка на наблюдателе в
    // polling-режиме: IsExcluded общий у событий (inotify) и у снапшот-диффа, а дифф
    // детерминирован на любой ОС — тест про признак, а не про inotify.
    [Fact]
    public async Task FileWatcher_AtomicTempFileIsNotSent_TargetFileIsSent()
    {
        var root = Path.Combine(_tempDir, "watch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var sent = new List<JsonElement>();
        var (svc, projectId) = BuildWatcherService(root, sent);
        try
        {
            svc.Watch(projectId, "conn-atomic").Should().BeTrue("наблюдатель поднят впервые");
            var tmpName = TreeExcludes.AtomicTempName("target.txt");
            File.WriteAllText(Path.Combine(root, tmpName), "недозаписанный");
            File.WriteAllText(Path.Combine(root, "target.txt"), "правка пользователя");

            var paths = await WaitSentPaths(sent, "target.txt", TimeSpan.FromSeconds(20));
            paths.Should().Contain("target.txt", "правка целевого файла доходит до клиента");
            paths.Should().NotContain(tmpName, "служебный файл атомарной записи — не правка пользователя");
        }
        finally { svc.Dispose(); }
    }

    // Наблюдатель с проектом на заданном корне (по образцу FileWatcherSubscriptionTests),
    // в polling-режиме и с коротким интервалом: снапшот-дифф даёт событие без inotify.
    private (FileWatcherService Svc, string ProjectId) BuildWatcherService(string root, List<JsonElement> sent)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "watcher_projects.json"),
            ["FileWatcher:UsePolling"] = "true",
            ["FileWatcher:PollIntervalMs"] = "200",
        }).Build();
        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var owner = userStore.Add("fw-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        var projects = new ProjectManager(config, userStore, new AppSettingsService(config));
        var project = projects.Create("fw-" + Guid.NewGuid().ToString("N")[..8], root, owner.Id, owner.Username);

        // Dify не настроен → QueueSync — тихий no-op: тест про фильтр путей, не про синк.
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var knowledgeSync = new ProjectKnowledgeSyncService(knowledge, wkStore, projects,
            new ProjectFileGateway(new FileService()), new RecordingHubNotifier(), new NullDifyMetrics(),
            NullLogger<ProjectKnowledgeSyncService>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, new ProjectRootLookup(projects),
            new GraphPersistence(_tempDir, NullLogger<GraphPersistence>.Instance), config);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                lock (sent) sent.Add(JsonSerializer.SerializeToElement(args[0]));
            })
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        return (new FileWatcherService(projects, hub.Object, knowledgeSync, graphs, config), project.Id);
    }

    // Собирает пути из всех событий filesChanged, пока не появится ожидаемый.
    private static async Task<List<string>> WaitSentPaths(List<JsonElement> sent, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var paths = new List<string>();
            lock (sent)
                foreach (var payload in sent)
                    paths.AddRange(payload.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!));
            if (paths.Contains(expected)) return paths;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"событие по {expected} не доехало до клиента: [{string.Join(", ", paths)}]");
            await Task.Delay(100);
        }
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
