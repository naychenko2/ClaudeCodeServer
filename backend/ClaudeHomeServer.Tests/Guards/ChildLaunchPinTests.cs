using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using ClaudeHomeServer.Services.Spend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// И1-бис (KM-B3a) + Б9a (решение Андрея 19.11, ADR-041 ред. 7): на запуске хода поля детского чата
/// сверяются, расхождение — ход не стартует (исключение из Compose), без тихой подстановки.
/// Урок — чат проекта-предмета: сверка не пускает чужой проект (и «старый урок» вне проекта —
/// только чтение), предмет не предмета проекта, персону не руководителя проекта, скрытый от куратора
/// урок (PrivateToUserId). Чат правил живёт вне проекта как прежде. По одному кейсу на каждый
/// отказ плюс «всё сошлось» и «обычный владелец не затронут». Тесты идут через настоящий
/// TurnLaunchContextFactory.BuildForNewSession, а не через гейт напрямую: мутация «убрать вызов
/// в Compose» должна краснить их все.
/// </summary>
[Trait("Category", "Host")]
public class ChildLaunchPinTests : IClassFixture<ChildLaunchPinTests.Hosts>
{
    // Хост на КЛАСС (храповик «хост на класс»): тесты хост не мутируют, детская политика и гейт строятся
    // локально поверх его DI, поэтому порядок тестов не важен
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _host;
    // Гейт последнего Build — для прямых проверок догона (LiveRouteFor) на том же составе, что и сверка
    private ChildModeGate? _lastGate;

    public ChildLaunchPinTests(Hosts host) => _host = host;

    private sealed class FakePolicy(ChildPolicySnapshot? snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => snapshot;
    }

