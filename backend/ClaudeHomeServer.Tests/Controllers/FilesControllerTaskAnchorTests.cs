using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// FS-B1a: GET files/content?taskId= — чтение материала из дерева задачи-автора (третий
// потребитель ProjectTreeLocator); PUT files/content с taskId — 400 «только читается».
[Trait("Category", "Host")]
public class FilesControllerTaskAnchorTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public FilesControllerTaskAnchorTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "task_anchor_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private static string Short() => Guid.NewGuid().ToString("N")[..8];

    // Проект с настоящим git-репозиторием в корне: без него worktree list пуст.
    private async Task<(string ProjectId, string MainRoot)> CreateGitProjectAsync(string tag)
    {
        var mainRoot = Path.Combine(_tempDir, tag + "_" + Short());
        Directory.CreateDirectory(mainRoot);
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name = "TaskAnchor " + tag, rootPath = mainRoot });
        resp.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        await _factory.Services.GetRequiredService<GitService>().InitAsync(null, mainRoot);
        return (id, mainRoot);
    }

    private async Task<(string Path, string Branch)> AddWorktreeAsync(string mainRoot, string tag)
    {
        var worktree = Path.Combine(_tempDir, "wt_" + tag + "_" + Short());
        Directory.CreateDirectory(worktree);
        var branch = "feat/" + tag + "-" + Guid.NewGuid().ToString("N")[..6];
        await _factory.Services.GetRequiredService<GitService>().WorktreeAddAsync(null, mainRoot, worktree, branch);
        return (worktree, branch);
    }

    private async Task<string> CreateTaskAsync(string projectId, string? worktree = null, string? branch = null)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = "task " + Short(), role = "feature",
            worktreePath = worktree, worktreeBranch = branch,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private string Url(string projectId, string path, string taskId) =>
        $"/api/projects/{projectId}/files/content?path={Uri.EscapeDataString(path)}&taskId={taskId}";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    // Файл есть и в дереве задачи, и в main: читается дерево задачи (анкер первым).
    [Fact]
    public async Task GetContent_TaskId_ФайлТолькоВWorktreeЗадачи_200_BranchИIsAnchor()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("only");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "only");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из ветки задачи");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "документ лежит только в ветке — раньше он не открывался");
        var body = await JsonOf(resp);
        body.GetProperty("content").GetString().Should().Be("из ветки задачи");
        body.GetProperty("isAnchor").GetBoolean().Should().BeTrue();
        body.GetProperty("branch").GetString().Should().Be(branch);
    }

    [Fact]
    public async Task GetContent_TaskId_ФайлИВДереве_ИВMain_АнкерПервый()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("prio");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "prio");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из ветки");
        File.WriteAllText(Path.Combine(mainRoot, "doc.md"), "из main");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var body = await JsonOf(await _client.GetAsync(Url(projectId, "doc.md", taskId)));

        body.GetProperty("content").GetString().Should().Be("из ветки", "порядок: анкер → main → прочие");
        body.GetProperty("isAnchor").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetContent_TaskId_ФайлТолькоВMain_200_IsAnchorFalse()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("main");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "main");
        File.WriteAllText(Path.Combine(mainRoot, "doc.md"), "из main");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(resp);
        body.GetProperty("content").GetString().Should().Be("из main");
        body.GetProperty("isAnchor").GetBoolean().Should().BeFalse("прочитано не из заявленного дерева");
        body.TryGetProperty("branch", out _).Should().BeFalse("у main ветки нет");
    }

    [Fact]
    public async Task GetContent_TaskId_ФайлаНетНигде_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("none");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "none");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var resp = await _client.GetAsync(Url(projectId, "nope.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetContent_TaskId_НесуществующаяЗадача_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("ghost");
        File.WriteAllText(Path.Combine(mainRoot, "doc.md"), "из main");

        var resp = await _client.GetAsync(Url(projectId, "doc.md", Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "нет задачи — отказ, а не фолбэк на main");
    }

    // Чужая задача (владелец — второй пользователь) в том же проекте: для вызывающего её нет.
    [Fact]
    public async Task GetContent_TaskId_ЧужаяЗадача_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("foreign");
        var (worktree, _) = await AddWorktreeAsync(mainRoot, "foreign");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "в чужой ветке");
        var otherUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var foreignTask = _factory.Services.GetRequiredService<TaskManager>().Create(
            projectId, otherUserId, new CreateTaskRequest("чужая задача"));
        foreignTask.WorktreePath = worktree;

        var resp = await _client.GetAsync(Url(projectId, "doc.md", foreignTask.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "задача ищется среди задач владельца");
    }

    // Задача другого проекта: ProjectId обязан совпасть с проектом маршрута.
    // Мутация: снять `task.ProjectId != project.Id` в OwnedTaskForRead — тест краснеет.
    [Fact]
    public async Task GetContent_TaskId_ЗадачаДругогоПроекта_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("projA");
        var (otherProjectId, otherRoot) = await CreateGitProjectAsync("projB");
        var (worktree, branch) = await AddWorktreeAsync(otherRoot, "projB");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из другого проекта");
        File.WriteAllText(Path.Combine(mainRoot, "doc.md"), "из main A");
        var otherTaskId = await CreateTaskAsync(otherProjectId, worktree, branch);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", otherTaskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "задача другого проекта — отказ, не чтение main");
    }

    // Чужое приватное вложение: гейт по корню проекта (первая линия) — 404 до резолва задачи.
    [Fact]
    public async Task GetContent_TaskId_ЧужоеPrivate_ПоКорнюПроекта_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("privroot");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "privroot");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var other = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var rel = PrivateAttachments.Build(other, Guid.NewGuid(), "чужое.txt")!;
        var abs = Path.Combine(worktree, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "чужое");

        var resp = await _client.GetAsync(Url(projectId, rel, taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Чужое private через ссылку «L» внутри дерева задачи: первая линия (по корню проекта) о ней
    // молчит — ловит вторая, по фактическому корню чтения. Свой автор — 200 (пара самопроверки).
    // Мутация: снять второй ForeignPrivateDenied в ветке taskId — чужое читается 200.
    [Fact]
    public async Task GetContent_TaskId_ЧужоеPrivate_ЧерезСсылкуВДереве_404_Своё_200()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var (projectId, mainRoot) = await CreateGitProjectAsync("privlink");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "privlink");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var users = _factory.Services.GetRequiredService<UserStore>();
        var me = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var other = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

        string ViaLink(string author, string content)
        {
            var rel = PrivateAttachments.Build(author, Guid.NewGuid(), "файл.txt")!;
            var abs = Path.Combine(worktree, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
            var link = Path.Combine(worktree, "L");
            if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, TreeExcludes.AttachmentsDir);
            return "L/" + rel[(TreeExcludes.AttachmentsDir.Length + 1)..];
        }

        var foreign = ViaLink(other, "чужое");
        var own = ViaLink(me, "своё");

        (await _client.GetAsync(Url(projectId, foreign, taskId))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "ссылка в дереве задачи не обходит гейт по фактическому корню");
        var ok = await _client.GetAsync(Url(projectId, own, taskId));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "своё через ссылку читается — значит 404 выше дал именно гейт");
        (await JsonOf(ok)).GetProperty("content").GetString().Should().Be("своё");
    }

    // Дерево снесли после создания задачи: файл из него пропал вместе с деревом — 404, не 500.
    [Fact]
    public async Task GetContent_TaskId_ДеревоСнесли_404_Без500()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("gone");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "gone");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "только в ветке");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        Directory.Delete(worktree, recursive: true);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // PUT с taskId — 400 до любого эффекта, файл не меняется.
    // Мутация: убрать отказ в SaveContent — файл перезаписан, тест краснеет.
    [Fact]
    public async Task PutContent_TaskId_400_ФайлНеИзменён()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("put");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "put");
        var inMain = Path.Combine(mainRoot, "doc.md");
        var inTree = Path.Combine(worktree, "doc.md");
        File.WriteAllText(inMain, "main до");
        File.WriteAllText(inTree, "ветка до");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/files/content?path=doc.md&taskId={taskId}", new { content = "правка" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("только читается");
        File.ReadAllText(inMain).Should().Be("main до", "корень проекта не тронут");
        File.ReadAllText(inTree).Should().Be("ветка до", "дерево задачи не тронуто");
    }
}
