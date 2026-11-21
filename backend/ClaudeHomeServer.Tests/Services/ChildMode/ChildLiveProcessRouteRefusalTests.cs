using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Х-W4-81 (дефект a771ca12): отказ недопустимого маршрута держит и при ЖИВОМ процессе урока.
///
/// Прежний исход: правка Х-W4-74 (cf4e7c1c6) закрыла путь пересборки процесса — refusal там
/// рождает Compose, а к нему ходо доходит только когда процесса нет. При живом процессе
/// EnsureProcessCoreAsync выходит на entry.Process is not null (TurnProcessHost.cs:189), Compose
/// не зовётся, и ни И1-бис (сверка маршрута), ни И3 (окно/лимиты) не спрашивались ни на одном
/// ходу живого чата: догон (CatchUpChildChatAsync) видел несводимый маршрут и публиковал отказ
/// (LogRefusal → уведомление опекуну), но его вердикт терялся — вызывающий не смотрел в bool, а
/// ход шёл на закреплённых полях чата, на прежней модели.
///
/// Правка — единая точка вердикта хода ребёнка в гейте (IChildModeGate.TurnRefusalFor = И1-бис +
/// И3, с публикацией отказа живого маршрута) и её вызов на обоих входах приёма сразу после догона
/// (TurnIntake.SendDirectAsync и SendMessageAndWaitAsync): до прикосновения к процессу, поэтому
/// живой процесс не поднимается и не роняется ради проверки. Отказ здесь — тот же исход приёма,
/// что отказ сферы/автора (RefuseTurnAsync: текст точки отказа в ленту, метка ChildRoute.RefusalAction), а на
/// REST-канале — тот же ответ вызывающему, что отказ отправителя (SendAndWaitResult.Refused).
/// Компанией того же пути закрыта ручная компакция (TurnProcessHost.CompactAsync): она мимо
/// приёма хода, её отказ — ChildRouteRefusedException с тем же текстом.
///
/// Ход проверяется на ядре приёма прявым вызовом SendMessageAsync — ровно тем, что зовёт хаб
/// (SessionHub.cs:307-309). Под процесса — считющий мокадтер (тот же приём, что SetCountingAdapter
/// в ChildSubjectProjectTurnsTests): по нему видно и что ход состоялся (контроль живого маршрута),
/// и что он не дошёл до процесса (отказ). Уведомление опекуну — настоящим подписчиком хоста
/// (ChildRouteRefusalNotifier), сигнал — наблюдателем ПОСЛЕ него на той же шине.
///
/// Мутации (каждая краснит ровно названное, проба — в копии scripts/mutation-copy.sh):
/// • снять блок вердикта в SendDirectAsync — краснеет ЖивойНетронутыйПроцесс: мокадтер получил бы
///   подачу (ровно симптом дефекта: ход на прежней модели), и ЖивойПроцессНедопустимыйМаршрут:
///   процесс был бы пересобран и поднят ради хода;
/// • снять тот же блок в SendMessageAndWaitAsync — краснеет ПодачаАгентскогоСообщения: мокадтер
///   получил бы подачу вместо Refused;
/// • снять И3 из TurnRefusalFor (последняя строка) — краснеет ЖивойПроцессВнеОкна: ход вне окна
///   дошёл бы до процесса;
/// • снять различие урока и чата правил в TurnRefusalFor (всегда RefusedTextFor) — краснеет
///   ЧатПравил: текст с обещанием там, где рассылки не будет никогда;
/// • наоборот, всегда RefusedTextNoGuardians — краснеет сверка текста в ленте урока
///   (обещание опекуна пропало бы).
///
/// Публикации LogRefusal в TurnRefusalFor эти тесты не меряют: на пути теста событие уже
/// публикует догон (LiveRouteFor), а дедуп нотификатора по (ребёнок, предмет, причина) делает
/// повтор той же причины молчанием — ContainSingle в первом тесте проверяет именно это.
/// Сама публикация нужна другому краю: когда догон маршрут нашёл, но перезакрепление отложено
/// (смена поставщика на идущем ходе, ProviderMigration.cs:320) и отказ называет только сверка —
/// без неё текст с обещанием не имел бы за собой рассылки.
/// </summary>
[Trait("Category", "Host")]
public class ChildLiveProcessRouteRefusalTests : IClassFixture<ChildLiveProcessRouteRefusalTests.Hosts>
{
    // Хост на своём конфиге провайдера: живой маршрут урока резолвится в реестр LlmProviders
    // хоста, поэтому deepseek-пара та же, что у фикстур ChildRouteKit и ChildTurnRouteRefusalTests
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
    // Модели нет ни у одного провайдера — живой маршрут не развёрнут (тот же исход, что у
    // BreakLiveRoute в ChildTurnRouteRefusalTests)
    private const string BrokenModel = "alien-model";

