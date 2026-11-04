using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Knowledge;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-6b-6 (хвост S2d-6b-5, доработка по ревью R-S2d-6b-5): IndexFile/IndexFolder не индексируют
/// чужие приватные вложения (раскладка S2d-6b-1 — .cc-attachments/private/{автор}/{guid}/{имя})
/// в базу знаний проекта. Прежняя версия звала FileService.Tree БЕЗ зрителя: фильтр FileService
/// работает только при viewerUserId is not null, а названный .cc-attachments / .cc-attachments/private
/// отдавал содержимое всего поддерева — чужое private попадало в датасет, доступный всем.
///
/// 1. Зритель (id из JWT) передаётся в обход: чужое private-поддерево выпадает из результата тем же
///    фильтром FileService.Tree, что у читающих ручек (S2d-6b-2 доработка 1).
/// 2. Явно названный чужой private (каталог автора или файл) — 404 до индексации/обхода, неотличим от
///    несуществующего — тот же приём, что у ручек FilesController. Своё private — индексируется как раньше.
/// 3. Сами контейнеры .cc-attachments и .cc-attachments/private — 404: индексация контейнера относится
///    ко всему closed- поддереву (тот же разбор, что Rename/Delete, S2d-6b-3 доработка 2). Корень
///    проекта — НЕ контейнер для индексации (обход TreeExcludes в .cc-attachments не спускается):
///    индексация корня и любых папок — как раньше, поэтому предикат без корня (IsAttachmentsContainer).
/// 4. Обычные папки и общая раскладка .cc-attachments/{guid}/… — без изменений.
/// 5. (хвост ревью R-S2d-6b-6) Белый тест на шов передачи зрителя: подменный FileService пишет
///    переданный viewerUserId обхода — тест требует id вызывающего; мутация «снять аргумент»
///    краснит этот тест (поведенческие тесты его не ловят: из разрешённых стартов без ссылки
///    фильтр чужого private недостижим).
/// 6. (дефект d07d7054) Обход через символьную ссылку в контейнер («L» → .cc-attachments):
///    содержимое чужих приватных не уходит в датасет — его режет канонический префикс обхода
///    (FileService.ClosedPrefixOf), тот же гейт, что у читающих ручек; обычные файлы, своё
///    private и общая раскладка — без изменений.
///
/// Стенд бесхостовый (образец FilesControllerPrivateAttachmentsReadTests): контроллер собирается
/// напрямую с настоящими ProjectManager/FileService/WorkspaceKnowledgeStore/ProjectKnowledgeSyncService;
/// Dify — фейковый HttpMessageHandler, записывающий вызовы: счётчик синка — имена документов,
/// созданных create_by_text. Три разных человека: владелец проекта, другой его участник и автор
/// приватного вложения — «чужой» здесь действительно другой человек, а не повторный вызов тем же.
/// </summary>
public sealed class KnowledgeControllerPrivateAttachmentsIndexTests : IDisposable
{
    // Фейковый Dify (тот же образец, что ProjectKnowledgeSyncServiceTests): create dataset → ds-1,
    // create_by_text → doc-N с именем из тела, GET documents → пусто. Все вызовы записываются —
    // по ним и считается, что реально ушло в синк знаний.
    private sealed class FakeDifyHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Calls = new();
        private int _docSeq;

