using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Б4b (ADR-041 ред. 7, И2″): отказ маршрута урока — уведомление кураторам с причиной, одно на
/// (ребёнок, предмет, причина) за час. Связь с гейтом — только событие шины (ChildModeGate.LogRefusal →
/// ChildRouteRefusalNotifier), как стоп-сигнал KM-B6d: прямой ссылки гейта на нотификатор нет.
/// Состав — тот же, что у боевого ядра (SessionCoreRegistration), но на своей шине и со своими
/// пользователями: хостовый подписчик не должен удваивать рассылку, а опекунские id не заводятся
/// в стор (получатели определяются по presence-проверке).
/// Ожидание рассылки — TCS-сигнал <see cref="RefusalSignal"/>, не опрос с паузой (п.4 ревью 06229b71):
/// сигнал ставится на том факте, который проверяется (уведомление сохранено и уходит получателю).
/// </summary>
[Trait("Category", "Host")]
public class ChildRouteRefusalNotifierTests : IClassFixture<ChildRouteRefusalNotifierTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _hosts;

    public ChildRouteRefusalNotifierTests(Hosts hosts) => _hosts = hosts;

    private const string ReasonNoConsent = "маршрут модели не допускается в детском режиме";

    private static ChildProfile Profile(string[] guardians, bool consent)
    {
        var p = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
            RulesAgreedAt = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc),
            GuardianUserIds = [.. guardians],
            Subjects = [new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", Enabled = true }],
            Spaces =
            {
                ["lessons"] = new SpaceLimits
                {
                    Windows = [new TimeWindow { From = "00:00", To = "23:59" }],
                    DailyMinutes = 1000,
                },
            },
        };
        if (consent) ChildTestSubjects.Consent(p);
        return p;
    }

    private sealed class FakePolicy : IChildModePolicy
    {
        private readonly Dictionary<string, ChildProfile> _byId = new();
        public void Add(string childId, ChildProfile profile) => _byId[childId] = profile;
        public ChildPolicySnapshot? For(string ownerId) =>
            _byId.TryGetValue(ownerId, out var p) ? new(ownerId, p, false) : null;
    }

    // Presence-стор: опекун «существует», если добавлен; несуществующий id — null.
    private sealed class FakeUsers : IUserStore
    {
        private readonly Dictionary<string, User> _byId = new();
        public void Add(string id, string displayName) =>
            _byId[id] = new User { Id = id, Username = id, DisplayName = displayName };
        public User? GetById(string id) => _byId.TryGetValue(id, out var u) ? u : null;
        public IReadOnlyList<User> GetAll() => _byId.Values.ToList();
        public bool IsTokenVersionCurrent(string userId, int version) => true;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// TCS-сигнал «N отказов отработаны подписчиком до конца»: наблюдатель ставится на ту же
    /// шину ПОСЛЕ нотификатора (его StartAsync в BuildCircuit раньше), а шина ждёт подписчиков
    /// по очереди и в порядке подписки (TurnEventBus.cs:57-61 — await каждого handler до
    /// следующего). Значит N-й сигнал = N-й OnRefusal уже завершён: все записи стораджа на
    /// месте, опрос с паузой не нужен (п.4 ревью 06229b71). Порог — ровно то число отказов,
    /// которое дал тест: при дедупе пяти отказов в одно уведомление порог — пять отработанных
    /// отказов, а не пять доставленных (иначе порог не закрылся бы никогда).
    /// </summary>
    private sealed class RefusalSignal
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        private int _target = 1;

        public void Expect(int times)
        {
            lock (_gate) _target = times;
            TryComplete();
        }

        public Task Await() => _done.Task;

        public Task OnRefusal(ChildRouteRefusalEvent e)
        {
            lock (_gate) _count++;
            TryComplete();
            return Task.CompletedTask;
        }

        private void TryComplete()
        {
            lock (_gate) if (_count >= _target) _done.TrySetResult();
        }
    }

    // Потолок-предохранитель ожидания сигнала: сигнала нет — гонка не построена.
    // Само ожидание — по сигналу.
    private static async Task WaitSignal(Task signal, string what)
    {
        var winner = await Task.WhenAny(signal, Task.Delay(10_000));
        winner.Should().Be(signal, $"{what}: сигнала нет — отказ не отработан подписчиком");
    }

    private sealed class Circuit(ChildModeGate gate, ChildRouteRefusalNotifier notifier)
    {
        public ChildModeGate Gate { get; } = gate;
        public ChildRouteRefusalNotifier Notifier { get; } = notifier;
    }

    // Гейт с шиной и нотификатор, подписанный на ней: та же проводка, что в боевом ядре
    // (SessionCoreRegistration + AddHostedService в Program.cs), но на своей шине.
    // signal — наблюдатель ПОСЛЕ нотификатора: его сигнал значит «предыдущий подписчик отработал
    // до конца» (TCS вместо опроса стораджа, п.4 ревью).
    private Circuit BuildCircuit(FakePolicy policy, FakeUsers users, TimeProvider? time = null,
        NotificationService? notifications = null, RefusalSignal? signal = null)
    {
        var sp = _hosts.Services;
        var (pool, llm) = ChildRouteKit.For("pool-key");
        var bus = new TurnEventBus();
        var gate = new ChildModeGate(policy, sp.GetRequiredService<ChildLimitsCalculator>(), null, pool, llm,
            sp.GetRequiredService<ProjectManager>(), sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ModelAssignmentResolver>(), bus);
        var notifier = new ChildRouteRefusalNotifier(users, policy, sp.GetRequiredService<ChildLimitsCalculator>(),
            notifications ?? sp.GetRequiredService<NotificationService>(), bus,
            NullLogger<ChildRouteRefusalNotifier>.Instance, time);
        notifier.StartAsync(default).GetAwaiter().GetResult();
        if (signal is not null)
            bus.OnNotification<ChildRouteRefusalEvent>(signal.OnRefusal, "test:refusal-signal");
        return new Circuit(gate, notifier);
    }

    // Проект-предмета с руководителем и явной моделью — тот же фикстур, что у ChildLaunchPinTests.
    private (string ProjectId, string LeaderId) SeedSubjectProject(string childId, string subjectId, string model)
    {
        var sp = _hosts.Services;
        var projects = sp.GetRequiredService<ProjectManager>();
        var root = Path.Combine(Path.GetTempPath(), "ccs-b4bproj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = projects.Create("Предмет " + subjectId, root, childId, childId, createDirectory: false,
            childSubjectId: subjectId);
        var tutor = sp.GetRequiredService<PersonaManager>().Create(childId, "Репетитор " + subjectId,
            null, null, null, model, null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        projects.SetDefaultPersona(project.Id, tutor.Id);
        return (project.Id, tutor.Id);
    }

    private static Session Lesson(string childId, string projectId, string leaderId) => new()
    {
        Id = "s-b4b-" + childId, OwnerId = childId, PersonaId = leaderId,
        Provider = "deepseek", Model = "deepseek-v4-pro",
        ProjectId = projectId, Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = "math",
    };

    private List<NotificationListItem> Refusals(string userId) =>
        _hosts.Services.GetRequiredService<NotificationStore>().GetListAsync(userId, 100).GetAwaiter().GetResult()
            .Where(n => n.Type == "child_route_refusal").ToList();

    // c1: отказ «нет согласия» (RiskUnacknowledged) — по уведомлению каждому из двух опекунов,
    // текст несёт причину и предмет; админ, не указанный опекуном, и сам ребёнок — ноль.
    [Fact]
    public async Task ОтказБезСогласия_УведомлениеКаждомуОпекуномСПричиной_АдминНеОпекунНеПолучает()
    {
        const string kid = "kid-b4b-consent";
        var policy = new FakePolicy();
        policy.Add(kid, Profile(["g1", "g2"], consent: false));
        var users = new FakeUsers();
        users.Add("g1", "Опекун Первый");
        users.Add("g2", "Опекун Второй");
        users.Add(kid, "Маша");
        // Настоящий админ хоста, «существующий» для presence-проверки, но не указанный опекуном:
        // не потому что id неизвестен, а потому что опекун — не роль админа.
        var adminId = _hosts.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.Add(adminId, "Админ хоста");

        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "deepseek-v4-pro");
        var signal = new RefusalSignal();
        var c = BuildCircuit(policy, users, signal: signal);

        c.Gate.LiveRouteFor(kid, Lesson(kid, projectId, leaderId)).Should().BeNull(
            "без согласия на риск маршрута живой маршрут не принимается (ChildRoute.RiskUnacknowledged)");

        // Сигнал одного отработанного отказа: после него оба SendAsync (на двух опекунов) уже
        // завершены — чтение стораджа итоговое, паузы нет.
        await WaitSignal(signal.Await(), "отказ отработан подписчиком до конца");

        foreach (var box in new[] { Refusals("g1"), Refusals("g2") })
        {
            box.Should().ContainSingle("одно уведомление опекуну на один отказ");
            (box[0].Title + " " + box[0].Body).Should()
                .Contain(ReasonNoConsent, "текст несёт причину отказа").And
                .Contain("Математика", "текст называет предмет").And
                .Contain("Маша", "текст называет ребёнка").And
                .Contain("Детский режим", "указание на окно настроек");
            box[0].SessionId.Should().BeNull("переписка и чат в уведомление не уходят");
            box[0].Url.Should().BeNull("deeplink на окно настроек у фронта нет — ссылка текстом в теле");
        }
        Refusals(kid).Should().BeEmpty("сам ребёнок уведомления не получает");
        Refusals(adminId).Should().BeEmpty("админ, не указанный опекуном, уведомления не получает");
    }

    // c2: пять одновременных отказов одной причины (ребёнок жмёт «отправить» подряд) — одно
    // уведомление на опекуна; 20 повторов, чтобы гонка не зависела от удачи планировщика.
    // Порог сигнала — пять доставок: он закрывается только когда все пять OnRefusal отработали
    // (четыре из них — молча, по дедупу), поэтому ContainSingle ниже не гонка с опережением.
    [Fact]
    public async Task ПятьПараллельныхОтказов_ОдноУведомлениеНаОпекуна()
    {
        for (var iter = 0; iter < 20; iter++)
        {
            var kid = $"kid-b4b-race-{iter}";
            var guardian = $"g-race-{iter}";
            var policy = new FakePolicy();
            policy.Add(kid, Profile([guardian], consent: false));
            var users = new FakeUsers();
            users.Add(guardian, "Опекун гонки");
            users.Add(kid, "Гонщик");

            var (projectId, leaderId) = SeedSubjectProject(kid, "math", "deepseek-v4-pro");
            var lesson = Lesson(kid, projectId, leaderId);
            var signal = new RefusalSignal();
            signal.Expect(5);
            var c = BuildCircuit(policy, users, signal: signal);

            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => c.Gate.LiveRouteFor(kid, lesson))));

            await WaitSignal(signal.Await(), $"итерация {iter}: все пять отказов отработали");
            Refusals(guardian).Should().ContainSingle($"итерация {iter}: пять отказов одной причины схлопываются в одно уведомление");
        }
    }

    // Сбой доставки (broadcaster бросает): исключение не выходит из подписчика, а отказ запуска
    // остаётся — LiveRouteFor по-прежнему null.
    [Fact]
    public async Task СбойОтправки_ИсключениеНеВыходит_ОтказОстаётся()
    {
        const string kid = "kid-b4b-broken";
        var policy = new FakePolicy();
        policy.Add(kid, Profile(["g-broken"], consent: false));
        var users = new FakeUsers();
        users.Add("g-broken", "Опекун сбоя");
        users.Add(kid, "Маша");

        var sp = _hosts.Services;
        var broken = new NotificationService(
            sp.GetRequiredService<NotificationStore>(), new ThrowingBroadcaster(),
            sp.GetRequiredService<PushService>(), sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ProjectManager>(), NullLogger<NotificationService>.Instance);
        var c = BuildCircuit(policy, users, notifications: broken);

        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "deepseek-v4-pro");

        var act = () => c.Notifier.OnRefusal(new ChildRouteRefusalEvent(
            new TurnContext("s-b4b-broken", kid, 0, 0, null), "math", ReasonNoConsent));
        await act.Should().NotThrowAsync("сбой рассылки не поднимается к шине и к запуску");

        c.Gate.LiveRouteFor(kid, Lesson(kid, projectId, leaderId)).Should().BeNull(
            "отказ запуска не зависит от того, дошло ли уведомление");
    }

    private sealed class ThrowingBroadcaster : ISessionBroadcaster
    {
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string userId, ServerMessage message) => throw new InvalidOperationException("сбой рассылки");
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    // Дедуп по ключу (ребёнок, предмет, причина): тот же ключ за час — молчит; другая причина
    // или другой предмет — не чужой отказ, он виден. OnRefusal здесь зовётся напрямую и ждётся
    // напрямую: после await сторадж уже итоговый — пауза не нужна (п.4 ревью).
    [Fact]
    public async Task ДедупПоКлючу_ТакаяЖеПричинаМолчит_ДругаяПричинаИДругойПредметВидны()
    {
        const string kid = "kid-b4b-key";
        var policy = new FakePolicy();
        policy.Add(kid, Profile(["g-key"], consent: true));
        var users = new FakeUsers();
        users.Add("g-key", "Опекун ключа");
        users.Add(kid, "Маша");
        var now = DateTimeOffset.UtcNow;
        var c = BuildCircuit(policy, users, new FixedTime(now));

        var e = new ChildRouteRefusalEvent(new TurnContext("s-b4b-key", kid, 0, 0, null), "math", "причина первая");
        await c.Notifier.OnRefusal(e);
        await c.Notifier.OnRefusal(e);
        await c.Notifier.OnRefusal(e with { Reason = "причина первая" });
        Refusals("g-key").Should().ContainSingle("повторы того же ключа схлопываются");

        await c.Notifier.OnRefusal(e with { Reason = "причина вторая" });
        await c.Notifier.OnRefusal(e with { SubjectId = "russian" });
        var all = Refusals("g-key");
        all.Should().HaveCount(3, "другая причина и другой предмет — отдельные отказы, каждый виден");
        all.Should().OnlyContain(n => n.Body.Contains("Маша"));

        // За окном (1 час) тот же ключ снова получает право на уведомление.
        var later = BuildCircuit(policy, users, new FixedTime(now + ChildRouteRefusalNotifier.Window + TimeSpan.FromMinutes(1)));
        await later.Notifier.OnRefusal(e);
        Refusals("g-key").Should().HaveCount(4, "за окном схлопывание не действует");
    }

    // Подписчик на событии, а не на сервисе: без StartAsync (подписки) событие гейта ничего
    // не рассылает — связь ровно та, что названа швом. Сигнальный наблюдатель на той же шине
    // доказывает, что событие гейт опубликовал: пустой сторадж при опубликованном событии —
    // отсутствие рассылки, а не отсутствие события.
    [Fact]
    public async Task БезПодписки_СобытиеГейтаНичегоНеРассылает()
    {
        const string kid = "kid-b4b-nosub";
        var policy = new FakePolicy();
        policy.Add(kid, Profile(["g-nosub"], consent: false));
        var users = new FakeUsers();
        users.Add("g-nosub", "Опекун");
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "deepseek-v4-pro");

        var sp = _hosts.Services;
        var (pool, llm) = ChildRouteKit.For("pool-key");
        var bus = new TurnEventBus();
        var gate = new ChildModeGate(policy, sp.GetRequiredService<ChildLimitsCalculator>(), null, pool, llm,
            sp.GetRequiredService<ProjectManager>(), sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ModelAssignmentResolver>(), bus);
        // Единственный подписчик на шине — сигнальный: он доказывает, что событие опубликовано.
        // Нотификатор собран, но не подписан (StartAsync не зван) — рассылки нет при
        // опубликованном событии: связь ровно та, что названа швом.
        var signal = new RefusalSignal();
        bus.OnNotification<ChildRouteRefusalEvent>(signal.OnRefusal, "test:refusal-signal");
        _ = new ChildRouteRefusalNotifier(users, policy, sp.GetRequiredService<ChildLimitsCalculator>(),
            sp.GetRequiredService<NotificationService>(), bus, NullLogger<ChildRouteRefusalNotifier>.Instance);

        gate.LiveRouteFor(kid, Lesson(kid, projectId, leaderId)).Should().BeNull();
        await WaitSignal(signal.Await(), "событие отказа опубликовано");
        Refusals("g-nosub").Should().BeEmpty("без подписки на событии рассылки нет: связь — только событие");
    }

    // ── Честный текст ребёнку (доработка ревью 06229b71, п.3; решение Софьи) ─────────
    // «взрослым отправлено уведомление» — только когда кому уходить: получатели уведомления —
    // опекуны профиля (ChildRouteRefusalNotifier.Recipients). Без опекунов и при отказе сверкой
    // запуска (LaunchMismatch) текст ведёт ребёнка к взрослому сам.

    private const string NoGuardiansText = "Занятие пока недоступно. Попроси взрослого открыть настройки детского режима.";

    // Текст выбирается по наличию опекунов в профиле — не по факту рассылки (она асинхронна):
    // пустой GuardianUserIds и нечитаемый профиль — честный текст без обещания; опекуны — прежний.
    [Fact]
    public void ТекстОтказа_ПоНаличиюОпекуновВПрофиле()
    {
        ChildRoute.RefusedTextFor(new("kid", Profile([], consent: true), false)).Should().Be(NoGuardiansText,
            "без опекунов рассылки не будет никогда — обещать некому");
        ChildRoute.RefusedTextFor(new("kid", Profile(["g1"], consent: true), false)).Should().Be(ChildRoute.RefusedText,
            "с опекунами обещание держится публикацией гейта — текст прежний");
        ChildRoute.RefusedTextFor(null).Should().Be(NoGuardiansText, "снимка нет — обещать некому");
        ChildRoute.RefusedTextFor(new("kid", null, true)).Should().Be(NoGuardiansText, "профиля нет — обещать некому");
    }

    // Отказ живого маршрута (LiveRouteFor → LogRefusal) и отказ сверки запуска (LaunchMismatch) —
    // обе точки дают ребёнку один и тот же текст по его профилю: без опекунов — «попроси
    // взрослого», с опекунами — прежний про уведомление. Решение принимается в момент отказа
    // по профилю, а не по тому, успел ли сработать подписчик (условие конкурентности постановки).
    [Fact]
    public void ОтказГейта_ТекстРебёнкуЧестный_БезОпекуновИПриLaunchMismatch()
    {
        const string noGuard = "kid-b4b-text-noguard";
        const string withGuard = "kid-b4b-text-guard";
        var policy = new FakePolicy();
        policy.Add(noGuard, Profile([], consent: false));
        policy.Add(withGuard, Profile(["g-text"], consent: false));
        var users = new FakeUsers();
        users.Add("g-text", "Опекун текста");

        var (noGuardProject, noGuardLeader) = SeedSubjectProject(noGuard, "math", "deepseek-v4-pro");
        var (withGuardProject, withGuardLeader) = SeedSubjectProject(withGuard, "math", "deepseek-v4-pro");
        var c = BuildCircuit(policy, users);

        // Живой маршрут отказами у обоих (без согласия) — текст по профилю каждого.
        c.Gate.LiveRouteFor(noGuard, Lesson(noGuard, noGuardProject, noGuardLeader)).Should().BeNull();
        ChildRoute.RefusedTextFor(c.Gate.PolicyFor(noGuard)).Should().Be(NoGuardiansText,
            "опекунов нет — текст без обещания уведомления");

        c.Gate.LiveRouteFor(withGuard, Lesson(withGuard, withGuardProject, withGuardLeader)).Should().BeNull();
        ChildRoute.RefusedTextFor(c.Gate.PolicyFor(withGuard)).Should().Be(ChildRoute.RefusedText,
            "опекуны есть — текст прежний");

        // Тот же выбор на отказе сверкой запуска: поля чата не совпадают с живым маршрутом —
        // текст ребёнка определяется профилем, а не причиной и не фактом рассылки.
        var stale = Lesson(noGuard, noGuardProject, noGuardLeader);
        stale.Model = "другая-модель";
        c.Gate.LaunchMismatch(noGuard, stale).Should().NotBeNull("сверка запуска отказывает");
        ChildRoute.RefusedTextFor(c.Gate.PolicyFor(noGuard)).Should().Be(NoGuardiansText);

        var staleWithGuard = Lesson(withGuard, withGuardProject, withGuardLeader);
        staleWithGuard.Model = "другая-модель";
        c.Gate.LaunchMismatch(withGuard, staleWithGuard).Should().NotBeNull();
        ChildRoute.RefusedTextFor(c.Gate.PolicyFor(withGuard)).Should().Be(ChildRoute.RefusedText);
    }
}
