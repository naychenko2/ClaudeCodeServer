using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// F2-P1b (дефект 3ad0be60, тот же класс и вердикт 892deb00, что у закрытой REST-ручки 3c531d5e8):
/// читающий MCP git_diff не должен отдавать содержимое чужого private. Для неотслеживаемого файла
/// DiffFileAsync фолбэчит на `git diff --no-index` (GitService.cs:296-300) без путевого гейта, а путь
/// .cc-attachments/private/{author}/… несёт id автора и guid вложения: ToPrivate сохраняет guid
/// (PrivateAttachments.cs:43-50), значит он известен каждому, кто видел вложение, пока чат был общим.
///
/// Гейт — общий канонический предикат (PrivateAttachments.IsForeignResolved), тот же, что держит
/// соседние точки тулсета (files_read:890, knowledge_index:1279) и REST-ручку (GitController.cs:125):
/// по ФАКТИЧЕСКОМУ корню операции (MCP-ветка не резолвит worktree — root = p.RootPath, :1334),
/// до обращения к git. Чужесть — по сегменту closed-поддерева в каноне на любой глубине (DEC-ROOTS
/// 5c8a4705), поэтому и ссылка на корень чужого проекта не раскрывает чужого содержимого. Отказ —
/// текст тот же, что у других путевых гейтов тулсета: наличия чужого private не раскрываем.
///
/// Актор — из реестра хода (тот же шов, что у files_*, :838): подменный ITurnActorRegistry
/// (образец McpFilesPrivateAttachmentTests:199), вызов через CallAsync с чатом-вызывателем в хвосте
/// маршрута. Актор неизвестен (null) — любое private чужое (fail-closed). Легальные пути не тронуты:
/// своё private, общая раскладка {guid}/…, новый файл, правка трекаемого, легальная ссылка наружу
/// на не-private.
///
/// Право актора на сам инструмент (git_diff — Read) здесь не проверяется: оно предмет
/// ActorRightGateTests; в фикстуре гейт прав актора сквозной, чтобы тест судил ровно путевой гейт.
/// Рамка — настоящая формула продукта (TurnFrame.Resolve): общий чат общего проекта — Narrow всем.
/// </summary>
public sealed class McpGitDiffPrivacyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectRoot;
    private readonly string _otherRoot;
    private readonly WorkspaceToolset _toolset;
    private readonly Actors _actors = new();
    private readonly string _ownerId;
    private readonly string _authorId; // автор чужого приватного вложения, участник (Editor)
    private readonly string _projectId;

    private const string AgentChatId = "agent-chat";

    private readonly Guid _foreignGuid = Guid.NewGuid();
    private readonly Guid _ownGuid = Guid.NewGuid();
    private readonly Guid _commonGuid = Guid.NewGuid();
    private readonly Guid _otherForeignGuid = Guid.NewGuid();

    // Раскладка файлов на диске (S2d-6b-1): id авторов — реальные id пользователей
    private string _foreignPath = ""; // чужое private (автор — _authorId)
    private string _ownPath = "";     // своё private (автор — _ownerId)
    private string _commonPath = "";  // общее вложение (прежняя раскладка {guid}/{имя})
    private const string PlainPath = "docs/plain.txt";

    // Чужое private ЧЕРЕЗ ссылку «other» на корень чужого проекта: первым сегментом имени ссылки
    // текстовая линия чужести не видит — её обязан закрыть канон разыменованного пути
    private string ForeignThroughLink => $"other/{TreeExcludes.AttachmentsDir}/private/{_authorId}/{_otherForeignGuid:N}/other-secret.txt";
    private const string ThroughLinkPlain = "other/docs/plain.txt";

    public McpGitDiffPrivacyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_git_diff_priv_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_tempDir, "project");
        _otherRoot = Path.Combine(_tempDir, "other-project");
        Directory.CreateDirectory(_projectRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = users.GetFirst()!.Id;
        _authorId = users.Add("git-diff-author", "pwd", "user").Id;
        users.SetFeatureFlag(_authorId, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _projectId = projects.Create("Общий проект", _projectRoot, _ownerId, users.GetFirst()!.Username).Id;
        projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _authorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        var personas = new PersonaManager(config);
        // Исполнительская специальность — та, что у агента общего проекта по продукту: ей git-секция
        // дана пресетом на ЧТЕНИЕ (PersonaBindingsService.SectionOrigin/SpecialtySections)
        var personaId = personas.Create(_ownerId, "Персона проекта", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false,
            specialty: PersonaSpecialty.BackendExecutor).Id;

        List<Session> seed =
        [
            new Session { Id = AgentChatId, ProjectId = _projectId, PersonaId = personaId,
                Status = SessionStatus.Finished },
        ];
        File.WriteAllText(Path.Combine(_tempDir, "sessions.json"), JsonSerializer.Serialize(seed));

        var sessions = CreateSessionManager(config, appSettings, users, projects, personas);
        var access = new ProjectAccessService(projects, sessions, null);

        WriteFixtureFiles();

        _toolset = new WorkspaceToolset(
            projects, null!, sessions, personas, new FileService(), null!, null!, null!, null!, null!,
            // git — живой сервис на настоящем git CLI: именно его no-index-фолбэк и отдаёт чужое
            new GitService(TestLauncherFactory.Instance), null!, users, null!, null!, null!, null!, null!,
            null!, null!, null!, access, new RealFrame(projects, sessions, _actors), _actors,
            new AllowAllGate());
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Живой SessionManager без процессов и LLM-вызовов (образец McpFilesPrivateAttachmentTests:155)
    private SessionManager CreateSessionManager(IConfiguration config, AppSettingsService appSettings,
        UserStore users, ProjectManager projects, PersonaManager personas)
    {
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var jwt = new JwtService(config, users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(personas, projects, wkStore, knowledge,
            new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(projects, new ChatHistoryService(config), config, adapters,
            new FalCostService(new Mock<IHttpClientFactory>().Object, config), new UsageService(config),
            appSettings, users, jwt, server.Object, llmProviders, new FeatureFlagService(users),
            personas, bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    // Репозиторий + раскладка вложений на диске. commited — только docs/plain.txt: всё остальное
    // остаётся неотслеживаемым, ровно тот путь DiffFileAsync, где содержимое и утекает
    private void WriteFixtureFiles()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "docs"));
        RunGit("init", "-q", "-b", "main");
        File.WriteAllText(Abs(PlainPath), "основа\n");
        RunGit("-c", "user.name=тест", "-c", "user.email=test@test", "add", PlainPath);
        RunGit("-c", "user.name=тест", "-c", "user.email=test@test", "commit", "-q", "-m", "начальный");
        File.WriteAllText(Abs(PlainPath), "основа\nправка\n"); // unstaged M
        File.WriteAllText(Abs("new.txt"), "НОВЫЙ\n");           // untracked

        _foreignPath = PrivateAttachments.Build(_authorId, _foreignGuid, "secret.txt")!;
        _ownPath = PrivateAttachments.Build(_ownerId, _ownGuid, "mine.txt")!;
        _commonPath = AttachmentsGitExclude.AttachmentPath("common.txt")!;

        Write(_foreignPath, "ЧУЖОЕ\n");
        Write(_ownPath, "СВОЁ\n");
        Write(_commonPath, "ОБЩЕЕ\n");

        // «Чужой проект» в общей папке теста + ссылка на его корень из нашего корня
        Directory.CreateDirectory(Path.Combine(_otherRoot, "docs"));
        var otherForeign = Path.Combine(_otherRoot, TreeExcludes.AttachmentsDir, "private", _authorId,
            _otherForeignGuid.ToString("N"));
        Directory.CreateDirectory(otherForeign);
        File.WriteAllText(Path.Combine(otherForeign, "other-secret.txt"), "ДРУГОЕ\n");
        File.WriteAllText(Path.Combine(_otherRoot, "docs", "plain.txt"), "ЛЕГАЛЬНОЕ\n");
        try { Directory.CreateSymbolicLink(Path.Combine(_projectRoot, "other"), _otherRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Abs(string rel) => Path.Combine(_projectRoot, rel);

    // Файл с каталогом: раскладка вложений глубокая, её никто не создаёт сама
    private void Write(string relPath, string content)
    {
        var abs = Abs(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _projectRoot,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000)) throw new InvalidOperationException("git не завершился: " + string.Join(' ', args));
        if (proc.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }

    // Рамка хода — настоящая формула продукта (TurnFrame.Resolve): общий чат общего проекта — Narrow
    private sealed class RealFrame(ProjectManager projects, SessionManager sessions, ITurnActorRegistry actors)
        : ITurnFrameSource
    {
        public TurnFrameKind Frame(string? sessionId, string ownerId)
        {
            var session = sessionId is null ? null : sessions.GetOwned(sessionId, ownerId);
            var project = session?.ProjectId is { } pid ? projects.GetById(pid) : null;
            var actor = sessionId is null ? null : actors.Actor(sessionId, ownerId);
            return TurnFrame.Resolve(project, session, ownerId, actor);
        }
    }

    // Реестр актора — подмена живого ядра (образец McpFilesPrivateAttachmentTests:199): id или null (фон)
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    // Право актора на инструмент — предмет ActorRightGateTests; здесь сквозной пропуск, чтобы
    // тест судил ровно путевой гейт чужого private
    private sealed class AllowAllGate : IActorRightGate
    {
        public string? Denied(string server, string tool, string? callerSessionId, string ownerId,
            string? projectId) => null;
    }

    // Вызов git_diff от имени конкретного актора идущего хода
    private async Task<McpToolCallResult> DiffAsync(string path, string? actorId, bool staged = false)
    {
        _actors.Map[AgentChatId] = actorId;
        return await _toolset.CallAsync("git_diff",
            new JsonObject { ["projectId"] = _projectId, ["path"] = path, ["staged"] = staged },
            new McpToolCallContext(_ownerId, AgentChatId, AgentChatId), CancellationToken.None);
    }

    // Ответ — { diff } (строка унифицированного diff), как REST
    private static string? DiffOf(McpToolCallResult result)
    {
        var el = JsonSerializer.Deserialize<JsonElement>(result.Text).GetProperty("diff");
        return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
    }

    [Fact]
    public async Task ХодВладельца_ЧужоеPrivate_ТотЖеОтказЧтоУНесуществующегоФайла_СвоёОбщееИОбычныеОтдаютDiff()
    {
        var foreign = await DiffAsync(_foreignPath, _ownerId);
        foreign.IsError.Should().BeTrue();
        foreign.Text.Should().Be($"Файл не найден: {_foreignPath}",
            "отказ идентичен отказу других путевых гейтов тулсета — существования чужого private не раскрываем");
        foreign.Text.Should().NotContain("ЧУЖОЕ", "содержимое не отдаётся ни в каком виде");

        // Та же отказанность на staged-ветке: путь чужой независимо от того, какой diff просят
        (await DiffAsync(_foreignPath, _ownerId, staged: true)).IsError.Should().BeTrue();

        // Легальное не тронуто: своё private, общая раскладка {guid}/…, новый файл, правка трекаемого
        DiffOf(await DiffAsync(_ownPath, _ownerId)).Should().Contain("СВОЁ");
        DiffOf(await DiffAsync(_commonPath, _ownerId)).Should().Contain("ОБЩЕЕ");
        DiffOf(await DiffAsync("new.txt", _ownerId)).Should().Contain("НОВЫЙ");
        DiffOf(await DiffAsync(PlainPath, _ownerId)).Should().Contain("+правка");
    }

    [Fact]
    public async Task ХодАвтора_СвоёПриватноеЧитается_PrivateВладельцаОтказано()
    {
        // токен тот же (арендатор), актор — автор вложения: для него оно своё (чужесть — по id, не по проекту)
        DiffOf(await DiffAsync(_foreignPath, _authorId)).Should().Contain("ЧУЖОЕ");

        // чужое для автора — private владельца: тот же отказ
        var own = await DiffAsync(_ownPath, _authorId);
        own.IsError.Should().BeTrue();
        own.Text.Should().Be($"Файл не найден: {_ownPath}");
        own.Text.Should().NotContain("СВОЁ");
    }

    [Fact]
    public async Task АкторНеизвестен_ЛюбоеPrivateОтказано_ОбщаяРаскладкаИОбычныеФайлыОтданы()
    {
        var own = await DiffAsync(_ownPath, null);
        own.IsError.Should().BeTrue();
        own.Text.Should().StartWith("Файл не найден: ",
            "без актора «своего» нет: любое private чужое (fail-closed, как IsForeign по пустому id)");
        (await DiffAsync(_foreignPath, null)).IsError.Should().BeTrue();

        DiffOf(await DiffAsync(_commonPath, null)).Should().Contain("ОБЩЕЕ",
            "общая раскладка {guid}/… — не closed-поддерево: ей этот гейт не ставился");
        DiffOf(await DiffAsync("new.txt", null)).Should().Contain("НОВЫЙ");
        DiffOf(await DiffAsync(PlainPath, null)).Should().Contain("+правка");
    }

    /// <summary>
    /// Тот же случай, что закрыт в 92e5873b для обхода FileService и в 3c531d5e8 для REST-ручки:
    /// ссылка в корне на каталог, СОДЕРЖАЩИЙ контейнер .cc-attachments (корень чужого проекта).
    /// Имя ссылки — первый сегмент, текстовая линия чужести его не видит; закрывает канон
    /// разыменованного пути (первая линия IsForeignResolved). Легальная ссылка наружу на не-private
    /// остаётся: она не чужое private ни по одной линии.
    /// </summary>
    /// <Named мутации: «гейт опущен» (IsForeignResolved в git_diff) краснит этот тест:
    /// через ссылку возвращается diff с содержимым чужого private.</Named>
    [Fact]
    public async Task СсылкаНаКореньЧужогоПроекта_ЧужоеНеРаскрывается_ЛегальнаяСсылкаНаружуОтдана()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий (тот же приём, что в REST-тесте)
        Directory.Exists(Path.Combine(_projectRoot, "other")).Should().BeTrue("арранж: ссылка на корень чужого проекта создана");

        (await DiffAsync(ForeignThroughLink, _ownerId)).IsError.Should().BeTrue(
            "ссылка разыменовывается в канон с сегментом чужого private — содержимое не отдаётся");
        (await DiffAsync(ForeignThroughLink, _ownerId)).Text.Should().NotContain("ДРУГОЕ");

        DiffOf(await DiffAsync(ThroughLinkPlain, _ownerId)).Should().Contain("ЛЕГАЛЬНОЕ",
            "обычное содержимое чужого проекта — легальная ссылка наружу, она открыта и раньше, и теперь");
        DiffOf(await DiffAsync(_ownPath, _ownerId)).Should().Contain("СВОЁ");
    }
}
