using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тесты шага 5 плана «Архив чатов» (v4): место chat-digest, кэш и инвалидация сводки
// (UpdatedAt > ArchiveSummaryAt = не актуальна), запрет десктопным чатам, защита от
// параллельных кликов (_inFlight) и очередь «1 поток на владельца», приоритет текста
// карточки. Сборка SessionManager — как в ChatArchiveFlagTests (свой минимальный граф).
public class ChatDigestServiceTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private ChatHistoryService _history = null!;

    public ChatDigestServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_digest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Место каталога ───────────────────────────────────────────────────────

    [Fact]
    public void МестоChatDigest_Зарегистрировано_SmallВГруппеСессии()
    {
        var place = LocalActionCatalog.Find(LocalActionCatalog.ChatDigest);

        place.Should().NotBeNull();
        place!.Profile.Should().Be(CheapProfile.Small);
        place.Group.Should().Be("Сессии");
        place.Agentic.Should().BeFalse();
        place.Title.Should().Be("Сводка карточки архива");
    }

    // ─── FreshSummary: кэш и инвалидация ─────────────────────────────────────

    [Fact]
    public void FreshSummary_СобранаПослеПоследнейАктивности_Актуальна()
    {
        var now = DateTime.UtcNow;
        var s = new Session { UpdatedAt = now, ArchiveSummary = "Обсудили релиз", ArchiveSummaryAt = now };
        ChatDigestService.FreshSummary(s).Should().Be("Обсудили релиз");

        s = new Session { UpdatedAt = now.AddHours(-1), ArchiveSummary = "Обсудили релиз", ArchiveSummaryAt = now };
        ChatDigestService.FreshSummary(s).Should().Be("Обсудили релиз");
    }

    [Fact]
    public void FreshSummary_АктивностьПослеСборки_СводкаНеВыдаётсяЗаАктуальную()
    {
        // чат вернули, написали в него, заархивировали снова — старый итог карточке не показывают
        var s = new Session
        {
            UpdatedAt = DateTime.UtcNow,
            ArchiveSummary = "Обсудили релиз",
            ArchiveSummaryAt = DateTime.UtcNow.AddHours(-2),
        };
        ChatDigestService.FreshSummary(s).Should().BeNull();
    }

    [Fact]
    public void FreshSummary_БезСводки_Null()
    {
        ChatDigestService.FreshSummary(new Session { ArchiveSummaryAt = DateTime.UtcNow }).Should().BeNull();
        ChatDigestService.FreshSummary(new Session { ArchiveSummary = "" }).Should().BeNull();
    }

    // ─── Сборка сводки ────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildDigest_СобираетСводку_иКэшируетВЧате()
    {
        var runner = new CountingRunner("Обсудили настройку значков проекта и закрыли задачу.");
        var (sut, chat) = BuildSut(runner);

        var updated = await sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        runner.LastActionKey.Should().Be(LocalActionCatalog.ChatDigest);
        runner.LastOwnerId.Should().Be(TestUserId);
        updated.ArchiveSummary.Should().Be("Обсудили настройку значков проекта и закрыли задачу.");
        updated.ArchiveSummaryAt.Should().NotBeNull();
        // кэш свежий по построению: собран после последней активности
        ChatDigestService.FreshSummary(updated).Should().NotBeNull();
    }

    [Fact]
    public async Task BuildDigest_ПустойЧат_ОшибкаГенерации()
    {
        var runner = new CountingRunner("сводка");
        var (sut, chat) = BuildSut(runner, withHistory: false);

        var act = () => sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);
        await act.Should().ThrowExactlyAsync<DigestGenerationException>()
            .WithMessage("*нет сообщений*");
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BuildDigest_ДесктопномуЧату_СводкаНеСтроится()
    {
        var runner = new CountingRunner("сводка");
        var (sut, chat) = BuildSut(runner);
        chat.DesktopChat = true;

        var act = () => sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("*десктоп*");
        runner.Calls.Should().Be(0);
        chat.ArchiveSummary.Should().BeNull();
    }

    [Fact]
    public async Task BuildDigest_Кэш_ВторойКликНеЗовётМодель()
    {
        var runner = new CountingRunner("Первая сводка.");
        var (sut, chat) = BuildSut(runner);

        await sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);
        var again = await sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        again.ArchiveSummary.Should().Be("Первая сводка.");
    }

    [Fact]
    public async Task BuildDigest_АктивностьПослеСборки_Пересобирает()
    {
        var runner = new CountingRunner("Первая сводка.");
        var (sut, chat) = BuildSut(runner);
        await sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);

        // активность чата инвалидировала кэш — сводку нужно собрать заново. Сводку
        // состариваем назад, активность — честное «сейчас» (как в реальном чате)
        runner.Answer = "Вторая сводка после продолжения.";
        chat.ArchiveSummaryAt = DateTime.UtcNow.AddHours(-2);
        chat.UpdatedAt = DateTime.UtcNow;

        var updated = await sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(2);
        updated.ArchiveSummary.Should().Be("Вторая сводка после продолжения.");
        ChatDigestService.FreshSummary(updated).Should().NotBeNull();
    }

    [Fact]
    public async Task BuildDigest_ЧужойЧат_KeyNotFound()
    {
        var runner = new CountingRunner("сводка");
        var (sut, chat) = BuildSut(runner);

        var act = () => sut.BuildDigestAsync("чужой-пользователь", chat.Id, CancellationToken.None);
        await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
    }

    // ─── Параллельные клики и очередь ────────────────────────────────────────

    [Fact]
    public async Task BuildDigest_ПараллельныеКлики_ОдинВызовМодели()
    {
        var runner = new GatedRunner("Сводка одного раза.");
        var (sut, chat) = BuildSut(runner);

        // первый клик синхронно доходит до TryAdd и подвисает в модели; второй успевает
        // дойти до TryAdd тоже синхронно — гонки «кто первый» нет
        var first = sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);
        var second = () => sut.BuildDigestAsync(TestUserId, chat.Id, CancellationToken.None);
        await second.Should().ThrowExactlyAsync<DigestInProgressException>();

        runner.Release();
        var updated = await first;

        runner.Calls.Should().Be(1);
        updated.ArchiveSummary.Should().Be("Сводка одного раза.");
    }

    [Fact]
    public async Task BuildDigest_ОчередьОдинПотокНаВладельца_РазныеЧатыИдутПодряд()
    {
        var runner = new GatedRunner("Сводка.");
        var (mgr, projects, notes) = BuildManager();
        var sut = new ChatDigestService(mgr, projects, notes, runner,
            NullLogger<ChatDigestService>.Instance);
        var chatA = NewProjectChat(mgr, projects, withHistory: true);
        var chatB = NewProjectChat(mgr, projects, withHistory: true);

        var a = sut.BuildDigestAsync(TestUserId, chatA.Id, CancellationToken.None);
        var b = sut.BuildDigestAsync(TestUserId, chatB.Id, CancellationToken.None);

        // A уже в модели, B стоит в очереди владельца — второго вызова модели нет, пока
        // A не отпустил гейт (порядок гарантирует семафор, без Task.Delay)
        await runner.EnteredModel.Task.WaitAsync(TimeSpan.FromSeconds(30));
        runner.Calls.Should().Be(1);
        b.IsCompleted.Should().BeFalse();

        runner.Release();
        await Task.WhenAll(a, b);
        runner.Calls.Should().Be(2);
        chatA.ArchiveSummary.Should().NotBeNull();
        chatB.ArchiveSummary.Should().NotBeNull();
    }

    // ─── Мутатор кэша ────────────────────────────────────────────────────────

    [Fact]
    public void SetArchiveSummary_НеДвигаетUpdatedAt_иСбрасываетКэш()
    {
        var (mgr, projects, _) = BuildManager();
        var chat = NewProjectChat(mgr, projects);
        var updatedAt0 = chat.UpdatedAt;

        mgr.SetArchiveSummary(chat.Id, "Сводка");
        chat.ArchiveSummary.Should().Be("Сводка");
        chat.ArchiveSummaryAt.Should().NotBeNull();
        chat.UpdatedAt.Should().Be(updatedAt0);

        mgr.SetArchiveSummary(chat.Id, null);
        chat.ArchiveSummary.Should().BeNull();
        chat.ArchiveSummaryAt.Should().BeNull();
        chat.UpdatedAt.Should().Be(updatedAt0);
    }

    // ─── Приоритет текста карточки ───────────────────────────────────────────

    [Fact]
    public void CardText_СвежаяСводкаВышеВсего()
    {
        var (sut, chat) = BuildSut();
        var now = DateTime.UtcNow;
        chat.ArchiveSummary = "Свежая сводка";
        chat.ArchiveSummaryAt = now;
        chat.UpdatedAt = now;
        chat.SummaryNoteId = "какая-то-заметка";
        chat.LastMessage = "последняя реплика";

        sut.CardText(chat).Should().Be("Свежая сводка");
    }

    [Fact]
    public void CardText_УстаревшаяСводка_ПервыеСтрокиЗаметки()
    {
        var (sut, chat, notes) = BuildSutWithNotes();
        var note = notes.Create(TestUserId, new CreateNoteRequest(
            Title: "Итог чата",
            Content: "---\ntitle: Итог чата\n---\n\nОбсудили миграцию значков.\nРешили идти двухходовым подбором.\n\nОстальное — детали.",
            Source: null));
        chat.SummaryNoteId = note.Id;
        chat.ArchiveSummary = "Устаревшая сводка";
        chat.ArchiveSummaryAt = DateTime.UtcNow.AddHours(-3);
        chat.UpdatedAt = DateTime.UtcNow;

        // frontmatter срезан, пустая строка пропущена — взяты первые строки тела
        var nl = Environment.NewLine;
        sut.CardText(chat).Should().Be(
            "Обсудили миграцию значков." + nl + "Решили идти двухходовым подбором." + nl + "Остальное — детали.");
    }

    [Fact]
    public void CardText_НетНичего_КромеПоследнейРеплики()
    {
        var (sut, chat) = BuildSut();
        chat.LastMessage = "  Последняя реплика чата  ";
        sut.CardText(chat).Should().Be("Последняя реплика чата");
    }

    [Fact]
    public void CardText_ПустоСовсем_СообщенийНет()
    {
        var (sut, chat) = BuildSut();
        chat.SummaryNoteId = "несуществующая-заметка"; // заметка удалена — не страшно
        sut.CardText(chat).Should().Be(ChatDigestService.NoMessagesText);
    }

    [Fact]
    public void FirstLines_ДлиннаяСтрокаРежется()
    {
        var text = ChatDigestService.FirstLines(new string('а', 500))!;
        text.Length.Should().BeLessThanOrEqualTo(301);
        text.Should().EndWith("…");
    }

    // ─── Сборка SUT ──────────────────────────────────────────────────────────

    private (ChatDigestService Sut, Session Chat) BuildSut(ICheapTextRunner runner, bool withHistory = true)
    {
        var (mgr, projects, notes) = BuildManager();
        return (new ChatDigestService(mgr, projects, notes, runner,
            NullLogger<ChatDigestService>.Instance), NewProjectChat(mgr, projects, withHistory));
    }

    private (ChatDigestService Sut, Session Chat) BuildSut()
    {
        var (mgr, projects, notes) = BuildManager();
        return (new ChatDigestService(mgr, projects, notes, new CountingRunner("x"),
            NullLogger<ChatDigestService>.Instance), NewProjectChat(mgr, projects));
    }

    private (ChatDigestService Sut, Session Chat, NotesService Notes) BuildSutWithNotes()
    {
        var (mgr, projects, notes) = BuildManager();
        return (new ChatDigestService(mgr, projects, notes, new CountingRunner("x"),
            NullLogger<ChatDigestService>.Instance), NewProjectChat(mgr, projects), notes);
    }

    private (SessionManager Manager, ProjectManager Projects, NotesService Notes) BuildManager()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            // Автосейв выключен: фоновая запись стора не должна вмешиваться между правкой
            // и ассертами (та же причина, что в SessionManagerTests)
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            // Профиль CLI внутри temp: поиск транскриптов не идёт в настоящий ~/.claude
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _history = new ChatHistoryService(config);

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

        var manager = new SessionManager(projectManager, hub.Object, _history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, cheap: null);
        return (manager, projectManager, notesSvc);
    }

    private Session NewProjectChat(SessionManager mgr, ProjectManager projects, bool withHistory = false)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект сводки", dir, TestUserId, TestUsername);
        // История обязана лежать на диске ДО создания чата: StartNewSessionAsync читает её
        // один раз в Accumulator и с диска больше не перечитывает (см. ChatArchiveFlagTests)
        string? csid = null;
        if (withHistory)
        {
            csid = "digest_hist_" + Guid.NewGuid().ToString("N");
            _history.SaveAsync(csid,
            [
                new StoredUserMessage("Настройте значки проектов"),
                new StoredTextMessage("Готово, значки настроены"),
            ]).GetAwaiter().GetResult();
        }
        return mgr.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid).GetAwaiter().GetResult();
    }

    // Стаб раннера: считает вызовы, помнит параметры последнего, отвечает фиксированно
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public string Answer = answer;
        public string? LastActionKey;
        public string? LastOwnerId;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastOwnerId = ownerId;
            return Task.FromResult(Answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(Answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Стаб раннера с гейтом: вызов модели висит, пока тест не отпустит гейт. Событие
    // EnteredModel даёт тесту детерминированную точку «модель вызвана» (без Task.Delay)
    private sealed class GatedRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnteredModel { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            EnteredModel.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return answer;
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
