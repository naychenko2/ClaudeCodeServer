using System.Collections.Concurrent;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Перенос в новый чат (шаг B-4, docs/features/chat-handoff.md): выбор пути по статусу кэша
// последнего хода (тёплый — записку пишет сам чат, холодный — дешёвый раннер по транскрипту),
// место chat-handoff, разделы промпта, черновик со ссылкой на старый чат, отказы (локаль,
// пустой чат), 409 на двойной клик, неизменность старого чата. Сборка SessionManager — как в
// ChatDigestServiceTests (свой минимальный граф).
public class ChatHandoffServiceTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private ChatHistoryService _history = null!;

    public ChatHandoffServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_handoff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Helpers.TestFs.DeleteDirectoryResilient(_tempDir);
    }

    // ─── Место каталога ───────────────────────────────────────────────────────

    [Fact]
    public void МестоChatHandoff_LargeБезЛокали_СвойПотолокВывода()
    {
        var place = LocalActionCatalog.Find(LocalActionCatalog.ChatHandoff);

        place.Should().NotBeNull();
        place!.Profile.Should().Be(CheapProfile.Large);
        place.DefaultLocal.Should().BeFalse();
        place.CloudNumPredict.Should().Be(4096);
        place.Group.Should().Be("Сессии");
        place.Agentic.Should().BeFalse();
    }

    // ─── Промпты: одна спецификация записки на оба пути ───────────────────────

    [Fact]
    public void Промпт_СемьРазделовПоПорядку_иЗапретВыдумывать()
    {
        var prompt = ChatHandoffService.BuildPrompt("Чат", "Пользователь:\nпривет");

        string[] sections =
        [
            "### Цель", "### Решения и их причины", "### От чего отказались", "### Изменённые файлы",
            "### Где остановились", "### Следующий шаг", "### Открытые вопросы",
        ];
        var last = -1;
        foreach (var s in sections)
        {
            var at = prompt.IndexOf(s, StringComparison.Ordinal);
            at.Should().BeGreaterThan(last, $"раздел «{s}» обязан идти после предыдущего");
            last = at;
        }
        prompt.Should().Contain("Не выдумывай того, чего нет в транскрипте");
        prompt.Should().Contain("Транскрипт:\nПользователь:\nпривет");
    }

    [Fact]
    public void ТёплыйПромпт_ТеЖеРазделыЧтоУХолодного_ТолькоТекстом()
    {
        var cold = ChatHandoffService.BuildPrompt("Чат", "Пользователь:\nпривет");
        var warm = ChatHandoffService.BuildWarmPrompt();

        var spec = cold[cold.IndexOf("Разделы —", StringComparison.Ordinal)..cold.IndexOf("Правила:", StringComparison.Ordinal)];
        warm.Should().Contain(spec, "записки двух путей не должны разойтись по составу");
        warm.Should().Contain("Не вызывай инструменты").And.Contain("Не выдумывай того, чего нет в нашем разговоре");
        warm.Should().NotContain("Транскрипт:");
    }

    // ─── Границы статуса кэша (данные B-1) ────────────────────────────────────

    [Fact]
    public void IsCacheWarm_ГраницаТTL_НеТёплый()
    {
        // Интервал живости кэша half-open [anchor, anchor+TTL): ровно на границе якоря и
        // срока кэш уже не читается, за секунду до — ещё читается
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var history = new List<StoredMessage>
        {
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, now, "5m", null, PromptCacheMissReason.None) },
        };
        ChatHandoffService.IsCacheWarm(history, now + 300_000).Should().BeFalse();
        ChatHandoffService.IsCacheWarm(history, now + 299_999).Should().BeTrue();
    }

    [Fact]
    public void IsCacheWarm_БезДанныхКэша_Холодный()
    {
        // Пустая история и result без PromptCache (сторонний провайдер, старая история) — холодный путь
        ChatHandoffService.IsCacheWarm([], 1).Should().BeFalse();
        var history = new List<StoredMessage>
        {
            new StoredResultMessage("success", 1, 1),
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, null, null, null, PromptCacheMissReason.Unavailable) },
        };
        ChatHandoffService.IsCacheWarm(history, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Should().BeFalse();
    }

    [Fact]
    public void IsCacheWarm_БезЯкоряНеГаситБолееРаннийТёплыйЯкорь()
    {
        // Тот же приём, что в F-1a: берётся последний result С данными. newer result без
        // якоря (сторонний провайдер, старая история) — не сигнал, живой якорь более
        // раннего хода он не гасит: кэш привязан к разговору, не к провайдеру
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var history = new List<StoredMessage>
        {
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, now, "5m", null, PromptCacheMissReason.None) },
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(0, 0, null, null, null, PromptCacheMissReason.Unavailable) },
        };

        ChatHandoffService.IsCacheWarm(history, now + 1000).Should().BeTrue();
    }

    [Fact]
    public void IsCacheWarm_ЯкорьБезВалидногоTTL_ПропускаетсяКакУФронта()
    {
        // Расхождение с promptCacheState (promptCache.ts): result с якорем, но без валидного
        // ttl, не гасит более ранний живой якорь — идём дальше, а не отвечаем «холодный»
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var history = new List<StoredMessage>
        {
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, now, "5m", null, PromptCacheMissReason.None) },
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, now, null, null, PromptCacheMissReason.None) },
            new StoredResultMessage("success", 1, 1)
            { PromptCache = new PromptCacheState(1000, 0, now, "2d", null, PromptCacheMissReason.None) },
        };

        ChatHandoffService.IsCacheWarm(history, now + 1000).Should().BeTrue();
        // Единственный result без валидного TTL — по-прежнему холодный
        ChatHandoffService.IsCacheWarm([history[1]], now + 1000).Should().BeFalse();
    }

    // ─── Холодный путь ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handoff_БезТёплогоКэша_ХолодныйПуть_ЧерновикСЗапискойИСсылкойНаСтарый()
    {
        var runner = new CountingRunner("### Цель\nнастроить значки");
        var (sut, mgr, chat) = BuildSut(runner);
        chat.Model = "claude-sonnet-5-5";

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        runner.LastActionKey.Should().Be(LocalActionCatalog.ChatHandoff);
        runner.LastOwnerId.Should().Be(TestUserId);
        runner.LastPrompt.Should().Contain("Настройте значки проектов");

        result.Warm.Should().BeFalse();
        result.Session.Id.Should().NotBe(chat.Id);
        result.Session.ProjectId.Should().Be(chat.ProjectId);
        result.Session.Model.Should().Be("claude-sonnet-5-5", "поля §11 наследуются");
        result.Session.BranchedFromSessionId.Should().BeNull("перенос — не ветка");
        mgr.GetById(result.Session.Id).Should().NotBeNull();

        result.Draft.Should().Contain("### Цель\nнастроить значки");
        result.Draft.Split('\n')[^1].Should().Be($"Исходный чат: {chat.Id} — детали через chats_history.");
        result.Draft.Should().NotContain("временный");
    }

    [Fact]
    public async Task Handoff_СтарыйЧатНеМеняется()
    {
        var (sut, _, chat) = BuildSut(new CountingRunner("записка"));
        var updatedAt = chat.UpdatedAt;
        var csid = chat.ClaudeSessionId;

        await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        chat.UpdatedAt.Should().Be(updatedAt);
        chat.ClaudeSessionId.Should().Be(csid);
        chat.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Handoff_ПреемникНеНаследуетCostState_НовыйРазговорСНуля()
    {
        // Перенос — не ветка: у преемника своя csid и свой накопленный итог, снимок
        // cost-state источника ему не нужен (иначе первый ход считался бы от чужой истории)
        var (sut, _, chat) = BuildSut(new CountingRunner("записка"));
        chat.LastCostState = new SessionCostState { ClaudeSessionId = "csid-source", TotalCostUsd = 3.5 };

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        result.Session.LastCostState.Should().BeNull();
        result.Session.BranchedFromSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Handoff_ВременныйЧат_ПредупреждениеВЧерновике_НовыйНеВременный()
    {
        var (sut, _, chat) = BuildSut(new CountingRunner("записка"));
        chat.ExpiresAfterMinutes = 60;

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        result.Draft.Should().Contain("Исходный чат временный");
        result.Session.ExpiresAfterMinutes.Should().BeNull();
    }

    [Fact]
    public async Task Handoff_КэшОстыл_ХолодныйПуть()
    {
        // Якорь старше TTL: кэш холодный, даже если когда-то был тёплым
        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10 * 60_000;
        var runner = new CountingRunner("### Цель\nхолодный путь");
        var (sut, _, chat) = BuildSut(runner, cache: new PromptCacheState(1000, 0, old, "5m", null, PromptCacheMissReason.None));

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        result.Warm.Should().BeFalse();
    }

    // ─── Тёплый путь ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Handoff_ТёплыйКэш_ЗапискуПишетСамЧат_РаннерНеЗовётся()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runner = new CountingRunner("не должна вызываться");
        var (sut, _, chat) = BuildSut(runner, cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));

        var sentPrompt = "";
        sut.SendOverride = (_, prompt, _) =>
        {
            sentPrompt = prompt;
            return Task.FromResult<SendAndWaitResult>(
                new SendAndWaitResult.Completed(new TurnResult("### Цель\nтёплый путь", 1, null, Succeeded: true)));
        };

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(0, "тёплый путь не платит дешёвый раннер");
        sentPrompt.Should().Be(ChatHandoffService.BuildWarmPrompt());
        result.Warm.Should().BeTrue();
        result.Draft.Should().Contain("тёплый путь");
    }

    [Fact]
    public async Task Handoff_ТёплыйКэшНоЧатЗанят_ТихоХолодныйПуть()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runner = new CountingRunner("### Цель\nхолодный путь");
        var (sut, _, chat) = BuildSut(runner, cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        chat.Status = SessionStatus.Working; // WarmHandoffBlocker: «идёт ход»

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        result.Warm.Should().BeFalse();
    }

    [Fact]
    public async Task Handoff_ТёплыйПутьНеДаетЧата_502_БезПреемника()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (sut, mgr, chat) = BuildSut(new CountingRunner("не должна вызываться"),
            cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        var before = mgr.GetAll().Count;
        sut.SendOverride = (_, _, _) => Task.FromResult<SendAndWaitResult>(new SendAndWaitResult.Running());

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<HandoffGenerationException>()
            .WithMessage("*не написал передачу*");
        mgr.GetAll().Count.Should().Be(before, "таймаут тёплого хода не создаёт чат");
    }

    [Fact]
    public async Task Handoff_ТёплыйКэшНоХодЗанят_HandoffBusyБезПреемника()
    {
        // «Чат занят» — временное состояние, а не отказ модели: отдельный отказ, у
        // контроллера 409 (не 502), повторный клик разрешён
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (sut, mgr, chat) = BuildSut(new CountingRunner("не должна вызываться"),
            cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        var before = mgr.GetAll().Count;
        sut.SendOverride = (_, _, _) => Task.FromResult<SendAndWaitResult>(
            new SendAndWaitResult.Busy(SessionStatus.Working));

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<HandoffBusyException>()
            .WithMessage("чат занят другим ходом, попробуйте позже");
        mgr.GetAll().Count.Should().Be(before, "занятый чат не создаёт преемника");
    }

    [Theory]
    [InlineData(false, "*поставлена в очередь старого чата*")]
    [InlineData(true, "*ушла в старый чат*")]
    public async Task Handoff_ТёплыйКэшПросьбаУжеВСтаромЧате_409СЧестнымТекстом(bool dispatched, string message)
    {
        // Queued: записка уже заказана у старого чата, отменить постановку нельзя, преемник
        // не создаётся — текст 409 говорит об этом, а не просто «чат занят»
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (sut, mgr, chat) = BuildSut(new CountingRunner("не должна вызываться"),
            cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None));
        var before = mgr.GetAll().Count;
        sut.SendOverride = (_, _, _) => Task.FromResult<SendAndWaitResult>(
            new SendAndWaitResult.Queued(1, false, dispatched));

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<HandoffBusyException>().WithMessage(message);
        mgr.GetAll().Count.Should().Be(before);
    }

    [Fact]
    public async Task Handoff_ТёплыйКэшНоБлокер_ЛогПричиныТихогоХолодногоПути()
    {
        // Тихий уход на холодный путь должен быть виден в логах: без причины в проде не
        // понять, сколько переносов идёт тёплыми
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var log = new RecordingLogger();
        var (sut, _, chat) = BuildSut(new CountingRunner("### Цель\nхолодный путь"),
            cache: new PromptCacheState(60_000, 0, now, "5m", null, PromptCacheMissReason.None),
            log: log);
        chat.Status = SessionStatus.Working; // WarmHandoffBlocker: «идёт ход или ждёт очередь»

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        result.Warm.Should().BeFalse();
        log.Lines.Should().Contain(l => l.Contains("Тёплый путь") && l.Contains("идёт ход"),
            "причина тихого холодного пути логируется");
    }

    // ─── Отказы ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handoff_ЧужойЧат_KeyNotFound_БезВызоваМодели()
    {
        var runner = new CountingRunner("записка");
        var (sut, _, _) = BuildSut(runner);

        var act = () => sut.HandoffAsync("чужой-пользователь", "нет-такого-чата", CancellationToken.None);
        await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Handoff_МестоНаЛокали_ОтказДоМодели()
    {
        var runner = new CountingRunner("записка") { Local = true };
        var (sut, mgr, chat) = BuildSut(runner);
        var before = mgr.GetAll().Count;

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage(ChatHandoffService.LocalRouteRefusal);
        runner.Calls.Should().Be(0);
        mgr.GetAll().Count.Should().Be(before);
    }

    [Fact]
    public async Task Handoff_ПустойЧат_ОтказДоМодели()
    {
        var runner = new CountingRunner("записка");
        var (sut, _, chat) = BuildSut(runner, withHistory: false);

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        // Пустой транскрипт отсекается ДО вызова модели: платить за записку, которую
        // некуда положить, незачем
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage(ChatHandoffService.NothingToHandOff);
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Handoff_ГрупповойЧат_ОтказДоМодели()
    {
        var runner = new CountingRunner("записка");
        var (mgr, projects, _) = BuildManager();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "grp_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Групповой проект", dir, TestUserId, TestUsername);
        var chat = mgr.CreateAsync(project.Id, ClaudeMode.Auto, name: "Группа").GetAwaiter().GetResult();
        chat.Participants = [TestUserId];

        var sut = new ChatHandoffService(mgr, runner, NullLogger<ChatHandoffService>.Instance);
        var before = mgr.GetAll().Count;

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("*Групповой чат нельзя переносить*");
        runner.Calls.Should().Be(0);
        mgr.GetAll().Count.Should().Be(before);
    }

    // ─── Параллельные клики ───────────────────────────────────────────────────

    [Fact]
    public async Task Handoff_ПараллельныеКлики_ОдинВызовМодели()
    {
        var runner = new GatedRunner("Записка одного раза.");
        var (sut, _, chat) = BuildSut(runner);

        // Первый клик синхронно доходит до TryAdd и подвисает в модели; второй успевает
        // дойти до TryAdd тоже синхронно — гонки «кто первый» нет
        var first = sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        var second = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        await second.Should().ThrowExactlyAsync<HandoffInProgressException>();

        runner.Release();
        var result = await first;

        runner.Calls.Should().Be(1);
        result.Draft.Should().Contain("Записка одного раза.");
    }

    [Fact]
    public async Task ХолодныйПуть_ГейтВладельцаОдинНаВладельца_НеВычищаетсяИПереиспользуется()
    {
        // Вычистка пустого гейта (Release → CurrentCount → TryRemove) неатомарна: снимала живой
        // семафор, в который уже входил другой перенос, и следующий создавал второй. Гейт
        // остаётся в словаре — один и тот же экземпляр на всех переносах владельца
        var (sut, mgr, chat) = BuildSut(new CountingRunner("записка"));
        var gates = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(ChatHandoffService)
            .GetField("_ownerGates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sut)!;

        await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        var first = gates.Should().ContainSingle().Which.Value;
        first.CurrentCount.Should().Be(1, "после переноса гейт свободен");

        await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        gates.Should().ContainSingle();
        gates[TestUserId].Should().BeSameAs(first, "второй перенос входит в тот же семафор");
    }

    // ─── Сборка SUT ───────────────────────────────────────────────────────────

    private (ChatHandoffService Sut, SessionManager Manager, Session Chat) BuildSut(
        ICheapTextRunner runner, bool withHistory = true, PromptCacheState? cache = null,
        ILogger<ChatHandoffService>? log = null)
    {
        var (mgr, projects, _) = BuildManager();
        return (new ChatHandoffService(mgr, runner, log ?? NullLogger<ChatHandoffService>.Instance),
            mgr, NewProjectChat(mgr, projects, withHistory, cache));
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
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
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
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        var manager = new SessionManager(projectManager, _history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, cheap: null, broadcaster: broadcaster);
        return (manager, projectManager, notesSvc);
    }

    // Проектный чат с историей на диске ДО создания (StartNewSessionAsync читает её один
    // раз в Accumulator); опционально — result с состоянием кэша (данные B-1)
    private Session NewProjectChat(SessionManager mgr, ProjectManager projects, bool withHistory = true,
        PromptCacheState? cache = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект переноса", dir, TestUserId, TestUsername);
        string? csid = null;
        if (withHistory)
        {
            csid = "handoff_hist_" + Guid.NewGuid().ToString("N");
            var messages = new List<StoredMessage>
            {
                new StoredUserMessage("Настройте значки проектов"),
                new StoredTextMessage("Готово, значки настроены"),
            };
            if (cache is not null)
                messages.Add(new StoredResultMessage("success", 1, 1) { PromptCache = cache });
            _history.SaveAsync(csid, messages).GetAwaiter().GetResult();
        }
        return mgr.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid).GetAwaiter().GetResult();
    }

    // Стаб раннера: считает вызовы, помнит параметры последнего, отвечает фиксированно
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public bool Local;
        public string? LastActionKey;
        public string? LastOwnerId;
        public string? LastPrompt;

        public bool UsesLocal(string actionKey) => Local;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastOwnerId = ownerId;
            LastPrompt = prompt;
            return Task.FromResult(answer);
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Стаб раннера с гейтом: вызов модели висит, пока тест не отпустит гейт
    private sealed class GatedRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            await _release.Task.WaitAsync(ct);
            return answer;
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Лог с sink: проверить причину тихого ухода на холодный путь
    private sealed class RecordingLogger : ILogger<ChatHandoffService>
    {
        public readonly List<string> Lines = [];

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, e));
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable BeginScope<TState>(TState state) => new NoopScope();
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }
}
