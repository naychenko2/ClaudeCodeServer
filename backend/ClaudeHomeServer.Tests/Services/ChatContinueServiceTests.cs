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
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// «Продолжить в новом чате»: ChatContinueService + SessionManager.CreateContinuationAsync.
// Настоящую отправку (процесс CLI) подменяет шов SendOverride: тест управляет исходом хода передачи
// и видит, что и куда ушло.
public class ChatContinueServiceTests : IDisposable
{
    private const string TestUserId = "continue-test-user";
    private const string TestUsername = "continue-test-user";

    private readonly string _tempDir;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly SessionManager _sut;
    private readonly NotesService _notes;
    // Шина событий хода: по ней подсистемы узнают о ветвлении и удалении чата (ADR-019 §2)
    private readonly TurnEventBus _bus = new();

    public ChatContinueServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_continue_tests_" + Guid.NewGuid().ToString("N"));
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
        _notes = notesSvc;
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
            broadcaster: broadcaster, turnEvents: _bus);
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

    // Заглушка ISessionBroadcaster: тестам продолжения рассылка не нужна
    private sealed class TrackingNoopBroadcaster : ISessionBroadcaster
    {
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
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


    private const string Note = "## Цель\nДоделать фичу X\n\n## Следующий шаг\nЗапустить тесты";

    private sealed record Sent(string SessionId, string Text, TimeSpan Timeout, bool SystemDirective);

    // Сервис с управляемой отправкой: на старый чат отвечает запиской, на любой другой — Running
    private ChatContinueService NewService(List<Sent> log, Func<string, SendAndWaitResult>? reply = null,
        bool withNotes = true)
    {
        var svc = new ChatContinueService(_sut, new TrackingNoopBroadcaster(),
            NullLogger<ChatContinueService>.Instance, _bus, withNotes ? _notes : null);
        svc.SendOverride = (sid, text, timeout, sys) =>
        {
            log.Add(new Sent(sid, text, timeout, sys));
            return Task.FromResult(reply?.Invoke(sid) ?? (sys
                ? new SendAndWaitResult.Completed(new TurnResult(Note, 100, null, Succeeded: true))
                : new SendAndWaitResult.Running()));
        };
        return svc;
    }

    private async Task<(Session Session, Project Project)> SeedChatAsync(string suffix, bool withTurns = true)
    {
        var project = _projectManager.Create("P-" + suffix, MkProjectDir(suffix), TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Рабочий чат " + suffix);
        var csid = "csid-" + Guid.NewGuid().ToString("N")[..12];
        var live = _sut.GetById(session.Id)!;
        live.ClaudeSessionId = csid;
        if (withTurns)
        {
            // GetHistoryAsync читает живой накопитель хода, а не файл, — подкладываем историю в него
            List<StoredMessage> turns =
            [
                new StoredUserMessage("первый вопрос"),
                new StoredTextMessage("ответ агента"),
                new StoredResultMessage("success", 100, 1),
            ];
            GetEntry(session.Id).GetType().GetField("Accumulator")!
                .SetValue(GetEntry(session.Id), new TurnAccumulator(turns, csid));
        }
        return (live, project);
    }

    [Fact]
    public async Task Продолжение_ПереноситПоляИСтавитСсылкиВОбеСтороны()
    {
        var (source, project) = await SeedChatAsync("fields");
        source.Model = "opus";
        source.Effort = "high";
        source.AutoAllowTools = ["Bash"];
        source.Context = [new SessionContextEntry { Type = SessionContextTypes.File, Id = "docs/a.md", Title = "A" }];
        var updatedBefore = source.UpdatedAt;
        var log = new List<Sent>();

        var result = await NewService(log).ContinueAsync(TestUserId, source.Id, "начни с тестов");

        var created = _sut.GetById(result.NewSessionId)!;
        created.Id.Should().NotBe(source.Id);
        created.ProjectId.Should().Be(project.Id);
        created.Model.Should().Be("opus");
        created.Effort.Should().Be("high");
        created.Provider.Should().Be(source.Provider);
        created.Mode.Should().Be(source.Mode);
        created.AutoAllowTools.Should().Equal("Bash");
        created.Context.Should().ContainSingle().Which.Id.Should().Be("docs/a.md");
        created.ClaudeSessionId.Should().BeNull("транскрипт CLI не копируется");
        created.Name.Should().Be("Рабочий чат fields · продолжение");
        created.ContinuedFromSessionId.Should().Be(source.Id);
        source.ContinuedInSessionId.Should().Be(created.Id);
        source.ArchivedAt.Should().BeNull("старый чат не архивируется");
        source.UpdatedAt.Should().Be(updatedBefore, "продолжение — не активность старого чата");
    }

    [Fact]
    public async Task Продолжение_СлужебныйХодПередачиВСтаромЧатеИПервоеСообщениеВНовом()
    {
        var (source, _) = await SeedChatAsync("messages");
        var log = new List<Sent>();

        var result = await NewService(log).ContinueAsync(TestUserId, source.Id, "начни с тестов");

        log.Should().HaveCount(2);
        log[0].SessionId.Should().Be(source.Id);
        log[0].Text.Should().Be(ChatContinueService.HandoffPrompt);
        log[0].SystemDirective.Should().BeTrue("ход передачи в ленте старого чата — служебный");
        log[0].Timeout.Should().BeGreaterThan(TimeSpan.Zero, "записку дожидаемся");
        log[1].SessionId.Should().Be(result.NewSessionId);
        log[1].Timeout.Should().Be(TimeSpan.Zero, "первый ход нового чата не ждём");
        log[1].Text.Should().Contain("## Цель").And.Contain("Предыдущий чат: [Рабочий чат messages](#/chats/" + source.Id + ")")
            .And.Contain("От человека: начни с тестов").And.EndWith(ChatContinueService.ConfirmRequest);
    }

    [Fact]
    public async Task Продолжение_ЗапискаСохраняетсяЗаметкойВПапкеПередачи()
    {
        var (source, project) = await SeedChatAsync("note");

        var result = await NewService(new List<Sent>()).ContinueAsync(TestUserId, source.Id, null);

        result.HandoffNoteId.Should().NotBeNullOrEmpty();
        var note = _notes.GetDetail(TestUserId, result.HandoffNoteId!)!;
        note.Title.Should().StartWith("Рабочий чат note — передача ");
        note.Path.Should().Contain("Передачи");
        note.Source.Should().Be(project.Id);
        note.Content.Should().Contain("Доделать фичу X").And.Contain("#/chats/" + source.Id);
    }

    [Fact]
    public async Task Продолжение_БезПодсистемыЗаметок_НеПадаетIНоваяСессияСоздана()
    {
        var (source, _) = await SeedChatAsync("nonotes");

        var result = await NewService(new List<Sent>(), withNotes: false).ContinueAsync(TestUserId, source.Id, null);

        result.HandoffNoteId.Should().BeNull();
        _sut.GetById(result.NewSessionId).Should().NotBeNull();
    }

    [Fact]
    public async Task Продолжение_ЗанятыйЧат_409_БезПлатногоХода()
    {
        var (source, _) = await SeedChatAsync("busy");
        EnqueueUser(GetEntry(source.Id), "сообщение в очереди");
        var log = new List<Sent>();

        var act = () => NewService(log).ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*идёт ход*");
        log.Should().BeEmpty();
        source.ContinuedInSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Продолжение_WorktreeНеПереходитВоВладение()
    {
        var (source, _) = await SeedChatAsync("worktree");
        source.WorktreePath = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        source.WorktreeBranch = "feat/x";
        source.WorktreeOwned = true;

        var result = await NewService(new List<Sent>()).ContinueAsync(TestUserId, source.Id, null);

        var created = _sut.GetById(result.NewSessionId)!;
        created.WorktreePath.Should().Be(source.WorktreePath);
        created.WorktreeBranch.Should().Be("feat/x");
        created.WorktreeOwned.Should().BeFalse("деревом по-прежнему владеет источник");
        source.WorktreeOwned.Should().BeTrue();
    }

    [Fact]
    public async Task Продолжение_НетХодовАгента_НеПоддерживается()
    {
        var (source, _) = await SeedChatAsync("empty", withTurns: false);

        var act = () => NewService(new List<Sent>()).ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<ChatContinueNotSupportedException>().WithMessage("*нет ходов агента*");
    }

    [Fact]
    public async Task Продолжение_ГрупповойЧат_НеПоддерживается()
    {
        var (source, _) = await SeedChatAsync("group");
        source.Participants = ["p1", "p2"];

        var act = () => NewService(new List<Sent>()).ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<ChatContinueNotSupportedException>().WithMessage("*Групповой чат*");
    }

    [Fact]
    public async Task Продолжение_ЧужойЧат_404()
    {
        var (source, _) = await SeedChatAsync("stranger");

        var act = () => NewService(new List<Sent>()).ContinueAsync("чужой-владелец", source.Id, null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Продолжение_АгентНеУспелНаписатьЗаписку_409_ИНовогоЧатаНет()
    {
        var (source, _) = await SeedChatAsync("timeout");
        var before = _sut.GetAll().Count();

        var act = () => NewService(new List<Sent>(), _ => new SendAndWaitResult.Running())
            .ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*не успел*");
        _sut.GetAll().Count().Should().Be(before);
        source.ContinuedInSessionId.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "Чат · продолжение")]
    [InlineData("Софья", "Софья · продолжение")]
    [InlineData("Софья · продолжение", "Софья · продолжение 2")]
    [InlineData("Софья · продолжение 2", "Софья · продолжение 3")]
    [InlineData("Софья · продолжение 9", "Софья · продолжение 10")]
    public void ИмяПродолжения_НумеруетсяПоСуффиксу(string? name, string expected) =>
        SessionManager.ContinuationName(name).Should().Be(expected);

    [Fact]
    public async Task ОтложенноеПродолжение_СтартуетПослеКонцаХодаЧата()
    {
        var (source, _) = await SeedChatAsync("deferred");
        var firstMessage = new TaskCompletionSource<Sent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<Sent>();
        var svc = NewService(log);
        var inner = svc.SendOverride!;
        svc.SendOverride = (sid, text, t, sys) =>
        {
            if (!sys) firstMessage.TrySetResult(new Sent(sid, text, t, sys));
            return inner(sid, text, t, sys);
        };
        await svc.StartAsync(default);

        svc.Schedule(TestUserId, source.Id, null, afterCurrentTurn: true).Should().BeTrue();
        log.Should().BeEmpty("пока ход чата не кончился, продолжение не стартует");
        svc.Schedule(TestUserId, source.Id, null, afterCurrentTurn: true).Should().BeFalse("дубль не ставится");

        await _bus.PublishAsync(new TurnCompleted(new TurnContext(source.Id, TestUserId, 1, 0, source.ProjectId), "success"));

        var done = await Task.WhenAny(firstMessage.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().BeSameAs(firstMessage.Task, "после TurnCompleted чат должен продолжиться");
        source.ContinuedInSessionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ОтложенноеПродолжение_ПрерванныйХод_СнимаетсяБезНовогоЧата()
    {
        var (source, _) = await SeedChatAsync("deferred-stop");
        var log = new List<Sent>();
        var svc = NewService(log);
        await svc.StartAsync(default);
        svc.Schedule(TestUserId, source.Id, null, afterCurrentTurn: true);

        await _bus.PublishAsync(new TurnCompleted(new TurnContext(source.Id, TestUserId, 1, 0, source.ProjectId), "interrupted"));
        await Task.Delay(300);

        log.Should().BeEmpty();
        source.ContinuedInSessionId.Should().BeNull();
        svc.Schedule(TestUserId, source.Id, null, afterCurrentTurn: true).Should().BeTrue("очередь освобождена");
    }

    // Блокер (п.1): ход передачи, завершившийся ОШИБКОЙ (протухший логин CLI), — не записка.
    // Без проверки Succeeded текст ошибки уехал бы заметкой и в новый чат
    [Fact]
    public async Task Продолжение_ХодПередачиСОшибка_409_БезНовогоЧатаИЗаметки()
    {
        var (source, _) = await SeedChatAsync("handoff-error");
        var before = _sut.GetAll().Count();

        var act = () => NewService(new List<Sent>(),
            _ => new SendAndWaitResult.Completed(new TurnResult("OAuth session expired", 0, null, Succeeded: false)))
            .ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*без штатного результата*");
        _sut.GetAll().Count().Should().Be(before, "новый чат не создан");
        source.ContinuedInSessionId.Should().BeNull();
    }

    // Блокер (п.1): «Стоп» во время записки — ход прерван без result, Reply = обрубок. Не записка
    [Fact]
    public async Task Продолжение_ХодПередачиПрерванСтопом_409_БезНовогоЧата()
    {
        var (source, _) = await SeedChatAsync("handoff-stop");
        var before = _sut.GetAll().Count();

        var act = () => NewService(new List<Sent>(),
            _ => new SendAndWaitResult.Completed(new TurnResult("обрубок записки", 0, null, Succeeded: false)))
            .ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*без штатного результата*");
        _sut.GetAll().Count().Should().Be(before, "новый чат не создан");
        source.ContinuedInSessionId.Should().BeNull();
    }

    // П.2: чат занялся между проверкой занятости и отправкой — промпт лёг в очередь.
    // Вычищаем его (иначе отработает позже отдельным ходом) и отказываем; чужой ход не прерван
    [Fact]
    public async Task Продолжение_ГонкаЗанятыйЧат_ПромптВычищенИзОчереди_409()
    {
        var (source, _) = await SeedChatAsync("race");
        var log = new List<Sent>();
        var svc = NewService(log);
        svc.SendOverride = (sid, text, t, sys) =>
        {
            log.Add(new Sent(sid, text, t, sys));
            if (sys)
            {
                // Имитация реального enqueue: служебный промпт лёг в очередь занятого чата
                EnqueueUser(GetEntry(sid), text);
                return Task.FromResult<SendAndWaitResult>(new SendAndWaitResult.Queued(1, Duplicate: false));
            }
            return Task.FromResult<SendAndWaitResult>(new SendAndWaitResult.Running());
        };

        var act = () => svc.ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*идёт ход*");
        var pending = (System.Collections.IList)GetEntry(source.Id).GetType().GetField("Pending")!
            .GetValue(GetEntry(source.Id))!;
        pending.Cast<SessionManager.QueuedMessage>()
            .Should().NotContain(p => p.Text == ChatContinueService.HandoffPrompt,
                "промпт записки вычищен: не отработает отдельным ходом");
        source.ContinuedInSessionId.Should().BeNull();
    }

    // П.5: повторное продолжение — ссылка уже стоит, третий чат не создаём
    [Fact]
    public async Task Продолжение_УжеПродолжен_409_СоСсылкой()
    {
        var (source, _) = await SeedChatAsync("re-continue");
        var target = await _sut.CreateAsync(source.ProjectId!, ClaudeMode.Auto, name: "продолжение");
        var before = _sut.GetAll().Count();
        source.ContinuedInSessionId = target.Id;

        var act = () => NewService(new List<Sent>()).ContinueAsync(TestUserId, source.Id, null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*уже продолжен в*");
        _sut.GetAll().Count().Should().Be(before, "третий чат не создан");
    }

    // П.6: Model/Provider/Effort переписаны ПОСЛЕ создания адаптера — следующий ход обязан
    // пересобрать адаптер с перенесёнными значениями (AdapterStale всегда, а не только для worktree)
    [Fact]
    public async Task Продолжение_НовыйЧат_АдаптерУстарел_ПересоберётсяСПеренесённымиЗначениями()
    {
        var (source, _) = await SeedChatAsync("adapter-stale");
        source.Model = "opus";
        source.Effort = "high";

        var created = await _sut.CreateContinuationAsync(source.Id, TestUserId);

        var entry = GetEntry(created.Id);
        var adapterStale = (bool)entry.GetType().GetField("AdapterStale")!.GetValue(entry)!;
        adapterStale.Should().BeTrue("первый ход обязан пересобрать адаптер с перенесёнными значениями");
        created.Model.Should().Be("opus");
        created.Effort.Should().Be("high");
    }
}
