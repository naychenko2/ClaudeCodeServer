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
/// S3-3b (разрез 4bc3d04f §3): человеческие ручки FilesController после УСПЕШНОЙ правки файла
/// сдают её в журнал правок (IHumanEditJournal, S3-3) — путём файла и автором вызывающего.
///
/// Что здесь проверяется — именно ПЕРЕДАЧА в журнал (шаг S3-3b), а не работа самого журнала:
/// её проверяет HumanEditCommitterTests. Поэтому стенд настоящий, но короткий:
/// - стенд бесхостовый (образец — FilesControllerEditorDenialsTests): контроллер собирается
///   напрямую с настоящими ProjectManager/ProjectAccessService/FileService/GitService и
///   ClaimsPrincipal вызывающего; область запроса ставится явно, как её ставит [ProjectScope];
/// - журнал — НАСТОЯЩИЙ HumanEditCommitter с настоящим git в корняx проектов: «запись →
///   журнал → коммит с автором-человеком» проходит целиком, включая pathspec только
///   записанных путей;
/// - правка 4xx в журнал не попадает: после отказа ни записи, ни коммита.
///
/// Границы, которые решает сам журнал, а не контроллер (личный проект без членов, не-репозиторий,
/// чужой автор) — здесь проверены только как отсутствие эффекта: контроллер их не ветвит.
/// Reader до записи не доходит — его закрывает [ProjectScope] (EditorScopeGate*), в этом стенде
/// фильтр не участвует, поэтому он здесь и не проверяется.
/// </summary>
public sealed class FilesControllerHumanEditJournalTests : IDisposable
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ProjectAccessService _access;
    private readonly GitService _git = new(TestLauncherFactory.Instance);
    private readonly FileService _files;
    private readonly ManualClock _clock = new();
    private readonly HumanEditCommitter _journal;
    private readonly string _ownerId;
    private readonly string _editorId;
    // Последний контекст вызова: чтобы читать ETag, который поставила запись (для ветки If-Match)
    private DefaultHttpContext _lastHttp = new();

    public FilesControllerHumanEditJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s3_3b_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
            ["HumanEdit:DebounceSeconds"] = "20",
            // Конвертер — печать файла как есть: хостовый markitdown не нужен (тот же приём, что в S2c-10)
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat",
        }).Build();

        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s33b-editor", "pwd", "user").Id;
        _users.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _files = new FileService(_git, _projects, NullLogger<FileService>.Instance);
        _journal = new HumanEditCommitter(_projects, _users, _git, _config,
            NullLogger<HumanEditCommitter>.Instance, _clock);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    // --- стенд ---

    /// <summary>Настоящее git-дерево с одним коммитом: «чистые отслеживаемые» файлы.</summary>
    private string GitRepo(string tag, params (string Path, string Content)[] committed)
    {
        var root = Path.Combine(_dir, tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run(root, "init", "-q");
        Run(root, "config", "user.email", "s33b@example.com");
        Run(root, "config", "user.name", "s33b");
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

    /// <summary>Общий проект с git-деревом и одним редактором.</summary>
    private (string ProjectId, string Root) SharedProject(params (string Path, string Content)[] committed)
    {
        var root = GitRepo("shared", committed);
        var id = _projects.Create("S3-3b " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id;
        _projects.SetMembers(id,
        [new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId }]);
        return (id, root);
    }

    private (string ProjectId, string Root) PersonalProject(params (string Path, string Content)[] committed)
    {
        var root = GitRepo("personal", committed);
        return (_projects.Create("S3-3b личный " + Guid.NewGuid().ToString("N")[..6], root, _ownerId,
            _users.GetFirst()!.Username).Id, root);
    }

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
        _lastHttp = http;
        return new FilesController(_files, _projects, _access, null!, _config, null!,
            NullLogger<FilesController>.Instance, DocAi(), null!, null!, git: _git, humanEdits: _journal)
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

    private static string Abs(string root, string rel) => Path.Combine(root, rel);

    // --- чтение состояния журнала и git ---

    /// <summary>Автор, которого журнал держит по пути (до сброса): null — путь в журнал не попал.</summary>
    private string? AuthorOf(string root, string rel) => _journal.PendingAuthor(root, rel)?.UserId;

    private async Task<int> CommitsAsync(string root) =>
        int.Parse((await _git.RunAsync(null, root, ["rev-list", "--count", "HEAD"])).Stdout.Trim());

    /// <summary>Файлы HEAD-коммита как «статус\tпуть», без сведения переименований: перенос
    /// виден двумя строками (D старый, A новый) — именно это и проверяется.</summary>
    private async Task<string[]> HeadPathsAsync(string root) =>
        (await _git.RunAsync(null, root, ["show", "--name-status", "--no-renames", "--format=", "HEAD"]))
        .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> HeadBodyAsync(string root) =>
        (await _git.RunAsync(null, root, ["log", "-1", "--format=%an %ae %B"])).Stdout;

    /// <summary>Сбросить буфер коммитом (окно дебаунса не ждём — тот же приём, что в HumanEditCommitterTests).</summary>
    private Task FlushAsync() => _journal.FlushAsync(all: true);

    // --- п.1: SaveContent / create / upload / to-markdown ---

    // Мутация шага («убрать Record в Upload») краснит этот тест: без передачи в журнала нет
    // ни автора по пути, ни коммита с автором-человеком.
    [Fact]
    public async Task Загрузка_ПослеУспеха_ПопадаетВЖурналСАвторомВызывающего_ИКоммититсяЕгоПравкой()
    {
        var (projectId, root) = SharedProject(("up.txt", "база"));   // чистый отслеживаемый
        var before = await CommitsAsync(root);

        (await As(_editorId, projectId).Upload(projectId, "", FormFile("up.txt", "правка редактора")))
            .Should().BeOfType<OkResult>();
        AuthorOf(root, "up.txt").Should().Be(_editorId, "успешная загрузка — правка человека, автор из JWT");

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().Equal("M\tup.txt");
        (await HeadBodyAsync(root)).Should().Contain($"CCS-User: {_editorId}");
        (await CommitsAsync(root)).Should().Be(before + 1);
        AuthorOf(root, "up.txt").Should().BeNull("после сброса буфера автор больше не «висящий»");
    }

    [Fact]
    public async Task СохранениеБезТегаИПоНазванномуТегу_ИСозданиеФайла_ПопадаютВЖурналСАвторомУчастника()
    {
        var (projectId, root) = SharedProject(("plain.txt", "база"), ("tagged.txt", "база"));

        (await As(_editorId, projectId).SaveContent(projectId, "plain.txt", new SaveContentRequest("правка"))).Should().BeOfType<OkResult>();
        AuthorOf(root, "plain.txt").Should().Be(_editorId);

        // Ветка If-Match — отдельная строка записи: по тому тегу, который отдала запись того же файла
        (await As(_editorId, projectId).SaveContent(projectId, "tagged.txt", new SaveContentRequest("версия1"))).Should().BeOfType<OkResult>();
        var etag = _lastHttp.Response.Headers["ETag"].ToString();
        etag.Should().NotBeEmpty("запись обязана отдать тег своей версии");
        (await As(_editorId, projectId, etag).SaveContent(projectId, "tagged.txt", new SaveContentRequest("версия2"))).Should().BeOfType<OkResult>();
        AuthorOf(root, "tagged.txt").Should().Be(_editorId);
        File.ReadAllText(Abs(root, "tagged.txt")).Should().Be("версия2");

        As(_editorId, projectId).CreateFile(projectId, new CreateFileRequest("created.txt", "новый")).Should().BeOfType<OkResult>();
        AuthorOf(root, "created.txt").Should().Be(_editorId);

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().BeEquivalentTo(["A\tcreated.txt", "M\tplain.txt", "M\ttagged.txt"]);
        (await HeadBodyAsync(root)).Should().Contain($"CCS-User: {_editorId}");
    }

    // --- доработка по ревью e5f6c10c: ключ журнала — канонический путь от корня ---

    // Ручка получает путь как его назвал клиент («sub/../x.txt»), файл пишется по каноническому
    // пути, и в журнал правка уходит тем же каноническим путём: иначе алиас не совпадает с
    // буквальным pathspec git (GitService.CommitAsync) и правка человека уходит автору-агенту.
    // Мутация «убрать нормализацию в HumanEditCommitter» краснит эту строку: ключ остался бы
    // «sub/../x.txt», и AuthorOf по «x.txt» — null.
    [Fact]
    public async Task СохранениеПоАлиасуПути_ЖурналКлючуетКаноническийПуть_ИКоммититПравкуЧеловека()
    {
        var (projectId, root) = SharedProject(("x.txt", "база"));   // чистый отслеживаемый
        var before = await CommitsAsync(root);

        (await As(_editorId, projectId).SaveContent(projectId, "sub/../x.txt", new SaveContentRequest("правка"))).Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "x.txt")).Should().Be("правка", "файл — по каноническому пути");

        AuthorOf(root, "x.txt").Should().Be(_editorId, "алиас пути — та же правка, что канонический путь");

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().Equal("M\tx.txt");
        (await HeadBodyAsync(root)).Should().Contain($"CCS-User: {_editorId}");
        (await CommitsAsync(root)).Should().Be(before + 1);
        AuthorOf(root, "x.txt").Should().BeNull("после сброса буфера автор больше не «висящий»");
    }

    // Три записи одного файла по разным формам пути — ОДНА запись журнала и ОДНИ коммит:
    // «одна запись» наблюдаема швом BeforeClaim (он вызывается на каждую снимаемую запись),
    // поэтому мутация «убрать нормализацию» краснит и этот тест (появятся ключи «./x.txt»,
    // «sub/../x.txt» рядом с «x.txt»).
    [Fact]
    public async Task ТотЖеФайлПоРазнымФормамПути_ОднаЗаписьЖурналаИОдинКоммит()
    {
        var (projectId, root) = SharedProject(("x.txt", "база"));
        var before = await CommitsAsync(root);
        var claimed = new List<string>();
        _journal.BeforeClaim = k => claimed.Add(k.Rel);

        (await As(_editorId, projectId).SaveContent(projectId, "x.txt", new SaveContentRequest("первая"))).Should().BeOfType<OkResult>();
        (await As(_editorId, projectId).SaveContent(projectId, "./x.txt", new SaveContentRequest("вторая"))).Should().BeOfType<OkResult>();
        (await As(_editorId, projectId).SaveContent(projectId, "sub/../x.txt", new SaveContentRequest("третья"))).Should().BeOfType<OkResult>();

        // Все три формы дают один и тот же ключ: автор один, и чтение по любой форме его находит.
        AuthorOf(root, "x.txt").Should().Be(_editorId);
        AuthorOf(root, "./x.txt").Should().Be(_editorId, "чтение по алиасу — тот же ключ");
        AuthorOf(root, "sub/../x.txt").Should().Be(_editorId);

        await FlushAsync();
        claimed.Should().Equal(new[] { "x.txt" }, "три записи одного пути — одна запись журнала");
        (await CommitsAsync(root)).Should().Be(before + 1, "три записи одного пути — один коммит");
        (await HeadPathsAsync(root)).Should().Equal("M\tx.txt");
        File.ReadAllText(Abs(root, "x.txt")).Should().Be("третья");
        (await _git.RunAsync(null, root, ["status", "--porcelain"])).Stdout.Should().BeEmpty("после коммита в дереве нечего");
    }

    // to-markdown пишет ЦЕЛЕВОЙ .md — в журнал идёт он; источник, который не менялся, — нет.
    [Fact]
    public async Task ToMarkdown_ВЖурналЦелевойМд_ИсточникНеИдёт()
    {
        var (projectId, root) = SharedProject(("base.txt", "база"));
        File.WriteAllText(Abs(root, "doc.txt"), "содержимое источника");   // untracked: его никто не правил

        var ok = (await As(_editorId, projectId).ToMarkdown(projectId, new ToMarkdownRequest("doc.txt"), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        Body(ok.Value).GetProperty("savedPath").GetString().Should().Be("doc.md");

        AuthorOf(root, "doc.md").Should().Be(_editorId);
        AuthorOf(root, "doc.txt").Should().BeNull("источник не записывался — его правки нет");

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().Equal("A\tdoc.md");
    }

    // --- п.1: rename — старый и новый путь ---

    [Fact]
    public async Task ПереносФайла_ВЖурналСтарыйИНовыйПуть_КоммитЗакрываетОба()
    {
        var (projectId, root) = SharedProject(("a.txt", "содержимое"));
        var before = await CommitsAsync(root);

        (await As(_editorId, projectId).Rename(projectId, new RenameRequest("a.txt", "moved.txt"))).Should().BeOfType<OkResult>();
        AuthorOf(root, "a.txt").Should().Be(_editorId, "старый путь — удаление, оно тоже правка");
        AuthorOf(root, "moved.txt").Should().Be(_editorId);

        await FlushAsync();
        // порядок строк в выводе git не часть контракта — сравниваем множество
        (await HeadPathsAsync(root)).Should().BeEquivalentTo("D\ta.txt", "A\tmoved.txt");
        (await CommitsAsync(root)).Should().Be(before + 1);
    }

    // --- п.1: удаление файла и папки ---

    [Fact]
    public async Task УдалениеФайла_ПопадаетВЖурналКакПравка_ВладельцуИУчастнику()
    {
        var (projectId, root) = SharedProject(("gone.txt", "база"));
        var before = await CommitsAsync(root);

        (await As(_editorId, projectId).Delete(projectId, "gone.txt")).Should().BeOfType<NoContentResult>();
        AuthorOf(root, "gone.txt").Should().Be(_editorId);

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().Equal("D\tgone.txt");
        (await CommitsAsync(root)).Should().Be(before + 1);
    }

    // Папка — СПИСКОМ файлов внутри, а не путём папки: pathspec папки затянул бы в коммит и
    // чужую незакоммиченную работу под ней (оговорка в HumanEditCommitter).
    [Fact]
    public async Task УдалениеПапкиВладельцем_ВЖурналСпискомФайловАНеПутёмПапки()
    {
        var (projectId, root) = SharedProject(
            ("sub/inner.txt", "внутри"), ("sub/nested/deep.txt", "глубоко"), ("keep.txt", "база"));
        File.WriteAllText(Abs(root, "sub/agent.txt"), "чужая работа, не коммичена");
        var before = await CommitsAsync(root);

        (await As(_ownerId, projectId).Delete(projectId, "sub")).Should().BeOfType<NoContentResult>();

        AuthorOf(root, "sub").Should().BeNull("путь папки журналу не сдаётся");
        AuthorOf(root, "sub/inner.txt").Should().Be(_ownerId);
        AuthorOf(root, "sub/nested/deep.txt").Should().Be(_ownerId);
        AuthorOf(root, "keep.txt").Should().BeNull("за папку отвечают только её файлы");

        await FlushAsync();
        var paths = await HeadPathsAsync(root);
        paths.Should().Contain("D\tsub/inner.txt").And.Contain("D\tsub/nested/deep.txt");
        paths.Should().NotContain(p => p.EndsWith("\tsub"));
        (await CommitsAsync(root)).Should().Be(before + 1);
    }

    [Fact]
    public async Task ПереносПапкиВладельцем_СтарыйИНовыйПутьКаждогоФайлаАНеПутьПапки()
    {
        var (projectId, root) = SharedProject(("sub/inner.txt", "внутри"));
        var before = await CommitsAsync(root);

        (await As(_ownerId, projectId).Rename(projectId, new RenameRequest("sub", "renamed"))).Should().BeOfType<OkResult>();

        AuthorOf(root, "sub").Should().BeNull();
        AuthorOf(root, "sub/inner.txt").Should().Be(_ownerId);
        AuthorOf(root, "renamed/inner.txt").Should().Be(_ownerId, "вложенность нового пути сохраняется");

        await FlushAsync();
        (await HeadPathsAsync(root)).Should().BeEquivalentTo("D\tsub/inner.txt", "A\trenamed/inner.txt");
        (await CommitsAsync(root)).Should().Be(before + 1);
    }

    // --- п.1: отказ — правки не было, в журнале нечего ---

    [Fact]
    public async Task Отказ4xx_ВЖурналНеПопадаетИКоммитаНеСтановится()
    {
        var (projectId, root) = SharedProject(("dirty.txt", "база"), ("taken.txt", "база"));
        File.AppendAllText(Abs(root, "dirty.txt"), " + чужая правка, не коммичена");
        var before = await CommitsAsync(root);
        var caller = As(_editorId, projectId);

        // Загрузка на незакоммиченный файл — 409 file_uncommitted
        (await caller.Upload(projectId, "", FormFile("dirty.txt", "поверх"))).Should().BeOfType<ConflictObjectResult>();
        // Удаление незакоммиченного — 409
        (await caller.Delete(projectId, "dirty.txt")).Should().BeOfType<ConflictObjectResult>();
        // rename на существующую цель — 409 exists
        (await caller.Rename(projectId, new RenameRequest("taken.txt", "dirty.txt"))).Should().BeOfType<ConflictObjectResult>();
        // create на существующий файл — 409
        caller.CreateFile(projectId, new CreateFileRequest("taken.txt", "x")).Should().BeOfType<ConflictObjectResult>();

        AuthorOf(root, "dirty.txt").Should().BeNull();
        AuthorOf(root, "taken.txt").Should().BeNull();
        await FlushAsync();
        (await CommitsAsync(root)).Should().Be(before, "отказанная правка не порождает коммита");
        File.ReadAllText(Abs(root, "dirty.txt")).Should().Be("база + чужая правка, не коммичена", "отказы не тронули файлы");
    }

    // --- границы: личный проект — по правилу журнала (без членов он молчит), без эффекта для ручки ---

    [Fact]
    public async Task ЛичныйПроект_РучкаРаботаетКакРаньше_ЖурналМолчитПоСвоемуПравилу()
    {
        var (projectId, root) = PersonalProject(("mine.txt", "база"));
        var before = await CommitsAsync(root);

        (await As(_ownerId, projectId).Upload(projectId, "", FormFile("mine.txt", "правка владельца")))
            .Should().BeOfType<OkResult>();
        File.ReadAllText(Abs(root, "mine.txt")).Should().Be("правка владельца");

        AuthorOf(root, "mine.txt").Should().BeNull("проект без членов — молчаливый no-op журнала");
        await FlushAsync();
        (await CommitsAsync(root)).Should().Be(before);
    }

    // Тело ответа читаем в той же casing, что отдаёт MVC (JsonSerializerDefaults.Web): иначе
    // тест спорит с именованием сериализатора, а не с контрактом ручки.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static JsonElement Body(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement;

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