        public void Clear() => Calls.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path, body));

            if (request.Method == HttpMethod.Post && path == "/v1/datasets")
                return Json("{\"id\":\"ds-1\"}");
            if (request.Method == HttpMethod.Get && path == "/v1/datasets")
                return Json("{\"data\":[],\"has_more\":false,\"total\":0}");
            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString();
                return Json($"{{\"document\":{{\"id\":\"doc-{++_docSeq}\",\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/documents"))
                return Json("{\"data\":[],\"has_more\":false,\"total\":0}");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly FileService _files;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly KnowledgeService _knowledge;
    private readonly ProjectKnowledgeSyncService _sync;
    private readonly FakeDifyHandler _dify = new();
    private readonly IHubContext<SessionHub> _hub;
    private readonly string _ownerId;   // владелец проекта, НЕ автор вложения
    private readonly string _editorId;  // другой участник проекта, НЕ автор вложения
    private readonly string _authorId;  // автор приватного вложения, участник проекта

    public KnowledgeControllerPrivateAttachmentsIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s2d6b6_index_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2d6b6-editor", "pwd", "user").Id;
        _authorId = _users.Add("s2d6b6-author", "pwd", "user").Id;

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        // git в этом шаге не нужен: ни одна проверяемая ветка до него не доходит,
        // а не-git корень FileService читает штатно (GetGitStatus его пропускает)
        _files = new FileService(null, _projects, NullLogger<FileService>.Instance);
        _wkStore = new WorkspaceKnowledgeStore(_config);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        _knowledge = new KnowledgeService(factory.Object,
            Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
            _wkStore);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        _hub = hub.Object;

        _sync = new ProjectKnowledgeSyncService(_knowledge, _wkStore, _projects, new ProjectFileGateway(_files),
            new NoopKnowledgeNotifier(), new Helpers.NullDifyMetrics(),
            NullLogger<ProjectKnowledgeSyncService>.Instance);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    // --- стенд ---

    /// <summary>Общий проект (владелец + два участника) с рабочей папкой на диске, без git.</summary>
    private (string ProjectId, string Root) Project()
    {
        var root = Path.Combine(_dir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var id = _projects.Create("S2d6b6 " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(id,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _authorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);
        return (id, root);
    }

    private static void Write(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    /// <summary>Путь приватного вложения — та же сборка, что у записи (PrivateAttachments.Build).</summary>
    private static string PrivateOf(string authorId, string name) =>
        PrivateAttachments.Build(authorId, Guid.NewGuid(), name)!;

    /// <summary>Общее вложение чата — прежняя раскладка .cc-attachments/{guid}/{имя}.</summary>
    private static string SharedAttachmentOf(string name) => AttachmentsGitExclude.AttachmentPath(name)!;

    /// <summary>Контроллер от имени зрителя; filesOverride — подменный FileService
    /// (записывающий обход для теста на шов), без него — общий стендовый.</summary>
    private KnowledgeController As(string userId, string projectId, FileService? filesOverride = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        };
        return new KnowledgeController(_projects, _knowledge, filesOverride ?? _files, _wkStore, _sync, _hub)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>Подменный FileService: пишет каждый переданный viewerUserId обхода Tree
    /// (шов — virtual-метод, см. комментарий у FileService.Tree, S2d-6b-8).</summary>
    private sealed class RecordingTreeFileService : FileService
    {
        public readonly List<string?> Viewers = new();

        public RecordingTreeFileService(IProjectManager projects)
            : base(null, projects, NullLogger<FileService>.Instance) { }

        public override IEnumerable<FileEntry> Tree(string rootPath, string relativePath, bool showHidden,
            string? viewerUserId)
        {
            Viewers.Add(viewerUserId);
            return base.Tree(rootPath, relativePath, showHidden, viewerUserId);
        }
    }

    /// <summary>Что реально ушло в синк знаний: имена документов create_by_text (сбрасывается между вызовами).</summary>
    private List<string> IndexedNames() =>
        _dify.Calls.Where(c => c.Method == "POST" && c.Path.Contains("/document/create_by_text"))
            .Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString()!)
            .ToList();

    // --- 1. корень проекта: обычные файлы индексируются, любое private (чужое и своё) — нет:
    //        обход TreeExcludes в .cc-attachments не спускается, это поведение не менялось ---

    [Fact]
    public async Task ИндексацияКорня_ОбычныеФайлыИндексуются_ЛюбоеPrivateНеУходит()
    {
        var (projectId, root) = Project();
        var plain = "docs/обычный.md";
        Write(root, plain, "обычное");
        Write(root, PrivateOf(_authorId, "своё-для-автора.md"), "своё");
        Write(root, PrivateOf(_editorId, "своё-для-редактора.md"), "своё");
        Write(root, SharedAttachmentOf("общее.md"), "общее");

        foreach (var viewer in new[] { _authorId, _editorId, _ownerId })
        {
            _dify.Clear();
            var res = await As(viewer, projectId).IndexFolder(projectId, new IndexFileRequest(""));
            res.Should().BeOfType<OkObjectResult>($"{viewer}: индексация корня — штатная цель, 200");
            IndexedNames().Should().Equal(new[] { plain },
                $"{viewer}: обход TreeExcludes не спускается в {TreeExcludes.AttachmentsDir}: ни чужое, ни своё private не индексируются — как раньше");
        }
    }

    // --- 2. свой private-каталог: индексируется как раньше (и через обход «..», ведущий к своему) ---

    [Fact]
    public async Task ИндексацияСвоегоPrivateКаталога_СвоёИндексируется()
    {
        var (projectId, root) = Project();
        var ownDir = $"{TreeExcludes.AttachmentsDir}/private/{_authorId}";
        var ownFile = PrivateOf(_authorId, "своё.md");
        var foreignFile = PrivateOf(_editorId, "чужое.md");
        Write(root, ownFile, "своё");
        Write(root, foreignFile, "чужое");

        _dify.Clear();
        var res = await As(_authorId, projectId).IndexFolder(projectId, new IndexFileRequest(ownDir));
        res.Should().BeOfType<OkObjectResult>("свой каталог автора — не контейнер и не чужой: индексация как раньше");
        IndexedNames().Should().Equal(new[] { ownFile }, "чужое поддерево не съедает индексацию своего: ни имени, ни количества");

        // обход «..» в названном пути: ведёт к своему каталогу — он свой, не чужой (тот же
        // характер нормализации, что у IsForeign: отказ ставится по каноническому пути)
        _dify.Clear();
        var viaTraversal = await As(_authorId, projectId).IndexFolder(projectId,
            new IndexFileRequest($"{TreeExcludes.AttachmentsDir}/private/{_editorId}/../{_authorId}"));
        viaTraversal.Should().BeOfType<OkObjectResult>();
        IndexedNames().Should().Equal(new[] { ownFile });
    }

    // --- 3. явно названный чужой private (каталог автора, файл, обход к чужому) — 404 до обхода,
    //        в синк не уходит ничего ---

    [Fact]
    public async Task ИндексацияЧужогоPrivateКаталогаИФайла_404_НичегоНеИндексируется()
    {
        var (projectId, root) = Project();
        var authorDir = $"{TreeExcludes.AttachmentsDir}/private/{_authorId}";
        var foreignFile = PrivateOf(_authorId, "чужое.md");
        Write(root, foreignFile, "чужое");
        Write(root, "обычное.md", "обычное");

        var c = As(_editorId, projectId);
        (await c.IndexFolder(projectId, new IndexFileRequest(authorDir)))
            .Should().BeOfType<NotFoundResult>("чужой каталог автора — 404, а не обход чужого поддерева");
        (await c.IndexFolder(projectId, new IndexFileRequest(foreignFile)))
            .Should().BeOfType<NotFoundResult>("чужой файл — 404, неотличим от несуществующего");
        (await c.IndexFolder(projectId, new IndexFileRequest($"{TreeExcludes.AttachmentsDir}/private/{_editorId}/../{_authorId}")))
            .Should().BeOfType<NotFoundResult>("обход «..» ведёт к чужому каталогу — 404 по каноническому пути");
        IndexedNames().Should().BeEmpty("отказ идёт до обхода: в синк не уходит ни чужое private, ни что-либо ещё");
    }

    // --- 4. сами контейнеры .cc-attachments и .cc-attachments/private — 404; корень (в любых
    //        формах записи) — не контейнер: индексация всего проекта работает как раньше ---

    [Fact]
    public async Task ИндексацияКонтейнеров_404_КореньНеКонтейнер()
    {
        var (projectId, root) = Project();
        var plain = "docs/обычный.md";
        Write(root, plain, "обычное");
        Write(root, PrivateOf(_authorId, "своё.md"), "своё");
        Write(root, PrivateOf(_editorId, "чужое.md"), "чужое");

        var c = As(_authorId, projectId);
        (await c.IndexFolder(projectId, new IndexFileRequest(TreeExcludes.AttachmentsDir)))
            .Should().BeOfType<NotFoundResult>("индексация контейнера относится ко всему private- поддереву: 404, как Rename/Delete (S2d-6b-3 доработка 2)");
        (await c.IndexFolder(projectId, new IndexFileRequest($"{TreeExcludes.AttachmentsDir}/private")))
            .Should().BeOfType<NotFoundResult>();
        IndexedNames().Should().BeEmpty();

        // корень — штатная цель IndexFolder: IsPrivateContainerPath считает его контейнером,
        // здесь предикат без корня; все формы записи корня индексируют как раньше
        foreach (var rootForm in new[] { "", ".", "docs/.." })
        {
            _dify.Clear();
            (await c.IndexFolder(projectId, new IndexFileRequest(rootForm)))
                .Should().BeOfType<OkObjectResult>($"корень в форме «{rootForm}» — не контейнер для индексации");
            IndexedNames().Should().Equal(new[] { plain }, "индексация всего проекта — как раньше: только обычные файлы");
        }
    }

    // --- 5. обычная папка и общая раскладка .cc-attachments/{guid}/… — как раньше ---

    [Fact]
    public async Task ИндексацияОбычнойПапкиИОбщегоВложения_КакРаньше()
    {
        var (projectId, root) = Project();
        var a = "docs/а.md";
        var b = "docs/под/б.md";
        var shared = SharedAttachmentOf("общее.md");
        var sharedDir = shared[..shared.LastIndexOf('/')];
        Write(root, a, "а");
        Write(root, b, "б");
        Write(root, shared, "общее");

        _dify.Clear();
        (await As(_editorId, projectId).IndexFolder(projectId, new IndexFileRequest("docs")))
            .Should().BeOfType<OkObjectResult>();
        IndexedNames().Should().BeEquivalentTo(new[] { a, b }, "обычная папка — без изменений");

        _dify.Clear();
        (await As(_editorId, projectId).IndexFolder(projectId, new IndexFileRequest(sharedDir)))
            .Should().BeOfType<OkObjectResult>();
        IndexedNames().Should().Equal(new[] { shared }, "общая раскладка .cc-attachments/{guid}/… этим шагом не тронута");
    }

    // --- 6. IndexFile (одиночный файл по названному пути): чужое private — 404 до индексации,
    //        своё и обычный файл — как раньше ---

    [Fact]
    public async Task ИндексацияФайла_ЧужоеPrivate_404_СвоёИОбычныйКакРаньше()
    {
        var (projectId, root) = Project();
        // «чужое» для редактора и «своё» для автора — один и тот же автор вложения: другой человек,
        // а не повторный вызов тем же
        var authorFile = PrivateOf(_authorId, "своё-для-автора.md");
        var foreignToEditor = PrivateOf(_authorId, "чужое-для-редактора.md");
        var plain = "обычное.md";
        Write(root, authorFile, "своё");
        Write(root, foreignToEditor, "чужое");
        Write(root, plain, "обычное");

        var editor = As(_editorId, projectId);
        _dify.Clear();
        (await editor.IndexFile(projectId, new IndexFileRequest(foreignToEditor)))
            .Should().BeOfType<NotFoundResult>("чужое private — 404 до индексации, неотличимо от несуществующего файла");
        IndexedNames().Should().BeEmpty();

        var author = As(_authorId, projectId);
        (await author.IndexFile(projectId, new IndexFileRequest(authorFile)))
            .Should().BeOfType<OkObjectResult>("своё private индексируется ровно как раньше");
        IndexedNames().Should().Equal(new[] { authorFile });

        (await author.IndexFile(projectId, new IndexFileRequest(plain)))
            .Should().BeOfType<OkObjectResult>();
        IndexedNames().Should().Equal(new[] { authorFile, plain }, "обычный файл — без изменений");
    }

    // --- 7. белый тест на шов: обход IndexFolder идёт от имени зрителя (хвост ревью R-S2d-6b-6) ---

    /// <summary>
    /// Фильтр чужого private в FileService.Tree работает только при непустом зрителе, а из
    /// разрешённых IndexFolder стартов он недостижим: обход ниже .cc-attachments не спускается
    /// (TreeExcludes), названный чужой путь ловит гейт. Поэтому снятие аргумента UserId не краснит
    /// ни один поведенческий тест (проба А ревьюера R-S2d-6b-6: 6/6 зелёные), и строка передачи
    /// зрителя держалась без теста. Этот тест — на сам шов: подменный FileService пишет переданного
    /// зрителя, тест требует id вызывающего в каждой обходящей ветке. Мутация «снять аргумент
    /// UserId из files.Tree» краснит этот тест; поведение при этом не меняется — и не должно.
    /// </summary>
    [Fact]
    public async Task ИндексацияПередаётЗрителяВОбход_TreeПолучаетНепустойIdВызывающего()
    {
        var (projectId, root) = Project();
        var plain = "docs/обычный.md";
        var ownFile = PrivateOf(_authorId, "своё.md");
        Write(root, plain, "обычное");
        Write(root, ownFile, "своё");
        Write(root, PrivateOf(_editorId, "чужое.md"), "чужое");

        var recorder = new RecordingTreeFileService(_projects);
        var c = As(_authorId, projectId, recorder);

        // Штатная цель индексации — корень проекта: обход обязан идти от имени зрителя
        _dify.Clear();
        (await c.IndexFolder(projectId, new IndexFileRequest(""))).Should().BeOfType<OkObjectResult>();
        recorder.Viewers.Should().Equal(new[] { _authorId },
            "обход корня передан с id зрителя: фильтр чужого private включён");
        IndexedNames().Should().Equal(new[] { plain }, "поведение не изменилось: только обычные файлы");

        // Вторая штатная цель — свой private-каталог автора: тот же зритель, своё индексируется
        _dify.Clear();
        (await c.IndexFolder(projectId, new IndexFileRequest($"{TreeExcludes.AttachmentsDir}/private/{_authorId}")))
            .Should().BeOfType<OkObjectResult>();
        recorder.Viewers.Should().Equal(new[] { _authorId, _authorId },
            "обход своего каталога — тоже от имени зрителя");
        IndexedNames().Should().Equal(new[] { ownFile }, "своё индексируется, чужое — нет");
    }

    // --- 8. (дефект d07d7054) индексация через символьную ссылку в контейнер: содержимое
    //        чужих приватных вложений не уходит в датасет; обычные файлы и своё — как раньше ---

    /// <summary>
    /// Обход FileService был текстовым: «L» → .cc-attachments в корне проекта ни не исключён по
    /// имени (имя «L»), ни не отфильтрован (первый сегмент «L») — обход спускался в настоящий
    /// .cc-attachments, и IndexFolder корня относил L/private/{чужой}/… в датасет, доступный
    /// всем: утечка содержимого, а не имён. Гейт названного пути (пп. 2–3 этого файла) был
    /// каноническим (шаг 0859cdb5) и названный «L» отбивал; за пределами названного пути —
    /// обход (FileService.Tree, канонический префикс на переход ссылки).
    /// </summary>
    [Fact]
    public async Task ИндексацияКорняЧерезСсылку_СодержимоеЧужихПриватныхНеИндексируется()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий
        var (projectId, root) = Project();
        var plain = "docs/обычный.md";
        var (foreignGuid, ownGuid, sharedGuid) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var foreign = $"{TreeExcludes.AttachmentsDir}/private/{_authorId}/{foreignGuid}/чужое.md";
        var own = $"{TreeExcludes.AttachmentsDir}/private/{_editorId}/{ownGuid}/своё.md";
        var shared = $"{TreeExcludes.AttachmentsDir}/{sharedGuid}/общее.md";
        // та же запись относительно ссылки L: то, что обязан (не)относить обход корня
        static string ViaL(string rel) => "L/" + rel[(TreeExcludes.AttachmentsDir.Length + 1)..];
        Write(root, plain, "обычное");
        Write(root, foreign, "чужое");
        Write(root, own, "своё");
        Write(root, shared, "общее");
        Directory.CreateSymbolicLink(Path.Combine(root, "L"), TreeExcludes.AttachmentsDir);

        var asEditor = As(_editorId, projectId);

        _dify.Clear();
        (await asEditor.IndexFolder(projectId, new IndexFileRequest("")))
            .Should().BeOfType<OkObjectResult>("корень — штатная цель индексации");
        IndexedNames().Should().NotContain(p => p.Contains($"/private/{_authorId}/"),
            "через ссылку содержимое чужих приватных не индексируется: ни чужого id, ни его файлов");
        IndexedNames().Should().BeEquivalentTo(new[] { plain, ViaL(shared), ViaL(own) },
            "обход не стал пустым: обычные файлы — как прежде; своё и общая раскладка через ссылку " +
            "видны так же, как от названного контейнера (п. 5) — их фильтр не касается");

        // Тот же контейнер через ссылку — тот же отказ, что прямым именем (п. 4): и «L», и «L/private»
        (await asEditor.IndexFolder(projectId, new IndexFileRequest("L")))
            .Should().BeOfType<NotFoundResult>("«L» → .cc-attachments — тот же контейнер на диске");
        (await asEditor.IndexFolder(projectId, new IndexFileRequest("L/private")))
            .Should().BeOfType<NotFoundResult>();

        // Явно названный чужой каталог через ссылку — 404 (тот же путь, что прямым именем, п. 3)
        (await asEditor.IndexFolder(projectId, new IndexFileRequest($"L/private/{_authorId}")))
            .Should().BeOfType<NotFoundResult>();

        // Свой каталог через ссылку — индексируется своё, чужое под ним не достигается
        _dify.Clear();
        (await asEditor.IndexFolder(projectId, new IndexFileRequest($"L/private/{_editorId}")))
            .Should().BeOfType<OkObjectResult>("свой каталог автора — не контейнер и не чужой, и через ссылку");
        IndexedNames().Should().Equal(new[] { ViaL(own) });
    }

    private sealed class NoopKnowledgeNotifier : IKnowledgeHubNotifier
    {
        public Task BroadcastKnowledgeChangedAsync(string userId, string action, string? datasetId) =>
            Task.CompletedTask;
    }
}
