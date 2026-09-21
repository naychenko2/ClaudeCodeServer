using System.Reflection;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// SessionManager.BranchAsync (шаг 3 фичи chat-branch): восемь отказов §9, неизменность
// оригинала, наследование полей §11, имя по умолчанию, draft у include=beforePrompt.
// Документ-основание: docs/research/chat-branching-2026-09.md.
[Collection(TestCollections.SessionStaticResolvers)]
public class SessionManagerBranchTests : IDisposable
{
    private const string TestUserId = "branch-test-user";
    private const string TestUsername = "branch-test-user";

    private readonly string _tempDir;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly SessionManager _sut;

    public SessionManagerBranchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_branch_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                ["Delivery:AwaitProcessExitSeconds"] = "0",
            })
            .Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

        var broadcaster = new TrackingNoopBroadcaster();
        _llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), _llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(userStore);
        var personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        _sut = new SessionManager(_projectManager, _historyService, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, _llmProviders, flags, personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster: broadcaster);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;
        for (var i = 1; ; i++)
        {
            try { Directory.Delete(_tempDir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i >= 5) return;
                Thread.Sleep(50 * i);
            }
        }
    }

    // Заглушка ISessionBroadcaster: тестам ветвления рассылка не нужна
    private sealed class TrackingNoopBroadcaster : ISessionBroadcaster
    {
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    private string MkProjectDir(string suffix) =>
        Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;

    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)field.GetValue(_sut)!;
        return sessions[sessionId]!;
    }

    private static void SetProcess(object entry, ILlmSessionAdapter adapter) =>
        entry.GetType().GetField("Process")!.SetValue(entry, adapter);

    private static void EnqueueUser(object entry, string text)
    {
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        pending.Add(new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), text, null, null,
            0, DateTime.UtcNow, Kind: SessionManager.PendingKind.User));
    }

    private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Синтетический транскрипт CLI с двумя ходами, находимый BranchAsync по соглашению
    // FlattenCwd(project.RootPath) внутри UserProfileDir (провайдер по умолчанию — "claude",
    // не настроен в тестовом конфиге → ConfigRootFor отдаёт UserProfileDir).
    private void WriteTranscript(string csid, string cwd, string userText1, string userText2)
    {
        var flat = TranscriptMigrator.FlattenCwd(cwd);
        var dir = Path.Combine(_llmProviders.UserProfileDir, "projects", flat);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, csid + ".jsonl");
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"system\",\"subtype\":\"init\",\"sessionId\":\"" + csid + "\",\"uuid\":\"s0\"}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(userText1) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 1\"}}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u2\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(userText2) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a2\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 2\"}}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // Готовый к ветвлению проектный чат: Session с ClaudeSessionId + history.json (два хода)
    // + синтетический транскрипт CLI по тому же тексту. userText1/2 — ≥40 значимых символов
    // (якоря не короткие — не задевают гейт «короткий якорь без соседа» резака).
    private async Task<(Session Session, Project Project, string Csid, string UserText1, string UserText2)>
        SeedBranchableChatAsync(string suffix,
            string userText1 = "первый вопрос разговора с запасом символов для якоря",
            string userText2 = "второй вопрос разговора с запасом символов для якоря",
            string? tailUuid1 = null, string? tailUuid2 = null)
    {
        var dir = MkProjectDir(suffix);
        var project = _projectManager.Create("P-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Оригинальный чат " + suffix);

        var csid = "csid-" + Guid.NewGuid().ToString("N")[..12];
        var live = _sut.GetById(session.Id)!;
        live.ClaudeSessionId = csid;

        await _historyService.SaveAsync(csid,
        [
            new StoredUserMessage(userText1),
            new StoredTextMessage("ответ 1"),
            new StoredResultMessage("success", 100, 1) { TranscriptTailUuid = tailUuid1 },
            new StoredUserMessage(userText2),
            new StoredTextMessage("ответ 2"),
            new StoredResultMessage("success", 100, 1) { TranscriptTailUuid = tailUuid2 },
        ]);

        WriteTranscript(csid, project.RootPath, userText1, userText2);

        return (live, project, csid, userText1, userText2);
    }

    // --- §9.1 — ход/фоновые агенты в полёте ---

    [Fact]
    public async Task Гейт_ИдётХод_409()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("turn-inflight");
        var entry = GetEntry(session.Id);
        EnqueueUser(entry, "ещё одно сообщение");

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*идёт ход*");
    }

    [Fact]
    public async Task Гейт_ФоновыеАгенты_409()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("bg-agents");
        var entry = GetEntry(session.Id);
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(session);
        adapter.SetupGet(a => a.HasTrackedBg).Returns(true);
        SetProcess(entry, adapter.Object);

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*фоновые агенты*");
    }

    // --- §9.2 — нет ClaudeSessionId ---

    [Fact]
    public async Task Гейт_НетClaudeSessionId_400()
    {
        var dir = MkProjectDir("no-csid");
        var project = _projectManager.Create("P-no-csid", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, "текст", SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ветвить нечего*");
    }

    // --- §9.3 — десктопный чат ---

    [Fact]
    public async Task Гейт_ДесктопныйЧат_400()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("desktop");
        session.DesktopChat = true;

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*транскрипт десктопного чата*");
    }

    // --- §9.6 — групповой чат и режим штаба ---

    [Fact]
    public async Task Гейт_ГрупповойЧат_400()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("group");
        session.Participants = ["p1", "p2"];

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Групповой чат*");
    }

    [Fact]
    public async Task Гейт_РежимШтаба_400()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("team");
        session.TeamImplement = new SessionTeamImplement();

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Командная реализация*");
    }

    // --- §9.8 — отдельный worktree ---

    [Fact]
    public async Task Гейт_ОтдельныйWorktree_400()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("worktree");
        session.WorktreePath = "/some/worktree/path";

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*рабочем дереве*");
    }

    // --- §9.4 — транскрипт не найден нигде (и в архиве тоже) ---

    [Fact]
    public async Task Гейт_ТранскриптНеНайден_400()
    {
        var dir = MkProjectDir("no-transcript");
        var project = _projectManager.Create("P-no-transcript", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var live = _sut.GetById(session.Id)!;
        live.ClaudeSessionId = "csid-" + Guid.NewGuid().ToString("N")[..12];
        await _historyService.SaveAsync(live.ClaudeSessionId,
            [new StoredUserMessage("сообщение без транскрипта на диске")]);
        // Транскрипт намеренно не пишем — ни в профиле, ни в архиве

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0,
            "сообщение без транскрипта на диске", SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*убрана плановой уборкой CLI*");
    }

    // --- §9.5 — границу не удалось сопоставить (отказ резака) ---

    [Fact]
    public async Task Гейт_ГраницаНеСопоставлена_409()
    {
        var text1 = "текст истории, которого в транскрипте на самом деле нет вообще";
        var (session, project, csid, _, _) = await SeedBranchableChatAsync("no-match", userText1: text1);
        // Перезаписываем транскрипт СОВСЕМ другими промптами — история говорит text1,
        // а в транскрипте его нет: якорь не найдётся резаком → 409
        WriteTranscript(csid, project.RootPath,
            "полностью посторонний текст первого промпта транскрипта, никак не связанный с историей",
            "полностью посторонний текст второго промпта транскрипта, тоже не связанный с историей");

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*не удалось найти этот шаг*");
    }

    // --- Индекс/текст разошлись (сверка с историей до резака) ---

    [Fact]
    public async Task АнкорТекст_НеСовпадаетСИндексом_409()
    {
        var (session, _, _, _, _) = await SeedBranchableChatAsync("anchor-mismatch");

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0,
            "совершенно другой текст, никак не связанный с сообщением по индексу",
            SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*разошлись*");
    }

    // --- Неизменность оригинала ---

    [Fact]
    public async Task Оригинал_НеИзменяетсяПослеВетвления()
    {
        var (session, _, csid, text1, _) = await SeedBranchableChatAsync("immutable");
        var updatedAtBefore = session.UpdatedAt;
        var archivedAtBefore = session.ArchivedAt;
        var csidBefore = session.ClaudeSessionId;

        await _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        var after = _sut.GetById(session.Id)!;
        after.UpdatedAt.Should().Be(updatedAtBefore);
        after.ArchivedAt.Should().Be(archivedAtBefore);
        after.ClaudeSessionId.Should().Be(csidBefore).And.Be(csid);
    }

    // --- Наследование полей §11 ---

    [Fact]
    public async Task Наследование_МоделиПровайдераИOptOut_Наследуются_АвтоРазрешенияИЗадача_Нет()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("inherit");
        session.Model = "glm-5.2";
        session.Provider = "glm";
        session.Effort = "high";
        session.ExcludeFromDossiers = true;
        session.VoiceMode = true;
        session.VoiceStyle = "digest";
        session.NotificationsMuted = true;
        session.AutoAllowTools = ["Bash"];
        session.TaskId = "some-task-id";
        session.TaskExecution = true;

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        result.Session.Model.Should().Be("glm-5.2");
        result.Session.Provider.Should().Be("glm");
        result.Session.Effort.Should().Be("high");
        result.Session.ExcludeFromDossiers.Should().BeTrue("явный opt-out «не сохранять решения» обязан наследоваться");
        result.Session.VoiceMode.Should().BeTrue();
        result.Session.VoiceStyle.Should().Be("digest");
        result.Session.NotificationsMuted.Should().BeTrue();
        result.Session.AutoAllowTools.Should().BeEmpty("«Разрешать всегда» — решение конкретного разговора, не наследуется");
        result.Session.TaskId.Should().BeNull("ветка чата-исполнителя не должна закрывать чужую задачу");
        result.Session.TaskExecution.Should().BeFalse();
        result.Session.WorktreePath.Should().BeNull();
        result.Session.BranchedFromSessionId.Should().Be(session.Id);
    }

    // --- Имя по умолчанию ---

    [Fact]
    public async Task ИмяПоУмолчанию_ОригиналПлюсВетка()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("name-default");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        result.Session.Name.Should().Be(session.Name + " (ветка)");
    }

    [Fact]
    public async Task ИмяЯвное_ПеребиваетДефолт()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("name-explicit");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn, name: "Моё имя ветки");

        result.Session.Name.Should().Be("Моё имя ветки");
    }

    // --- include=beforePrompt: draft + якорь не входит в историю ветки ---

    [Fact]
    public async Task BeforePrompt_ВозвращаетDraftИНеВключаетЯкорноеСообщение()
    {
        var (session, _, csid, text1, text2) = await SeedBranchableChatAsync("before-prompt");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 1, text2,
            SessionManager.ChatBranchInclude.BeforePrompt);

        result.Draft.Should().Be(text2);
        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        branchHistory.OfType<StoredUserMessage>().Should().ContainSingle(m => m.Text == text1);
        branchHistory.OfType<StoredUserMessage>().Should().NotContain(m => m.Text == text2,
            "якорное сообщение не входит в ветку — его текст уходит в draft композера");
        branchHistory.Should().Contain(m => m is StoredBranchedFromMessage);
    }

    [Fact]
    public async Task Turn_НеВозвращаетDraft_ВключаетЯкорьИОтвет()
    {
        var (session, _, csid, text1, text2) = await SeedBranchableChatAsync("turn-include");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 1, text2,
            SessionManager.ChatBranchInclude.Turn);

        result.Draft.Should().BeNull();
        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        branchHistory.OfType<StoredUserMessage>().Should().Contain(m => m.Text == text2);
    }

    // --- Плашка «Ветка от …» ---

    [Fact]
    public async Task Плашка_СодержитИмяИИдОригинала()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("marker");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        var marker = branchHistory.OfType<StoredBranchedFromMessage>().Should().ContainSingle().Subject;
        marker.SourceSessionId.Should().Be(session.Id);
        marker.SourceName.Should().Be(session.Name);
    }

    // Ветка от ветки (дефект QA): у источника хвостом истории стоит его собственная плашка
    // «Ветка от …», и при include=turn от ПОСЛЕДНЕГО хода она попадала в копию — у новой
    // ветки оказывались две плашки, причём первая указывала на прадеда.
    [Fact]
    public async Task ВеткаОтВетки_ОднаПлашка_НаНепосредственныйИсточник()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("branch-of-branch");

        // A → B: ветвим от первого хода, у B история кончается плашкой «Ветка от A»
        var b = (await _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn)).Session;
        var bHistory = await _historyService.LoadAsync(b.ClaudeSessionId!);
        bHistory[^1].Should().BeOfType<StoredBranchedFromMessage>("затравка сценария: плашка — хвост истории B");

        // B → C: тот же ход, он же последний в B — после него только плашка
        var c = (await _sut.BranchAsync(b.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn)).Session;

        var cHistory = await _historyService.LoadAsync(c.ClaudeSessionId!);
        var marker = cHistory.OfType<StoredBranchedFromMessage>().Should().ContainSingle(
            "плашка в ветке ровно одна — унаследованная от источника не копируется").Subject;
        marker.SourceSessionId.Should().Be(b.Id, "источник — непосредственный, а не прадед");
        marker.SourceName.Should().Be(b.Name);
    }

    // --- Новый ClaudeSessionId — не общий с оригиналом ---

    [Fact]
    public async Task Ветка_ПолучаетСвойClaudeSessionId()
    {
        var (session, _, csid, text1, _) = await SeedBranchableChatAsync("own-csid");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1, SessionManager.ChatBranchInclude.Turn);

        result.Session.ClaudeSessionId.Should().NotBeNullOrEmpty().And.NotBe(csid);
        result.Session.Id.Should().NotBe(session.Id);
    }

    // --- Точный якорь (шаг 5): uuid конца хода из истории бьёт текстовое сопоставление ---

    // Короткий якорный текст без подтверждающего соседа — тот случай, на котором текстовый
    // путь честно отказывает (соседний тест ниже). С записанным uuid конца хода ветвление
    // проходит: граница берётся точным сравнением.
    [Fact]
    public async Task ТочныйЯкорь_КороткийТекст_ВетвитсяПоUuid()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("uuid-anchor",
            userText1: "да", tailUuid1: "a1");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn);

        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        branchHistory.OfType<StoredUserMessage>().Should().ContainSingle(m => m.Text == "да");
    }

    // Контроль к тесту выше: та же затравка БЕЗ uuid — отказ. Без него первый тест не
    // доказывал бы, что сработал именно точный путь.
    [Fact]
    public async Task БезЯкоря_КороткийТекст_Отказ()
    {
        var (session, _, _, text1, _) = await SeedBranchableChatAsync("uuid-anchor-missing",
            userText1: "да");

        var act = () => _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*в памяти модели*");
    }

    // --- Содержимое ТРАНСКРИПТА ветки (блокер финального ревью) ---

    // Путь транскрипта ветки — тот же, что строит BranchAsync: профиль по умолчанию,
    // projects/{уплощённый корень проекта}/{csid ветки}.jsonl
    private string BranchTranscriptPath(Session branch, Project project) =>
        Path.Combine(_llmProviders.UserProfileDir, "projects",
            TranscriptMigrator.FlattenCwd(project.RootPath), branch.ClaudeSessionId + ".jsonl");

    // До фикса резак получал вызов БЕЗ include и всегда резал по концу хода: история ветки
    // обрывалась до якоря (правильно), а её транскрипт включал и якорный вопрос, и прежний
    // ответ на него — модель помнила ровно то, от чего пользователь уходил.
    [Fact]
    public async Task Транскрипт_BeforePrompt_НеСодержитЯкорногоХода()
    {
        var (session, project, _, text1, text2) = await SeedBranchableChatAsync("transcript-before");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 1, text2,
            SessionManager.ChatBranchInclude.BeforePrompt);

        var transcript = await File.ReadAllTextAsync(BranchTranscriptPath(result.Session, project));
        transcript.Should().Contain(text1).And.Contain("ответ 1");
        transcript.Should().NotContain(text2,
            "якорный промпт в память ветки не входит — его текст уходит черновиком в композер");
        transcript.Should().NotContain("ответ 2",
            "прежний ответ на якорный вопрос в памяти ветки остаться не может");
    }

    [Fact]
    public async Task Транскрипт_Turn_СодержитЯкорныйХодЦеликом()
    {
        var (session, project, _, text1, text2) = await SeedBranchableChatAsync("transcript-turn");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 1, text2,
            SessionManager.ChatBranchInclude.Turn);

        var transcript = await File.ReadAllTextAsync(BranchTranscriptPath(result.Session, project));
        transcript.Should().Contain(text1).And.Contain(text2).And.Contain("ответ 2");
    }

    // --- Прерванный якорный ход: AnchorTurnExcluded (блокер финального ревью) ---

    // Затравка с оборванным вторым ходом: в транскрипте tool_use без tool_result, в истории
    // ход есть. Резак на таком хвосте отступает к началу хода — история обязана уехать туда же.
    private async Task<(Session Session, Project Project, string Text1, string Text2)>
        SeedInterruptedChatAsync(string suffix)
    {
        const string text1 = "первый вопрос разговора с запасом символов для якоря";
        const string text2 = "второй вопрос разговора с запасом символов для якоря";

        var dir = MkProjectDir(suffix);
        var project = _projectManager.Create("P-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Прерванный чат " + suffix);
        var csid = "csid-" + Guid.NewGuid().ToString("N")[..12];
        var live = _sut.GetById(session.Id)!;
        live.ClaudeSessionId = csid;

        await _historyService.SaveAsync(csid,
        [
            new StoredUserMessage(text1),
            new StoredTextMessage("ответ 1"),
            new StoredResultMessage("success", 100, 1),
            new StoredUserMessage(text2),
            new StoredTextMessage("начал"),
        ]);

        var flat = TranscriptMigrator.FlattenCwd(project.RootPath);
        var transcriptDir = Path.Combine(_llmProviders.UserProfileDir, "projects", flat);
        Directory.CreateDirectory(transcriptDir);
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"system\",\"subtype\":\"init\",\"sessionId\":\"" + csid + "\",\"uuid\":\"s0\"}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(text1) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 1\"}}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u2\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(text2) + "}}\n");
        // Оборванный ход: tool_use t9 без парного tool_result (кнопка «Стоп»)
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a2\",\"message\":{\"role\":\"assistant\",\"content\":"
            + "[{\"type\":\"text\",\"text\":\"начал\"},{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{}}]}}\n");
        await File.WriteAllTextAsync(Path.Combine(transcriptDir, csid + ".jsonl"), sb.ToString(),
            new UTF8Encoding(false));

        return (live, project, text1, text2);
    }

    // Продуктовое решение: 409 не отдаём — история уезжает к фактической границе транскрипта,
    // а текст прерванного сообщения возвращается черновиком в композер.
    [Fact]
    public async Task ПрерванныйХод_ИсторияСинхроннаТранскрипту_ТекстВЧерновик()
    {
        var (session, project, text1, text2) = await SeedInterruptedChatAsync("interrupted");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 1, text2,
            SessionManager.ChatBranchInclude.Turn);

        result.Draft.Should().Be(text2, "незавершённый ввод возвращается человеку, а не теряется");

        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        branchHistory.OfType<StoredUserMessage>().Should().ContainSingle()
            .Which.Text.Should().Be(text1, "прерванный ход в ленте ветки не остался");
        branchHistory.OfType<StoredTextMessage>().Should().NotContain(m => m.Text == "начал");

        var transcript = await File.ReadAllTextAsync(BranchTranscriptPath(result.Session, project));
        transcript.Should().Contain(text1);
        transcript.Should().NotContain(text2, "лента и память ветки обрезаны по одной границе");
        transcript.Should().NotContain("t9", "непарный tool_use в память ветки не попадает");
    }

    // --- Страховка на смену csid (Session.BranchedFromSessionId) ---

    // Повторяет то, что делает ClaudeSession на system/init: сначала переписывает
    // Session.ClaudeSessionId пришедшим от CLI значением, затем шлёт session_started.
    private async Task SimulateCliInitAsync(string sessionId, string cliSessionId)
    {
        var entry = GetEntry(sessionId);
        var acc = entry.GetType().GetField("Accumulator")!.GetValue(entry)!;
        _sut.GetById(sessionId)!.ClaudeSessionId = cliSessionId;
        var method = typeof(SessionManager).GetMethod("OnMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_sut,
            [sessionId, acc, new SessionStartedMessage(cliSessionId, true, "model", "auto"), 0L])!;
    }

    // Разведка шага 0: CLI 2.1.276 сессию по --resume не форкает. Поведение версионно-зависимое,
    // поэтому подготовленная ветвлением пара «транскрипт + история» обязана убираться, если CLI
    // всё-таки выдал свой session_id — иначе она пролежит мусором до плановой уборки CLI.
    [Fact]
    public async Task СтраховкаCsid_CliВыдалСвоюСессию_ОсиротевшаяПараУбрана()
    {
        var (session, project, _, text1, _) = await SeedBranchableChatAsync("csid-guard");
        var branch = (await _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn)).Session;
        var prepared = branch.ClaudeSessionId!;
        var preparedPath = BranchTranscriptPath(branch, project);
        File.Exists(preparedPath).Should().BeTrue("затравка: ветвление подложило транскрипт под своим csid");
        (await _historyService.LoadAsync(prepared)).Should().NotBeEmpty("затравка: история ветки подготовлена");

        await SimulateCliInitAsync(branch.Id, "csid-" + Guid.NewGuid().ToString("N")[..12]);

        File.Exists(preparedPath).Should().BeFalse("подготовленный транскрипт осиротел — его убирают");
        (await _historyService.LoadAsync(prepared)).Should().BeEmpty("осиротевшая история ветки убрана");
    }

    // Контроль к тесту выше: у обычного чата (не ветки) гейт BranchedFromSessionId закрыт,
    // и смена csid ничего не удаляет — иначе страховка сносила бы чужие транскрипты.
    [Fact]
    public async Task СтраховкаCsid_ОбычныйЧат_ТранскриптНеТрогает()
    {
        var (session, project, csid, _, _) = await SeedBranchableChatAsync("csid-guard-plain");
        var path = Path.Combine(_llmProviders.UserProfileDir, "projects",
            TranscriptMigrator.FlattenCwd(project.RootPath), csid + ".jsonl");
        File.Exists(path).Should().BeTrue("затравка: транскрипт обычного чата на месте");

        await SimulateCliInitAsync(session.Id, "csid-" + Guid.NewGuid().ToString("N")[..12]);

        File.Exists(path).Should().BeTrue("чат не ветка — страховка к нему не применяется");
        (await _historyService.LoadAsync(csid)).Should().NotBeEmpty();
    }

    // Якорь берётся у ЯКОРНОГО хода, а не у последнего в чате: ветка от первого хода не
    // должна утащить второй.
    [Fact]
    public async Task ТочныйЯкорь_БерётсяУСвоегоХода_НеУПоследнего()
    {
        var (session, _, _, text1, text2) = await SeedBranchableChatAsync("uuid-own-turn",
            tailUuid1: "a1", tailUuid2: "a2");

        var result = await _sut.BranchAsync(session.Id, TestUserId, 0, text1,
            SessionManager.ChatBranchInclude.Turn);

        var branchHistory = await _historyService.LoadAsync(result.Session.ClaudeSessionId!);
        branchHistory.OfType<StoredUserMessage>().Should().ContainSingle()
            .Which.Text.Should().Be(text1);
    }
}
