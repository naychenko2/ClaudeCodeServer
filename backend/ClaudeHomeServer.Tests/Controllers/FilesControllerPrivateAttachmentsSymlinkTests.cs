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
/// Хвост ревью 6db670ff (к bf664b319): на читающих/пишущих ручках FilesController не было ни
/// одного теста через символьную ссылку (grep CreateSymbolicLink в Tests/Controllers был пуст).
/// Здесь — тот же контракт, что у прямого пути, но имя идёт через ссылку «L» → .cc-attachments
/// в корне проекта: чужое private через неё — 404 на чтение и отказ на запись ДО эффекта
/// (файл жертвы цел), своё через L — 200 ровно как прямым именем (гейт смотрит на автора
/// пути, а не на форму имени). 403 на запись — контракт WebDAV (WebDavPrivateAttachmentTests);
/// в FilesController запись чужого private — 404 (S2d-6b-5), здесь проверяется он.
///
/// TAIL-ROOTS-GATE (DEC-ROOTS 5c8a4705) добавил здесь два guard'а на новой формуле чужести
/// (сегмент канона, а не префикс относительно корня): чужое private в worktree ПОД корнем
/// проекта — 404 и своему автору там же — 200; легальная ссылка наружу на не-private путь —
/// чтение и запись проходят (отвергнутый вариант «канон обязан остаться под корнем»).
///
/// Стенд бесхостовый (образец FilesControllerPrivateAttachmentsReadTests). Ссылка на каталог
/// требует привилегий на Windows, поэтому тест идёт только на Linux (образец
/// PrivateAttachmentsResolvedTests).
/// </summary>
public sealed class FilesControllerPrivateAttachmentsSymlinkTests : IDisposable
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
    private readonly string _editorId;  // другой участник проекта, автор своего вложения
    private readonly string _authorId;  // автор чужого приватного вложения, участник проекта

    public FilesControllerPrivateAttachmentsSymlinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s2d6b2_symlink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("symlink-editor", "pwd", "user").Id;
        _authorId = _users.Add("symlink-author", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _authorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _sync = new SyncService(_config);
        // git не нужен: ни одна проверяемая ветка до него не доходит (отказ стоит до него)
        _files = new FileService(null, _projects, NullLogger<FileService>.Instance);
        _locator = new ProjectTreeLocator(TestLauncherFactory.Instance, NullLogger<ProjectTreeLocator>.Instance);
        _jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    // --- стенд ---

    /// <summary>Общий проект (владелец + два участника) с рабочей папкой на диске, без git.</summary>
    private (string ProjectId, string Root) Project()
    {
        var root = Path.Combine(_dir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var id = _projects.Create("SymLink " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(id,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _authorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);
        return (id, root);
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

    /// <summary>Тот же путь через ссылку «L» → .cc-attachments: префикс каталога заменён на «L»
    /// (ссылка УЖЕ есть .cc-attachments, двойной сегмент давал бы 404 отсутствующего пути).</summary>
    private static string ViaLink(string rel) => "L/" + rel[(TreeExcludes.AttachmentsDir.Length + 1)..];

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

    private static DocumentAiService DocAi() =>
        new(new MarkitdownService(ConfigWithCat(), NullLogger<MarkitdownService>.Instance),
            new Mock<ICheapTextRunner>().Object, ConfigWithCat());

    private static IConfiguration ConfigWithCat() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

    // --- контракт через ссылку ---

    /// <Named мутации: «IsForeignResolved судит чужость префиксом относительно корня
    /// (прежняя формула)» краснит этот тест: файл чужого автора в worktree ПОД корнем
    /// проекта (segments[0]="worktrees", канон не под private/ корня) читался бы 200.</Named>
    [Fact]
    public async Task ВложенныйWorktreeПодКорнем_ЧужоеPrivate_404_СвоёАвтору_200()
    {
        var (projectId, root) = Project();
        // защитный кейс: worktree, созданный ВНУТРИ корня. Штатные деревья чатов и слотов
        // ревью лежат вне главного дерева: {home}/.worktrees/<проект>/<ветка>
        // (SessionManager.WorktreesBase — SessionManager.cs:3012; ReviewTreePool.cs:49 — та же
        // база), поэтому эта раскладка продуктовой не является. private образуется там сам,
        // без всякой символьной ссылки — та же дыра DEC-ROOTS (5c8a4705): прежняя формула
        // по префиксу её не ловит, сегментная линия — ловит
        var foreign = "worktrees/x/" + PrivateOf(_authorId, "чужое.txt");
        Write(root, foreign, "чужое во вложенном дереве");
        var own = "worktrees/x/" + PrivateOf(_editorId, "своё.txt");
        Write(root, own, "своё во вложенном дереве");

        var c = As(_editorId, projectId);
        (await c.GetContent(projectId, foreign)).Should().BeOfType<NotFoundResult>(
            "чужое private на глубине ниже корня — тот же 404, что у прямого .cc-attachments/private/{автор}");
        var ownView = (await c.GetContent(projectId, own)).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)ownView.Value!).Content.Should().Be("своё во вложенном дереве",
            "свой автор во вложенном дереве — своё: сегмент канона судится по автору, а не по глубине");

        var asAuthor = As(_authorId, projectId);
        var theirView = (await asAuthor.GetContent(projectId, foreign)).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)theirView.Value!).Content.Should().Be("чужое во вложенном дереве",
            "автор читает своё private в worktree, как и в корне проекта");
    }

    /// <Named мутации: «предикат отказывает любой канон, вышедший за opRoot (отвергнутый
    /// вариант «канон обязан остаться под корнем»)» краснит этот тест: легальная ссылка
    /// наружу на не-private путь — чтение и запись — отказались бы вместе с
    /// frontend/node_modules → primary checkout из worktree-setup.sh.</Named>
    [Fact]
    public async Task СсылкаНаружуНаНеPrivate_ЧтениеИЗаписьПроходят()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий (образец)
        var (projectId, root) = Project();
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        File.WriteAllText(Path.Combine(outside, "x.txt"), "внешнее содержимое");
        Directory.CreateSymbolicLink(Path.Combine(root, "out"), outside); // тот же класс, что node_modules → checkout

        var c = As(_ownerId, projectId);
        var view = (await c.GetContent(projectId, "out/x.txt")).Should().BeOfType<OkObjectResult>().Subject;
        ((FileContentView)view.Value!).Content.Should().Be("внешнее содержимое",
            "ссылка наружу сама по себе — не причина отказывать: чужость судится по сегменту, а не по месту канона");
        (await c.SaveContent(projectId, "out/x.txt", new SaveContentRequest("правка"))).Should().BeOfType<OkResult>();
        File.ReadAllText(Path.Combine(outside, "x.txt")).Should().Be("правка", "запись через легальную ссылку дошла до цели");
    }

    /// <Named мутации: «гейт FilesController текстовый (IsForeignResolved заменён на IsForeign)»
    /// краснит этот тест: чужое private через «L» читается и перечисляется, чужой каталог
    /// автора через ссылку перестаёт быть 404.</Named>
    [Fact]
    public async Task ЧужоеPrivateЧерезСимлинкВКорне_Чтение404_ЗаписьОтказана_СвоёЧерезL_200()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий (образец PrivateAttachmentsResolvedTests)
        var (projectId, root) = Project();
        Directory.CreateSymbolicLink(Path.Combine(root, "L"), TreeExcludes.AttachmentsDir);

        var foreign = PrivateOf(_authorId, "чужое.txt");
        var foreignAbs = Write(root, foreign, "чужое содержимое");

        foreach (var viewer in new[] { _ownerId, _editorId })
        {
            var own = PrivateOf(viewer, "своё.txt");
            Write(root, own, "своё содержимое");

            var c = As(viewer, projectId);
            var why = $"{viewer}: симлинок в корне не обходит канонический гейт: чужое private через «L» — тот же путь, что прямым именем";

            // чтение через ссылку — 404, как прямым именем: существования чужого не раскрывает
            (await c.GetContent(projectId, ViaLink(foreign))).Should().BeOfType<NotFoundResult>(why);
            (await c.Stream(projectId, ViaLink(foreign))).Should().BeOfType<NotFoundResult>(why);
            c.List(projectId, $"L/private/{_authorId}").Should().BeOfType<NotFoundResult>(
                "каталог чужого автора через ссылку — тот же 404, а не список содержимого");

            // запись через ссылку — отказ ДО эффекта (контракт FilesController — 404, S2d-6b-5;
            // 403 на запись — контракт WebDAV, проверен в WebDavPrivateAttachmentTests)
            var newRel = $"L/private/{_authorId}/{Guid.NewGuid():N}/новый.txt";
            c.CreateFile(projectId, new CreateFileRequest(newRel, "к")).Should().BeOfType<NotFoundResult>(why);
            File.Exists(Path.Combine(root, newRel)).Should().BeFalse("создание не дошло до диска");
            (await c.SaveContent(projectId, ViaLink(foreign), new SaveContentRequest("чужая правка")))
                .Should().BeOfType<NotFoundResult>(why);
            File.ReadAllText(foreignAbs).Should().Be("чужое содержимое", "отказанная запись через ссылку не тронула файл жертвы");

            // своё через L — 200 ровно как прямым именем: «L/» — та же цель на диске
            var ownView = (await c.GetContent(projectId, ViaLink(own))).Should().BeOfType<OkObjectResult>().Subject;
            ((FileContentView)ownView.Value!).Content.Should().Be("своё содержимое",
                "своё через ссылку читается так же, как прямым именем: гейт смотрит на автора пути, а не на форму имени");
        }
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
