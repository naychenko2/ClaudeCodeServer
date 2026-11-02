using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// F-4 (разрез 4bc3d04f §3): что отказано РЕДАКТОРУ общего проекта в файловых дверях
/// FilesController, и что осталось открытым владельцу и в личном проекте.
///
/// 1. (п.2) Не-владелец удаляет или перезаписывает (Delete файла, Upload, ToMarkdown — по
///    ЦЕЛЕВОМУ .md) только ЧИСТЫЙ отслеживаемый git файл. Незакоммиченное — правка агента или
///    другого человека: для редактора она необратима (Revert на Write, у него её нет), поэтому
///    409 file_uncommitted, а не тихая потеря. Владелец и личный проект — как раньше.
/// 2. (п.3) rename на существующую цель — 409 «exists», а не 500 (File.Move/Directory.Move на
///    существующую цель бросают IOException — раньше он выходил 500).
/// 3. (п.4) Переименование и перенос ПАПКИ редактору закрыты (403 тем же Resolve(Write), что у
///    удаления папки); файл — открыт, как раньше.
///
/// Стенд бесхостовый (образец — SharedProjectEditorScopeGatesTests, GetOwnedVisibleTests):
/// контроллер собирается напрямую с настоящими ProjectManager/ProjectAccessService/FileService/
/// GitService и ClaimsPrincipal вызывающего; область запроса ставится явно, как её ставит
/// [ProjectScope]. Git — НАСТОЯЩИЙ: в корень проекта `git init` + коммит, поэтому «чистый
/// отслеживаемый», «изменённый» и «новый» — три реальных состояния рабочего дерева, а не
/// подставленный статус. Конвертер ToMarkdown — печать файла как есть (Markitdown:Command =
/// cat/type), как в FilesControllerUploadToMarkdownLockTests: хостовый markitdown не нужен.
/// </summary>
public sealed class FilesControllerEditorDenialsTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ProjectAccessService _access;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly FileService _files;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _secondEditorId;

    public FilesControllerEditorDenialsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "f4_editor_denials_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
            // Конвертер — печать файла как есть: тот же приём, что в S2c-10
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("f4-editor", "pwd", "user").Id;
        _secondEditorId = _users.Add("f4-editor-2", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _secondEditorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _files = new FileService(_git, _projects, NullLogger<FileService>.Instance);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    // --- стенд ---

    // Настоящее git-дерево: committed — закоммиченные (чистые отслеживаемые) файлы.
    private string GitRepo(string tag, params (string Path, string Content)[] committed)
    {
        var root = Path.Combine(_dir, tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run(root, "init", "-q");
        Run(root, "config", "user.email", "f4@example.com");
        Run(root, "config", "user.name", "f4");
        foreach (var (p, c) in committed)
        {
            var abs = Path.Combine(root, p);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, c);
            Run(root, "add", "--", p);
        }
        Run(root, "commit", "-q", "-m", "init");
        return root;
    }

    private static void Run(string dir, params string[] args)
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
        if (!proc.WaitForExit(15_000)) throw new InvalidOperationException("git не завершился: " + string.Join(' ', args));
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} в {dir}: {err}");
    }

    /// <summary>Общий проект с git-деревом и двумя редакторами: «второй редактор» — реальный
    /// второй человек, а не повторный вызов тем же.</summary>
    private (string ProjectId, string Root) SharedProject(params (string Path, string Content)[] committed)
    {
        var root = GitRepo("shared", committed);
        var id = _projects.Create("F4 " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(id,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _secondEditorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);
        return (id, root);
    }

    /// <summary>Личный проект с git-деревом: участников нет, вызывающий — владелец.</summary>
    private (string ProjectId, string Root) PersonalProject(params (string Path, string Content)[] committed)
    {
        var root = GitRepo("personal", committed);
        return (_projects.Create("F4 личный " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id, root);
    }

    // Контроллер напрямую; область — как её кладёт [ProjectScope]: вызывающий, арендатор
    // (владелец проекта), роль вызывающего. Для личного проекта арендатор = вызывающий.
    private FilesController As(string userId, string projectId, string? ifMatch = null)
    {
        var isOwner = userId == _ownerId;
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        };
        if (ifMatch is not null) http.Request.Headers["If-Match"] = ifMatch;
        http.SetRequestScope(new RequestScope(userId, _projects.GetById(projectId)!.OwnerId!,
            isOwner ? ProjectRole.Owner : ProjectRole.Editor));
        return new FilesController(_files, _projects, _access, null!, _config, null!,
            NullLogger<FilesController>.Instance, DocAi(), null!, null!, git: _git)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    // Конвертер — настоящий MarkitdownService с «печатью файла как есть»; модель не зовётся
    // (Enhance в тестах нет), поэтому ICheapTextRunner не участвует.
    private static DocumentAiService DocAi() =>
        new(new MarkitdownService(ConfigWithCat(), NullLogger<MarkitdownService>.Instance),
            new Mock<ICheapTextRunner>().Object, ConfigWithCat());

    private static IConfiguration ConfigWithCat() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

    private static IFormFile FormFile(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var mock = new Mock<IFormFile>();
        mock.SetupGet(f => f.FileName).Returns(name);
        mock.SetupGet(f => f.Length).Returns(bytes.LongLength);
        mock.SetupGet(f => f.ContentType).Returns("application/octet-stream");
        mock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream target, CancellationToken ct) => new MemoryStream(bytes).CopyToAsync(target, 81920, ct));
        return mock.Object;
    }

    // Тело ответа читаем в той же casing, что отдаёт MVC (JsonSerializerDefaults.Web →
    // camelCase): иначе тест спорит с именованием сериализатора, а не с контрактом ручки.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static JsonElement Body(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement;

    private static string Abs(string root, string rel) => Path.Combine(root, rel);

    // --- п.1: загрузка стоит под замком пути (тот же контракт, что у SaveContent с S2c-1) ---

    // Доказательство «замок есть» без гадания по таймерам в обратную сторону: тест сам держит
    // замок этого пути и проверяет, что загрузка в него НЕ вошла (за 2 с при работе порядка
    // десятков миллисекунд). Мутация «Upload без замка» (снять WaitAsync/Release) — загрузка
    // заканчивается сразу, тест красный; с замком — ждёт освобождения и проходит.
    [Fact]
    public async Task ЗагрузкаЖдётЗамкаПути_БезНегоПишетМимоДвери()
    {
        var (projectId, root) = SharedProject(("up.txt", "база"));   // чистый отслеживаемый: проверка п.2 его пускает
        var abs = Abs(root, "up.txt");
        var sem = FilesController.LockFor(abs);
        await sem.WaitAsync();
        var upload = Task.Run(() => As(_editorId, projectId).Upload(projectId, "", FormFile("up.txt", "поверх")));
        try
        {
            var finished = await Task.WhenAny(upload, Task.Delay(2000));
            finished.Should().NotBe(upload,
                "замок этого пути занят тестом: загрузка обязана ждать перед дверью (без замка — прошла сразу)");
        }
        finally { sem.Release(); }

        (await upload).Should().BeOfType<OkResult>("освободили замок — загрузка проходит и пишет");
        File.ReadAllText(abs).Should().Be("поверх");
    }

    // П.1 (контракт If-Match на этих двух дверях — тот же, что у SaveContent с S2c-1): названа
    // не та версии → 409 file_changed, файл на диске цел. Это то, что отличает «конкурентная
    // правка отшвлена» от «конкурентная правка затёрта молча».
    [Fact]
    public async Task ЗагрузкаИToMarkdown_СЧужимIfMatch_409FileChanged_ЦельЦела()
    {
        const string WrongTag = "\"0000000000000000000000000000000000000000000000000000000000000000\"";
        var (projectId, root) = SharedProject(("up.txt", "база"), ("doc.md", "база"));
        File.WriteAllText(Abs(root, "doc.txt"), "источник");

        // Порядок важен: пока цель чистая, отказ идёт по версии (file_changed). Запись без тега
        // сделала бы её незакоммиченной, и следующим был бы уже другой отказ.
        var conflict = (await As(_editorId, projectId, WrongTag).Upload(projectId, "", FormFile("up.txt", "поверх")))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value).GetProperty("code").GetString().Should().Be("file_changed");
        File.ReadAllText(Abs(root, "up.txt")).Should().Be("база", "отказ по версии не пишет файл");

        var md = (await As(_editorId, projectId, WrongTag).ToMarkdown(projectId, new ToMarkdownRequest("doc.txt"), default))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(md.Value).GetProperty("code").GetString().Should().Be("file_changed");
        File.ReadAllText(Abs(root, "doc.md")).Should().Be("база", "отказ по версии не пишет целевой .md");

        // Без If-Match — прежнее поведение (предсказуемая перезапись), обратная совместимость S2c-1
        (await As(_editorId, projectId).Upload(projectId, "", FormFile("up.txt", "поверх"))).Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "up.txt")).Should().Be("поверх");
    }

    // --- п.2: Delete ---

    [Fact]
    public async Task Удаление_НезакоммиченногоФайла_Редактору409FileUncommitted_ЧистыйОтслеживаемыйПроходит()
    {
        var (projectId, root) = SharedProject(("clean.txt", "база"), ("mod.txt", "база"));
        File.AppendAllText(Abs(root, "mod.txt"), " + чужая правка, не коммичена");   // изменённый отслеживаемый
        File.WriteAllText(Abs(root, "new.txt"), "новый файл, не коммичен");          // untracked

        var editor = As(_editorId, projectId);

        foreach (var path in new[] { "mod.txt", "new.txt" })
        {
            var denied = (await editor.Delete(projectId, path)).Should().BeOfType<ConflictObjectResult>().Subject;
            Body(denied.Value).GetProperty("code").GetString().Should().Be("file_uncommitted", path);
            File.Exists(Abs(root, path)).Should().BeTrue("отказ не удаляет файл: " + path);
        }

        // Чистый отслеживаемый — открыт, как раньше
        (await editor.Delete(projectId, "clean.txt")).Should().BeOfType<NoContentResult>();
        File.Exists(Abs(root, "clean.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Удаление_НезакоммиченногоФайла_ВладельцуКакРаньше_204()
    {
        var (projectId, root) = SharedProject(("mod.txt", "база"));
        File.AppendAllText(Abs(root, "mod.txt"), " + правка агента");
        File.WriteAllText(Abs(root, "new.txt"), "новое");

        var owner = As(_ownerId, projectId);
        (await owner.Delete(projectId, "mod.txt")).Should().BeOfType<NoContentResult>(
            "владелец отвечает за своё дерево: ему незакоммиченное открыто, как до шага");
        (await owner.Delete(projectId, "new.txt")).Should().BeOfType<NoContentResult>();
    }

    // --- п.2: Upload ---

    [Fact]
    public async Task Загрузка_НаНезакоммиченныйФайл_Редактору409_НаЧистыйПишет_ВторойРедакторОтшит()
    {
        var (projectId, root) = SharedProject(("mod.txt", "база"), ("clean.txt", "база"));
        File.AppendAllText(Abs(root, "mod.txt"), " + чужая правка");

        var editor = As(_editorId, projectId);

        var denied = (await editor.Upload(projectId, "", FormFile("mod.txt", "поверх чужой правки")))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(denied.Value).GetProperty("code").GetString().Should().Be("file_uncommitted");
        File.ReadAllText(Abs(root, "mod.txt")).Should().Be("база + чужая правка", "отказ не затёр чужую правку");

        // Чистый отслеживаемый — перезапись открыта (как в S2c-10 без If-Match)
        (await editor.Upload(projectId, "", FormFile("clean.txt", "правка редактора")))
            .Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "clean.txt")).Should().Be("правка редактора");

        // Та же запись теперь сама — незакоммиченная: второй редактор этого же пути отшит,
        // а не затирает чужую правку молча (проверка — внутри замка пути)
        var second = (await As(_secondEditorId, projectId).Upload(projectId, "", FormFile("clean.txt", "поверх")))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(second.Value).GetProperty("code").GetString().Should().Be("file_uncommitted");
        File.ReadAllText(Abs(root, "clean.txt")).Should().Be("правка редактора");

        // Владелец — как раньше
        (await As(_ownerId, projectId).Upload(projectId, "", FormFile("mod.txt", "правка владельца")))
            .Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "mod.txt")).Should().Be("правка владельца");
    }

    // --- п.2: ToMarkdown (по целевому .md) ---

    // Цель to-markdown — имя ИСТОЧНИКА с расширением .md рядом с ним (baseName от req.Path),
    // поэтому источник называется doc.txt: его цель — doc.md, тот самый файл состояния.
    [Fact]
    public async Task ToMarkdown_НаНезакоммиченныйЦелевой_Редактору409_НаЧистыйПишет()
    {
        var (dirtyProject, dirtyRoot) = SharedProject(("doc.md", "старый markdown"));
        File.AppendAllText(Abs(dirtyRoot, "doc.md"), " + чужая правка");
        File.WriteAllText(Abs(dirtyRoot, "doc.txt"), "содержимое источника");

        var denied = (await As(_editorId, dirtyProject).ToMarkdown(dirtyProject, new ToMarkdownRequest("doc.txt"), default))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(denied.Value).GetProperty("code").GetString().Should().Be("file_uncommitted");
        File.ReadAllText(Abs(dirtyRoot, "doc.md")).Should().Be("старый markdown + чужая правка",
            "отказ не пишет целевой .md");

        var (cleanProject, cleanRoot) = SharedProject(("doc.md", "старый markdown"));
        File.WriteAllText(Abs(cleanRoot, "doc.txt"), "содержимое источника");
        (await As(_editorId, cleanProject).ToMarkdown(cleanProject, new ToMarkdownRequest("doc.txt"), default))
            .Should().BeOfType<OkObjectResult>();
        File.ReadAllText(Abs(cleanRoot, "doc.md")).Should().Be("содержимое источника",
            "чистый отслеживаемый целевой — перезапись открыта");
    }

    // --- п.3: rename на существующую цель ---

    [Fact]
    public async Task Rename_НаСуществующуюЦель_409Exists_АНе500_НаСвободнуюЦельКакРаньше()
    {
        var (projectId, root) = SharedProject(("a.txt", "А"), ("b.txt", "Б"));
        var editor = As(_editorId, projectId);

        var denied = (await editor.Rename(projectId, new RenameRequest("a.txt", "b.txt")))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Body(denied.Value).GetProperty("error").GetString().Should().Be("exists",
            "та же форма коллизии имени, что у create");
        File.ReadAllText(Abs(root, "a.txt")).Should().Be("А", "отказ не трогает исходный файл");
        File.ReadAllText(Abs(root, "b.txt")).Should().Be("Б", "отказ не затёр цель");

        // Свободная цель — как раньше
        (await editor.Rename(projectId, new RenameRequest("a.txt", "free.txt"))).Should().BeOfType<OkResult>();
        File.Exists(Abs(root, "a.txt")).Should().BeFalse();
        File.ReadAllText(Abs(root, "free.txt")).Should().Be("А");
    }

    // --- п.4: папка редактору ---

    [Fact]
    public async Task ПереименованиеИПереносПапки_Редактору403_ВладельцуКакРаньше_ФайлРедакторуОткрыт()
    {
        var (projectId, root) = SharedProject(("sub/inner.txt", "внутри папки"), ("file.txt", "файл"));
        Directory.CreateDirectory(Abs(root, "other"));
        var editor = As(_editorId, projectId);

        var denied = (await editor.Rename(projectId, new RenameRequest("sub", "sub2")))
            .Should().BeOfType<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(403);
        Body(denied.Value).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
        Directory.Exists(Abs(root, "sub")).Should().BeTrue("отказ не трогает папку");
        Directory.Exists(Abs(root, "sub2")).Should().BeFalse();

        // Перенос папки в другую папку — та же закрытая дверь
        var moved = (await editor.Rename(projectId, new RenameRequest("sub", "other/sub")))
            .Should().BeOfType<ObjectResult>().Subject;
        moved.StatusCode.Should().Be(403);
        Directory.Exists(Abs(root, "other/sub")).Should().BeFalse();

        // Файл редактору остаётся открыт (закрыта только папка)
        (await editor.Rename(projectId, new RenameRequest("file.txt", "file2.txt"))).Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "file2.txt")).Should().Be("файл");

        // Владелец — как раньше
        (await As(_ownerId, projectId).Rename(projectId, new RenameRequest("sub", "sub2"))).Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "sub2/inner.txt")).Should().Be("внутри папки");
    }

    // --- границы: личный проект и не-git ---

    [Fact]
    public async Task ЛичныйПроект_КакРаньше_НезакоммиченноеПишетсяИУдаляется()
    {
        var (projectId, root) = PersonalProject(("mod.txt", "база"));
        File.AppendAllText(Abs(root, "mod.txt"), " + не коммичено");
        File.WriteAllText(Abs(root, "new.txt"), "новое");

        var owner = As(_ownerId, projectId);
        (await owner.Upload(projectId, "", FormFile("mod.txt", "поверх"))).Should().BeOfType<OkResult>(
            "в личном проекте вызывающий — владелец: проверка его не касается");
        File.ReadAllText(Abs(root, "mod.txt")).Should().Be("поверх");
        (await owner.Delete(projectId, "new.txt")).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task НеGitПроект_РедакторПишетКакРаньше_СудитьОНезакоммиченномНечем()
    {
        var root = Path.Combine(_dir, "nogit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "база");
        var projectId = _projects.Create("F4 без git " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(projectId, [new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId }]);

        var editor = As(_editorId, projectId);
        (await editor.Upload(projectId, "", FormFile("a.txt", "поверх"))).Should().BeOfType<OkResult>(
            "не-репозиторий: «незакоммиченного» нет как класса, дверь не закрывается вслепую");
        (await editor.Delete(projectId, "a.txt")).Should().BeOfType<NoContentResult>();
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
