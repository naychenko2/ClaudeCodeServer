using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-6b-5 (дефект e5d55007, находка ревью f24f7533; асимметрия с MCP/WebDAV): запись через
/// REST в чужое приватное поддерево (.cc-attachments/private/{чужой}/…) закрыта тем же 404,
/// что и чтение (S2d-6b-2).
///
/// 1. Каждая пишущая ручка FilesController (SaveContent, Create, mkdir, Rename — ОБА пути,
///    Delete файла и папки, Upload — каталог назначения, ToMarkdown — targetDir, Revert,
///    SaveFromUrl, office-discard) на пути под чужим автором — 404 ДО любого эффекта: до замка
///    пути, до CreateDirectory, до сетевой загрузки, до снятия edit-ключа и до git-отката.
///    Файл жертвы цел, побочных каталогов нет.
/// 2. Контейнеры closed-поддерева (корень проекта, .cc-attachments, .cc-attachments/private)
///    для Rename/Delete — 404 и владельцу: операция над контейнером относится ко всему
///    private- поддереву (тот же предикат PrivateAttachments.IsPrivateContainerPath, что у
///    MCP files_rename/files-delete и WebDAV COPY/MOVE). Свой {id}-каталог автора и общие
///    {guid}-каталоги — не контейнеры: с ними как раньше.
/// 3. Автор своего private, обычные файлы и общее вложение (.cc-attachments/{guid}/…) —
///    как раньше: гейт смотрит на автора пути, а не на папку.
///
/// S2d-6b-8 (хвосты ревью R-S2d-6b-5, п.1 и п.3): office-discard закрыт тем же гейтом — с
/// записывающим git-швом, доказывающим «отказ ДО RevertFile» (без гейта откат был бы попытан);
/// ветка SaveContent с If-Match проверяется с ВЕРНЫМ тегом файла-жертвы: без гейта этот If-Match
/// совпал бы и запись состоялась — то есть отказ даёт именно гейт, а не расхождение хеша.
///
/// Стенд бесхостовый (образец FilesControllerPrivateAttachmentsReadTests): контроллер
/// собирается напрямую. Три разных человека: владелец проекта, другой его участник и автор
/// приватного вложения — «чужой» здесь действительно другой человек.
/// </summary>
public sealed class FilesControllerPrivateAttachmentsWriteTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ProjectAccessService _access;
    private readonly SyncService _sync;
    private readonly FileService _files;
    private readonly ProjectTreeLocator _locator;
    private readonly JwtService _jwt;
    private readonly string _ownerId;   // владелец проекта, НЕ автор вложения
    private readonly string _editorId;  // другой участник проекта, НЕ автор вложения
    private readonly string _authorId;  // автор приватного вложения, участник проекта

    public FilesControllerPrivateAttachmentsWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s2d6b5_write_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
            // Конвертер — печать файла как есть (тот же приём, что в ReadTests): хостовый
            // markitdown не нужен; на 404-отказе цели он и не зовётся
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2d6b5-editor", "pwd", "user").Id;
        _authorId = _users.Add("s2d6b5-author", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _authorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _sync = new SyncService(_config);
        // git в этом шаге не нужен: ни одна проверяемая ветка до него не доходит (отказ идёт
        // до него), а не-git корень FileService читает штатно
        _files = new FileService(null, _projects, NullLogger<FileService>.Instance);
        _locator = new ProjectTreeLocator(TestLauncherFactory.Instance, NullLogger<ProjectTreeLocator>.Instance);
        _jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    // --- стенд ---

    /// <summary>Общий проект (владелец + два участника) с рабочей папкой на диске, без git.</summary>
    private (string ProjectId, string Root) Project()
    {
        var root = Root();
        var id = _projects.Create("S2d6b5 " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(id,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _authorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);
        return (id, root);
    }

    private string Root()
    {
        var root = Path.Combine(_dir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Личный проект указанного человека: участников нет, вызывающий — владелец.</summary>
    private (string ProjectId, string Root) PersonalProjectOf(string ownerId)
    {
        var root = Root();
        return (_projects.Create("S2d6b5 личный " + Guid.NewGuid().ToString("N")[..6], root, ownerId,
            _users.GetById(ownerId)!.Username).Id, root);
    }

    private static string Write(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return abs;
    }

    private static string AbsOf(string root, string rel) => Path.Combine(root, rel);

    /// <summary>Корень private-поддерева автора (без {guid}-слоя): .cc-attachments/private/{автор}.</summary>
    private string AuthorPrivateRoot() => $"{TreeExcludes.AttachmentsDir}/private/{_authorId}";

    /// <summary>Каталог-жертва под чужим автором: .cc-attachments/private/{автор}/{guid}.</summary>
    private string ForeignDir() => $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}";

    /// <summary>Путь приватного вложения — та же сборка, что у записи (PrivateAttachments.Build).</summary>
    private string ForeignFile(string name) => $"{ForeignDir()}/{name}";

    /// <summary>Общее вложение чата — прежняя раскладка .cc-attachments/{guid}/{имя}.</summary>
    private static string SharedAttachmentOf(string name) => AttachmentsGitExclude.AttachmentPath(name)!;

    /// <summary>
    /// Контроллер от имени зрителя. ifMatch — заголовок If-Match на запросе (ветка «сравнить и
    /// записать» SaveContent); filesOverride — подменный FileService (напр. с записывающим git-швом
    /// для office-discard); без него — общий стендовый FileService, как во всех прежних тестах класса.
    /// </summary>
    private FilesController As(string userId, string projectId, string? ifMatch = null, FileService? filesOverride = null)
    {
        var tenant = _projects.GetById(projectId)!.OwnerId!;
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        };
        if (ifMatch is not null) http.Request.Headers["If-Match"] = ifMatch;
        http.SetRequestScope(new RequestScope(userId, tenant, userId == tenant ? ProjectRole.Owner : ProjectRole.Editor));
        return new FilesController(filesOverride ?? _files, _projects, _access, _sync, _config, _jwt,
            NullLogger<FilesController>.Instance, DocAi(), null!, _locator)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>Ручка без зрителя: в JWT нет sub (машинный шов) — любое private чужое (fail-closed).</summary>
    private FilesController AsNoViewer(string projectId)
    {
        var tenant = _projects.GetById(projectId)!.OwnerId!;
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        http.SetRequestScope(new RequestScope("", tenant, ProjectRole.Editor));
        return new FilesController(_files, _projects, _access, _sync, _config, _jwt,
            NullLogger<FilesController>.Instance, DocAi(), null!, _locator)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static DocumentAiService DocAi() =>
        new(new MarkitdownService(ConfigWithCat(), NullLogger<MarkitdownService>.Instance),
            new Mock<ICheapTextRunner>().Object, ConfigWithCat());

    private static IConfiguration ConfigWithCat() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

    /// <summary>Минимальный IFormFile: контроллер зовёт только FileName/Length/CopyToAsync.</summary>
    private sealed class TestFormFile(string fileName, string content) : IFormFile
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        public string FileName { get; } = fileName;
        public string Name => "file";
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{fileName}\"";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => _bytes.Length;
        public Stream OpenReadStream() => new MemoryStream(_bytes);
        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            OpenReadStream().CopyToAsync(target, cancellationToken);
    }

    // --- 1. каждая пишущая ручка: чужое private — 404 до любого эффекта ---

    [Fact]
    public async Task ЧужоеPrivate_КаждаяПишущаяРучка_404_ВладелецИДругойУчастник()
    {
        var (projectId, root) = Project();
        var foreignDir = ForeignDir();
        var victimFile = $"{foreignDir}/жертва.txt";
        var victimAbs = Write(root, victimFile, "целое содержимое");
        var ordinary = Write(root, "обычный.txt", "обычное");

        foreach (var viewer in new[] { _ownerId, _editorId })
        {
            var c = As(viewer, projectId);
            var why = $"{viewer}: запись в чужое private — 404 до любого эффекта (тот же приём, что чтение)";

            // SaveContent — обе ветки (с If-Match и без) стоят за гейтом: отказ до замка и до записи
            (await c.SaveContent(projectId, victimFile, new SaveContentRequest("новая правка")))
                .Should().BeOfType<NotFoundResult>(why);
            File.ReadAllText(victimAbs).Should().Be("целое содержимое", "отказанная правка не тронула файл жертвы");
            // обход пути не обходит гейт: «./» — тот же чужой private
            (await c.SaveContent(projectId, "./" + victimFile, new SaveContentRequest("новая правка")))
                .Should().BeOfType<NotFoundResult>(why);

            c.CreateFile(projectId, new CreateFileRequest($"{foreignDir}/новый.txt", "контент"))
                .Should().BeOfType<NotFoundResult>(why);
            File.Exists(AbsOf(root, $"{foreignDir}/новый.txt")).Should().BeFalse("создание не дошло до диска");

            c.CreateDir(projectId, new PathRequest($"{AuthorPrivateRoot()}/новая-папка"))
                .Should().BeOfType<NotFoundResult>(why);
            Directory.Exists(AbsOf(root, $"{AuthorPrivateRoot()}/новая-папка"))
                .Should().BeFalse("mkdir не создал каталог под чужим автором");

            // Rename — ОБА пути: исток чужого private…
            (await c.Rename(projectId, new RenameRequest(victimFile, "вынесенный.txt")))
                .Should().BeOfType<NotFoundResult>(why);
            File.Exists(victimAbs).Should().BeTrue();
            File.Exists(AbsOf(root, "вынесенный.txt")).Should().BeFalse("перенос не исполнился");
            // …и цель в чужом private (перенос обычного файла под чужого автора)
            (await c.Rename(projectId, new RenameRequest("обычный.txt", $"{foreignDir}/перенесённый.txt")))
                .Should().BeOfType<NotFoundResult>(why);
            File.Exists(ordinary).Should().BeTrue("исток остался на месте");

            // Delete файла и папки чужого private
            (await c.Delete(projectId, victimFile)).Should().BeOfType<NotFoundResult>(why);
            File.Exists(victimAbs).Should().BeTrue("файл жертвы цел");
            (await c.Delete(projectId, foreignDir)).Should().BeOfType<NotFoundResult>(why);
            Directory.Exists(AbsOf(root, foreignDir)).Should().BeTrue("каталог жертвы цел");
            (await c.Delete(projectId, "%2E%2F" + victimFile)).Should().BeOfType<NotFoundResult>(
                "%-экранированный обход — тот же чужой private");

            // Upload — каталог назначения: отказ до SafeJoin/CreateDirectory, файла нет
            (await c.Upload(projectId, foreignDir, new TestFormFile("новое.txt", "данные")))
                .Should().BeOfType<NotFoundResult>(why);
            File.Exists(AbsOf(root, $"{foreignDir}/новое.txt")).Should().BeFalse();

            // ToMarkdown — targetDir: отказ до работы конвертера и до CreateDirectory цели
            (await c.ToMarkdown(projectId, new ToMarkdownRequest("обычный.txt", $"{AuthorPrivateRoot()}/новая-папка-md"), default))
                .Should().BeOfType<NotFoundResult>(why);
            Directory.Exists(AbsOf(root, $"{AuthorPrivateRoot()}/новая-папка-md"))
                .Should().BeFalse("целевая папка не создана");
            File.Exists(AbsOf(root, "обычный.md")).Should().BeFalse("конвертация не дошла до записи");

            c.Revert(projectId, new PathRequest(victimFile)).Should().BeOfType<NotFoundResult>(
                "отказ — 404, а не BadRequest «не удалось откатить»: чужой файл неотличим от несуществующего");

            // SaveFromUrl — цель под чужим private: 404 до сетевой работы (клиент не создан)
            var factory = new Mock<IHttpClientFactory>();
            (await c.SaveFromUrl(projectId, new SaveFromUrlRequest("https://example.com/x.txt", $"{foreignDir}/скачанный.txt"),
                factory.Object, default)).Should().BeOfType<NotFoundResult>(why);
            factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never,
                "скачивать ответ ради отказанной цели не за чем");
            File.Exists(AbsOf(root, $"{foreignDir}/скачанный.txt")).Should().BeFalse();
        }
    }

    // --- 2. контейнеры closed-поддерева: Rename/Delete — 404 и владельцу ---

    [Fact]
    public async Task Контейнеры_RenameDelete_404_ДажеВладельцу_СодержимоеЦело()
    {
        var (projectId, root) = Project();
        var victimFile = ForeignFile("жертва.txt");
        var victimAbs = Write(root, victimFile, "целое содержимое");
        var shared = SharedAttachmentOf("общее.txt");
        Write(root, shared, "общее");
        Write(root, "docs/внутри.txt", "docs");

        foreach (var caller in new[] { _ownerId, _editorId })
        {
            var c = As(caller, projectId);
            var why = $"{caller}: операция над контейнером относится ко всему private- поддереву — 404, владельцу тоже";

            (await c.Delete(projectId, TreeExcludes.AttachmentsDir)).Should().BeOfType<NotFoundResult>(why);
            (await c.Delete(projectId, $"{TreeExcludes.AttachmentsDir}/private")).Should().BeOfType<NotFoundResult>(why);
            (await c.Delete(projectId, "")).Should().BeOfType<NotFoundResult>(why);
            (await c.Delete(projectId, ".")).Should().BeOfType<NotFoundResult>("«.» — тот же корень");
            (await c.Delete(projectId, "docs/..")).Should().BeOfType<NotFoundResult>("обход к корню — тот же контейнер");

            (await c.Rename(projectId, new RenameRequest(TreeExcludes.AttachmentsDir, "attachments-вынесенные")))
                .Should().BeOfType<NotFoundResult>(why);
            (await c.Rename(projectId, new RenameRequest("docs", TreeExcludes.AttachmentsDir)))
                .Should().BeOfType<NotFoundResult>("цель — контейнер: перенос внутрь закрыт тем же 404");
            (await c.Rename(projectId, new RenameRequest(".", "renamed-root")))
                .Should().BeOfType<NotFoundResult>("корень как исток — тоже контейнер");

            Directory.Exists(AbsOf(root, TreeExcludes.AttachmentsDir)).Should().BeTrue("контейнер цел");
            File.Exists(victimAbs).Should().BeTrue("файл жертвы цел после всех отказов");
            File.Exists(AbsOf(root, "docs/внутри.txt")).Should().BeTrue();
            Directory.Exists(root).Should().BeTrue("корень проекта цел");
            File.Exists(AbsOf(root, "attachments-вынесенные")).Should().BeFalse();
        }
    }

    /// <summary>
    /// Свой {id}-каталог автора и общие {guid}-каталоги — не контейнеры: с ними как раньше
    /// (PrivateAttachments.IsPrivateContainerPath их не считает).
    /// </summary>
    [Fact]
    public async Task СвойКаталогАвтораИОбщийGuid_НеКонтейнеры_КакРаньше()
    {
        var (projectId, root) = Project();
        var ownDir = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}";
        Write(root, $"{ownDir}/своё.txt", "своё");
        var shared = SharedAttachmentOf("общее.txt");
        Write(root, shared, "общее");
        var sharedDir = shared[..shared.LastIndexOf('/')];

        // Автор — свой {id}-каталог: mkdir, rename, удаление папки (в личном проекте автор —
        // владелец, поэтому ветка папки открыта правом, а не скважи)
        var (ownProject, ownRoot) = PersonalProjectOf(_authorId);
        var ownInOwn = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}";
        var a = As(_authorId, ownProject);
        a.CreateDir(ownProject, new PathRequest(ownInOwn)).Should().BeOfType<OkResult>();
        Directory.Exists(AbsOf(ownRoot, ownInOwn)).Should().BeTrue();
        Write(ownRoot, $"{ownInOwn}/файл.txt", "ф");
        (await a.Rename(ownProject, new RenameRequest($"{ownInOwn}/файл.txt", $"{ownInOwn}/файл2.txt")))
            .Should().BeOfType<OkResult>();
        (await a.Rename(ownProject, new RenameRequest(ownInOwn, $"{AuthorPrivateRoot()}/каталог-переименованный")))
            .Should().BeOfType<OkResult>("свой private-каталог автор переносит, как раньше");
        (await a.Delete(ownProject, $"{AuthorPrivateRoot()}/каталог-переименованный"))
            .Should().BeOfType<NoContentResult>("свой private-каталог автор удаляет, как раньше");

        // Общий {guid}-каталог: владельцу перенос открыт (не контейнер), как раньше
        var c = As(_ownerId, projectId);
        (await c.Rename(projectId, new RenameRequest(sharedDir, TreeExcludes.AttachmentsDir + "/moved-guid")))
            .Should().BeOfType<OkResult>();
        Directory.Exists(AbsOf(root, TreeExcludes.AttachmentsDir + "/moved-guid")).Should().BeTrue();

        // Автору в общем проекте — свой файл: 404 нет (своё), а чужой контейнер — 404 даже ему
        var asAuthor = As(_authorId, projectId);
        (await asAuthor.Delete(projectId, $"{ownDir}/своё.txt")).Should().BeOfType<NoContentResult>();
        (await asAuthor.Delete(projectId, $"{TreeExcludes.AttachmentsDir}/private"))
            .Should().BeOfType<NotFoundResult>("контейнер закрыт и автору своего поддерева");
    }

    // --- 3. автор своего private, обычные файлы и общее вложение — как раньше ---

    [Fact]
    public async Task АвторСвоегоPrivate_ОбычныеФайлыИОбщееВложение_КакРаньше()
    {
        var (ownProject, ownRoot) = PersonalProjectOf(_authorId);
        var own = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}/своё.txt";
        var ownDir = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}";
        var ownAbs = Write(ownRoot, own, "своё");
        var plain = Write(ownRoot, "обычный.txt", "обычное");
        var shared = SharedAttachmentOf("общее.png");
        Write(ownRoot, shared, "PNG");

        var a = As(_authorId, ownProject);

        (await a.SaveContent(ownProject, own, new SaveContentRequest("своя правка")))
            .Should().BeOfType<OkResult>("своя запись — ровно как раньше");
        File.ReadAllText(ownAbs).Should().Be("своя правка");
        a.CreateFile(ownProject, new CreateFileRequest($"{AuthorPrivateRoot()}/{Guid.NewGuid():N}/новый.txt", "н"))
            .Should().BeOfType<OkResult>();
        a.CreateDir(ownProject, new PathRequest(ownDir)).Should().BeOfType<OkResult>();
        // rename в тот же каталог: Rename не создаёт каталог цели (прежнее поведение)
        (await a.Rename(ownProject, new RenameRequest(own, own[..own.LastIndexOf('/')] + "/новое-имя.txt")))
            .Should().BeOfType<OkResult>();
        (await a.Upload(ownProject, ownDir, new TestFormFile("загружено.txt", "данные"))).Should().BeOfType<OkResult>();
        File.Exists(AbsOf(ownRoot, $"{ownDir}/загружено.txt")).Should().BeTrue();
        var md = (await a.ToMarkdown(ownProject, new ToMarkdownRequest("обычный.txt", AuthorPrivateRoot()), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((string)md.Value!.GetType().GetProperty("savedPath")!.GetValue(md.Value)!).Should().StartWith(AuthorPrivateRoot(),
            "targetDir в своё private — открыт: гейт смотрит на автора пути, а не на папку");
        a.Revert(ownProject, new PathRequest(own)).Should().BeOfType<BadRequestObjectResult>(
            "своя цель дошла до git-ветки (400 «не git-репозиторий»), а не получила 404");

        // Обычные файлы и общее вложение прежней раскладки — как раньше (в своём проекте)
        (await a.SaveContent(ownProject, "обычный.txt", new SaveContentRequest("правка"))).Should().BeOfType<OkResult>();
        (await a.Upload(ownProject, "", new TestFormFile("up.txt", "u"))).Should().BeOfType<OkResult>();
        (await a.Delete(ownProject, "up.txt")).Should().BeOfType<NoContentResult>();
        (await a.Rename(ownProject, new RenameRequest("обычный.txt", "обычный-2.txt"))).Should().BeOfType<OkResult>();
        (await a.Delete(ownProject, shared)).Should().BeOfType<NoContentResult>("общее вложение — не чужое private");

        // Общий проект: автор-участник пишет в своё private (WriteContent открыта участнику);
        // чужое — 404 и ему; свой каталог-папку удаляет только владелец (прежнее правило F-4, не тронуто)
        var (projectId, root) = Project();
        var ownInShared = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}/своё.txt";
        var ownInSharedAbs = Write(root, ownInShared, "своё в общем");
        // чужое для автора — под другим автором (здесь: участником-редактором)
        var foreignToAuthor = $"{TreeExcludes.AttachmentsDir}/private/{_editorId}/{Guid.NewGuid():N}/чужое.txt";
        var foreignToAuthorAbs = Write(root, foreignToAuthor, "чужое");
        var asAuthor = As(_authorId, projectId);
        (await asAuthor.SaveContent(projectId, ownInShared, new SaveContentRequest("своя правка")))
            .Should().BeOfType<OkResult>();
        File.ReadAllText(ownInSharedAbs).Should().Be("своя правка");
        (await asAuthor.Delete(projectId, ownInShared)).Should().BeOfType<NoContentResult>();
        (await asAuthor.SaveContent(projectId, foreignToAuthor, new SaveContentRequest("чужая правка")))
            .Should().BeOfType<NotFoundResult>("своё — своё, чужое — чужое даже автору-участнику");
        File.ReadAllText(foreignToAuthorAbs).Should().Be("чужое", "файл жертвы цел");
    }

    // --- 4. машинный шов без зрителя: любое private чужое (fail-closed), обычный файл — как раньше ---

    [Fact]
    public async Task БезЗрителя_ЗаписьВЛюбоеPrivate_404_ОбычныйФайлКакРаньше()
    {
        var (projectId, root) = Project();
        var anyPrivate = ForeignFile("файл.txt");
        var plain = Write(root, "обычный.txt", "обычное");
        Write(root, anyPrivate, "содержимое");

        var anon = AsNoViewer(projectId);
        (await anon.SaveContent(projectId, anyPrivate, new SaveContentRequest("правка")))
            .Should().BeOfType<NotFoundResult>("без JWT своего нет: любое private чужое (fail-closed, как IsForeign по пустому id)");
        (await anon.Upload(projectId, $"{AuthorPrivateRoot()}", new TestFormFile("новое.txt", "н")))
            .Should().BeOfType<NotFoundResult>();
        (await anon.Delete(projectId, anyPrivate)).Should().BeOfType<NotFoundResult>();
        File.Exists(AbsOf(root, anyPrivate)).Should().BeTrue();

        (await anon.SaveContent(projectId, "обычный.txt", new SaveContentRequest("правка")))
            .Should().BeOfType<OkResult>("пути вне closed-поддерева — без изменений");
        File.ReadAllText(plain).Should().Be("правка");
    }

    // --- 5. office-discard: чужое private — 404 ДО RevertFile; своё — доходит до git-ветки (хвост R-S2d-6b-5, п.1) ---

    /// <summary>
    /// Раньше инвариант для этой ручки держался только git-исключением .cc-attachments (git не знает
    /// такого pathspec → откат не происходит). Чтобы «отказ ДО RevertFile» был наблюдаем, а не выведен
    /// из кода: корень помечен .git (признак IsGitRepo), а вместо git — записывающий шов. Снятый гейт
    /// (мутация) доводит чужой путь до DiscardAsync — запись попытки делает тест красным.
    /// </summary>
    [Fact]
    public void ЧужоеPrivate_OfficeDiscard_404_ДоRevertFile_СвоёДоходитДоGit()
    {
        var (projectId, root) = Project();
        var victimFile = ForeignFile("жертва.txt");
        var victimAbs = Write(root, victimFile, "целое содержимое");
        var ownFile = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}/своё.txt";
        var ownAbs = Write(root, ownFile, "своё");
        Directory.CreateDirectory(Path.Combine(root, ".git")); // признак git-репо для IsGitRepo

        var git = new RecordingGit();
        var filesWithGit = new FileService(git, _projects, NullLogger<FileService>.Instance);

        foreach (var viewer in new[] { _ownerId, _editorId })
        {
            var c = As(viewer, projectId, filesOverride: filesWithGit);
            c.OfficeDiscard(projectId, victimFile).Should().BeOfType<NotFoundResult>(
                $"{viewer}: office-discard чужого private — 404, а не доведённый до конца «откат не удался»");
            git.DiscardCalls.Should().BeEmpty($"{viewer}: отказ стоит до RevertFile — git-откат не был даже попытан");
            File.ReadAllText(victimAbs).Should().Be("целое содержимое", "отказанный discard не тронул файл жертвы");
        }

        // Своё private автора — как раньше: гейт пропускает, цель доходит до git-ветки
        var asAuthor = As(_authorId, projectId, filesOverride: filesWithGit);
        asAuthor.OfficeDiscard(projectId, ownFile).Should().BeOfType<OkResult>("свой office-discard открыт");
        git.DiscardCalls.Should().Equal(new[] { ownFile }, "своя цель дошла до git-ветки, как до шага");
        File.ReadAllText(ownAbs).Should().Be("своё");
    }

    // --- 6. SaveContent с If-Match — тоже за гейтом (хвост R-S2d-6b-5, п.3) ---

    /// <summary>
    /// Ветка If-Match раньше тестом класса не прогонялась. If-Match берётся ВЕРНЫЙ (тот же
    /// канонический хеш содержимого, что выдаёт GET): без гейта он совпал бы с версией файла и
    /// запись состоялась бы (200, файл изменён) — краснит тест именно снятие гейта, а не подбор
    /// хеша. Своё private под If-Match — открыто: «сравнить и записать» не сломано гейтом.
    /// </summary>
    [Fact]
    public async Task ЧужоеPrivate_SaveContentСIfMatch_404_ФайлЦел_СвойПодIfMatchОткрыт()
    {
        var (projectId, root) = Project();
        var victimFile = ForeignFile("жертва.txt");
        var victimAbs = Write(root, victimFile, "целое содержимое");
        var etag = "\"" + Sha256Hex("целое содержимое") + "\"";

        foreach (var viewer in new[] { _ownerId, _editorId })
        {
            var c = As(viewer, projectId, ifMatch: etag);
            (await c.SaveContent(projectId, victimFile, new SaveContentRequest("новая правка")))
                .Should().BeOfType<NotFoundResult>(
                    $"{viewer}: ветка с If-Match — за тем же гейтом: 404 до сверки хеша и до замка пути");
            File.ReadAllText(victimAbs).Should().Be("целое содержимое", "отказанная If-Match запись не тронула файл");
        }

        var (ownProject, ownRoot) = PersonalProjectOf(_authorId);
        var own = $"{AuthorPrivateRoot()}/{Guid.NewGuid():N}/своё.txt";
        var ownAbs = Write(ownRoot, own, "своё");
        var a = As(_authorId, ownProject, ifMatch: "\"" + Sha256Hex("своё") + "\"");
        (await a.SaveContent(ownProject, own, new SaveContentRequest("своя правка")))
            .Should().BeOfType<OkResult>("своя правка по верному тегу — ровно как раньше");
        File.ReadAllText(ownAbs).Should().Be("своя правка");
    }

    /// <summary>Тот же канонический хеш текста, что считает GET/сверка If-Match (образец Sha256Hex
    /// из FilesControllerTests): UTF-8 без BOM → SHA-256, hex в нижнем регистре.</summary>
    private static string Sha256Hex(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    /// <summary>Записывающий git-шов: RevertFile при наличии git зовёт только DiscardAsync —
    /// список попыток и есть доказательство «дошла операция до отката / не дошла».</summary>
    private sealed class RecordingGit : IGitWorkingTree
    {
        public readonly List<string> DiscardCalls = new();

        public Task DiscardAsync(string? ownerId, string root, string relPath, CancellationToken ct = default)
        {
            DiscardCalls.Add(relPath);
            return Task.CompletedTask;
        }

        public Task<GitStatusDto> StatusPathsAsync(string? ownerId, string root, CancellationToken ct = default) =>
            Task.FromResult(new GitStatusDto(true, null, null, 0, 0, false, [], [], []));

        public Task<string?> DiffFileVsHeadAsync(string? ownerId, string root, string relPath, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
