using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
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
/// S2d-6b-3 (разрез 4bc3d04f §1 путь 19): MCP wsp files_* на чужом приватном вложении
/// (.cc-attachments/private/{author}/…, S2d-6b-1) отказывает как «файл не найден» — существования
/// не раскрывает; в деревьях и поиске чужое private не перечисляется. Актор — из реестра хода
/// (тот же источник, что у chats_list, S2d-4e), не из аргументов; актор неизвестен — чужое
/// private не отдаётся (fail-closed). Свои private, общие вложения и прочие пути — без изменений.
///
/// Проверяется без хоста: тулсет собран с живыми сервисами (состав — McpChatsListVisibilityTests,
/// образец этого шага фичи), вызов через CallAsync с чатом-вызывателем в хвосте маршрута, ответ
/// читается из сериализованного JSON — то, что реально уходит на wire. Рамка — Narrow (живой
/// вердикт общего проекта), гейт актора настоящий (files_read в ActorToolRights — Read).
/// </summary>
public sealed class McpFilesPrivateAttachmentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectRoot;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly WorkspaceToolset _toolset;
    private readonly Actors _actors = new();
    private readonly string _ownerId;
    private readonly string _authorId; // автор чужого приватного вложения, участник (Editor)
    private readonly string _projectId;
    private readonly string _personaId;

    private const string AgentChatId = "agent-chat";
    // Приватный чат владельца: по настоящей формуле рамки он Wide (TurnFrame.cs:75) — единственный
    // ход общего проекта, где безвозвратное удаление вообще допускается (в узкой рамке files_delete
    // отказан раньше любого путевого гейта, WorkspaceToolset.cs:344-347)
    private const string OwnerPrivateChatId = "owner-private-chat";

    // Раскладка файлов в рабочей папке проекта (S2d-6b-1): id авторов — реальные id пользователей
    private string _foreignPath = ""; // чужое private (автор — _authorId)
    private string _ownPath = "";     // своё private (автор — _ownerId)
    private string _commonPath = "";  // общее вложение (прежняя раскладка {guid}/{имя})
    private const string PlainPath = "docs/plain.txt";

    public McpFilesPrivateAttachmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_files_priv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _authorId = _users.Add("other-author", "pwd", "user").Id;
        _users.SetFeatureFlag(_authorId, FeatureFlagKeys.SharedProjects, true);
        // files_delete живёт за секцией destructive (SessionManager.BuildWorkspacePlan, :4758) —
        // без флага владельца она не монтируется и тест не дошёл бы до гейта
        _users.SetFeatureFlag(_ownerId, FeatureFlagKeys.WorkspaceDestructive, true);

        var appSettings = new AppSettingsService(_config);
        _projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", _projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _authorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        _personas = new PersonaManager(_config);
        _personaId = _personas.Create(_ownerId, "Персона проекта", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;

        List<Session> seed =
        [
            new Session { Id = AgentChatId, ProjectId = _projectId, PersonaId = _personaId,
                Status = SessionStatus.Finished },
            new Session { Id = OwnerPrivateChatId, ProjectId = _projectId, PersonaId = _personaId,
                PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
        ];
        File.WriteAllText(Path.Combine(_tempDir, "sessions.json"), JsonSerializer.Serialize(seed));

        _sessions = CreateSessionManager(appSettings);

        WriteFixtureFiles();

        var access = new ProjectAccessService(_projects, _sessions, null);
        // knowledge_index проверяется через настоящий синк-сервис: Dify в тесте не настроен, и
        // EnsureDatasetAsync — первый шаг IndexPathAsync (KnowledgeService.cs:396) — отвечает
        // «Dify не настроен». По тексту отказа видно, достигнута ли индексация: отказ гейта обязан
        // стоять раньше
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledgeSync = new ProjectKnowledgeSyncService(
            new KnowledgeService(new Mock<IHttpClientFactory>().Object, Options.Create(new DifyOptions()), wkStore),
            wkStore, _projects, new ProjectFileGateway(new FileService()), new RecordingHubNotifier(),
            new NullDifyMetrics(), NullLogger<ProjectKnowledgeSyncService>.Instance);
        _toolset = new WorkspaceToolset(
            _projects, null!, _sessions, _personas, new FileService(), null!, null!, null!, knowledgeSync, null!,
            null!, null!, _users, null!, null!, null!, null!, null!, null!, null!,
            null!, access, new RealFrame(_projects, _sessions, _actors), _actors,
            new ActorRightGate(_actors, access));
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Фикстура на диске: чужое private (с вложенным каталогом для дерева), своё private,
    // общее вложение прежней раскладки и обычный файл
    private void WriteFixtureFiles()
    {
        _foreignPath = PrivateAttachments.Build(_authorId, Guid.NewGuid(), "foreign.txt")!;
        _ownPath = PrivateAttachments.Build(_ownerId, Guid.NewGuid(), "own.txt")!;
        _commonPath = AttachmentsGitExclude.AttachmentPath("common.txt")!;

        Write(_foreignPath, "чужое");
        Write(_foreignPath.Replace("foreign.txt", "deep/nested.txt"), "чужое глубокое");
        Write(_ownPath, "своё");
        Write(_commonPath, "общее");
        Write(PlainPath, "обычное");
    }

    private void Write(string relPath, string content)
    {
        var abs = Path.Combine(_projectRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private string AbsOf(string relPath) => Path.Combine(_projectRoot, relPath);

    // Живой SessionManager без процессов и LLM-вызовов (образец McpChatsListVisibilityTests)
    private SessionManager CreateSessionManager(AppSettingsService appSettings)
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, knowledge,
            new SkillsService(), _users, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, new ChatHistoryService(_config), _config, adapters,
            new FalCostService(new Mock<IHttpClientFactory>().Object, _config), new UsageService(_config),
            appSettings, _users, jwt, server.Object, llmProviders, new FeatureFlagService(_users),
            _personas, bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    // Рамка хода — настоящая формула продукта (TurnFrame.Resolve): общий чат общего проекта —
    // Narrow всем (и чтение, как было в этом наборе), приватный чат владельца при акторе-владельце —
    // Wide. Заменять её константой значило бы проверять files_delete в рамке, где он не выполняется
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

    // Реестр актора — подмена живого ядра (образец ActorRightGateTests): id или null (фон)
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    private async Task<McpToolCallResult> CallAsync(string tool, JsonObject args, string? actorId) =>
        await CallAsync(AgentChatId, tool, args, actorId);

    // Вызов от имени конкретного чата-вызывателя (рамка хода считается по нему — RealFrame)
    private async Task<McpToolCallResult> CallAsync(string chatId, string tool, JsonObject args, string? actorId)
    {
        _actors.Map[chatId] = actorId;
        return await _toolset.CallAsync(tool, args,
            new McpToolCallContext(_ownerId, chatId, chatId), CancellationToken.None);
    }

    private Task<McpToolCallResult> ReadAsync(string path, string? actorId) =>
        CallAsync("files_read", new JsonObject { ["projectId"] = _projectId, ["path"] = path }, actorId);

    private static string ContentOf(McpToolCallResult result) =>
        JsonSerializer.Deserialize<JsonElement>(result.Text).GetProperty("content").GetString()!;

    // Путь из ответа files_*: files_tree — объект {entries:[…]}, files_search — массив [{…}]
    private static List<string> PathsOf(McpToolCallResult result)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(result.Text);
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("entries", out var entries) ? entries : default;
        return items.ValueKind != JsonValueKind.Array ? []
            : items.EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    // ── files_read ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ХодВладельца_ЧужоеPrivate_ТотЖеОтказЧтоУНесуществующегоФайла_СвоёИОбщееКакПрежде()
    {
        var foreign = await ReadAsync(_foreignPath, _ownerId);
        foreign.IsError.Should().BeTrue();
        foreign.Text.Should().Be($"Файл не найден: {_foreignPath}",
            "отказ идентичен отказу отсутствующего файла по этому же пути — существования не раскрывает");
        foreign.Text.Should().NotContain("чужое");

        // тот же текст, что у реально отсутствующего файла в том же поддереве
        var missing = await ReadAsync(_foreignPath.Replace("foreign.txt", "missing.txt"), _ownerId);
        missing.IsError.Should().BeTrue();
        missing.Text.Should().StartWith("Файл не найден: ");

        ContentOf(await ReadAsync(_ownPath, _ownerId)).Should().Be("своё", "своё private автора — как раньше");
        ContentOf(await ReadAsync(_commonPath, _ownerId)).Should().Be("общее", "общее вложение — вне поддерева private");
        ContentOf(await ReadAsync(PlainPath, _ownerId)).Should().Be("обычное");
    }

    [Fact]
    public async Task ХодАвтора_СвоёПриватноеЧитается()
    {
        // токен тот же (арендатор), актор — автор вложения: для него оно своё
        ContentOf(await ReadAsync(_foreignPath, _authorId)).Should().Be("чужое");
    }

    [Fact]
    public async Task АкторНеизвестен_ПриватныеЗакрыты_ОбщиеФайлыКакПрежде()
    {
        var foreign = await ReadAsync(_foreignPath, null);
        foreign.IsError.Should().BeTrue();
        foreign.Text.Should().StartWith("Файл не найден: ",
            "неизвестный актор не равен автору — чужое private не отдаётся (fail-closed)");

        var own = await ReadAsync(_ownPath, null);
        own.IsError.Should().BeTrue();
        own.Text.Should().StartWith("Файл не найден: ",
            "без актора приватность некому отнести: любое private чужое, как IsForeign по пустому userId");

        ContentOf(await ReadAsync(_commonPath, null)).Should().Be("общее");
        ContentOf(await ReadAsync(PlainPath, null)).Should().Be("обычное");
    }

    [Fact]
    public async Task ОбходыПути_ЧужоеPrivateНеОтдаётся_СвоёНеЗакрыт()
    {
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var ownIdSeg = _ownPath.Split('/')[2];
        var guidSeg = _foreignPath.Split('/')[3];

        // «..» из чужого каталога в чужой: нормализация до решения — тот же отказ
        await AssertForeignDenied($"docs/../.cc-attachments/private/{foreignIdSeg}/{guidSeg}/foreign.txt");
        // обратные слеши
        await AssertForeignDenied($".cc-attachments\\private\\{foreignIdSeg}\\{guidSeg}\\foreign.txt");
        // регистр сегментов (id — hex, поднимем его тоже)
        await AssertForeignDenied($".CC-Attachments/PRIVATE/{foreignIdSeg.ToUpperInvariant()}/{guidSeg}/foreign.txt");
        // двойные разделители
        await AssertForeignDenied($".cc-attachments//private//{foreignIdSeg}//{guidSeg}//foreign.txt");
        // «./» внутри пути
        await AssertForeignDenied($".cc-attachments/private/{foreignIdSeg}/./{guidSeg}/foreign.txt");
        // %2F/%2E — тот же путь, записанный экранированно
        await AssertForeignDenied($".cc-attachments%2Fprivate%2F{foreignIdSeg}%2F{guidSeg}%2Fforeign.txt");
        // %2E%2E как «..»: чужой каталог через своё private — нормализация до вызова IsForeign
        await AssertForeignDenied($".cc-attachments/private/{ownIdSeg}/%2E%2E/{foreignIdSeg}/{guidSeg}/foreign.txt");

        // обход не должен закрывать своё: «..» из своей папки в своё private — читается
        ContentOf(await ReadAsync($"docs/../.cc-attachments/private/{ownIdSeg}/{_ownPath.Split('/')[3]}/own.txt", _ownerId))
            .Should().Be("своё");
    }

    private async Task AssertForeignDenied(string path)
    {
        var result = await ReadAsync(path, _ownerId);
        result.IsError.Should().BeTrue($"путь {path} после нормализации — чужое private, отказ обязателен");
        result.Text.Should().StartWith("Файл не найден: ",
            "отказ — как у несуществующего файла, существования не раскрывает");
    }

    [Fact]
    public async Task Дерево_ЧужоеPrivateНеПеречисляется_СвоёИОбщееВидно()
    {
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var ownIdSeg = _ownPath.Split('/')[2];

        var tree = await CallAsync("files_tree", new JsonObject { ["projectId"] = _projectId, ["path"] = ".cc-attachments" }, _ownerId);
        tree.IsError.Should().BeFalse(tree.Text);
        var paths = PathsOf(tree);
        paths.Should().Contain(".cc-attachments/private", "сам каталог private — не чужой, он остаётся");
        paths.Should().Contain($".cc-attachments/private/{ownIdSeg}", "своё private автора — видно");
        paths.Should().Contain(_commonPath, "общее вложение прежней раскладки — видно");
        paths.Should().NotContain($".cc-attachments/private/{foreignIdSeg}",
            "каталог чужого автора и всё под ним — в дерево не попадает");
        paths.Should().NotContain(_foreignPath);
        paths.Should().NotContain(_foreignPath.Replace("foreign.txt", "deep/nested.txt"));

        var inner = await CallAsync("files_tree", new JsonObject { ["projectId"] = _projectId, ["path"] = $".cc-attachments/private/{foreignIdSeg}" }, _ownerId);
        inner.IsError.Should().BeFalse(inner.Text);
        PathsOf(inner).Should().BeEmpty("явный запрос чужого каталога — пустой список, а не список чужого автора");
    }

    [Fact]
    public async Task Поиск_ЧужоеPrivateНеПеречисляется_СвоёОбщееИФайлыПроектаКакПрежде()
    {
        var found = await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "foreign" }, _ownerId);
        PathsOf(found).Should().BeEmpty("чужое private выпадает из поиска — ни именем, ни количеством");

        PathsOf(await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "own" }, _ownerId))
            .Should().Contain(_ownPath, "своё private автора — в поиске как раньше");
        PathsOf(await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "common" }, _ownerId))
            .Should().Contain(_commonPath);
        PathsOf(await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "plain" }, _ownerId))
            .Should().Contain(PlainPath);

        // актор неизвестен — любое private закрыто (fail-closed, как FileService.Search без зрителя)
        PathsOf(await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "own" }, null))
            .Should().BeEmpty();
        PathsOf(await CallAsync("files_search", new JsonObject { ["projectId"] = _projectId, ["query"] = "plain" }, null))
            .Should().Contain(PlainPath);
    }

    [Fact]
    public async Task ДокументЧужогоPrivate_ОтказКакНесуществующемуФайлу_БезКасанияДиска()
    {
        // files_document_read тоже читает файл по пути: чужой scan.pdf — тот же отказ,
        // и он стоит до конвертера (отсутствующий в тесте docAi не должен быть достигнут)
        var pdf = _foreignPath.Replace("foreign.txt", "scan.pdf");
        Write(pdf, "pdf-like");
        var result = await CallAsync("files_document_read", new JsonObject { ["projectId"] = _projectId, ["path"] = pdf }, _ownerId);
        result.IsError.Should().BeTrue();
        result.Text.Should().StartWith("Файл не найден: ");
    }

    // ── запись (доработка 1 по вердикту ревью 426b21ad) ─────────────────────────

    [Fact]
    public async Task WriteMMkdir_ЧужоеPrivate_Отказ_ФайлыЦелы()
    {
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var foreignDir = $".cc-attachments/private/{foreignIdSeg}";

        // перезапись чужого файла — отказ, содержимое не тронуто
        var w = await CallAsync("files_write",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _foreignPath, ["content"] = "поверх чужого" }, _ownerId);
        w.IsError.Should().BeTrue();
        w.Text.Should().Be($"Файл не найден: {_foreignPath}", "тот же отказ, что у несуществующего файла");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // новый файл внутри чужого private — чужой путь не создаётся
        var newInForeign = _foreignPath.Replace("foreign.txt", "new.txt");
        w = await CallAsync("files_write",
            new JsonObject { ["projectId"] = _projectId, ["path"] = newInForeign, ["content"] = "новое в чужом" }, _ownerId);
        w.IsError.Should().BeTrue();
        w.Text.Should().StartWith("Файл не найден: ");
        File.Exists(AbsOf(newInForeign)).Should().BeFalse();

        // каталог внутри чужого private — не создаётся
        var newDir = $"{foreignDir}/newdir";
        var m = await CallAsync("files_mkdir", new JsonObject { ["projectId"] = _projectId, ["path"] = newDir }, _ownerId);
        m.IsError.Should().BeTrue();
        m.Text.Should().StartWith("Файл не найден: ");
        Directory.Exists(AbsOf(newDir)).Should().BeFalse();

        // актор неизвестен — любое private чужое: своя запись тоже отказана (fail-closed)
        w = await CallAsync("files_write",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _ownPath, ["content"] = "x" }, null);
        w.IsError.Should().BeTrue();
        File.ReadAllText(AbsOf(_ownPath)).Should().Be("своё");

        // своё и общее — как раньше
        w = await CallAsync("files_write",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _ownPath, ["content"] = "правка своего" }, _ownerId);
        w.IsError.Should().BeFalse(w.Text);
        File.ReadAllText(AbsOf(_ownPath)).Should().Be("правка своего");
        w = await CallAsync("files_write",
            new JsonObject { ["projectId"] = _projectId, ["path"] = "docs/new.txt", ["content"] = "новый" }, _ownerId);
        w.IsError.Should().BeFalse(w.Text);
        File.ReadAllText(AbsOf("docs/new.txt")).Should().Be("новый");
        m = await CallAsync("files_mkdir",
            new JsonObject { ["projectId"] = _projectId, ["path"] = $".cc-attachments/private/{_ownPath.Split('/')[2]}/mydir" }, _ownerId);
        m.IsError.Should().BeFalse(m.Text);
        Directory.Exists(AbsOf($".cc-attachments/private/{_ownPath.Split('/')[2]}/mydir")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ЧужогоPrivate_ФайлаИКаталога_Отказ_ПоддеревоЦело()
    {
        // files_delete допускается только в широкой рамке (WorkspaceToolset.cs:344-347) — в общем
        // проекте это приватный чат владельца при акторе-владельце (TurnFrame.cs:75). В нём
        // безвозвратное удаление чужого private и обязано быть отказано
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var foreignDir = $".cc-attachments/private/{foreignIdSeg}";
        var nested = _foreignPath.Replace("foreign.txt", "deep/nested.txt");

        // удаление чужого файла — отказ, файл цел
        var d = await CallAsync(OwnerPrivateChatId, "files_delete",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _foreignPath }, _ownerId);
        d.IsError.Should().BeTrue();
        d.Text.Should().Be($"Файл не найден: {_foreignPath}", "тот же отказ, что у несуществующего файла");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // рекурсивное удаление каталога чужого автора — отказ, всё поддерево цело
        d = await CallAsync(OwnerPrivateChatId, "files_delete",
            new JsonObject { ["projectId"] = _projectId, ["path"] = foreignDir }, _ownerId);
        d.IsError.Should().BeTrue();
        d.Text.Should().Be($"Файл не найден: {foreignDir}");
        Directory.Exists(AbsOf(foreignDir)).Should().BeTrue();
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");
        File.ReadAllText(AbsOf(nested)).Should().Be("чужое глубокое");

        // своё и обычный файл — удаляются, как раньше
        Write("docs/new.txt", "новый");
        d = await CallAsync(OwnerPrivateChatId, "files_delete",
            new JsonObject { ["projectId"] = _projectId, ["path"] = "docs/new.txt" }, _ownerId);
        d.IsError.Should().BeFalse(d.Text);
        File.Exists(AbsOf("docs/new.txt")).Should().BeFalse();
        d = await CallAsync(OwnerPrivateChatId, "files_delete",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _ownPath }, _ownerId);
        d.IsError.Should().BeFalse(d.Text);
        File.Exists(AbsOf(_ownPath)).Should().BeFalse();
    }

    [Fact]
    public async Task Rename_ИсточникИЦельЧужогоPrivate_Отказ_КаталогЖертвыНаМесте()
    {
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var foreignDir = $".cc-attachments/private/{foreignIdSeg}";

        // источник — чужой файл: перенос в общее место отказан, файл на месте
        var r = await CallAsync("files_rename",
            new JsonObject { ["projectId"] = _projectId, ["oldPath"] = _foreignPath, ["newPath"] = "moved.txt" }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().Be($"Файл не найден: {_foreignPath}");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");
        File.Exists(AbsOf("moved.txt")).Should().BeFalse();

        // источник — каталог чужого автора целиком: после переноса путевое правило уже не видит
        // private, и чужой материал стал бы общим. Отказ, каталог на месте
        r = await CallAsync("files_rename",
            new JsonObject { ["projectId"] = _projectId, ["oldPath"] = foreignDir, ["newPath"] = "stolen" }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().Be($"Файл не найден: {foreignDir}");
        Directory.Exists(AbsOf(foreignDir)).Should().BeTrue();
        Directory.Exists(AbsOf("stolen")).Should().BeFalse();
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // цель — чужой private: свой файл не переносится в чужой каталог, источник цел
        r = await CallAsync("files_rename",
            new JsonObject { ["projectId"] = _projectId, ["oldPath"] = PlainPath, ["newPath"] = _foreignPath }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().Be($"Файл не найден: {_foreignPath}");
        File.ReadAllText(AbsOf(PlainPath)).Should().Be("обычное");
        File.ReadAllText(AbsOf(_foreignPath)).Should().Be("чужое");

        // актор неизвестен — чужой источник отказан и для него (fail-closed)
        r = await CallAsync("files_rename",
            new JsonObject { ["projectId"] = _projectId, ["oldPath"] = _foreignPath, ["newPath"] = "moved.txt" }, null);
        r.IsError.Should().BeTrue();
        File.Exists(AbsOf(_foreignPath)).Should().BeTrue();

        // своё — как раньше: свой private-файл переносим внутри своего каталога
        var ownNew = _ownPath.Replace("own.txt", "renamed.txt");
        r = await CallAsync("files_rename",
            new JsonObject { ["projectId"] = _projectId, ["oldPath"] = _ownPath, ["newPath"] = ownNew }, _ownerId);
        r.IsError.Should().BeFalse(r.Text);
        File.ReadAllText(AbsOf(ownNew)).Should().Be("своё");
    }

    [Fact]
    public async Task ToMarkdown_ЦельВЧужомPrivate_ОтказДоКонвертера()
    {
        var foreignIdSeg = _foreignPath.Split('/')[2];
        var foreignDir = $".cc-attachments/private/{foreignIdSeg}";

        // явный targetDir в чужой private — отказ и до чтения, и до конвертера (docAi в этой
        // фикстуре null: до него дошло бы только неотрезаанное обращение)
        var r = await CallAsync("files_to_markdown",
            new JsonObject { ["projectId"] = _projectId, ["path"] = PlainPath, ["targetDir"] = foreignDir }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().Be($"Файл не найден: {foreignDir}");
        Directory.GetFiles(AbsOf(foreignDir), "*.md", SearchOption.AllDirectories).Should().BeEmpty();

        // обход того же чужого каталога через «..» — тот же отказ (нормализация до решения)
        r = await CallAsync("files_to_markdown",
            new JsonObject { ["projectId"] = _projectId, ["path"] = PlainPath, ["targetDir"] = $"docs/../.cc-attachments/private/{foreignIdSeg}" }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().StartWith("Файл не найден: ");
        Directory.GetFiles(AbsOf(foreignDir), "*.md", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task KnowledgeIndex_ЧужоеPrivate_Отказ_ИндексацияНеНачата()
    {
        var r = await CallAsync("knowledge_index",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _foreignPath }, _ownerId);
        r.IsError.Should().BeTrue();
        r.Text.Should().Be($"Файл не найден: {_foreignPath}");
        // IndexPathAsync не достигнут: его первый шаг EnsureDatasetAsync при не настроенном Dify
        // отвечает «Dify не настроен» (KnowledgeService.cs:396) — этого текста в отказе нет,
        // документ в датасете не создавался
        r.Text.Should().NotContain("Dify");

        // свой путь гейт не трогает: он доходит до индексации (в тесте — до отказа Dify)
        var own = await CallAsync("knowledge_index",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _ownPath }, _ownerId);
        own.IsError.Should().BeTrue();
        own.Text.Should().Contain("Dify не настроен", "свой private-путь не закрыт гейтом — поток идёт в knowledge-сервис, как раньше");

        // актор неизвестен — чужое private закрыто и для него
        r = await CallAsync("knowledge_index",
            new JsonObject { ["projectId"] = _projectId, ["path"] = _foreignPath }, null);
        r.IsError.Should().BeTrue();
        r.Text.Should().StartWith("Файл не найден: ");
    }
}
