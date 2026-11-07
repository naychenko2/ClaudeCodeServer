using System.Linq;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
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
/// S2d-6b-2 (разрез 4bc3d04f §1 путь 19): чужие приватные вложения (раскладка S2d-6b-1 —
/// .cc-attachments/private/{автор}/{guid}/{имя}) закрыты в читающих ручках FilesController и
/// не попадают в Search/Locate.
///
/// 1. Каждая читающая ручка (content, stream, diff, document/convert|summary|extract|tags,
///    document/to-markdown по источнику, office-config, office-version, office-download по
///    владельцу токена) для чужого private — 404, а не 403: «нет такого файла» неотличим от
///    чужого приватного (тот же приём, что GetOwnedSession на чужом приватном чата). Автор своего
///    private, общее вложение прежней раскладки и обычный файл — как раньше.
/// 2. Search и Locate: чужие private-выпадаются из выдачи ни именем, ни количеством; автор своего
///    находит. В FileService.Search фильтр стоит до Take(100) — 150 чужих не съедают слоты своих.
/// 3. Дерево и листинг: вся папка .cc-attachments скрыта из корневого обхода TreeExcludes — и чужое
///    private, и своё (служебная папка, не «чужая»; поведение не менялось). TreeExcludes режет ОБХОД,
///    а не явно названный путь: прямая ссылка внутрь .cc-attachments отдаёт содержимое — поэтому
///    доработкой 1 (ревью f24f7533) записи по названному пути фильтруются по зрителю: чужого —
///    ни имён, ни id автора, ни количества; названный чужой private-путь — 404; без зрителя —
///    fail-closed.
///
/// Стенд бесхостовый (образец FilesControllerEditorDenialsTests): контроллер собирается напрямую
/// с настоящими ProjectManager/ProjectAccessService/FileService/ProjectTreeLocator/JwtService и
/// ClaimsPrincipal зрителя; область запроса ставится явно, как её ставит [ProjectScope].
/// Три разных человека: владелец проекта, другой его участник и автор приватного вложения —
/// «чужой» здесь действительно другой человек, а не повторный вызов тем же.
/// </summary>
public sealed class FilesControllerPrivateAttachmentsReadTests : IDisposable
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

    public FilesControllerPrivateAttachmentsReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s2d6b2_read_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
            // Конвертер — печать файла как есть (тот же приём, что в FilesControllerEditorDenialsTests):
            // хостовый markitdown не нужен; на 404-отказе он и не зовётся
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2d6b2-editor", "pwd", "user").Id;
        _authorId = _users.Add("s2d6b2-author", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _authorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _sync = new SyncService(_config);
        // git в этом шаге не нужен: ни одна проверяемая ветка до него не доходит (отказ идёт
        // до него), а не-git корень FileService читает штатно (GetGitStatus его пропускает)
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
        var id = _projects.Create("S2d6b2 " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
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
        return (_projects.Create("S2d6b2 личный " + Guid.NewGuid().ToString("N")[..6], root, ownerId,
            _users.GetById(ownerId)!.Username).Id, root);
    }

    private static string Write(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return abs;
    }

    /// <summary>Путь приватного вложения — та же сборка, что у записи (PrivateAttachments.Build).</summary>
    private static string PrivateOf(string authorId, string name) =>
        PrivateAttachments.Build(authorId, Guid.NewGuid(), name)!;

    /// <summary>Общее вложение чата — прежняя раскладка .cc-attachments/{guid}/{имя}.</summary>
    private static string SharedAttachmentOf(string name) => AttachmentsGitExclude.AttachmentPath(name)!;

    private FilesController As(string userId, string projectId)
    {
        var tenant = _projects.GetById(projectId)!.OwnerId!;
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        };
        http.SetRequestScope(new RequestScope(userId, tenant, userId == tenant ? ProjectRole.Owner : ProjectRole.Editor));
        return new FilesController(_files, _projects, _access, _sync, _config, _jwt,
            NullLogger<FilesController>.Instance, DocAi(), null!, _locator)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>Ручка без зрителя: в JWT нет sub (машинный шов) — area та же, человека нет.</summary>
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

    private static List<string> PathsOf(IActionResult result) =>
        ((IEnumerable<FileEntry>)result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Select(e => e.Path).ToList();

    // --- 1. каждая читающая ручка: чужое private — 404 ---

    [Fact]
    public async Task ЧужоеPrivate_КаждаяЧитающаяРучка_404_ВладелецИДругойУчастник()
    {
        var (projectId, root) = Project();
        var doc = PrivateOf(_authorId, "отчёт.pdf");
        var text = PrivateOf(_authorId, "заметка.txt");
        var img = PrivateOf(_authorId, "фото.png");
        Write(root, doc, "%PDF-1.4 чужой документ");
        Write(root, text, "чужое содержимое");
        Write(root, img, "не png, но имя — картинка");

        foreach (var viewer in new[] { _ownerId, _editorId })
        {
            var c = As(viewer, projectId);
            var why = $"{viewer}: чужое private-вложение в читающей ручке — 404, а не 403";

            (await c.GetContent(projectId, text)).Should().BeOfType<NotFoundResult>(why);
            (await c.GetContent(projectId, img)).Should().BeOfType<NotFoundResult>(why);
            (await c.GetContent(projectId, doc)).Should().BeOfType<NotFoundResult>(why);
            (await c.Stream(projectId, text)).Should().BeOfType<NotFoundResult>(why);
            (await c.GetDiff(projectId, text)).Should().BeOfType<NotFoundResult>(why);
            (await c.DocumentConvert(projectId, doc, default)).Should().BeOfType<NotFoundResult>(why);
            (await c.DocumentSummary(projectId, doc, default)).Should().BeOfType<NotFoundResult>(why);
            (await c.DocumentExtract(projectId, doc, default)).Should().BeOfType<NotFoundResult>(why);
            (await c.DocumentTags(projectId, doc, default)).Should().BeOfType<NotFoundResult>(why);
            (await c.ToMarkdown(projectId, new ToMarkdownRequest(doc), default)).Should().BeOfType<NotFoundResult>(why);
            c.OfficeVersion(projectId, doc).Should().BeOfType<NotFoundResult>(why);
            c.OfficeConfig(projectId, doc).Should().BeOfType<NotFoundResult>(why);

            // office-download: зрителя нет (анонимный вход), но токен выдал конкретный человек —
            // автор; для владельца и редактора файл остаётся чужым
            var token = _jwt.IssueOfficeToken(_authorId, projectId, doc);
            c.OfficeDownload(projectId, doc, token).Should().BeOfType<NotFoundResult>(why);

            // обход пути не обходит гейт: «./» и %-экранированный «./» — тот же чужой private
            (await c.GetContent(projectId, "./" + text)).Should().BeOfType<NotFoundResult>(why);
            (await c.GetContent(projectId, "%2E%2F" + text)).Should().BeOfType<NotFoundResult>(why);
        }
    }

    // --- 1. автор своего private, общее вложение и обычный файл — как раньше ---

    [Fact]
    public async Task АвторСвоегоPrivate_ОбщееВложениеИОбычныйФайл_КакРаньше()
    {
        // Личный проект автора: он здесь владелец, поэтому office-двери (Write-двери
        // [ProjectScope]) открыты по праву, а не сквозь отсутствующий в бесхостовом стенде атрибут
        var (ownProject, ownRoot) = PersonalProjectOf(_authorId);
        var own = PrivateOf(_authorId, "своё.txt");
        var shared = SharedAttachmentOf("общее.png");
        var plain = "обычный.txt";
        Write(ownRoot, own, "своё содержимое");
        Write(ownRoot, shared, "PNG-bytes");
        Write(ownRoot, plain, "обычное содержимое");

        var a = As(_authorId, ownProject);

        var ownView = (await a.GetContent(ownProject, own)).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)ownView.Value!).Content.Should().Be("своё содержимое",
            "своё private читается ровно как раньше: гейт смотрит на автора пути, а не на папку");
        (await a.Stream(ownProject, own)).Should().BeOfType<PhysicalFileResult>();
        a.OfficeVersion(ownProject, own).Should().BeOfType<OkObjectResult>();
        a.OfficeConfig(ownProject, own).Should().BeOfType<OkObjectResult>();
        var token = _jwt.IssueOfficeToken(_authorId, ownProject, own);
        a.OfficeDownload(ownProject, own, token).Should().BeOfType<PhysicalFileResult>(
            "анонимный office-download меряется владельцем токена: своё — открыто, иначе свой документ нельзя открыть в редакторе");

        (await a.GetContent(ownProject, shared)).Should().BeOfType<OkObjectResult>(
            "общая раскладка .cc-attachments/{guid}/… этим шагом не тронута");
        var plainView = (await a.GetContent(ownProject, plain)).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)plainView.Value!).Content.Should().Be("обычное содержимое");

        // Общий проект: участник-автор своего читает, чужое — 404 («своё» — только своё)
        var (projectId, root) = Project();
        var ownInShared = PrivateOf(_authorId, "своё-в-общем.txt");
        var foreignToAuthor = PrivateOf(_editorId, "чужое-для-автора.txt");
        Write(root, ownInShared, "своё в общем");
        Write(root, foreignToAuthor, "чужое");

        var c = As(_authorId, projectId);
        var ownSharedView = (await c.GetContent(projectId, ownInShared)).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)ownSharedView.Value!).Content.Should().Be("своё в общем");
        (await c.Stream(projectId, ownInShared)).Should().BeOfType<PhysicalFileResult>();
        (await c.GetContent(projectId, foreignToAuthor)).Should().BeOfType<NotFoundResult>();
    }

    // --- 2. Search: чужого нет, своё и обычный есть, количество не утекает ---

    [Fact]
    public void Поиск_ЧужоеPrivateНеОтдаётся_СвоёИОбычныйЕсть_КоличествоНеУтекает()
    {
        var (projectId, root) = Project();
        // 150 чужих private-вложений с общим префиксом имени: если фильтр стоял бы ПОСЛЕ
        // Take(100), своё и обычный молча выпадали бы из выдачи по порядку обхода
        var foreignDir = $"{TreeExcludes.AttachmentsDir}/private/{_authorId}/{Guid.NewGuid():N}";
        for (var i = 1; i <= 150; i++) Write(root, $"{foreignDir}/отчёт-{i:D3}.txt", "чужое");
        var own = PrivateOf(_editorId, "отчёт-своё.txt");
        var plain = "отчёт-обычный.txt";
        Write(root, own, "своё");
        Write(root, plain, "обычное");

        PathsOf(As(_editorId, projectId).Search(projectId, "отчёт-"))
            .Should().BeEquivalentTo(new[] { own, plain },
                "чужие private не дают ни имён, ни количества: 150 чужих не съедают слоты своих");

        PathsOf(As(_ownerId, projectId).Search(projectId, "отчёт-")).Should().Equal(plain);

        // по точному имени чужого вложения: чужим — пусто, автору — есть
        var oneForeign = $"{foreignDir}/отчёт-001.txt";
        PathsOf(As(_editorId, projectId).Search(projectId, "отчёт-001")).Should().BeEmpty();
        PathsOf(As(_authorId, projectId).Search(projectId, "отчёт-001")).Should().Equal(oneForeign);
    }

    /// <summary>
    /// Машинный шов (ProjectFiles.SearchAsync → FileService.Search без зрителя: актора в
    /// контракте IProjectFiles нет) — fail-closed: любое private закрыто, обычный файл
    /// находится. Чтение вложений агентом — шаг S2d-6b-3, не этот.
    /// </summary>
    [Fact]
    public void МашинныйШовБезЗрителя_PrivateЗакрытоВообще_ОбычныйФайлНаходится()
    {
        var root = Root();
        Write(root, PrivateOf(_authorId, "своё.txt"), "своё");
        Write(root, "обычное.txt", "обычное");

        _files.Search(root, "своё").Select(e => e.Path).Should().BeEmpty();
        _files.Search(root, "обычное").Select(e => e.Path).Should().Equal("обычное.txt");
    }

    // --- 2. Locate: чужого нет в ответе ---

    [Fact]
    public async Task Locate_ЧужоеPrivateНеВОтвете_СвоёИОбычныйЕсть()
    {
        var (projectId, root) = Project();
        var foreign = PrivateOf(_authorId, "чужое.png");
        var own = PrivateOf(_editorId, "своё.png");
        var plain = "обычное.txt";
        Write(root, foreign, "чужое");
        Write(root, own, "своё");
        Write(root, plain, "обычное");

        var res = (await As(_editorId, projectId).Locate(projectId, new LocateRequest([foreign, own, plain], null), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((IReadOnlyList<LocatedPath>)res.Value!).Select(p => p.Path)
            .Should().BeEquivalentTo(new[] { own, plain },
                "чужой путь выпадает из запроса до локатора: в ответе нет ни его имени, ни его количества");

        var forOwner = (await As(_ownerId, projectId).Locate(projectId, new LocateRequest([foreign, own, plain], null), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((IReadOnlyList<LocatedPath>)forOwner.Value!).Select(p => p.Path).Should().Equal(plain);
    }

    // --- 3. дерево и листинг: TreeExcludes режет обход; названный путь внутри .cc-attachments
    //        фильтруется по зрителю (доработка 1, ревью f24f7533) ---

    [Fact]
    public void ДеревоИЛистинг_ЯвныйПутьВЧужомPrivate_404_ЧужихЗаписейНет_АвторСвойВидит()
    {
        var (projectId, root) = Project();
        var authorDir = $"{TreeExcludes.AttachmentsDir}/private/{_authorId}";
        var editorDir = $"{TreeExcludes.AttachmentsDir}/private/{_editorId}";
        var foreignFile = $"{authorDir}/чужое.txt";
        var ownFile = $"{editorDir}/своё.txt";
        var shared = SharedAttachmentOf("общее.txt");
        var sharedDir = shared[..shared.LastIndexOf('/')];
        Write(root, foreignFile, "чужое");
        Write(root, ownFile, "своё");
        Write(root, shared, "общее");
        Write(root, "visible.txt", "видимое");

        // Корневой обход: вся папка .cc-attachments скрыта TreeExcludes — и чужое private, и своё
        // (служебная папка, не «чужая»); showHidden=true её тоже не открывает: делает это именно
        // TreeExcludes, а не правило скрытых файлов. Это поведение не менялось
        foreach (var viewer in new[] { _ownerId, _authorId })
        {
            var c = As(viewer, projectId);
            var why = $"{viewer}: вся папка {TreeExcludes.AttachmentsDir} скрыта из корневого обхода TreeExcludes, а не правилом private";
            PathsOf(c.List(projectId, "")).Should().Contain("visible.txt");
            PathsOf(c.List(projectId, "")).Should().NotContain(p => p.StartsWith(TreeExcludes.AttachmentsDir), why);
            PathsOf(c.Tree(projectId, "", true)).Should().Contain("visible.txt");
            PathsOf(c.Tree(projectId, "", true)).Should().NotContain(p => p.StartsWith(TreeExcludes.AttachmentsDir), why);
        }

        // TreeExcludes режет ОБХОД, а не явно названный путь: родитель private/, названный прямо,
        // отдаёт своё содержимое — отфильтрованное по зрителю. Прежняя версия теста закрепляла
        // утечку (каталог чужого автора в List); доработка 1 переписывает на противоположное:
        // участник видит только свой каталог — ни имён чужих, ни id автора, ни их количества,
        // и showHidden=true этого не меняет
        var asEditor = As(_editorId, projectId);
        PathsOf(asEditor.List(projectId, $"{TreeExcludes.AttachmentsDir}/private"))
            .Should().Equal(new[] { editorDir }, "участник .cc-attachments/private: только свой каталог");
        PathsOf(asEditor.Tree(projectId, $"{TreeExcludes.AttachmentsDir}/private", true))
            .Should().Equal(new[] { editorDir, ownFile }, "showHidden=true: чужое поддерево не открывается");
        PathsOf(asEditor.Tree(projectId, TreeExcludes.AttachmentsDir, true))
            .Should().Contain(new[] { editorDir, ownFile })
            .And.NotContain(p => p.StartsWith(authorDir), "в дереве от названного контейнера чужое private-поддерево тоже закрыто");

        // Явно названный чужой private-путь — 404, как читающие ручки: ни каталог автора, ни его
        // файл не отличимы от несуществующего
        asEditor.List(projectId, authorDir).Should().BeOfType<NotFoundResult>("чужой каталог автора — 404, а не пустой список");
        asEditor.Tree(projectId, authorDir, true).Should().BeOfType<NotFoundResult>();
        asEditor.List(projectId, foreignFile).Should().BeOfType<NotFoundResult>();

        // Сам контейнер — не «чужой» путь: .cc-attachments перечисляется, чужой автор отфильтрован
        // в нём; общая раскладка guid — как раньше
        PathsOf(asEditor.List(projectId, TreeExcludes.AttachmentsDir))
            .Should().BeEquivalentTo(new[] { $"{TreeExcludes.AttachmentsDir}/private", sharedDir },
                "контейнер не закрыт: в нём только своё зрителя");

        // Автор своего — как раньше: и родитель, и своё поддерево
        var asAuthor = As(_authorId, projectId);
        PathsOf(asAuthor.List(projectId, $"{TreeExcludes.AttachmentsDir}/private")).Should().Equal(new[] { authorDir });
        PathsOf(asAuthor.Tree(projectId, $"{TreeExcludes.AttachmentsDir}/private", true)).Should().Equal(new[] { authorDir, foreignFile });

        // Владелец проекта без своего private: пустой список — ни id чужих авторов, ни их количества
        PathsOf(As(_ownerId, projectId).List(projectId, $"{TreeExcludes.AttachmentsDir}/private"))
            .Should().BeEmpty("количество чужого не утекает через список");

        // Поиск по имени чужого: участнику пуст, автору — своё (как раньше)
        PathsOf(asEditor.Search(projectId, "чужое")).Should().BeEmpty();
        PathsOf(asAuthor.Search(projectId, "чужое")).Should().Equal(foreignFile);

        // Без зрителя (в JWT нет sub — машинный шов ручки): fail-closed — родитель пуст,
        // названный private-путь 404; контейнер и общая раскладка — как раньше
        var anon = AsNoViewer(projectId);
        PathsOf(anon.List(projectId, $"{TreeExcludes.AttachmentsDir}/private"))
            .Should().BeEmpty("без зрителя любое private закрыто");
        anon.List(projectId, authorDir).Should().BeOfType<NotFoundResult>();
        anon.Tree(projectId, authorDir, true).Should().BeOfType<NotFoundResult>();
        PathsOf(anon.List(projectId, TreeExcludes.AttachmentsDir))
            .Should().BeEquivalentTo(new[] { $"{TreeExcludes.AttachmentsDir}/private", sharedDir });
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
