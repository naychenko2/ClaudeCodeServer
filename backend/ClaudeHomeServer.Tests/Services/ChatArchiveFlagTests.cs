using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тесты шага 1 плана «Архив чатов» (v4): производный признак IsArchived, мутатор
// SetArchived (не двигает UpdatedAt/LastReadAt, копирует/возвращает транскрипт) и защита
// UpdatedAt у «не-активностей» — пакетная простановка значков, RetitleAsync, UpdateAsync.
// Своя минимальная сборка SessionManager (как SessionManagerSubscriptionMigrationTests):
// тестам RetitleAsync нужен cheap-раннер, который общий SessionManagerTests не передаёт.
public class ChatArchiveFlagTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;

    public ChatArchiveFlagTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_archive_flag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // --- Производный признак: чистая модель, без SessionManager ---

    [Fact]
    public void IsArchived_ЧатНеАрхивирован_False()
    {
        new Session { UpdatedAt = DateTime.UtcNow }.IsArchived.Should().BeFalse();
    }

    [Fact]
    public void IsArchived_Архивирован_БезАктивности_True()
    {
        var now = DateTime.UtcNow;
        // ArchivedAt ставится ПОСЛЕ последней активности: равный и меньший UpdatedAt — в архиве
        new Session { UpdatedAt = now, ArchivedAt = now }.IsArchived.Should().BeTrue();
        new Session { UpdatedAt = now.AddHours(-1), ArchivedAt = now }.IsArchived.Should().BeTrue();
    }

    [Fact]
    public void IsArchived_АктивностьПослеАрхивации_СнимаетАрхивБезМутатора()
    {
        var archivedAt = DateTime.UtcNow;
        var chat = new Session { UpdatedAt = archivedAt, ArchivedAt = archivedAt };

        // Активность = UpdatedAt двигается (ход, сообщение — любое из десятков мест
        // SessionManager) — отдельный вызов «снять архив» не нужен
        chat.UpdatedAt = archivedAt.AddMinutes(1);
        chat.IsArchived.Should().BeFalse();
    }

    // --- SetArchived ---

    [Fact]
    public void SetArchived_СтавитПоля_иНеДвигаетUpdatedAtИLastReadAt()
    {
        var (sut, projects) = BuildSut();
        var chat = NewChat(sut, projects);
        chat.LastReadAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt0 = chat.UpdatedAt;
        var lastRead0 = chat.LastReadAt.Value;

        sut.SetArchived(chat.Id, archived: true, by: "user").Should().NotBeNull();

        chat.ArchivedAt.Should().NotBeNull();
        chat.ArchivedBy.Should().Be("user");
        chat.ArchiveBatchId.Should().BeNull("ручная архивация — без пачки правила");
        chat.IsArchived.Should().BeTrue();
        chat.UpdatedAt.Should().Be(updatedAt0, "архивация — не активность: сортировка и непрочитанность не меняются");
        chat.LastReadAt.Should().Be(lastRead0);
    }

    [Fact]
    public void SetArchived_Возврат_СбрасываетПоля_иНеДвигаетВремена()
    {
        var (sut, projects) = BuildSut();
        var chat = NewChat(sut, projects);
        sut.SetArchived(chat.Id, archived: true, by: "rule", batchId: "batch-1");
        var updatedAt0 = chat.UpdatedAt;

        sut.SetArchived(chat.Id, archived: false, by: "user").Should().NotBeNull();

        chat.ArchivedAt.Should().BeNull();
        chat.ArchivedBy.Should().BeNull();
        chat.ArchiveBatchId.Should().BeNull();
        chat.IsArchived.Should().BeFalse();
        chat.UpdatedAt.Should().Be(updatedAt0, "возврат не всплывает чат наверх списка");
    }

    [Fact]
    public void SetArchived_НесуществующийЧат_Null()
    {
        var (sut, projects) = BuildSut();
        sut.SetArchived("no-such-chat", archived: true, by: "user").Should().BeNull();
    }

    [Fact]
    public void АктивностьПинПослеАрхивации_ВозвращаетЧатИзАрхива()
    {
        // Пин двигает UpdatedAt — осознанное решение плана («закрепление означает: чат
        // нужен»), и это живой публичный путь «активность снимает архив сама»
        var (sut, projects) = BuildSut();
        var chat = NewChat(sut, projects);
        sut.SetArchived(chat.Id, archived: true, by: "user");
        chat.IsArchived.Should().BeTrue();

        sut.SetPinned(chat.Id, true).Should().BeTrue();

        chat.IsPinned.Should().BeTrue();
        chat.IsArchived.Should().BeFalse("любая активность (UpdatedAt > ArchivedAt) снимает архив сама");
    }

    // --- Сторож: пакетный прогон значков не выводит чат из архива ---

    [Fact]
    public async Task SetChatIconsAsync_АрхивныйЧат_ПропущенБезВызоваМодели()
    {
        // Стаб считает вызовы: архивный чат с пустым Topic не должен дойти до модели
        var cheap = new CountingCheapRunner("{\"iconName\": \"Cat\"}");
        var (sut, projects) = BuildSut(cheap);
        var archived = NewChat(sut, projects);
        sut.SetArchived(archived.Id, archived: true, by: "user");
        var updatedAt0 = archived.UpdatedAt;

        var result = await sut.SetChatIconsAsync(TestUserId, CancellationToken.None);

        // Единственный чат владельца — архивный: модель не звалась, чат остался в архиве
        cheap.Calls.Should().Be(0,
            "архивные не проходят предфильтр — иначе один клик «Проставить значки» вернул бы из архива все старые чаты и заказал сотни вызовов модели");
        result.Processed.Should().Be(0);
        result.Skipped.Should().Be(1);
        archived.Topic.Should().BeNull();
        archived.IsArchived.Should().BeTrue();
        archived.UpdatedAt.Should().Be(updatedAt0);
    }

    [Fact]
    public async Task SetChatIconsAsync_ОбычныйЧат_ДошёлДоМодели()
    {
        // Контроль того же предфильтра: обычный чат с пустым Topic модель получает.
        // Значок ставится только по переписке — кладём историю до создания чата
        var cheap = new CountingCheapRunner("{\"iconName\": \"Cat\"}");
        var (sut, projects) = BuildSut(cheap);
        var chat = NewChatWithHistory(sut, projects);

        var result = await sut.SetChatIconsAsync(TestUserId, CancellationToken.None);

        cheap.Calls.Should().Be(1);
        result.Processed.Should().Be(1);
        chat.Topic.Should().Be("Cat");
    }

    // --- RetitleAsync и UpdateAsync у архивного чата ---

    [Fact]
    public async Task RetitleAsync_АрхивныйЧат_Переименовывает_НеДвигаяUpdatedAt()
    {
        var (sut, projects) = BuildSut(new CountingCheapRunner("{\"title\": \"Новое название\"}"));
        var chat = NewChatWithHistory(sut, projects);
        sut.SetArchived(chat.Id, archived: true, by: "user");
        var updatedAt0 = chat.UpdatedAt;

        var retitled = await sut.RetitleAsync(TestUserId, chat.Id, CancellationToken.None);

        retitled!.Name.Should().Be("Новое название");
        retitled.NameLocked.Should().BeTrue();
        retitled.UpdatedAt.Should().Be(updatedAt0,
            "«Обновить название» доступен в разделе «Архив» и не выводит чат из архива");
        retitled.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_АрхивныйЧат_ПравкаИмениНеДвигаетUpdatedAt()
    {
        var (sut, projects) = BuildSut();
        var chat = NewChat(sut, projects);
        sut.SetArchived(chat.Id, archived: true, by: "user");
        var updatedAt0 = chat.UpdatedAt;

        var updated = await sut.UpdateAsync(chat.Id, TestUserId, name: "Переименован", model: null, effort: null);

        updated!.Name.Should().Be("Переименован");
        updated.UpdatedAt.Should().Be(updatedAt0, "правка имени/модели/тегов архивного чата — не активность");
        updated.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ОбычныйЧат_ПравкаИмениДвигаетUpdatedAt()
    {
        var (sut, projects) = BuildSut();
        var chat = NewChat(sut, projects);
        var updatedAt0 = chat.UpdatedAt;

        await sut.UpdateAsync(chat.Id, TestUserId, name: "Переименован", model: null, effort: null);

        chat.UpdatedAt.Should().BeAfter(updatedAt0);
    }

    // --- Формат стора ---

    [Fact]
    public void BackupSchema_ВерсияНеИзменена()
    {
        // Поля архива аддитивны (nullable с дефолтом) — версию формата не двигаем
        BackupSchema.Version.Should().Be(8);
    }

    // --- Подключение стора копий: SetArchived копирует и возвращает транскрипт ---

    [Fact]
    public async Task SetArchived_КопируетТранскрипт_иВозвратКладётОбратно()
    {
        var (sut, projects) = BuildSut();
        var (chat, project) = NewChatWithProject(sut, projects);
        chat.ClaudeSessionId = "csid-copy-abc123";

        // Транскрипт лежит в профиле CLI по правилу уплощения cwd
        var cwdDir = Path.Combine(_tempDir, "claude-profile", "projects",
            TranscriptMigrator.FlattenCwd(project.RootPath));
        Directory.CreateDirectory(cwdDir);
        var src = Path.Combine(cwdDir, chat.ClaudeSessionId + ".jsonl");
        await File.WriteAllTextAsync(src, "переписка");

        sut.SetArchived(chat.Id, archived: true, by: "user");

        var archivedCopy = Path.Combine(_tempDir, "archived-transcripts", chat.ClaudeSessionId + ".jsonl");
        File.Exists(archivedCopy).Should()
            .BeTrue("архивация копирует транскрипт — ретенция CLI не съест контекст возврата");

        // Ретенция CLI вычистила оригинал; возврат обязан положить копию обратно —
        // по путям, резолвленным НА МОМЕНТ возврата
        File.Delete(src);
        sut.SetArchived(chat.Id, archived: false, by: "user");

        File.Exists(src).Should().BeTrue("возврат кладёт транскрипт обратно в профиль");
        (await File.ReadAllTextAsync(src)).Should().Be("переписка");
    }

    // --- Сборка ---

    // История последней сборки BuildSut: тестам RetitleAsync нужна для записи переписки.
    // Каждый тест зовёт BuildSut ровно раз, поэтому «последняя» здесь = «своя».
    private ChatHistoryService? _historyForBuild;

    private (SessionManager Sut, ProjectManager Projects) BuildSut(ICheapTextRunner? cheap = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            // Автосейв выключен: фоновая запись стора не должна вмешиваться между правкой
            // и ассертами (та же причина, что в SessionManagerTests)
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            // Профиль CLI внутри temp: и поиск транскриптов, и возврат копии не идут
            // в настоящий ~/.claude пользователя
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _historyForBuild = new ChatHistoryService(config);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, hub.Object, _historyForBuild, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, cheap: cheap), projectManager);
    }

    private Session NewChat(SessionManager sut, ProjectManager projects, string? resumeSessionId = null) =>
        NewChatWithProject(sut, projects, resumeSessionId).Chat;

    // Чат с перепиской: история на диске ДО создания + csid как resumeSessionId — так её
    // увидит Accumulator (после создания с диска не перечитывается)
    private Session NewChatWithHistory(SessionManager sut, ProjectManager projects,
        string csid = "csid-history-abc123")
    {
        _historyForBuild!.SaveAsync(csid,
        [
            new StoredUserMessage("Обсуждаем архитектуру архива"),
            new StoredTextMessage("Предлагаю производный признак"),
        ]).GetAwaiter().GetResult();
        return NewChat(sut, projects, resumeSessionId: csid);
    }

    // resumeSessionId имитирует чат, продолжающий чужой транскрипт: история при этом
    // обязана лежать на диске ДО создания (StartNewSessionAsync читает её один раз в
    // Accumulator и с диска больше не перечитывает)
    private (Session Chat, Project Project) NewChatWithProject(
        SessionManager sut, ProjectManager projects, string? resumeSessionId = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект архива", dir, TestUserId, TestUsername);
        var chat = sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: resumeSessionId)
            .GetAwaiter().GetResult();
        return (chat, project);
    }

    // Стаб дешёвого раннера: считает вызовы и отдаёт заданный ответ (RetitleAsync-тесты;
    // в тесте значков Calls — детектор «модель вызвана или нет»)
    private sealed class CountingCheapRunner(string answer) : ICheapTextRunner
    {
        public int Calls;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
