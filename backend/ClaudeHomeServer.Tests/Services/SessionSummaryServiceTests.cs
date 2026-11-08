using Microsoft.Extensions.DependencyInjection;
﻿using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тесты чистой логики «Итога сессии»: сборка транскрипта из StoredMessage и заголовок.
// Полный пайплайн с настоящим claude.exe здесь не гоняется, но пайплайн разбора доступа и
// адреса заметки — гоняется: вместо LLM подменный ICheapTextRunner, заметки — настоящий
// NotesService на временном дереве (его и проверяют тесты S2d-6a: куда легла сводка).
public class SessionSummaryServiceTests
{
    // ─── BuildTranscript ─────────────────────────────────────────────────────

    [Fact]
    public void BuildTranscript_РепликиИИнструменты_ВПравильномФормате()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("Сделай фичу"),
            new StoredToolUseMessage { Name = "Read" },
            new StoredFileChangedMessage("src/a.ts", 10, 2),
            new StoredTextMessage("Готово, фича сделана"),
            new StoredThinkingMessage("внутренние размышления"),
            new StoredResultMessage("success", 100, 1),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 10_000);

        t.Should().Contain("Пользователь:").And.Contain("Сделай фичу");
        t.Should().Contain("AI:").And.Contain("Готово, фича сделана");
        t.Should().Contain("[инструмент Read]");
        t.Should().Contain("[изменён файл src/a.ts +10/-2]");
        // thinking и метаданные result в транскрипт не попадают
        t.Should().NotContain("размышления").And.NotContain("success");
    }

    [Fact]
    public void BuildTranscript_ПустаяЛента_ПустаяСтрока()
    {
        SessionSummaryService.BuildTranscript([], 10_000).Should().BeEmpty();
    }

    [Fact]
    public void BuildTranscript_ПереполнениеБюджета_ГоловаПлюсХвост()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("НАЧАЛО " + new string('а', 5000)),
            new StoredTextMessage(new string('б', 5000) + " КОНЕЦ"),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 1000);

        t.Length.Should().BeLessThan(1100); // бюджет + маркер сокращения
        t.Should().StartWith("Пользователь:").And.Contain("НАЧАЛО");
        t.Should().Contain("[…транскрипт сокращён…]");
        t.Should().EndWith("КОНЕЦ");
    }

    [Fact]
    public void BuildTranscript_ПустыеРеплики_Пропускаются()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("   "),
            new StoredTextMessage(""),
        };
        SessionSummaryService.BuildTranscript(messages, 1000).Should().BeEmpty();
    }

    // ─── BuildTitle ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildTitle_СИменемСессии()
    {
        var s = new Session { Name = "Рефакторинг заметок" };
        SessionSummaryService.BuildTitle(s).Should()
            .StartWith("Итог: Рефакторинг заметок · ");
    }

    [Fact]
    public void BuildTitle_БезИмени_Чат()
    {
        SessionSummaryService.BuildTitle(new Session()).Should().StartWith("Итог: чат · ");
    }

    [Fact]
    public void BuildTitle_ДлинноеИмя_Обрезается()
    {
        var s = new Session { Name = new string('х', 100) };
        var title = SessionSummaryService.BuildTitle(s);
        title.Should().Contain("…");
        title.Length.Should().BeLessThan(90);
    }

    // ─── SummarizeAsync: проверка гейта подсистемы Notes ────────────────────

    // Подсистема Notes отключена (notes = null). Проверка в SummarizeAsync должна
    // стоять ДО платного cheap.RunAsync: иначе пользователь платит за LLM-конспект,
    // который некуда сохранить, и тот гарантированно выбрасывается (эталон —
    // DailyBriefingService.BuildAndWriteAsync).
    [Fact]
    public async Task Summarize_NotesDisabled_LlmНеВызывается_503Возвращается()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = new SessionSummaryService(
                ctx.Sessions, ctx.Projects, runner,
                ctx.Broadcaster, ctx.Notif, ctx.Config,
                NullLogger<SessionSummaryService>.Instance,
                notes: null, kb: null); // ← подсистема Notes выключена
            var setup = NewProjectChatWithHistory(ctx);

            var act = () => sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);

            // SummaryUnavailableException → контроллер мапит в 503 + reason="notes_disabled"
            // (см. SessionSummaryController) — тот же контракт, что у «Утреннего брифа».
            // Тип важен: SummaryGenerationException ушёл бы в 502 «повтори», а повторять
            // при выключенной подсистеме бессмысленно.
            await act.Should().ThrowExactlyAsync<SummaryUnavailableException>()
                .WithMessage("*«Заметки» отключена*");
            runner.Calls.Should().Be(0,
                "при выключенной Notes платный LLM-вызов не должен случаться — " +
                "конспект некуда сохранить, и это известно ещё до первого токена");
            // inFlight снимается в finally, иначе следующий клик получит 409 навсегда
            var second = () => sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);
            // второй вызов должен дойти до той же проверки (а не 409 inFlight),
            // поэтому кидаем и его — runner.Calls всё ещё 0
            await second.Should().ThrowExactlyAsync<SummaryUnavailableException>();
            runner.Calls.Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── SummarizeAsync: приватный чат — только автору и в его личный vault (S2d-6a) ──
    //
    // Инвариант: ни один вызов «Итога сессии» не пишет сводку приватного чата в notes/
    // проекта и не отдаёт её никому, кроме автора. Проверяется местом файла на диске
    // (notes/ проекта vs личный vault) — это то наблюдаемое состояние, ради которого
    // весь разбор и делается.

    [Fact]
    public async Task Summarize_ВладелецПроектаНаЧужомПриватном_404ИНиОдногоПлатногоВызова()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_priv_foreign_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = BuildSut(ctx, runner);
            var setup = NewProjectChatWithHistory(ctx, createdBy: ctx.EditorId, privateTo: ctx.EditorId);

            var act = () => sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);

            // Чужой приватный чат для владельца проекта не существует: KeyNotFound → 404,
            // а не 403 — приватность не различает «чужое» и «несуществующее» (тот же разбор,
            // что у read-ручек чатов и ResolveRoot NotesService).
            await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
            runner.Calls.Should().Be(0,
                "отказ обязан быть ДО платного LLM-вызова: чужую приватную ленту не сводит никто");
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(0,
                "сводка чужого приватного чата не имеет права появиться в notes/ проекта");
            NotesChangedRecipients(ctx).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Summarize_АвторПриватногоИзУчастников_ЗаметкаВЕгоЛичномVault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_priv_author_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var kb = new Mock<INoteSemanticIndex>();
            var sut = BuildSut(ctx, runner, kb);
            var setup = NewProjectChatWithHistory(ctx, createdBy: ctx.EditorId, privateTo: ctx.EditorId);

            var note = await sut.SummarizeAsync(ctx.EditorId, setup.Chat.Id, CancellationToken.None);

            // Автор своего приватного чата проходит без права на проект: заметка — его
            // личный vault (Source «personal»), а не notes/ проекта, где он не владелец.
            note.Source.Should().Be("personal");
            note.Title.Should().StartWith("Итог:");
            NoteFiles(PersonalVault(ctx, ctx.EditorId)).Should().Be(1);
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(0,
                "инвариант S2d-6a: сводка приватного чата не появляется в notes/ проекта");
            ctx.Sessions.GetById(setup.Chat.Id)!.SummaryNoteId.Should().Be(note.Id,
                "повторный вызов обязан обновлять эту же заметку автора, а не плодить дубль");
            // Оплата LLM не менялась: её платит владелец проекта, не автор
            runner.LastOwnerId.Should().Be(ctx.OwnerId);
            // Переиндексация и событие — автору (его личный vault, его лента)
            kb.Verify(x => x.QueueSync(ctx.EditorId), Times.Once);
            NotesChangedRecipients(ctx).Should().BeEquivalentTo(new[] { ctx.EditorId });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Summarize_ВладелецНаСвоёмПриватном_ЗаметкаВЕгоЛичномVaultАНеВNotesПроекта()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_priv_own_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = BuildSut(ctx, runner);
            var setup = NewProjectChatWithHistory(ctx, createdBy: ctx.OwnerId, privateTo: ctx.OwnerId);

            var note = await sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);

            // Тот же инвариант для автора-владельца: приватный чат своего же проекта —
            // сводка в его личный vault, в notes/ проекта она не ложится ни при каком вызове.
            note.Source.Should().Be("personal");
            NoteFiles(PersonalVault(ctx, ctx.OwnerId)).Should().Be(1);
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Summarize_ОбщийЧат_ЗаметкаВNotesПроекта_КакПрежняя()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_shared_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = BuildSut(ctx, runner);
            var setup = NewProjectChatWithHistory(ctx); // общий чат владельца, приватности нет

            var note = await sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);

            // Общий чат и чат вне проекта — без изменений: путь владельца (owner-гейт +
            // notes/Сессии проекта) остался тем же, что до S2d-6a.
            note.Source.Should().Be(setup.ProjectId);
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(1);
            NoteFiles(PersonalVault(ctx, ctx.OwnerId)).Should().Be(0);
            NotesChangedRecipients(ctx).Should().BeEquivalentTo(new[] { ctx.OwnerId });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Summarize_ПустаяПриватность_404ИВладельцуИЛюбомуДругому()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_priv_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = BuildSut(ctx, runner);
            // PrivateToUserId = "" — «не видит никто» (fail-closed, как в SessionManager.CreateAsync):
            // пустая строка не нормализуется в null и не совпадает ни с одним userId.
            var setup = NewProjectChatWithHistory(ctx, createdBy: ctx.OwnerId, privateTo: "");

            var asOwner = () => sut.SummarizeAsync(ctx.OwnerId, setup.Chat.Id, CancellationToken.None);
            var asOther = () => sut.SummarizeAsync(ctx.EditorId, setup.Chat.Id, CancellationToken.None);

            await asOwner.Should().ThrowExactlyAsync<KeyNotFoundException>();
            await asOther.Should().ThrowExactlyAsync<KeyNotFoundException>();
            runner.Calls.Should().Be(0);
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(0);
            NoteFiles(PersonalVault(ctx, ctx.OwnerId)).Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Гонка: приватность снимают посреди платной генерации. Решение сервиса принято по
    // снимку на входе (PrivateToUserId читается один раз), поэтому заметка всё равно уходит
    // автору. Обратный переход закрыт самим чатом (chat_has_messages), поэтому исход один.
    [Fact]
    public async Task Summarize_ПриватностьСнятаПосредиГенерации_ЗаметкаВсёРавноАвтору()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_priv_race_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new BlockingRunner("сводка");
            var sut = BuildSut(ctx, runner);
            var setup = NewProjectChatWithHistory(ctx, createdBy: ctx.EditorId, privateTo: ctx.EditorId);

            var task = sut.SummarizeAsync(ctx.EditorId, setup.Chat.Id, CancellationToken.None);
            // Ждём, пока генерация реально в полёте (не по таймеру — по входу раннера в RunAsync)
            var arrived = await Task.WhenAny(runner.Arrived.Task, Task.Delay(10_000));
            arrived.Should().BeSameAs(runner.Arrived.Task, "LLM-вызов обязан быть в полёте до снятия приватности");

            ctx.Sessions.SetChatVisibility(setup.Chat.Id, ctx.EditorId, false)
                .Outcome.Should().Be(ChatVisibilityOutcome.Ok, "тест бессмысленен, если приватность не снялась");

            runner.Release();
            var note = await task;

            note.Source.Should().Be("personal");
            NoteFiles(PersonalVault(ctx, ctx.EditorId)).Should().Be(1);
            NoteFiles(ProjectNotes(ctx, setup)).Should().Be(0,
                "снимок, а не состояние на момент записи: снятая посреди генерации приватность " +
                "не переносит сводку автора в notes/ проекта");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── Сборка графа для теста SummarizeAsync ──────────────────────────────

    private sealed record Pipeline(
        SessionManager Sessions, ProjectManager Projects,
        string OwnerId, string EditorId, NotesService Notes,
        IConfiguration Config, TestSessionBroadcaster Broadcaster,
        NotificationService Notif);

    private static Pipeline BuildPipeline(string tempDir)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "projects.json"),
            // Автосейв выключен (как в SessionManagerTests): фон не вмешивается между
            // правкой и ассертами
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(tempDir, "claude-profile"),
        }).Build();

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(users);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projects, wkStore,
            knowledge, new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        var sessions = SessionCoreTestHost.Build(tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(projects);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(history);
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(config);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(adapters);
            s.AddSingleton<global::ClaudeHomeServer.Services.FalCostService>(falCost);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.UsageService>(usage);
            s.AddSingleton<global::ClaudeHomeServer.Services.AppSettingsService>(appSettings);
            s.AddSingleton<global::ClaudeHomeServer.Services.UserStore>(users);
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
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(broadcaster);
        }).Manager;
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var push = new PushService(config,
            new PushSubscriptionStore(config), jwt, NullLogger<PushService>.Instance);
        var notif = new NotificationService(notifStore, broadcaster, push, personas, projects,
            NullLogger<NotificationService>.Instance);

        var owner = users.GetFirst()!;
        // Второй пользователь — автор своих приватных чатов (S2d-6a): на нём проверяется,
        // что сводку приватного чата определяет автор, а не владелец проекта.
        var editor = users.Add("summary-editor", "pwd", "user");

        return new Pipeline(sessions, projects, owner.Id, editor.Id, notesSvc, config, broadcaster, notif);
    }

    private static SessionSummaryService BuildSut(Pipeline ctx, ICheapTextRunner runner,
        Mock<INoteSemanticIndex>? kb = null) =>
        new SessionSummaryService(
            ctx.Sessions, ctx.Projects, runner, ctx.Broadcaster, ctx.Notif, ctx.Config,
            NullLogger<SessionSummaryService>.Instance, notes: ctx.Notes, kb: kb?.Object);

    // Чат с лентой в свежем проекте: createdBy/privateTo — автор и приватность чата
    private sealed record ChatSetup(Session Chat, string ProjectId, string ProjectRoot);

    private static ChatSetup NewProjectChatWithHistory(Pipeline ctx,
        string? createdBy = null, string? privateTo = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            (string)ctx.Config["DefaultProjectsPath"]!,
            "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = ctx.Projects.Create("Проект", dir, ctx.OwnerId, "user");
        // Проект общий (с участником): приватный чат возможен только в общем проекте,
        // поэтому общий проект здесь — настоящий, а не мнимый. Право участника на проект
        // «Итог сессии» не использует: для приватного чата единственный гейт — авторство.
        ctx.Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = ctx.EditorId, Role = ProjectRole.Editor, AddedBy = ctx.OwnerId }]);
        // История обязана лежать на диске ДО создания чата: StartNewSessionAsync читает её
        // один раз в Accumulator и с диска больше не перечитывается
        var csid = "summary_hist_" + Guid.NewGuid().ToString("N");
        var history = new ChatHistoryService(ctx.Config);
        history.SaveAsync(csid,
        [
            new StoredUserMessage("Сделай X"),
            new StoredTextMessage("Готово, X сделано"),
        ]).GetAwaiter().GetResult();
        var chat = ctx.Sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid,
            createdByUserId: createdBy, privateToUserId: privateTo).GetAwaiter().GetResult();
        return new ChatSetup(chat, project.Id, dir);
    }

    // ── Где живёт заметка: observed-состояние, которым проверяются тесты S2d-6a ──

    // notes/ проекта = корень проекта (NotesService.ResolveRoot → ProjectCapabilityGuard.ServerRoot)
    private static string ProjectNotes(Pipeline ctx, ChatSetup setup) =>
        Path.Combine(setup.ProjectRoot, "notes");

    // Личный vault = <папка DataPath>/notes/<userId> (NotesService.ResolveRoot, PersonalKey)
    private static string PersonalVault(Pipeline ctx, string userId) =>
        Path.Combine(Path.GetDirectoryName((string)ctx.Config["DataPath"]!)!, "notes", userId);

    private static int NoteFiles(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.md", SearchOption.AllDirectories).Length
            : 0;

    // Кому ушло событие NotesChangedMessage (адрес, а не текст: адрес и есть контракт)
    private static IEnumerable<string> NotesChangedRecipients(Pipeline ctx) =>
        ctx.Broadcaster.Owner
            .Where(m => m.Message is NotesChangedMessage)
            .Select(m => m.OwnerId);

    // Стаб раннера: считает вызовы, помнит параметры последнего
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public string Answer = answer;
        public string? LastActionKey;
        public string? LastOwnerId;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastOwnerId = ownerId;
            return Task.FromResult(Answer);
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(Answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Стаб раннера для теста гонки: вход в RunAsync замечен (Arrived), сам вызов держится
    // до Release — тест успевает снять приватность посреди генерации.
    private sealed class BlockingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Arrived.TrySetResult();
            await _gate.Task;
            return answer;
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