    // Сутки целиком, дневной лимит с запасом, правила согласованы: И3 (KM-B5d) этим тестом не проверяется,
    // ему нужно лишь пропустить ход, чтобы дойти до сверки И1-бис и сборки контекста.
    private static ChildProfile Profile() => new()
    {
        Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
        RulesAgreedAt = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc),
        RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt, RouteRiskAcknowledgedBy = ChildTestSubjects.ConsentBy,
        Subjects =
        [
            new ChildSubject
            {
                Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", Enabled = true,
            },
        ],
        Spaces =
        {
            ["lessons"] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }],
                DailyMinutes = 1000,
            },
        },
    };

    private sealed class ZeroSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
    }

    // Валидный урок (Б9a): чат проекта-предмета этого ребёнка, персона — руководитель проекта,
    // маршрут — живая модель руководителя на аккаунте профиля ребёнка («pool-key», Б17c: полей предмета
    // нет). Расхождение поля чата и маршрута меряют отдельные тесты.
    private static Session Lesson(string childId, string projectId, string leaderId) => new()
    {
        Id = "s-lesson-" + childId, OwnerId = childId, PersonaId = leaderId, Provider = "pool-key", Model = "haiku",
        ProjectId = projectId, Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = "math",
    };

    // Детский чат вне проекта (форма до Б9a): для сверки это «старый урок» или чат правил.
    private static Session Projectless(string childId) => new()
    {
        Id = "s-old-" + childId, OwnerId = childId, PersonaId = "tutor-math", Provider = "pool-math", Model = "haiku",
        ProjectId = null, Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = "math",
    };

    private TurnLaunchContextFactory Build(ChildPolicySnapshot? snapshot) => Build(snapshot, withSubjectProject: false);

    // withSubjectProject — true для тестов урока: гейт получает реестр проектов, резолвер персоны и
    // резолвер модели — тот же состав, что в боевом ядре (SessionCoreRegistration). С Б9a реестр
    // проектов нужен и сверке проекта урока: без него урока нет («урок не в проекте-предмете»).
    private TurnLaunchContextFactory Build(ChildPolicySnapshot? snapshot, bool withSubjectProject)
    {
        _ = _host.CreateClient();
        var sp = _host.Services;
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var users = new UserStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataPath"] = Path.Combine(tempDir, "projects.json") }).Build(),
            new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var policy = new FakePolicy(snapshot);
        var calc = new ChildLimitsCalculator(policy, new ChildUsageStore(Path.Combine(tempDir, ChildUsageStore.DirName)),
            new ZeroSpend(), users,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChildLimitsCalculator.ConfigTimeZoneKey] = "Europe/Moscow",
            }).Build(), NullLogger<ChildLimitsCalculator>.Instance);
        var (pool, llm) = ChildRouteKit.For("pool-key", "pool-math");
        var gate = withSubjectProject
            ? new ChildModeGate(policy, calc, null, pool, llm,
                sp.GetRequiredService<ProjectManager>(),
                sp.GetRequiredService<PersonaManager>(),
                sp.GetRequiredService<ModelAssignmentResolver>())
            : new ChildModeGate(policy, calc, null, pool, llm);
        _lastGate = gate;
        // BuiltinMcpContexts строим заново — с подменённым гейтом; остальное берём из DI как есть
        var builtin = new BuiltinMcpContexts(
            sp.GetRequiredService<McpEndpoint>(), sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaBindingsService>(), sp,
            sp.GetService<ClaudeHomeServer.Services.Composition.SubsystemStateStore>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>(),
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), gate);
        var factory = new TurnLaunchContextFactory(builtin, sp.GetRequiredService<PersonaLayerBuilder>(),
            sp.GetRequiredService<ExternalMcpContexts>(), sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(), null,
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<ITurnMessageSink>>(), sp.GetRequiredService<Lazy<ITurnStarter>>());
        return factory;
    }

    private static ClaudeHomeServer.Services.Llm.LlmSessionContext Launch(TurnLaunchContextFactory f, Session s) =>
        f.BuildForNewSession(s, s.OwnerId, null, Path.GetTempPath(), Path.GetTempPath(), null, null,
            new TurnAccumulator(new List<StoredMessage>()), 1, (_, _, _, _, _) => Task.CompletedTask,
            null, Path.GetTempPath(), true);

    private static ChildPolicySnapshot Child(string kid) => new(kid, Profile(), DenyAll: false);

    // Проект-предмета и его руководитель с явной моделью — то, что будет провижнить Б8b/Б17a; здесь
    // прямая запись. Модель руководителя — единственный источник маршрута урока (решение 71).
    private (string ProjectId, string LeaderId) SeedSubjectProject(string childId, string subjectId, string model)
    {
        var sp = _host.Services;
        var projects = sp.GetRequiredService<ProjectManager>();
        var root = Path.Combine(Path.GetTempPath(), "ccs-kidproj-" + Guid.NewGuid().ToString("N"));
        // Create с createDirectory: false папку требует существующую, а не создаёт.
        Directory.CreateDirectory(root);
        var project = projects.Create("Предмет " + subjectId, root, childId, childId, createDirectory: false,
            childSubjectId: subjectId);
        var tutor = sp.GetRequiredService<PersonaManager>().Create(childId, "Репетитор " + subjectId,
            null, null, null, model, null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        projects.SetDefaultPersona(project.Id, tutor.Id);
        return (project.Id, tutor.Id);
    }

    // Обычный (не предметный) проект того же ребёнка — для отказа «чужой проект».
    private string SeedPlainProject(string childId)
    {
        var root = Path.Combine(Path.GetTempPath(), "ccs-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return _host.Services.GetRequiredService<ProjectManager>()
            .Create("Обычный проект", root, childId, childId, createDirectory: false).Id;
    }

    [Fact]
    public void ВсёСошлось_КонтекстСобираетсяБезВнешнихСерверов()
    {
        var kid = "kid-pin-ok";
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var factory = Build(Child(kid), withSubjectProject: true);
        var ctx = Launch(factory, Lesson(kid, projectId, leaderId));
        ctx.ExternalMcpProvider.Should().BeNull("внешние серверы владельца ребёнку не объявляются");
        ctx.ModulesMcp.Should().BeNull();
        ctx.WebSearchMcp.Should().BeNull("веб-поиск вне детского allow-list");
        ctx.WorkspaceMcp.Should().BeNull();
        ctx.NotesMcp.Should().BeNull();
        ctx.TasksMcp.Should().BeNull();
        ctx.MemoryMcp.Should().BeNull();
        // п.10 (К3, К9): серверы детского хода — tutor и widgets; ради них и активен http-транспорт
        ctx.TutorMcp.Should().NotBeNull("ребёнку объявляется tutor");
        ctx.WidgetsMcp.Should().NotBeNull("виджет в ленте — пара из allow-list (К9)");
        // «ровно widget_show»: статический тулсет widgets отдаёт один инструмент — тот же, что в
        // allow-list; ребёнку не из чего взять на этом сервере что-то другое
        var widgets = (ClaudeHomeServer.Services.Mcp.Http.IMcpStaticToolset?)_host.Services
            .GetRequiredService<ClaudeHomeServer.Services.Mcp.Http.McpToolsetRegistry>().Find("widgets");
        widgets.Should().NotBeNull("тулсет widgets обязан быть в реестре");
        widgets!.Tools.Select(t => t.Name).Should().Equal("widget_show");
        ctx.HttpMcpActive.Should().Be(ctx.TutorMcp!.UseHttp || ctx.WidgetsMcp!.UseHttp,
            "у ребёнка два http-сервера: tutor и widgets");
    }

    [Fact]
    public void ЧатПравил_ТоженеСетсяСервер_АСоставПустойРешаетТулсет()
    {
        var kid = "kid-pin-rules";
        var s = Projectless(kid);
        s.OnboardingKind = OnboardingKinds.ChildRules;
        s.ChildSubject = null; s.PersonaId = "tutor-1"; s.Provider = "pool-key"; s.Model = "sonnet";
        Launch(Build(Child(kid)), s).TutorMcp.Should().NotBeNull(
            "отдельной ветки «не объявлять в чате правил» нет: ToolsFor там пуст");
    }

    [Fact]
    public void ОбычныйВладелец_TutorMcpНеОбъявляется()
    {
        // п.10: у взрослого AllowsServer = true для любого сервера — tutor объявился бы всем, если бы
        // шёл через Gate(...). Он идёт по IsChild && allow-list.
        var ctx = Launch(Build(null), Projectless("kid-pin-adult"));
        ctx.TutorMcp.Should().BeNull();
    }

    // R-Б9c (43cf325f): в снимке секций ребёнка встречалась mcp-personas. Гейт на месте
    // (TurnLaunchContextFactory: Gate("personas", …) через childGate.AllowsServer, в детском
    // allow-list только tutor/widgets — IChildModeGate), но «ВсёСошлось_…» перечисляет другие
    // серверы без него — мутация «Gate("personas") без childGate» не краснила ничего. Этот тест
    // закрывает дыру; взрослая половина — чтобы снятие гейта не снимало сервер и взрослым.
    [Fact]
    public void ПерсонСервер_РебёнкуНеДоезжает_ВзрослыйВидитЕго()
    {
        var kid = "kid-pin-personas";
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var kidCtx = Launch(Build(Child(kid), withSubjectProject: true), Lesson(kid, projectId, leaderId));
        var adultCtx = Launch(Build(null), Projectless("kid-pin-personas-adult"));

        using var scope = new AssertionScope();
        kidCtx.PersonasMcp.Should().BeNull("персон-сервер вне детского allow-list — ребёнку не доезжает");
        adultCtx.PersonasMcp.Should().NotBeNull("взрослый ход видит персон-сервер, как раньше");
    }

    public static TheoryData<string> Fields => ["persona", "provider", "model", "mode"];

    [Theory]
    [MemberData(nameof(Fields))]
    public void РасхождениеОдногоПоля_ХодНеСтартует(string field)
    {
        var kid = "kid-pin-field-" + field;
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var factory = Build(Child(kid), withSubjectProject: true);
        var s = Lesson(kid, projectId, leaderId);
        switch (field)
        {
            case "persona": s.PersonaId = "other-persona"; break;
            case "provider": s.Provider = "claude"; break;
            case "model": s.Model = "opus"; break;
            case "mode": s.Mode = ClaudeMode.Bypass; break;
        }
        var act = () => Launch(factory, s);
        // Текст в ленту — честный текст ребёнка (внутреннего имени причины в нём нет); сама
        // причина — на Reason: для журнала и разбора (доработка ревью Б4b, п.3).
        var refused = act.Should().Throw<ChildRouteRefusedException>().Which;
        refused.Message.Should().Be(ChildRoute.RefusedTextNoGuardians, "текст ребёнка без обещания, которого не будет");
        refused.Reason.Should().NotBeNull("причина отказа названа для журнала");
    }

    // Б9a (решение Андрея 19.11): урок — чат проекта-предмета этого ребёнка. Каждый отказ — своим
    // текстом; порядок проверок — от «чата нет где надо» к «поля не живые».
    [Theory]
    [InlineData("old-lesson")] // детский чат вне проекта: только чтение, новых ходов нет
    [InlineData("foreign-project")] // чат в обычном проекте этого ребёнка
    [InlineData("foreign-child")] // чат в проекте-предмете другого ребёнка (per-owner)
    [InlineData("subject-mismatch")] // предмет чата ≠ предмет проекта
    [InlineData("hidden")] // PrivateToUserId — скрытый от куратора урок
    public void УрокВнеСвоегоПроектаИлиСкрыт_ХодНеСтартует(string how)
    {
        var kid = "kid-pin-proj-" + how;
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var factory = Build(Child(kid), withSubjectProject: true);
        var s = Lesson(kid, projectId, leaderId);
        var expected = "неизвестный кейс";
        switch (how)
        {
            case "old-lesson":
                s.ProjectId = null;
                expected = "старый урок только для чтения";
                break;
            case "foreign-project":
                s.ProjectId = SeedPlainProject(kid);
                expected = "урок не в проекте-предмете";
                break;
            case "foreign-child":
                var (otherId, _) = SeedSubjectProject("kid-pin-other-" + how, "math", "haiku");
                // Тот же slug «math» у другого ребёнка — per-owner: чужой проект не резолвится
                // GetByOwner этого ребёнка → тот же отказ.
                s.ProjectId = otherId;
                expected = "урок не в проекте-предмете";
                break;
            case "subject-mismatch":
                // Свой проект-предмет, но другого предмета: сверка предмета — до живого маршрута.
                var (russianId, _) = SeedSubjectProject(kid, "russian", "haiku");
                s.ProjectId = russianId;
                expected = "предмет чата не совпадает с предметом проекта";
                break;
            case "hidden":
                s.PrivateToUserId = kid;
                expected = "скрытый от куратора урок не стартует";
                break;
        }
        var act = () => Launch(factory, s);
        // Порядок и причина отказа — на Reason (в тексте ребёнка их нет — доработка ревью Б4b, п.3).
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain(expected);
    }

    // И2′ (Т5a): маркер в закреплённой модели чата — не живой маршрут: при живой модели руководителя
    // чат на маркере («tier:strong») не совпадает с ней уже моделью — маркер в маршрут не входит.
    [Fact]
    public void МаркерВЗакреплённойМоделиЧата_ХодНеСтартует()
    {
        var kid = "kid-pin-marker";
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var factory = Build(Child(kid), withSubjectProject: true);
        var s = Lesson(kid, projectId, leaderId);
        s.Model = "tier:strong";
        var act = () => Launch(factory, s);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("модель чата не совпадает с моделью репетитора");
    }

    [Fact]
    public void ОблачнаяМодельРуководителя_БезСогласияАдмина_ХодНеСтартует_СоСогласием_Стартует()
    {
        var kid = "kid-pin-cloud";
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "deepseek-v4-pro");
        var snapshot = Child(kid);
        // отказ без согласия профиля: фикстура по умолчанию его ставит, здесь снимаем намеренно
        snapshot.Profile!.RouteRiskAcknowledgedAt = null;
        snapshot.Profile.RouteRiskAcknowledgedBy = null;
        var s = Lesson(kid, projectId, leaderId);
        s.Provider = "deepseek"; s.Model = "deepseek-v4-pro";
        var act = () => Launch(Build(snapshot, withSubjectProject: true), s);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("маршрут модели не допускается");

        ChildTestSubjects.Consent(snapshot.Profile);
        Launch(Build(snapshot, withSubjectProject: true), s).Should().NotBeNull();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    public void ПредметЗакрыт_ХодНеСтартует(string how)
    {
        var kid = "kid-pin-closed-" + how;
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var snapshot = Child(kid);
        var s = Lesson(kid, projectId, leaderId);
        switch (how)
        {
            case "disabled": snapshot.Profile!.Subjects[0].Enabled = false; break;
            case "deleted": snapshot.Profile!.Subjects.Clear(); break;
        }
        var act = () => Launch(Build(snapshot, withSubjectProject: true), s);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("Этот предмет закрыт");
    }

    // Предмета у чата нет вовсе (старая запись или сбой): проект-предмет с другим предметом —
    // отказ по предмету, не по живому маршруту.
    [Fact]
    public void УрокБезПредмета_ХодНеСтартует()
    {
        var kid = "kid-pin-nosubj";
        var (projectId, leaderId) = SeedSubjectProject(kid, "math", "haiku");
        var factory = Build(Child(kid), withSubjectProject: true);
        var s = Lesson(kid, projectId, leaderId);
        s.ChildSubject = null;
        var act = () => Launch(factory, s);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("предмет чата не совпадает с предметом проекта");
    }

    [Fact]
    public void ЧатПравил_СверяетсяСПрофилем_БезПредмета()
    {
        var kid = "kid-pin-rules2";
        var s = Projectless(kid);
        s.OnboardingKind = OnboardingKinds.ChildRules;
        s.ChildSubject = null; s.PersonaId = "tutor-1"; s.Provider = "pool-key"; s.Model = "sonnet";
        var factory = Build(Child(kid));
        var act = () => Launch(factory, s);
        act.Should().NotThrow();
        s.Model = "haiku"; // запись предмета чату правил не указ
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().NotBeNull();
    }

    // Чат правил — вне проекта, как был (Б9a): проектному чату он не принадлежит никогда.
    [Fact]
    public void ЧатПравил_ВПроекте_ХодНеСтартует()
    {
        var kid = "kid-pin-rules-proj";
        var (projectId, _) = SeedSubjectProject(kid, "math", "haiku");
        var s = Projectless(kid);
        s.OnboardingKind = OnboardingKinds.ChildRules;
        s.ChildSubject = null; s.PersonaId = "tutor-1"; s.Provider = "pool-key"; s.Model = "sonnet";
        s.ProjectId = projectId;
        var act = () => Launch(Build(Child(kid), withSubjectProject: true), s);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("чат правил не может принадлежать проекту");
    }

    [Fact]
    public void ПрофильDenyAll_ХодНеСтартует()
    {
        var factory = Build(new ChildPolicySnapshot("kid-pin-deny", null, DenyAll: true));
        var act = () => Launch(factory, Projectless("kid-pin-deny"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ОбычныйВладелец_ПоляЧатаНеСверяются()
    {
        var factory = Build(null);
        var s = Projectless("kid-pin-adult2");
        s.PersonaId = null; s.Provider = "claude"; s.Model = null; s.Mode = ClaudeMode.Bypass;
        var act = () => Launch(factory, s);
        act.Should().NotThrow();
    }

    // --- Б2b (И10, решение 71): живой маршрут урока — модель персоны-руководителя проекта-предмета ---

    [Fact]
    public void ПроектПредмета_УрокИдётПоМоделиРуководителя()
    {
        // Модель урока — модель руководителя проекта-предмета (полей предмета больше нет, Б17c):
        // чат на модели руководителя стартует, чат на старой модели или чужом репетиторе — нет.
        var kid = "kid-proj-1";
        var (projectId, tutorId) = SeedSubjectProject(kid, "math", "sonnet");
        var factory = Build(Child(kid), withSubjectProject: true);

        var onLeader = Lesson(kid, projectId, tutorId);
        onLeader.Model = "sonnet";
        Launch(factory, onLeader).Should().NotBeNull("урок идёт по живой модели репетитора");

        // Чат на записанных полях предмета — не совпадает уже персона (первый отказ по порядку
        // сверки): репетитор чата — руководитель проекта, а не тьютор из записки предмета.
        var stale = Lesson(kid, projectId, "tutor-math");
        stale.Model = "haiku";
        var staleAct = () => Launch(factory, stale);
        staleAct.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("персона чата не совпадает с репетитором");

        // Чат на живом репетиторе, но со старой моделью — отказ по модели: записанная модель
        // предмета не указ.
        var oldModel = Lesson(kid, projectId, tutorId);
        oldModel.Model = "haiku";
        var oldAct = () => Launch(factory, oldModel);
        oldAct.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("модель чата не совпадает с моделью репетитора");

        // Догон приводит закреплённые поля к тому же живому маршруту: та же точка развёртки, что у
        // сверки (LiveRouteFor и LaunchMismatch зовут LiveRouteOf — второй точки правды нет).
        _lastGate!.LiveRouteFor(kid, Lesson(kid, projectId, "tutor-math"))
            .Should().Be(new ChildRepinTarget(tutorId, "pool-key", "sonnet"));
    }

    [Fact]
    public void ПредметБезПроектаПредмета_ЖивогоМаршрутНет_НеУрокПоСтарымПолям()
    {
        // Б17c: переходной ветки «проекта-предмета нет → поля предмета» больше нет — пропавший
        // проект даёт отказ (живого маршрута нет), а не урок по записи предмета.
        var kid = "kid-no-proj";
        var factory = Build(Child(kid), withSubjectProject: true);
        _ = factory;
        _lastGate!.LiveRouteFor(kid, Lesson(kid, "no-such-project", "tutor-math")).Should().BeNull(
            "проекта-предмета у ребёнка нет — маршрут уроку не развёрнут");
    }

    [Fact]
    public void ПроектПредмета_РепетитораНет_ОтказЗапускаСПричиной_БезИсключения()
    {
        // Сбой резолва персоны (куратор удалил руководителя) и проект без руководителя — отказ
        // запуска с причиной; необработанного исключения наружу нет, догон такие чаты не трогает.
        var kid = "kid-proj-2";
        var (projectId, tutorId) = SeedSubjectProject(kid, "math", "sonnet");
        var factory = Build(Child(kid), withSubjectProject: true);
        var s = Lesson(kid, projectId, tutorId);
        s.Model = "sonnet";
        Launch(factory, s).Should().NotBeNull("на живом репетиторе ход идёт");

        _host.Services.GetRequiredService<PersonaManager>().Delete(tutorId, kid);
        var removed = Lesson(kid, projectId, tutorId);
        removed.Model = "sonnet";
        var act = () => Launch(factory, removed);
        act.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("персона-репетитор предмета не найдена");
        _lastGate!.LiveRouteFor(kid, removed).Should().BeNull("догон нечего приводить — живого маршрута нет");

        // Тот же исход, когда проект остался без руководителя (DefaultPersonaId снят).
        var orphanKid = "kid-proj-3";
        var (orphanId, orphanLeader) = SeedSubjectProject(orphanKid, "math", "sonnet");
        var projects = _host.Services.GetRequiredService<ProjectManager>();
        projects.SetDefaultPersona(orphanId, null);
        var noLead = Lesson(orphanKid, orphanId, orphanLeader);
        noLead.Model = "sonnet";
        var noLeadFactory = Build(Child(orphanKid), withSubjectProject: true);
        var noLeadAct = () => Launch(noLeadFactory, noLead);
        noLeadAct.Should().Throw<ChildRouteRefusedException>().Which.Reason.Should().Contain("у предмета нет репетитора");
    }
}