    private readonly Hosts _hosts;
    public ChildLiveProcessRouteRefusalTests(Hosts hosts) => _hosts = hosts;

    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _hosts.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _hosts.Services.GetRequiredService<UserStore>();
    private PersonaManager Personas => _hosts.Services.GetRequiredService<PersonaManager>();

    // ── фикстура: ребёнок с опекуном, проект-предмет, репетитор с валидной моделью ──

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // Окно относительно «сейчас» в таймзоне владельца (тот же приём, что Window(from,to) в
    // ChildLaunchLimitsTests): (0, 120) — внутри него всегда, (120, 180) — вне него всегда.
    private static string[] Window(int fromOffsetMin, int toOffsetMin)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow);
        return [now.AddMinutes(fromOffsetMin).ToString("HH:mm"), now.AddMinutes(toOffsetMin).ToString("HH:mm")];
    }

    private (string ChildId, string GuardianId, string ProjectId, string TutorId) Fixture(string tag)
    {
        var child = Users.Add("w481-kid-" + tag, "w481-kid-pw-123456", "user");
        child.TimeZone = "Europe/Moscow";
        var guardian = Users.Add("w481-cur-" + tag, "w481-cur-pw-123456", "user");
        var dir = Path.Combine(_hosts.TempDir, "w481_" + tag);
        Directory.CreateDirectory(dir);
        var project = Projects.Create("Предмет-" + tag, dir, child.Id, child.Username,
            createDirectory: false, childSubjectId: SubjectId);
        var tutor = Personas.Create(child.Id, "Репетитор-" + tag, null, null, null, ValidModel,
            null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(project.Id, tutor.Id);
        Users.SetChildProfile(child.Id, Profile(tutor.Id, [guardian.Id]), resetRules: false);
        return (child.Id, guardian.Id, project.Id, tutor.Id);
    }

    // Профиль с согласием на риск маршрута (И2″, решение 26): отказ должен быть по сломанному
    // маршруту, а не по отсутствию согласия. Окно — сутки, лимиты с запасом: И3 этим тестом не
    // задевается (для него — отдельный тест со своим окном).
    // Окно открытое («сейчас + два часа»): И3 этим профилем не задевается, для него — CloseWindow
    private static ChildProfile Profile(string tutorId, List<string> guardianIds)
    {
        var w = Window(0, 120);
        return new ChildProfile
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
                    Windows = [new TimeWindow { From = w[0], To = w[1] }],
                    DailyMinutes = 1000,
                },
            },
        };
    }

    // Урок — тем же порядком, что детская точка его создания (ChildChatsController.cs:115-125):
    // живой маршрут даёт модель и поставщика ДО создания (И1-бис читает их в момент старта).
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

    private void BreakLiveRoute(string tutorId, string childId) =>
        Personas.Update(tutorId, childId, name: null, role: null, description: null,
            systemPrompt: null, model: BrokenModel, effort: null, scope: null, projectId: null,
            color: null, greeting: null, memoryEnabled: null);

    // Живой урок закрытым окном (И3): сам чат создаётся при открытом окне — при закрытом его
    // не создать вовсе (тот же И3 на первом старте, TurnLaunchContextFactory.Compose). Правка
    // окна после создания — ровно та ситуация, что проверяет шаг: живой чат, живой процесс,
    // состояние изменилось со следующего хода.
    private void CloseWindow(string childId)
    {
        var profile = Users.GetById(childId)!.ChildMode!;
        var w = Window(120, 180);
        profile.Spaces["lessons"] = new SpaceLimits { Windows = [new TimeWindow { From = w[0], To = w[1] }], DailyMinutes = 1000 };
        Users.SetChildProfile(childId, profile, resetRules: false);
    }

    // Живой урок с закрытым дневным лимитом (И3 по другой границе того же состояния):
    // DailyMinutes = 0 при открытом окне → minutesLeft = 0 → time_up (ChildLimitsCalculator.cs:146).
    // Окно остаётся открытым намеренно: состояние должно быть по лимиту, а не по окну.
    private void CloseDailyLimit(string childId)
    {
        var profile = Users.GetById(childId)!.ChildMode!;
        var w = Window(0, 120);
        profile.Spaces["lessons"] = new SpaceLimits { Windows = [new TimeWindow { From = w[0], To = w[1] }], DailyMinutes = 0 };
        Users.SetChildProfile(childId, profile, resetRules: false);
    }

    // Маршрут сделан недопустимым БЕЗ касания персоны (И2″, решение 26: без согласия профиля не
    // годен ни один маршрут). Правка персоны — не этот случай: Personas.Update гасит адаптер
    // чата (entry.AdapterStale), и следующий ход пересобрался бы на прежней точке отказа
    // (cf4e7c1c6). Здесь процесс не тронут ничем: ровно тот случай, где прежняя правка молчала, —
    // живой процесс и живой чат, а маршрут больше не принимается.
    private void RevokeConsent(string childId)
    {
        var profile = Users.GetById(childId)!.ChildMode!;
        profile.RouteRiskAcknowledgedAt = null;
        profile.RouteRiskAcknowledgedBy = null;
        Users.SetChildProfile(childId, profile, resetRules: false);
    }

    // ── белый ящик: entry.Process через рефлексию (публичного доступа к ядру нет, тот же приём,
    // что SetCountingAdapter в ChildSubjectProjectTurnsTests). HasLiveTurn=false: чат свободен,
    // сообщение идёт прямым путём, не в очередь — т.е. ровно тем, где живёт правка. ──

    private object GetEntry(string sessionId)
    {
        var map = (System.Collections.IDictionary)typeof(SessionManager)
            .GetProperty("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Sessions)!;
        return map[sessionId]!;
    }

    private object? ProcessOf(string sessionId) =>
        GetEntry(sessionId).GetType().GetField("Process")!.GetValue(GetEntry(sessionId));

    private Mock<ILlmSessionAdapter> SetCountingAdapter(string sessionId)
    {
        var entry = GetEntry(sessionId);
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.SubmittedTurnSeq).Returns(0);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        return adapter;
    }

    private static void AssertTurnNotSent(Mock<ILlmSessionAdapter> adapter, string what) =>
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never, what);

    private static void AssertTurnSent(Mock<ILlmSessionAdapter> adapter, string what) =>
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.AtLeastOnce, what);

    // ── сигнал рассылки: наблюдатель ПОСЛЕ нотификатора на шине хоста (шина ждёт подписчиков
    // по очереди и в порядке подписки → сигнал = рассылка отработана до конца) ──

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

    // Текст отказа чата правил: эта точка события не публикует (единственная публикация —
    // LogRefusal по уроку), поэтому и обещать «взрослым отправлено уведомление» нечем: его не
    // будет никогда. Проверка на самой точке гейта — тот же инвариант, что у внутренних точек
    // ChildRoute (RefusedText только там, где есть рассылка).
    [Fact]
    public void ЧатПравил_ОтказБезОбещанияУведомления()
    {
        var (childId, _, _, _) = Fixture("rulestext");
        RevokeConsent(childId);
        var rulesChat = new Session
        {
            Id = "rules-text", OwnerId = childId, ChildSpace = ChildSpaces.Lessons,
            OnboardingKind = OnboardingKinds.ChildRules, Mode = ChildModeLaunch.RequiredMode,
        };

        _hosts.Services.GetRequiredService<IChildModeGate>()
            .TurnRefusalFor(childId, rulesChat, DateTimeOffset.UtcNow)
            .Should().Be(ChildRoute.RefusedTextNoGuardians,
                "без публикации отказа текст не вправе обещать уведомление, даже при опекунах в профиле");
    }

    // ── c1+c2: живой процесс + недопустимый маршрут → отказ в ленту, уведомление опекуну,
    // ход не идёт, процесс не тронут ──

    [Fact]
    public async Task ЖивойПроцессНедопустимыйМаршрут_ОтказВЛенте_ХодНеИдёт_ПроцессНеТронут()
    {
        var (childId, guardianId, projectId, tutorId) = Fixture("live");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);
        BreakLiveRoute(tutorId, childId);

        var signal = new RefusalSignal();
        _hosts.Services.GetRequiredService<ITurnEventBus>()
            .OnNotification<ChildRouteRefusalEvent>(signal.OnRefusal, "test:w481-signal");

        // Ровно тот вызов, который делает хаб SendMessage (SessionHub.cs:307-309): при живом
        // процессе прежний путь проходил его молча — с1 проверяется самим этим фактом
        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText,
                "в ленте урока — детский текст точки отказа (опекуны в профиле, отказ опубликован)");
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText && m.Action == ChildRoute.RefusalAction,
                "детский отказ в ленте несёт метку «child-route-refusal»");

        // Ход не дошёл до процесса: мокадтер — тот же живой процесс, что был до правки
        AssertTurnNotSent(adapter, "отказанный ход не дошёл до процесса");
        ProcessOf(lessonId).Should().BeSameAs(adapter.Object,
            "живой процесс ради проверки не поднимается и не роняется");
        Sessions.GetById(lessonId)!.Status.Should().NotBe(SessionStatus.Working, "ход не стартовал");

        await WaitSignal(signal.Await(), "уведомление опекуну");
        Refusals(guardianId).Should().ContainSingle(
            "отказ живого маршрута уведомляет опекуна ровно один раз (повтор той же причины — дедуп нотификатора)");
    }

    // ── чистый случай дефекта: процесс не тронут ничем, кроме недопустимости маршрута ──
    // Снятие согласия (И2″, решение 26) не касается персоны и не гасит адаптер: без правки этого
    // шага ход дошёл бы до мокадтера на прежней модели — ровно то, что видел QA-4.
    [Fact]
    public async Task ЖивойНетронутыйПроцессМаршрутНеПринят_ХодНеДоходитДоПроцесса()
    {
        var (childId, _, projectId, _) = Fixture("norenew");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);
        RevokeConsent(childId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        AssertTurnNotSent(adapter, "ход на непринятом маршруте не дошёл до процесса");
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildRoute.RefusedText && m.Action == ChildRoute.RefusalAction,
                "тот же детский текст отказа с меткой, что на сломанной модели: причина в текст не выходит");
        ProcessOf(lessonId).Should().BeSameAs(adapter.Object,
            "процесс не пересобран и не поднят ради проверки");
    }

    // ── контроль: живой маршрут и живой процесс — ход идёт, отказа нет ──

    [Fact]
    public async Task ЖивойМаршрутЖивойПроцесс_ХодИдётБезОтказа()
    {
        var (childId, _, projectId, _) = Fixture("allow");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        AssertTurnSent(adapter, "на живом маршруте ход дошёл до процесса");
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .NotContain(m => m.Text == ChildRoute.RefusedText || m.Text == ChildRoute.RefusedTextNoGuardians,
                "на живом маршруте отказа нет: правка не превращает каждый детский ход в отказ");
        ProcessOf(lessonId).Should().BeSameAs(adapter.Object, "живой процесс не тронут");
    }

    // ── И3 тем же путём: живой процесс не обходит состояние окна ──

    [Fact]
    public async Task ЖивойПроцессВнеОкна_ХодНеИдёт()
    {
        // Живой маршрут, закрытое окно: до правки И3 спрашивался только на пересборке процесса,
        // живой чат его обходил (тот же проём, что у маршрута)
        var (childId, _, projectId, _) = Fixture("window");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);
        CloseWindow(childId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        AssertTurnNotSent(adapter, "ход вне окна не дошёл до процесса");
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildLimitTexts.For(ChildState.OutOfWindow),
                "состояние окна названо ребёнку его же текстом");
        history.OfType<StoredErrorMessage>().Should()
            .NotContain(m => m.Text == ChildRoute.RefusedText, "отказ окна — не отказ маршрута");
    }

    // ── И3 по дневной границе: та же точка отказа, другое состояние лимита (п.3(а) задачи) ──

    [Fact]
    public async Task ЖивойПроцессСверхДневногоЛимита_ХодНеИдёт()
    {
        var (childId, _, projectId, _) = Fixture("timeup");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);
        CloseDailyLimit(childId);

        await Sessions.SendMessageAsync(lessonId, "объясни мне дроби", [], senderUserId: childId);

        AssertTurnNotSent(adapter, "ход сверх дневного лимита не дошёл до процесса");
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should()
            .Contain(m => m.Text == ChildLimitTexts.For(ChildState.TimeUp),
                "дневный лимит назван ребёнку его же текстом, а не общим");
        history.OfType<StoredErrorMessage>().Should()
            .NotContain(m => m.Text == ChildRoute.RefusedText, "отказ лимита — не отказ маршрута");
    }

    // ── c3: подача агентского сообщения (SendMessageAndWaitAsync) завершается штатно ──

    [Fact]
    public async Task ПодачаАгентскогоСообщения_ЗавершаетсяОтказомБезИсключения()
    {
        var (childId, _, projectId, tutorId) = Fixture("sendwait");
        var lessonId = await LessonAsync(projectId, childId);
        var adapter = SetCountingAdapter(lessonId);
        BreakLiveRoute(tutorId, childId);

        // Тот же вызов, что делает REST-канал агентов (SessionMessagingService.cs:151). До правки
        // он прошёл бы молча при живом процессе и бросил бы ChildRouteRefusedException при мёртвом
        var result = await Sessions.SendMessageAndWaitAsync(lessonId, "доклад по задаче",
            TimeSpan.FromSeconds(5), senderUserId: childId);

        result.Should().BeOfType<SendAndWaitResult.Refused>()
            .Which.Reason.Should().Be(ChildRoute.RefusedText,
                "тот же текст, что на хаб-пути: он рождается точкой отказа, здесь только переносится");
        AssertTurnNotSent(adapter, "отказанный ход не дошёл до процесса");
        var history = await Sessions.GetHistoryAsync(lessonId);
        history.OfType<StoredErrorMessage>().Should().BeEmpty(
            "отказ — вызывающему, не в ленту ребёнка (тот же приём, что отказ отправителя)");
    }
}
