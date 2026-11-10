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

    // ---- FS-B1b: Р1 (хвост ревью FS-B1a: гонка «дерево пропало между locate и чтением») ----

    // Настоящее окно «между локатором и чтением» по HTTP недостижимо: локатор отсекает несуществующее
    // дерево до чтения (ProjectTreeLocator.cs:101,134), и анкерный поток выходит 404 на :228, не доходя
    // до catch на :255/:257 — подтверждено мутацией прошлого круга: GetContent_TaskId_ДеревоСнили_404_Без500
    // при снятых catch остаётся зелёным. Требование Р1 закрывается как недостижимое по HTTP (решение
    // осознанное, по ревью R-FS-B1b). Что эти catch держат в реальности — безанкерная ветка (:236,
    // root = корень проекта) читает без предваряющего File.Exists: файл лежит только в дереве задачи
    // (по анкеру читается — :75), повтор того же пути без анкера не дотягивается до дерева и
    // ловится catch на :255/:257 — 404, а не 500. Тот же инвариант держат базовые
    // GetContent_NonExistentFile_Returns404 и GetContent_FeatureId_НеИщетПоЧужимДеревьям_404
    // (FilesControllerTests) — «ровно этот тест» на снятии catch недостижим в принципе.
    // Мутация: убрать catch (FileNotFoundException/DirectoryNotFoundException) в GetContent —
    // тест краснеет (500).
    [Fact]
    public async Task GetContent_ФайлТолькоВДеревеЗадачи_ПовторБезАнкера_404_Без500()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("noanchor");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "noanchor");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из ветки");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        // Файл есть — но только в дереве задачи: по анкеру читается.
        (await _client.GetAsync(Url(projectId, "doc.md", taskId))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Повтор без анкера: корень проекта, где этого файла никогда не было; безанкерная ветка
        // не ищет по деревьям задачи и читает без предваряющего File.Exists —
        // Read бросает FileNotFoundException, catch оборачивает это в 404.
        var resp = await _client.GetAsync($"/api/projects/{projectId}/files/content?path=doc.md");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "файла нет в корне — 404 через catch на :255/:257, а не 500; в дерево задачи безанкерный путь не заходит");
    }

    // ---- FS-B1b: Р2 (порядок веток content) ----

    // content: sessionId + taskId одновременно — порядок featureId → sessionId → taskId, побеждает
    // дерево чата. Мутация: в GetContent поменять местами ветки sessionId и taskId — тест краснеет.
    [Fact]
    public async Task GetContent_SessionIdИTaskId_Одновременно_SessionIdВыигрывает()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("order");
        var (taskTree, taskBranch) = await AddWorktreeAsync(mainRoot, "ordertask");
        var (chatTree, _) = await AddWorktreeAsync(mainRoot, "orderchat");
        File.WriteAllText(Path.Combine(taskTree, "draft.md"), "из дерева задачи");
        File.WriteAllText(Path.Combine(chatTree, "draft.md"), "из дерева чата");
        var taskId = await CreateTaskAsync(projectId, taskTree, taskBranch);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = await sessions.CreateAsync(projectId, ClaudeMode.Auto, name: "chat with own tree");
        (await sessions.AttachWorktreeAsync(session.Id, chatTree)).Should().BeTrue();

        var resp = await _client.GetAsync(
            $"/api/projects/{projectId}/files/content?path=draft.md&sessionId={session.Id}&taskId={taskId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(resp)).GetProperty("content").GetString().Should().Be("из дерева чата",
            "sessionId стоит в цепочке веток раньше taskId");
    }

    // ---- FS-B1b: GET files/stream?taskId= ----

    private string StreamUrl(string projectId, string path, string taskId) =>
        $"/api/projects/{projectId}/files/stream?path={Uri.EscapeDataString(path)}&taskId={taskId}";

    [Fact]
    public async Task Stream_TaskId_ОтдаётБайтыИзWorktreeЗадачи_НеИзMain()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("stream");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "stream");
        File.WriteAllText(Path.Combine(worktree, "clip.mp4"), "байты ветки");
        File.WriteAllText(Path.Combine(mainRoot, "clip.mp4"), "байты main");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);

        var resp = await _client.GetAsync(StreamUrl(projectId, "clip.mp4", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("байты ветки");
    }

    [Fact]
    public async Task Stream_TaskId_ЧужаяЗадача_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("streamforeign");
        var (worktree, _) = await AddWorktreeAsync(mainRoot, "streamforeign");
        File.WriteAllText(Path.Combine(worktree, "clip.mp4"), "чужое");
        File.WriteAllText(Path.Combine(mainRoot, "clip.mp4"), "main");
        var otherUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var foreignTask = _factory.Services.GetRequiredService<TaskManager>().Create(
            projectId, otherUserId, new CreateTaskRequest("чужая задача"));
        foreignTask.WorktreePath = worktree;

        var resp = await _client.GetAsync(StreamUrl(projectId, "clip.mp4", foreignTask.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "отказ, а не фолбэк на main");
    }

    [Fact]
    public async Task Stream_TaskId_ЧужоеPrivate_ПоКорнюПроекта_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("streampriv");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "streampriv");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var other = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var rel = PrivateAttachments.Build(other, Guid.NewGuid(), "чужое.png")!;
        var abs = Path.Combine(worktree, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "чужое");

        var resp = await _client.GetAsync(StreamUrl(projectId, rel, taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Мутация: снять второй ForeignPrivateDenied в ветке taskId у Stream — чужое отдаётся 200.
    [Fact]
    public async Task Stream_TaskId_ЧужоеPrivate_ЧерезСсылкуВДереве_404_Своё_200()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var (projectId, mainRoot) = await CreateGitProjectAsync("streamlink");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "streamlink");
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

        (await _client.GetAsync(StreamUrl(projectId, foreign, taskId))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "ссылка в дереве задачи не обходит гейт по фактическому корню");
        var ok = await _client.GetAsync(StreamUrl(projectId, own, taskId));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "своё через ссылку читается — значит 404 выше дал именно гейт");
        (await ok.Content.ReadAsStringAsync()).Should().Be("своё");
    }

    [Fact]
    public async Task Stream_SessionIdИTaskId_400()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("streamboth");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "streamboth");
        File.WriteAllText(Path.Combine(worktree, "clip.mp4"), "x");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.Auto, name: "chat");

        var resp = await _client.GetAsync(
            $"/api/projects/{projectId}/files/stream?path=clip.mp4&sessionId={session.Id}&taskId={taskId}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- FS-B1b: POST files/locate {taskId} ----

    private Task<HttpResponseMessage> LocateAsync(string projectId, object body) =>
        _client.PostAsJsonAsync($"/api/projects/{projectId}/files/locate", body);

    // Файл только в дереве задачи — hit с её веткой и isAnchor; файл только в чужом дереве —
    // не анкер (ветка того дерева, isAnchor=false): подпись «из ветки» не врёт.
    [Fact]
    public async Task Locate_TaskId_АнкерЗадачи_ВеткаИIsAnchor()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("loc");
        var (taskTree, taskBranch) = await AddWorktreeAsync(mainRoot, "loctask");
        var (otherTree, otherBranch) = await AddWorktreeAsync(mainRoot, "locother");
        File.WriteAllText(Path.Combine(taskTree, "mine.md"), "моё");
        File.WriteAllText(Path.Combine(otherTree, "theirs.md"), "чужое");
        var taskId = await CreateTaskAsync(projectId, taskTree, taskBranch);

        var resp = await LocateAsync(projectId, new { paths = new[] { "mine.md", "theirs.md" }, taskId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(resp);
        body.GetArrayLength().Should().Be(2);
        var mine = body.EnumerateArray().Single(e => e.GetProperty("path").GetString() == "mine.md");
        mine.GetProperty("isAnchor").GetBoolean().Should().BeTrue();
        mine.GetProperty("branch").GetString().Should().Be(taskBranch);
        var theirs = body.EnumerateArray().Single(e => e.GetProperty("path").GetString() == "theirs.md");
        theirs.GetProperty("isAnchor").GetBoolean().Should().BeFalse("файл не из дерева задачи");
        theirs.GetProperty("branch").GetString().Should().Be(otherBranch);
    }

    [Fact]
    public async Task Locate_TaskId_ЧужаяИНесуществующаяЗадача_404()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("locforeign");
        var (worktree, _) = await AddWorktreeAsync(mainRoot, "locforeign");
        File.WriteAllText(Path.Combine(worktree, "a.md"), "x");
        var otherUserId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var foreignTask = _factory.Services.GetRequiredService<TaskManager>().Create(
            projectId, otherUserId, new CreateTaskRequest("чужая задача"));
        foreignTask.WorktreePath = worktree;

        (await LocateAsync(projectId, new { paths = new[] { "a.md" }, taskId = foreignTask.Id })).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "чужая задача");
        (await LocateAsync(projectId, new { paths = new[] { "a.md" }, taskId = Guid.NewGuid().ToString() })).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "несуществующая задача");
    }

    // Чужой private выпадает из запроса до локатора и при taskId: в ответе его нет.
    [Fact]
    public async Task Locate_TaskId_ЧужоеPrivate_ВОтветеНет()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("locpriv");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "locpriv");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var other = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var rel = PrivateAttachments.Build(other, Guid.NewGuid(), "чужое.txt")!;
        var abs = Path.Combine(worktree, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "чужое");

        var resp = await LocateAsync(projectId, new { paths = new[] { rel }, taskId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(resp)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Locate_SessionIdИTaskId_400()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("locboth");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "locboth");
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        var session = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(projectId, ClaudeMode.Auto, name: "chat");

        var resp = await LocateAsync(projectId, new { paths = new[] { "a.md" }, sessionId = session.Id, taskId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- FS-B2: запасной путь — материал из коммита ветки задачи, когда дерева уже нет ----
    // Worktree снесли после мержа/отказа от ветки, файл остаётся в коммите ветки, пока жива
    // сама ветка (§7.2 п.3, вариант Б §7.3). Арранжи делаются прямым git: коммит в дерево
    // задачи и снос ветки — то, чего сервис по этим путям сам не делает.

    // Прямой git (идентичность — ключами -c, чтобы тест не зависел от глобальной настройки
    // git на машине, как GitFixture).
    private static void GitRun(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000)) throw new InvalidOperationException("git не завершился: " + string.Join(' ', args));
        if (proc.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} в {dir}: {err}");
    }

    // Зафиксировать содержимое дерева задачи коммитом: файл должен выжить в ветке, когда
    // каталог снесут.
    private static void CommitTree(string dir)
    {
        GitRun(dir, "add", "-A");
        GitRun(dir, "-c", "user.name=тест", "-c", "user.email=test@test", "commit", "-q", "-m", "материал фичи");
    }

    private GitService Git => _factory.Services.GetRequiredService<GitService>();

    [Fact]
    public async Task GetContent_TaskId_ДеревоСнесли_ВеткаЖива_200_FromCommit()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("brc");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "brc");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из коммента ветки");
        CommitTree(worktree);
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        Directory.Delete(worktree, recursive: true);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "дерева нет, а файл живёт в комменте ветки");
        var body = await JsonOf(resp);
        body.GetProperty("content").GetString().Should().Be("из коммента ветки");
        body.GetProperty("branch").GetString().Should().Be(branch);
        body.GetProperty("fromCommit").GetBoolean().Should().BeTrue();
    }

    // Ветки тоже нет (добитая после мержа): отдельный признак для подписи фронта «удалён
    // вместе с веткой», и это 404, а не 500. Снос ветки возможен только после worktree prune:
    // git не отдаёт ветку, «используемую» несуществующим деревом (проверено на живом репо).
    [Fact]
    public async Task GetContent_TaskId_ВеткаУдалена_404_BranchDeleted_Без500()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("brdel");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "brdel");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "было в ветке");
        CommitTree(worktree);
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        Directory.Delete(worktree, recursive: true);
        await Git.WorktreePruneAsync(null, mainRoot);
        GitRun(mainRoot, "branch", "-D", branch);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(resp)).GetProperty("reason").GetString().Should().Be("branch_deleted");
    }

    // Бинарь в комменте ветки — не текст: отдельный reason, чтобы фронт не рисовал пустой
    // редактор там, где читать нечего.
    [Fact]
    public async Task GetContent_TaskId_БинарьВКомментеВетки_404_ReasonBinary()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("brbin");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "brbin");
        File.WriteAllBytes(Path.Combine(worktree, "raw.bin"), [1, 2, 0, 3]);
        CommitTree(worktree);
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        Directory.Delete(worktree, recursive: true);

        var resp = await _client.GetAsync(Url(projectId, "raw.bin", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "не текст — не отдаём");
        (await JsonOf(resp)).GetProperty("reason").GetString().Should().Be("binary");
    }

    // Ветка берётся только из задачи: клиентский ?branch= не читается вовсе — живой файл в
    // названной клиентом ветке остаётся непрочитанным (задача ветки не знает → фолбэка нет).
    [Fact]
    public async Task GetContent_TaskId_ПараметрBranchИгнорируется()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("brparam");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "brparam");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из ветки");
        CommitTree(worktree);
        var taskId = await CreateTaskAsync(projectId, worktree, null);
        Directory.Delete(worktree, recursive: true);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId) + "&branch=" + Uri.EscapeDataString(branch));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "ветку просил клиент, а не задача");
        (await JsonOf(resp)).TryGetProperty("reason", out _).Should().BeFalse("фолбэк не запускался");
    }

    // Порядок «анкер → main → прочие деревья → git show» целиком: файл есть и в main, и в
    // комменте снесённой ветки — читается живое дерево, а не коммент.
    // Мутация: перенести ветку git show в GetContent перед locator.LocateAsync — тест краснеет.
    [Fact]
    public async Task GetContent_TaskId_ДеревоСнесли_ФайлВMain_ЧитаетMainНеВетку()
    {
        var (projectId, mainRoot) = await CreateGitProjectAsync("brorder");
        var (worktree, branch) = await AddWorktreeAsync(mainRoot, "brorder");
        File.WriteAllText(Path.Combine(worktree, "doc.md"), "из коммента ветки");
        CommitTree(worktree);
        File.WriteAllText(Path.Combine(mainRoot, "doc.md"), "из main");
        CommitTree(mainRoot);
        var taskId = await CreateTaskAsync(projectId, worktree, branch);
        Directory.Delete(worktree, recursive: true);

        var resp = await _client.GetAsync(Url(projectId, "doc.md", taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(resp);
        body.GetProperty("content").GetString().Should().Be("из main", "живое дерево раньше git show");
        body.TryGetProperty("fromCommit", out _).Should().BeFalse("это обычный путь локатора");
    }
}
