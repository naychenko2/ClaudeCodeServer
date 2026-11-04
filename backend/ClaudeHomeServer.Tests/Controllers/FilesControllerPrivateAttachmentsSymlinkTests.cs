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
