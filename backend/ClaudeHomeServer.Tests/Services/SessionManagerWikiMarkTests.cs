using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using ClaudeHomeServer.Models;
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

// S6i (LLM-wiki, ADR-035 §12а/§12г мера 5): пометка человека для отбора чатов в вики
// (Session.WikiMark, шов ISessionDirectory.SetWikiMark). Поментка обязана переживать
// перезагрузку стора: ночной отбор читает sessions.json, а не память сервера; при этом
// она — настройка чата и не двигает UpdatedAt (соглашение проекта).
// Фикстура своя (как в SessionStatusTests): здесь два менеджера на одном каталоге данных —
// второй и есть «перезагрузка из sessions.json».
public class SessionManagerWikiMarkTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private readonly string _tempDir;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public SessionManagerWikiMarkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_wikimark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            // Автосейв выключен: на диск пишет только то, что явно зовёт сам тест
            ["Session:AutoSaveSeconds"] = "0",
        }).Build();
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Минимальная сборка SessionManager (как в SessionStatusTests): состояние читается из
    // _tempDir, поэтому второй вызов = перезагрузка стора.
    private SessionManager CreateSessionManager()
    {
        var userStore = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var projectManager = new ProjectManager(_config, userStore, appSettings);
        var historyService = new ChatHistoryService(_config);

        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(_config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, knowledge,
            new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(projectManager);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(historyService);
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(_config);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(adapters);
            s.AddSingleton<global::ClaudeHomeServer.Services.FalCostService>(falCost);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.UsageService>(usage);
            s.AddSingleton<global::ClaudeHomeServer.Services.AppSettingsService>(appSettings);
            s.AddSingleton<global::ClaudeHomeServer.Services.UserStore>(userStore);
            s.AddSingleton<global::ClaudeHomeServer.Services.JwtService>(jwt);
            s.AddSingleton<global::Microsoft.AspNetCore.Hosting.Server.IServer>(server.Object);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.LlmProviderRegistry>(llmProviders);
            s.AddSingleton<global::ClaudeHomeServer.Services.FeatureFlagService>(flags);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaManager>(personas);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaBindingsService>(bindings);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ClaudeSubscriptionPool>(subPool);
            s.AddSingleton<global::Microsoft.Extensions.Logging.ILogger<ClaudeHomeServer.Services.SessionManager>>(NullLogger<SessionManager>.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.ILauncherFactory>(TestLauncherFactory.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.SandboxManager>(sandbox);
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(_broadcaster);
        }).Manager;
    }

    [Fact]
    public void SetWikiMark_Skip_ПереживаетПерезагрузкуСтора_НеТрогаетUpdatedAt()
    {
        var updatedBefore = DateTime.UtcNow.AddDays(-3);
        var session = new Session { ProjectId = null, OwnerId = TestUserId, UpdatedAt = updatedBefore };

        var sut = CreateSessionManager();
        sut.AddSessionForTest(session);
        sut.SetWikiMark(session.Id, WikiMarks.Skip).Should().BeTrue();

        session.WikiMark.Should().Be(WikiMarks.Skip);
        // Инвариант: пометка — настройка чата, а не активность (по UpdatedAt идут
        // сортировка списка и непрочитанность; смена настройки их двигать не должна)
        session.UpdatedAt.Should().Be(updatedBefore, "пометка не должна двигать UpdatedAt");
        File.ReadAllText(_sessionsJsonPath).Should().Contain("\"skip\"", "пометка доехала до стора");

        // Перезагрузка: новый менеджер на том же каталоге данных (ночной отбор читает
        // sessions.json, а не память сервера)
        sut.Dispose();
        var stored = CreateSessionManager().GetById(session.Id);
        stored.Should().NotBeNull("чат читается из sessions.json");
        stored!.WikiMark.Should().Be(WikiMarks.Skip, "пометка переживает рестарт");
        stored.UpdatedAt.Should().Be(updatedBefore, "стор возвращает UpdatedAt как был");
    }

    // Стор версии до поля: записи свойства нет — читается как null (аддитивное поле без
    // миграции, BackupSchema.Version не двигается). Формат — как его пишет стор:
    // PascalCase, Status целым числом (Finished = 4).
    [Fact]
    public void СтарыйСторБезWikiMark_ЧитаетсяNull()
    {
        File.WriteAllText(_sessionsJsonPath, """
            [{"Id":"s-legacy","OwnerId":"test-user-id","Status":4,
             "CreatedAt":"2026-10-01T00:00:00Z","UpdatedAt":"2026-10-01T00:00:00Z"}]
            """);

        var stored = CreateSessionManager().GetById("s-legacy");

        stored.Should().NotBeNull("старая запись читается без миграции");
        stored!.WikiMark.Should().BeNull("в сторе свойства нет — пометки нет");
    }

    // «Человек может снять пометку» (ADR-035 §12г мера 5): null — снять.
    [Fact]
    public void SetWikiMark_Null_СнимаетПометку()
    {
        var session = new Session { OwnerId = TestUserId };
        var sut = CreateSessionManager();
        sut.AddSessionForTest(session);
        sut.SetWikiMark(session.Id, WikiMarks.Important).Should().BeTrue();
        session.WikiMark.Should().Be(WikiMarks.Important);

        sut.SetWikiMark(session.Id, null).Should().BeTrue();
        session.WikiMark.Should().BeNull();
    }

    // Валидация на входе: допустимы только значения WikiMarks и null; отказовый вызов
    // не трогает ни чат, ни стор.
    [Fact]
    public void SetWikiMark_НеизвестнаяПометкаИлиНетСессии_FalseБезЗаписиВСтор()
    {
        var session = new Session { OwnerId = TestUserId };
        var sut = CreateSessionManager();
        sut.AddSessionForTest(session);

        sut.SetWikiMark(session.Id, "urgent").Should().BeFalse();
        session.WikiMark.Should().BeNull("отказовый вызов не трогает чат");
        sut.SetWikiMark("does-not-exist", WikiMarks.Skip).Should().BeFalse();

        File.Exists(_sessionsJsonPath).Should().BeFalse("отказовый вызов не пишет стор");
    }

    // Круг сериализации стора (тот же _jsonOpts, что LoadSessions/SaveSessions): поле
    // доезжает до записи и обратно без потери значения.
    [Fact]
    public void WikiMark_ПереживаетКругСериализацииСтора()
    {
        var original = new Session { Id = "s-round", WikiMark = WikiMarks.Important };
        var json = JsonSerializer.Serialize(new List<Session> { original });
        json.Should().Contain("\"WikiMark\":\"important\"");

        var restored = JsonSerializer.Deserialize<List<Session>>(json)!;
        restored[0].WikiMark.Should().Be(WikiMarks.Important);
    }
}
