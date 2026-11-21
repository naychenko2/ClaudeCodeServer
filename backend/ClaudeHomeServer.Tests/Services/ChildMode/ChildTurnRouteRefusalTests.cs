using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Х-W4-74 (дефект f974e03f, QA-4, И10): при отказе живого маршрута приём хода ребёнка
/// завершается штатно, а ребёнок читает детский текст отказа.
///
/// Прежний исход: refusal живого маршрута на пересборке процесса (додогон не привёл чат —
/// LiveRouteFor null; BuildForReconnect → Compose → ChildRouteRefusedException) доезжал до
/// хаба: вызов падал «An unexpected error occurred invoking 'SendMessage'», а сформированный
/// точкой отказа текст ребёнка в ленту не шёл — в ней оставался техтекст мёртвого CLI-процесса.
/// Правка — TurnIntake.EnsureProcessOrRefuseAsync: исключение становится тем же отказом хода,
/// что отказ сферы/автора (RefuseTurnAsync: текст в ленту, процесс не тронут), текст — из
/// исключения как есть (RefusedTextFor по опекунам профиля).
///
/// Ход проверяется на ядре приёма прявым вызовом SendMessageAsync — ровно тем, что зовёт хаб
/// (SessionHub.cs:307-309); падение вызова краснит тест необработанным исключением (c1).
/// Состояние «процесс не жив» (смерть процесса, рестарт сервера) ставится снятием entry.Process:
/// только на этом пути refusal доходит до Compose — при живом процессе пересборки нет.
/// Уведомление опекуну — настоящим подписчиком хоста (ChildRouteRefusalNotifier, hosted service):
/// сигнал — наблюдатель ПОСЛЕ него на той же шине (шина ждёт подписчиков по очереди, поэтому
/// сигнал = рассылка отработана до конца; тот же приём, что ChildRouteRefusalNotifierTests).
///
/// Х-W4-81 (дефект a771ca12) перенесла точку отказа раньше: вердикт хода спрашивается до
/// прикосновения к процессу (IChildModeGate.TurnRefusalFor, вызов в TurnIntake после догона),
/// поэтому на этой фикстуре refusal приходит уже с неё, а не из Compose. Сам тест от этого не
/// меняется: он мерит тот же пользовательский исход на мёртвом процессе; живой процесс мерит
/// ChildLiveProcessRouteRefusalTests. Catch в EnsureProcessOrRefuseAsync остаётся guard'ом путей,
/// которые вердикт не спрашивают: вердиктный блок TurnIntake держит только чаты с меткой
/// ChildSpace и резолвящимся владельцем (TurnIntake.cs:489), поэтому детский чат БЕЗ метки
/// при пересборке процесса доходит до Compose и отказывается через исключение.
///
/// Т9б закрыла разрыв покрытия этого guard'а: ЧатБезМетки_ОтказНаПересборке_НесётМеткуМаршрута
/// мерит ровно catch — refusal в нём ставит ДРУГУЮ строку TurnIntake (:310), чем вердиктный
/// путь (:506), и метка обязана быть той же на обеих. Снятие метки на любой из двух краснит
/// только свой тест: вердиктные тесты идут через :506 и на мутации catch не меняются (именно
/// так gap и был найден в вердикте R-Т9a).
///
/// Мутации (каждая краснит ровно названное; после Х-W4-81 — по новой точке):
/// • снять блок вердикта в SendDirectAsync — краснеет НедопустимыйМаршрут (ход пошёл бы на
///   недопустимом маршруте, в ленте не было бы отказа);
/// • заменить RefusedTextFor(snapshot) на техтекст/Generic — краснеет сверка текста в ленте;
/// • снять метку отказа (action: null или дефолт «change-companion») — краснеет сверка
///   Action == ChildRoute.RefusalAction;
/// • вернуть action: null в catch (TurnIntake.cs:310) — краснеет ЧатБезМетки… (метка на
///   guard-пути), вердиктные тесты зелёные: они идут через :506;
/// • снять публикацию события отказа (LogRefusal без events.PublishAsync) — краснеет ожидание
///   сигнала и сверка уведомления.
/// </summary>
[Trait("Category", "Host")]
public class ChildTurnRouteRefusalTests : IClassFixture<ChildTurnRouteRefusalTests.Hosts>
{
    // Хост на класс с своим конфигом провайдера: живой маршрут урока резолвится в реестр
    // LlmProviders хоста, поэтому deepseek-пара та же, что у фикстур ChildRouteKit
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            foreach (var (key, value) in new Dictionary<string, string?>
            {
                ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic",
                ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
                ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
                ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
                ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            }) ExtraConfig[key] = value;
        }
    }

    private const string SubjectId = "math";
    private const string ValidModel = "deepseek-v4-pro";
    // Модели нет ни у одного провайдера — живой маршрут не развёрнут (тот же исход, что
    // слот medium=kids-alien-model у QA-4)
    private const string BrokenModel = "alien-model";

    private readonly Hosts _hosts;
    public ChildTurnRouteRefusalTests(Hosts hosts) => _hosts = hosts;

    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _hosts.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _hosts.Services.GetRequiredService<UserStore>();
    private PersonaManager Personas => _hosts.Services.GetRequiredService<PersonaManager>();

    // ── фикстура: ребёнок с опекуном, проект-предмет, репетитор с валидной моделью ──

    // Фикстура без профиля: ребёнок с опекуном, проект-предмет, репетитор с валидной моделью.
    // Профиль — отдельным шагом SetChildProfile: guard-тесту (ЧатБезМетки…) чат нужен ДО
    // детского режима — при живом профиле чат без метки не создать вовсе (И3 в Compose старте,
    // см. SubjectChatWithoutMarkAsync).
    private (string ChildId, string GuardianId, string ProjectId, string TutorId) FixtureCore(string tag)
    {
        var child = Users.Add("w474-kid-" + tag, "w474-kid-pw-123456", "user");
        var guardian = Users.Add("w474-cur-" + tag, "w474-cur-pw-123456", "user");
        var dir = Path.Combine(_hosts.TempDir, "w474_" + tag);
        Directory.CreateDirectory(dir);
        var project = Projects.Create("Предмет-" + tag, dir, child.Id, child.Username,
            createDirectory: false, childSubjectId: SubjectId);
        var tutor = Personas.Create(child.Id, "Репетитор-" + tag, null, null, null, ValidModel,
            null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(project.Id, tutor.Id);
        return (child.Id, guardian.Id, project.Id, tutor.Id);
    }

    private (string ChildId, string GuardianId, string ProjectId, string TutorId) Fixture(string tag)
    {
        var f = FixtureCore(tag);
        Users.SetChildProfile(f.ChildId, Profile(f.TutorId, [f.GuardianId]), resetRules: false);
        return f;
    }

    // Профиль с согласием на риск маршрута (И2″, решение 26): отказ должен быть именно по
    // сломанному маршруту, а не по отсутствию согласия. Опекуны — ради обещания в тексте
    // («взрослым отправлено уведомление») и ради получателя уведомления. Окно — сутки,
    // лимиты с запасом: И3 этим тестом не проверяется, ход обязан дойти до сверки маршрута.
    private static ChildProfile Profile(string tutorId, List<string> guardianIds) => new()
    {
        Enabled = true, TutorPersonaId = tutorId, ProviderKey = "deepseek", Model = ValidModel,
        RulesAgreedAt = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc),
        GuardianUserIds = [.. guardianIds],
        RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt,
        RouteRiskAcknowledgedBy = "admin",
        Subjects = [new ChildSubject { Id = SubjectId, Label = "Математика", Glyph = "×÷", Color = "info", Enabled = true }],
        Spaces =
        {
            ["lessons"] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }],
                DailyMinutes = 1000,
            },
        },
    };

    // Урок на живом маршруте — тем же порядком, что детская точка создания урока
    // (ChildChatsController.cs:115-125): живой маршрут даёт модель и закреплённого поставщика
    // ДО создания (И1-бис читает их в момент старта сессии, после старта поправить нельзя),
    // режим — RequiredMode детского профиля.
    private async Task<string> LessonAsync(string projectId, string childId)
    {
        var probe = new Session { OwnerId = childId, ChildSpace = ChildSpaces.Lessons, ChildSubject = SubjectId };
        var route = _hosts.Services.GetRequiredService<IChildModeGate>().LiveRouteFor(childId, probe)
            ?? throw new InvalidOperationException("фикстура: живой маршрут урока не развёрнут");
        var chat = await Sessions.CreateAsync(projectId, ChildModeLaunch.RequiredMode,
            name: "Урок", model: route.Model, personaId: route.PersonaId, providerKey: route.ProviderKey,
            createdByUserId: childId, childSpace: ChildSpaces.Lessons, childSubject: SubjectId);
        return chat.Id;
    }

    // Живой маршрут сломан после создания урока: модель репетитора не принадлежит ни одному
    // провайдеру. Догон на старте хода чат не приведёт (LiveRouteFor null) и опубликует
    // событие отказа; сверка Compose отвернет пересборку процесса.
    private void BreakLiveRoute(string tutorId, string childId) =>
        Personas.Update(tutorId, childId, name: null, role: null, description: null,
            systemPrompt: null, model: BrokenModel, effort: null, scope: null, projectId: null,
            color: null, greeting: null, memoryEnabled: null);

    // Чат проекта-предмета БЕЗ детской метки: поле ChildSpace не поставлено (метку ставит
    // только детская точка создания урока — POST /api/child/spaces/{space}/chat). Создаётся
    // ДО профиля: при живом детском профиле такой чат не создать вовсе — И3 в Compose
    // старте видит пустое пространство («входить некуда», ChildModeGate.cs:69-80) и отказывает
    // ещё до регистрации. Реальный guard-путь и есть этот порядок: чат жил до детского
    // режима (или предмет завёлся позже) — метки нет, а владелец уже ребёнок.
    private async Task<string> SubjectChatWithoutMarkAsync(string projectId)
    {
        var chat = await Sessions.CreateAsync(projectId, ClaudeMode.Default,
            name: "Чат до детского режима", model: ValidModel, providerKey: "deepseek");
        return chat.Id;
    }

    // ── белый ящик: entry.Process через рефлексию (публичного доступа к ядру нет, тот же
    // приём, что SetCountingAdapter в ChildSubjectProjectTurnsTests) ──

    private object GetEntry(string sessionId)
    {
        var map = (System.Collections.IDictionary)typeof(SessionManager)
            .GetProperty("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(Sessions)!;
        return map[sessionId]!;
    }

    private void ClearProcess(string sessionId) =>
        GetEntry(sessionId).GetType().GetField("Process")!.SetValue(GetEntry(sessionId), null);

    private object? ProcessOf(string sessionId) =>
        GetEntry(sessionId).GetType().GetField("Process")!.GetValue(GetEntry(sessionId));

    // ── сигнал рассылки: наблюдатель ПОСЛЕ нотификатора на шине хоста (шина ждёт
    // подписчиков по очереди и в порядке подписки → N-й сигнал = N-я рассылка отработана) ──

    private sealed class RefusalSignal
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OnRefusal(ChildRouteRefusalEvent e)
        {
            _done.TrySetResult();
            return Task.CompletedTask;
        }
        public Task Await() => _done.Task;
    }

    private static async Task WaitSignal(Task signal, string what)
    {
        var winner = await Task.WhenAny(signal, Task.Delay(10_000));
        winner.Should().Be(signal, $"{what}: сигнала нет — отказ не отработан подписчиком");
    }

    private List<NotificationListItem> Refusals(string userId) =>
        _hosts.Services.GetRequiredService<NotificationStore>().GetListAsync(userId, 100).GetAwaiter().GetResult()
            .Where(n => n.Type == "child_route_refusal").ToList();

    // ── c1+c2+c3: недопустимый маршрут — вызов штатен, в ленте детский текст, опекуну —
    // уведомление ──

    [Fact]
    public async Task НедопустимыйМаршрут_ДетскийТекстВЛенте_УведомлениеОпекуну_ВызовНеПадает()
    {
        var (childId, guardianId, projectId, tutorId) = Fixture("refuse");
        var lessonId = await LessonAsync(projectId, childId);
        BreakLiveRoute(tutorId, childId);
        // Процесс не жив (смерть процесса, рестарт сервера): следующий message пересобирает
        // его — на этом пути refusal и доходит до Compose
        ClearProcess(lessonId);

        var signal = new RefusalSignal();
        _hosts.Services.GetRequiredService<ITurnEventBus>()
            .OnNotification<ChildRouteRefusalEvent>(signal.OnRefusal, "test:refusal-signal");

        // Ровно тот вызов, который делает хаб SendMessage (SessionHub.cs:307-309): падение
        // вызова краснит весь тест необработанным исключением — с1 проверяется самим этим фактом
        await Sessions.SendMessageAsync(lessonId, "привет, репетитор", [], senderUserId: childId);

        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText,
                "в ленте урока — детский текст с обещанием (опекуны в профиле, отказ опубликован событием)");
        history.OfType<StoredErrorMessage>().Should()
            .NotContain(m => m.Text.Contains("Процесс модели завершился"),
                "техтекст мёртвого процесса в ленту ребёнка не выходит");
        // Карточка отказа несёт свою метку: по ней фронт (childFeedFilter.ts) узнаёт отказ
        // маршрута и показывает ребёнку текст как есть; «сменить собеседника» ребёнку не даётся
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText && m.Action == ChildRoute.RefusalAction,
                "детский отказ в ленте несёт метку «child-route-refusal»");

        await WaitSignal(signal.Await(), "уведомление опекуну");
        Refusals(guardianId).Should().ContainSingle("отказ маршрута уведомляет опекуна (это работает и должно остаться)");

        // Ход не стартовал и не оставил полупроцесса: на отказанном маршруте процесс не поднят
        ProcessOf(lessonId).Should().BeNull("отказанный ход не поднял процесс на недопустимом маршруте");
        Sessions.GetById(lessonId)!.Status.Should().NotBe(SessionStatus.Working, "ход не стартовал");
    }

    // ── Т9б: guard-путь catch в EnsureProcessOrRefuseAsync (TurnIntake.cs:308-312) ──
    // Чат проекта-предмета без метки ChildSpace: вердиктный блок приёма (TurnIntake.cs:489)
    // его пропускает вовсе — refusal живёт только на пересборке процесса и ставится другой
    // строкой TurnIntake (:310), чем вердиктный путь (:506). Метка на карточке отказа обязана
    // быть той же и здесь: без неё фронт (childFeedFilter.ts) подменит её общей плашкой.
    // Порядок фикстуры — реальный путь такого чата: он жил до детского режима (метку и
    // предмет ставит только детская точка создания урока), а владелец уже ребёнок. Состояние
    // меняется профилем, не персоной: адаптер чата не гасится — та же чистота, что у
    // RevokeConsent на живом процессе.
    [Fact]
    public async Task ЧатБезМетки_ОтказНаПересборке_НесётМеткуМаршрута()
    {
        var (childId, guardianId, projectId, tutorId) = FixtureCore("nomarker");
        var chatId = await SubjectChatWithoutMarkAsync(projectId);
        // Детский режим включается ПОСЛЕ создания чата: при живом профиле такой чат не
        // создать — И3 в Compose старте отказывает пустое пространство («Сейчас уроки
        // отдыхают», проба первой версии теста упала именно здесь).
        Users.SetChildProfile(childId, Profile(tutorId, [guardianId]), resetRules: false);
        ClearProcess(chatId);

        // Тот же вызов хаба (SessionHub.cs:307-309): до правки Х-W4-74 исключение доезжало
        // до него; здесь проверяется метка на штатном отказе, который из него вышел
        await Sessions.SendMessageAsync(chatId, "объясни мне дроби", [], senderUserId: childId);

        var history = await Sessions.GetHistoryAsync(chatId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText && m.Action == ChildRoute.RefusalAction,
                "отказ маршрута на guard-пути несёт тот же детский текст и ту же метку, что на вердиктной точке");
        ProcessOf(chatId).Should().BeNull("отказанный ход не поднял процесс на непринятом маршруте");
        Sessions.GetById(chatId)!.Status.Should().NotBe(SessionStatus.Working, "ход не стартовал");
    }

    // ── контроль: на живом маршруте тот же путь (после пересборки) ход идёт — правка не
    // превращает каждый детский ход в отказ ──

    [Fact]
    public async Task ЖивойМаршрут_ХодИдётБезДетскогоОтказа()
    {
        var (childId, _, projectId, _) = Fixture("allow");
        var lessonId = await LessonAsync(projectId, childId);
        ClearProcess(lessonId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .NotContain(m => m.Text == ChildRoute.RefusedText || m.Text == ChildRoute.RefusedTextNoGuardians,
                "на живом маршруте отказа нет");
        ProcessOf(lessonId).Should().NotBeNull("процесс пересобран на живом маршруте — ход идёт");
    }
}
