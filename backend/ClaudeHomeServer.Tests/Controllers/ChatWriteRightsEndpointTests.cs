using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2): права на чат общего проекта.
/// Переименование (PUT /api/projects/{p}/sessions/{id} и POST /api/chats/{id}/retitle) и архив
/// (PUT /api/chats/{id}/archived) — право записи контента чата (WriteContent: редактор и выше,
/// DenyContentWrite в TurnRightExtensions); доработка QA-P1 (возврат Глеба a21170f7): тот же PUT
/// в сессии открывает WriteContent только на name и tags — срок жизни, мьют уведомлений, озвучка
/// и opt-out «Не сохранять решения» остаются настройками владельца (403 owner_settings);
/// удаление (DELETE /api/projects/{p}/sessions/{id}) —
/// автор чата (CreatedByUserId; nil — автор неизвестен и автором не считается) или владелец проекта.
/// Читатель — ничего (403 project_scope), посторонний и чужой приватный — 404: существование
/// не раскрываем, решение о праве идёт ПОСЛЕ проверки видимости.
/// Стенд — как у ChatVisibilityEndpointTests: контроллеры напрямую, стор сессий сидируется
/// sessions.json, в корне DI — настоящая точка доступа. «Дешёвый» раннер держится с счётчиком
/// и удержанием первого вызова: на отказанной ручке модель не звалась вовсе; в тесте гонки
/// первый вызов (retitle) держится, пока второй (удаление) не прошёл.
/// </summary>
public sealed class ChatWriteRightsEndpointTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _outsideId;
    private readonly string _projectId;
    private readonly ChatHistoryService _history;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;
    private readonly HoldingCheapRunner _cheap = new();

    private const string OwnerChatId = "owner-chat";
    private const string EditorChatId = "editor-chat";
    private const string NoAuthorChatId = "no-author-chat";
    private const string OwnerPrivateId = "owner-private";
    private const string EditorPrivateId = "editor-private";
    private const string ReaderPrivateId = "reader-private";

    public ChatWriteRightsEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_write_rights_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // ResolveSession идёт через IProjectAccess: участнику нужен рубильник инстанса
                // и личный флаг — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        var userStore = _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("kuzma-editor", "pwd", "user").Id;
        _readerId = userStore.Add("some-reader", "pwd", "user").Id;
        // Посторонний: не участник и без флага shared-projects — чат проекта для него
        // неотличим от несуществующего
        _outsideId = userStore.Add("outside-user", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _readerId })
            userStore.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, userStore, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, userStore.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _readerId, Role = ProjectRole.Reader, AddedBy = _ownerId },
        ]);

        // Авторские сиды — с CreatedByUserId: право удаления решается по нему (nil — fail-closed,
        // чат без автора удаляет только владелец). Приватные — с флагом opt-out true (инвариант
        // PrivateToUserId != null ⇒ ExcludeFromDossiers == true, S2d-7a)
        WriteSessions(sessionsJsonPath, [
            new Session { Id = OwnerChatId, ProjectId = _projectId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = EditorChatId, ProjectId = _projectId, CreatedByUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = NoAuthorChatId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = OwnerPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId, PrivateToUserId = _ownerId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
            new Session { Id = EditorPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId, PrivateToUserId = _editorId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
            new Session { Id = ReaderPrivateId, ProjectId = _projectId, CreatedByUserId = _readerId, PrivateToUserId = _readerId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
        ]);

        _history = new ChatHistoryService(_config);
        _personas = new PersonaManager(_config);
        _sessions = CreateSessionManager(userStore, appSettings);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sessions.ToList()));

    // Состав — как в SessionStatusTests: живой SessionManager без процессов; вместо настоящего
    // «дешёвого» раннера — держимый фейк (тот же состав, что у GetOwnedVisibleTests)
    private SessionManager CreateSessionManager(UserStore userStore, AppSettingsService appSettings)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = _personas;
        var bindings = new PersonaBindingsService(personas, _projects, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, _history, _config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster(),
            cheap: _cheap, services: AccessProvider());
    }

    // Корень DI с настоящей точкой доступа (тот же ленивый шов, что в проде). Лямбда читает
    // _sessions позже — к первому GetService он уже собран.
    private ServiceProvider AccessProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectAccess>(_ => new ProjectAccessService(
            _projects, _sessions, new SharedProjectsGate(_config, new FeatureFlagService(_users))));
        services.AddSingleton<IFeatureChatLinks, NoFeatureChatLinks>();
        return services.BuildServiceProvider();
    }

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    private SessionsController SessionsAs(string userId) =>
        new(_sessions, _projects, null!, _personas, null!, null!, null!) { ControllerContext = AsUser(userId) };

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    private static (int? Status, string? Code) Read(IActionResult r)
    {
        switch (r)
        {
            case NotFoundResult: return (404, null);
            case OkObjectResult: return (200, null);
            case NoContentResult: return (204, null);
            case ObjectResult o:
                var code = o.Value?.GetType().GetProperty("code")?.GetValue(o.Value) as string;
                return (o.StatusCode, code);
            default: return (null, null);
        }
    }

    // История чата: ключ — HistoryKeyOf (у сидированных чатов ClaudeSessionId нет → id чата),
    // та же точка, что у GetHistoryAsync. Нужна, чтобы «дошёл до раннера» отличался от
    // «транскрипта нет»
    private Task SeedHistory(string sessionId) =>
        _history.SaveAsync(sessionId, [new StoredUserMessage("о чём был разговор")]);

    // ── переименование и архив: право записи (редактор и выше) ────────────────────

    [Fact]
    public async Task РедакторПереименуетЧужойСвойИСвойПриватныйЧат_200_ИмяЗаписано()
    {
        var c = SessionsAs(_editorId);

        Read(await c.Update(_projectId, OwnerChatId, new UpdateSessionRequest(Name: "имя редактора")))
            .Status.Should().Be(200, "чужой общий чат: право записи, а не авторство");
        _sessions.GetById(OwnerChatId)!.Name.Should().Be("имя редактора");

        Read(await c.Update(_projectId, EditorChatId, new UpdateSessionRequest(Name: "своё имя")))
            .Status.Should().Be(200);
        _sessions.GetById(EditorChatId)!.Name.Should().Be("своё имя");

        // Свой приватный чат тоже видим редактору — переименование открыто и ему
        Read(await c.Update(_projectId, EditorPrivateId, new UpdateSessionRequest(Name: "свой приватный")))
            .Status.Should().Be(200);
        _sessions.GetById(EditorPrivateId)!.Name.Should().Be("свой приватный");
    }

    [Fact]
    public async Task РедакторАрхивируетЧатВладельцаИВозвращает_200()
    {
        var c = ChatsAs(_editorId);

        Read(await c.SetArchived(OwnerChatId, new SetArchivedRequest(true))).Status.Should().Be(200);
        var s = _sessions.GetById(OwnerChatId)!;
        s.ArchivedAt.Should().NotBeNull();
        s.ArchivedBy.Should().Be("user");

        Read(await c.SetArchived(OwnerChatId, new SetArchivedRequest(false))).Status.Should().Be(200);
        _sessions.GetById(OwnerChatId)!.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task РедакторОбновляетНазваниеЧужогоЧата_200_МодельЗванаОдинРаз()
    {
        await SeedHistory(OwnerChatId);

        var ok = (await ChatsAs(_editorId).Retitle(OwnerChatId, default)).Should().BeOfType<OkObjectResult>().Subject;
        ((Session)ok.Value!).Name.Should().Be("Новое название");
        _sessions.GetById(OwnerChatId)!.Name.Should().Be("Новое название");
        _cheap.Calls.Should().Be(1, "редактор переименовывает чужой чат: модель звана ровно раз");
    }

    [Fact]
    public async Task ЧитательПереименованиеАрхивИRetitle_403projectScope_НичегоНеЗаписано()
    {
        await SeedHistory(OwnerChatId);

        Read(await SessionsAs(_readerId).Update(_projectId, OwnerChatId, new UpdateSessionRequest(Name: "имя читателя")))
            .Should().Be((403, "project_scope"));
        Read(await ChatsAs(_readerId).SetArchived(OwnerChatId, new SetArchivedRequest(true)))
            .Should().Be((403, "project_scope"));
        Read(await ChatsAs(_readerId).Retitle(OwnerChatId, default))
            .Should().Be((403, "project_scope"));

        var s = _sessions.GetById(OwnerChatId)!;
        s.Name.Should().BeNull("отказ права не пишет имя чужому чату");
        s.ArchivedAt.Should().BeNull("отказ права не архивирует чужой чат");
        _cheap.Calls.Should().Be(0, "на отказанном retitle модель не звалась вовсе");

        // Свой приватный чат читателя-автора: тот же 403 — читателю нет права и на его чат
        Read(await SessionsAs(_readerId).Update(_projectId, ReaderPrivateId, new UpdateSessionRequest(Name: "своё")))
            .Should().Be((403, "project_scope"));
        _sessions.GetById(ReaderPrivateId)!.Name.Should().BeNull();
    }

    [Fact]
    public async Task ПостороннийИЧужойПриватный_404_СуществованиеНеРаскрывается_НичегоНеТронуто()
    {
        await SeedHistory(OwnerPrivateId);

        // Посторонний: неотличимо от несуществующего на всех трёх ручках
        Read(await SessionsAs(_outsideId).Update(_projectId, OwnerChatId, new UpdateSessionRequest(Name: "чужое")))
            .Should().Be((404, null));
        Read(await ChatsAs(_outsideId).SetArchived(OwnerChatId, new SetArchivedRequest(true)))
            .Should().Be((404, null));
        Read(await ChatsAs(_outsideId).Retitle(OwnerChatId, default)).Should().Be((404, null));

        // Редактор на чужом приватном (чате владельца): 404, а не 403 — решение о праве
        // после видимости; чат не переименован, не архивирован, модель не звалась
        Read(await SessionsAs(_editorId).Update(_projectId, OwnerPrivateId, new UpdateSessionRequest(Name: "чужое")))
            .Should().Be((404, null));
        Read(await ChatsAs(_editorId).SetArchived(OwnerPrivateId, new SetArchivedRequest(true)))
            .Should().Be((404, null));
        Read(await ChatsAs(_editorId).Retitle(OwnerPrivateId, default)).Should().Be((404, null));

        _sessions.GetById(OwnerChatId)!.Name.Should().BeNull();
        _sessions.GetById(OwnerPrivateId)!.Name.Should().BeNull();
        _sessions.GetById(OwnerPrivateId)!.ArchivedAt.Should().BeNull();
        _cheap.Calls.Should().Be(0);
    }

    // ── настройки владельца чата: срок, opt-out, мьют, озвучка — только владельцу ──────────
    // Доработка QA-P1 (возврат Глеба a21170f7): право записи контента (WriteContent) открывает
    // редактору только name и tags; остальные поля PUT сессии — тот же гейт владельца-арендатора,
    // что у модели и режима (DenyOwnerTurn → 403 owner_settings). Состояние проверяется по всем
    // полям разом: мутация «срок пишется до гейта владельца» краснит случай срока и смешанный
    // тест ниже — то есть отказ действительно стоит ДО записи, а не после неё.

    // Запрошенное поле — ровно то, которое запись ниже написала бы: срок — любое значение,
    // кроме -1 («не прислано»); флаги и строка — любой не-null.
    private static UpdateSessionRequest OwnerSettingReq(string field) => field switch
    {
        "expiresAfterMinutes" => new UpdateSessionRequest(ExpiresAfterMinutes: 1),
        "excludeFromDossiers" => new UpdateSessionRequest(ExcludeFromDossiers: true),
        "notificationsMuted" => new UpdateSessionRequest(NotificationsMuted: true),
        "voiceMode" => new UpdateSessionRequest(VoiceMode: true),
        "voiceStyle" => new UpdateSessionRequest(VoiceStyle: "digest"),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "неизвестное поле запроса"),
    };

    [Theory]
    [InlineData("expiresAfterMinutes")]
    [InlineData("excludeFromDossiers")]
    [InlineData("notificationsMuted")]
    [InlineData("voiceMode")]
    [InlineData("voiceStyle")]
    public async Task РедакторНеПишетНастройкиВладельцаЧата_403ownerSettings_ЧатНеТронут(string field)
    {
        var s = _sessions.GetById(OwnerChatId)!;

        Read(await SessionsAs(_editorId).Update(_projectId, OwnerChatId, OwnerSettingReq(field)))
            .Should().Be((403, "owner_settings"), $"{field}: настройка владельца чата, не контент чата");

        s.ExpiresAfterMinutes.Should().BeNull("срок чата ставит только владелец: expiresAfterMinutes=1 не принят");
        s.ExpiryAnchor.Should().BeNull("якорь отсчёта срока — вместе со сроком");
        s.ExcludeFromDossiers.Should().BeFalse("opt-out «Не сохранять решения» не переключён");
        s.NotificationsMuted.Should().BeFalse();
        s.VoiceMode.Should().BeFalse();
        s.VoiceStyle.Should().BeNull();
        // Чат жив: срок не поставлен — уборка (ChatExpiryService) его не снесёт
        _sessions.GetById(OwnerChatId).Should().NotBeNull("редактор не удаляет чат чужой заявкой срока");
    }

    // Конкурентность порядка: смешанный запрос (name + срок) отказывается целиком — гейт стоит
    // до любой записи поля, поэтому имя из отказанной заявки не записано, срок не поставлен.
    [Fact]
    public async Task СмешанныйЗапросИмяИСрок_403ownerSettings_НичегоНеЗаписано()
    {
        Read(await SessionsAs(_editorId).Update(_projectId, OwnerChatId,
            new UpdateSessionRequest(Name: "имя редактора", ExpiresAfterMinutes: 1)))
            .Should().Be((403, "owner_settings"));

        var s = _sessions.GetById(OwnerChatId)!;
        s.Name.Should().BeNull("отказ всей заявки: имя из неё не записано");
        s.ExpiresAfterMinutes.Should().BeNull();
        s.ExpiryAnchor.Should().BeNull();
    }

    // Позитивный контроль гейта: владельцу-арендатору те же поля открыты (200, состояние
    // изменилось); редактору — name и tags (200).
    [Fact]
    public async Task ВладелецПишетНастройкиЧата_200_РедакторИмяИМетки_200()
    {
        Read(await SessionsAs(_ownerId).Update(_projectId, OwnerChatId,
            new UpdateSessionRequest(ExpiresAfterMinutes: 60, NotificationsMuted: true, VoiceMode: true,
                VoiceStyle: "digest", ExcludeFromDossiers: true))).Status.Should().Be(200);
        var s = _sessions.GetById(OwnerChatId)!;
        s.ExpiresAfterMinutes.Should().Be(60, "срок — настройка владельца: ему она открыта");
        s.NotificationsMuted.Should().BeTrue();
        s.VoiceMode.Should().BeTrue();
        s.VoiceStyle.Should().Be("digest");
        s.ExcludeFromDossiers.Should().BeTrue();

        Read(await SessionsAs(_editorId).Update(_projectId, OwnerChatId,
            new UpdateSessionRequest(Name: "имя редактора", Tags: ["метка"]))).Status.Should().Be(200);
        s.Name.Should().Be("имя редактора");
        s.Tags.Should().Equal("метка");
    }

    // ── удаление: автор чата или владелец проекта ─────────────────────────────────

    [Fact]
    public async Task АвторУдаляетСвойОбщийИПриватныйЧат_204()
    {
        var c = SessionsAs(_editorId);

        Read(await c.Delete(_projectId, EditorChatId)).Status.Should().Be(204);
        _sessions.GetById(EditorChatId).Should().BeNull();

        Read(await c.Delete(_projectId, EditorPrivateId)).Status.Should().Be(204);
        _sessions.GetById(EditorPrivateId).Should().BeNull();
    }

    [Fact]
    public async Task ВладелецПроектаУдаляетЧатУчастника_204()
    {
        Read(await SessionsAs(_ownerId).Delete(_projectId, EditorChatId)).Status.Should().Be(204);
        _sessions.GetById(EditorChatId).Should().BeNull();
    }

    [Fact]
    public async Task НеАвтор_403notChatAuthor_ЧатЦел_ПостороннемуПрежнийИдемпотентный204()
    {
        Read(await SessionsAs(_editorId).Delete(_projectId, OwnerChatId)).Should().Be((403, "not_chat_author"));
        Read(await SessionsAs(_readerId).Delete(_projectId, OwnerChatId)).Should().Be((403, "not_chat_author"));
        _sessions.GetById(OwnerChatId).Should().NotBeNull("отказанный отказ права не трогает чат");

        // Постороннему — тот же 204, что несуществующему (прежний контракт идемпотентного
        // удаления), сам чат цел
        Read(await SessionsAs(_outsideId).Delete(_projectId, OwnerChatId)).Status.Should().Be(204);
        _sessions.GetById(OwnerChatId).Should().NotBeNull();
    }

    [Fact]
    public async Task ЧатБезАвтора_FailClosedУчастникНеАвтор403_ВладелецМожет()
    {
        // nil CreatedByUserId — автор неизвестен и автором не считается (тот же fail-closed,
        // что у DenyTurnAuthor): видимому участнику — отказ, чат цел
        Read(await SessionsAs(_editorId).Delete(_projectId, NoAuthorChatId)).Should().Be((403, "not_chat_author"));
        _sessions.GetById(NoAuthorChatId).Should().NotBeNull();

        // Владелец проекта — может (он резолвится по владению, а не по авторству)
        Read(await SessionsAs(_ownerId).Delete(_projectId, NoAuthorChatId)).Status.Should().Be(204);
        _sessions.GetById(NoAuthorChatId).Should().BeNull();
    }

    // ── гонка: переименование ‖ удаление одного чата ───────────────────────────────

    // Удаление побеждает: retitle держится фейком на первом вызове модели, удаление проходит
    // параллельно, освобождённый retitle не находит чат — 404, как несуществующему, и не оставляет
    // следов (имя не записано). Таймаут каждой половины гонки — 2 с.
    [Fact]
    public async Task ГонкаRetitleИУдаление_УдалениеПобеждает_Reitle404()
    {
        await SeedHistory(OwnerChatId);
        _cheap.HoldNextCall = true;

        var retitle = ChatsAs(_editorId).Retitle(OwnerChatId, default);
        (await Task.WhenAny(_cheap.Entered, Task.Delay(2000))).Should().Be(_cheap.Entered,
            "retitle не дошёл до модели — гонка не началась");

        Read(await SessionsAs(_ownerId).Delete(_projectId, OwnerChatId)).Status.Should().Be(204);

        _cheap.Release();
        (await Task.WhenAny(retitle, Task.Delay(2000))).Should().Be(retitle,
            "освобождённый retitle не завершился за 2 с");
        (await retitle).Should().BeOfType<NotFoundResult>(
            "чат удалён посреди переименования — 404, неотличимо от несуществующего");
        _sessions.GetById(OwnerChatId).Should().BeNull("удаление побеждает: чата нет, имя не восстановлено");
    }

    /// <summary>
    /// «Дешёвый» раннер со счётчиком и удержанием: HoldNextCall держит первый вызов до Release()
    /// (тест гонки), без него — immediate answer. Ответ годен для retitle (поле title).
    /// </summary>
    private sealed class HoldingCheapRunner : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        private readonly TaskCompletionSource _entered = new();
        private readonly TaskCompletionSource _release = new();

        public int Calls;
        public bool HoldNextCall;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            _entered.TrySetResult();
            if (HoldNextCall) await _release.Task.ConfigureAwait(false);
            return """{"title":"Новое название"}""";
        }

        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            await RunAsync(actionKey, prompt, null, ownerId, jsonFormat, ct);

        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new ClaudeHomeServer.Services.Llm.OneShotResult("""{"title":"Новое название"}""", null, 0));
    }
}
