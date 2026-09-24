using System.Reflection;
using System.Security.Cryptography;
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

/// <summary>
/// Сторож ADR-016 3.7: транскрипт CLI чата локального проекта живёт только на устройстве, и
/// сервер не ищет, не копирует и не удаляет его по своему пути. Ловушка — транскрипт с тем же
/// csid, разложенный на диске сервера ровно там, где его нашёл бы серверный поиск (во всех
/// профилях CLI по уплощённому пути проекта). Каждый вход в TranscriptProbe/TranscriptMigrator/
/// TranscriptBrancher/ArchivedTranscriptStore для такого чата обязан оставить ловушку и
/// соседние каталоги байт в байт: ни чтения с последствиями (ветка, архивная копия, перенос),
/// ни удаления.
/// </summary>
public sealed class SessionManagerLocalTranscriptTests : IDisposable
{
    private const string OwnerId = "local-transcript-user";

    private readonly string _tempDir;
    private readonly string _profilesDir;
    private readonly string _archiveDir;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly SessionManager _sut;

    public SessionManagerLocalTranscriptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_local_transcript_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profilesDir = Path.Combine(_tempDir, "claude-profiles");
        _archiveDir = Path.Combine(_tempDir, "archived-transcripts");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                ["Delivery:AwaitProcessExitSeconds"] = "0",
                [$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a",
                [$"{ClaudeSubscriptionPool.Section}:acc-a:Tier"] = "max",
                [$"{ClaudeSubscriptionPool.Section}:acc-b:OAuthToken"] = "token-b",
            })
            .Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

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
            broadcaster: new TestSessionBroadcaster());
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    private const string UserText1 = "первый вопрос разговора с запасом символов для якоря";
    private const string UserText2 = "второй вопрос разговора с запасом символов для якоря";

    private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Чат локального проекта с резюме-якорем и историей на сервере плюс ловушки: полноценный
    // двухходовой транскрипт того же csid в профиле по умолчанию и в профиле acc-a (провайдер
    // чата) по уплощённому пути проекта. Профиль acc-b пуст — перенос туда был бы виден.
    // Путь проекта нарочно правдоподобен для сервера — промах поиска не случаен.
    private async Task<(Session Session, string Csid)> SeedLocalChatAsync(bool local = true)
    {
        var root = Path.Combine(_tempDir, "device-proj-" + Guid.NewGuid().ToString("N")[..6]);
        var project = local
            ? _projectManager.CreateLocal("L", root, OwnerId, "dev-1")
            : _projectManager.Create("S", Directory.CreateDirectory(root).FullName, OwnerId, OwnerId);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Локальный чат");
        var csid = "csid-" + Guid.NewGuid().ToString("N")[..12];
        var live = _sut.GetById(session.Id)!;
        live.ClaudeSessionId = csid;

        await _historyService.SaveAsync(csid,
        [
            new StoredUserMessage(UserText1),
            new StoredTextMessage("ответ 1"),
            new StoredResultMessage("success", 100, 1),
            new StoredUserMessage(UserText2),
            new StoredTextMessage("ответ 2"),
            new StoredResultMessage("success", 100, 1),
        ]);

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(UserText1) + "}}\n");
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid + "\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 1\"}}\n");
        sb.Append("{\"type\":\"user\",\"sessionId\":\"" + csid + "\",\"uuid\":\"u2\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(UserText2) + "}}\n");
        // Оборванный хвост: серверная проверка целостности сочла бы его повреждённым
        sb.Append("{\"type\":\"assistant\",\"sessionId\":\"" + csid);
        var flat = TranscriptMigrator.FlattenCwd(project.RootPath);
        foreach (var profile in new[]
                 {
                     _llmProviders.UserProfileDir,
                     _llmProviders.GetProfileDir("sub-acc-a"),
                 })
        {
            var dir = Path.Combine(profile, "projects", flat);
            Directory.CreateDirectory(dir);
            // Корни поиска TranscriptProbe — реестр процесса: без регистрации ловушка лежала бы
            // мимо серверного поиска, и сторож проходил бы вакуумно
            TranscriptRoots.AddAllowedRoot(Path.Combine(profile, "projects"));
            File.WriteAllText(Path.Combine(dir, csid + ".jsonl"), sb.ToString(), new UTF8Encoding(false));
        }
        return (live, csid);
    }

    // Состояние всех мест, куда серверные механики транскрипта пишут: профили CLI и архив копий
    private Dictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in new[] { _llmProviders.UserProfileDir, _profilesDir, _archiveDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                result[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    private string? FindResumeTranscript(string sessionId)
    {
        var sessions = (System.Collections.IDictionary)typeof(SessionManager)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_sut)!;
        var method = typeof(SessionManager).GetMethod("FindResumeTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (string?)method.Invoke(_sut, [sessions[sessionId]]);
    }

    [Fact]
    public async Task Архивация_ИВозврат_НеКопируютТранскрипт()
    {
        var (session, _) = await SeedLocalChatAsync();
        var before = Snapshot();

        await _sut.SetArchivedAsync(session.Id, OwnerId, archived: true);
        await _sut.SetArchivedAsync(session.Id, OwnerId, archived: false);

        Snapshot().Should().Equal(before, "архивная копия и её возврат у локального проекта — no-op");
        Directory.Exists(_archiveDir).Should().BeFalse();
    }

    [Fact]
    public async Task Удаление_НеТрогаетФайлПоСерверномуПути()
    {
        var (session, _) = await SeedLocalChatAsync();
        var before = Snapshot();

        await _sut.DeleteAsync(session.Id);

        Snapshot().Should().Equal(before, "одноимённый файл на сервере — чужой, удалять его нельзя");
    }

    [Fact]
    public async Task Ветвление_ЯвныйОтказ_ДоДиска()
    {
        var (session, _) = await SeedLocalChatAsync();
        var before = Snapshot();

        var act = () => _sut.BranchAsync(session.Id, OwnerId, 0, UserText1, SessionManager.ChatBranchInclude.Turn);

        (await act.Should().ThrowAsync<LocalProjectException>())
            .WithMessage(ProjectCapabilities.TranscriptOnDeviceReason);
        Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task СменаПровайдера_НеПереноситТранскрипт()
    {
        var (session, _) = await SeedLocalChatAsync();
        session.Provider.Should().Be("acc-a");
        var before = Snapshot();

        var updated = await _sut.MigrateProviderAsync(session.Id, OwnerId, "sonnet", subscriptionKey: "acc-b");

        updated.Provider.Should().Be("acc-b", "провайдер меняется, транскрипт остаётся на устройстве");
        Snapshot().Should().Equal(before, "перенос между профилями у локального проекта пропускается");
    }

    [Fact]
    public async Task ПоискТранскриптаДляResume_НеИщетНаСервере()
    {
        var (session, _) = await SeedLocalChatAsync();

        FindResumeTranscript(session.Id).Should().BeNull(
            "целость транскрипта на --resume проверяет CLI устройства, не серверный путь");
    }

    [Fact]
    public async Task Контроль_СерверныйПроект_ЛовушкуНаходит()
    {
        // Обратная половина: у серверного проекта тот же посев находится — иначе ловушка
        // лежала бы мимо серверного поиска, и сторож выше проходил бы вакуумно
        var (session, _) = await SeedLocalChatAsync(local: false);

        FindResumeTranscript(session.Id).Should().NotBeNull();
    }
}
