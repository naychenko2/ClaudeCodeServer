using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// И3 на точке запуска хода ребёнка (KM-B5d, ADR-041): вне окна, сверх дневного лимита
/// пространства и сверх бюджета новый ход не стартует; отказ — штатная ошибка хода с детским
/// текстом состояния, без технических слов.
///
/// Тесты идут через настоящий TurnLaunchContextFactory.BuildForNewSession / BuildForReconnect,
/// а не через гейт напрямую: мутация «убрать вызов из Compose» краснеет на всех отказах, мутация
/// «проверять только ручные ходы» (в копии: CheckLimits только в BuildForNewSession) — на тесте
/// автопродолжения. Гейт стоит после И1-бис (KM-B3a), и fixture собирает чат, который И1-бис
/// проходит: красный тест значит именно И3.
///
/// Часы: Compose передаёт DateTimeOffset.UtcNow, поэтому окна профиля строятся ОТНОСИТЕЛЬНО
/// текущего момента в таймзоне ребёнка — «в окне» = [сейчас, сейчас+30 мин), «вне окна» =
/// [сейчас+2ч, сейчас+3ч). Состояние по времени не зависит от журнала (он пустой на каждый тест):
/// time_up — дневной лимит 0, warn — остаток ровно на пороге warnMinutes, ok — остаток больше
/// порога. Таймзона — Europe/Moscow (IANA без DST, есть и на Linux, и на Windows).
/// Тексты отказа в тестах — дословные: это обещание ребёнку, его правка — видимая правка теста.
/// </summary>
[Trait("Category", "Host")]
public sealed class ChildLaunchLimitsTests : IDisposable
{
    private const string TimeUpText = "На сегодня уроки закончились. Отдохни и возвращайся завтра.";
    private const string OutOfWindowText = "Сейчас уроки отдыхают. Вернись, когда они снова начнутся.";
    private const string BudgetText = "На сегодня всё по деньгам закончилось. Позови взрослого.";
    private const string RulesText = "Сначала надо договорить о правилах. Позови взрослого — правила вы ставите вместе.";

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ChildUsageStore _usage;
    private readonly ChildModePolicy _policy;

    public ChildLaunchLimitsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-child-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var userConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        _users = new UserStore(userConfig, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        // Пустой журнал присутствия: ни одной засчитанной минуты сегодня — остаток равен лимиту.
        _usage = new ChildUsageStore(Path.Combine(_tempDir, ChildUsageStore.DirName));
        _policy = new ChildModePolicy(_users, NullLogger<ChildModePolicy>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    // ─── Fixture ────────────────────────────────────────────────────────────────

    /// <summary>Окно относительно текущего момента ребёнка, в минутах от него.</summary>
    private static string[] Window(int fromOffsetMin, int toOffsetMin)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow);
        return [now.AddMinutes(fromOffsetMin).ToString("HH:mm"), now.AddMinutes(toOffsetMin).ToString("HH:mm")];
    }

    /// <summary>
    /// Профиль, который И1-бис проходит: тьютор/поставщик/модель — те же, что у ChildChat ниже.
    /// </summary>
    private static ChildProfile Profile(
        int? dailyMinutes = 60,
        string[]? window = null,
        int warn = 5,
        bool rulesAgreed = true,
        decimal? dailyBudgetUsd = null)
    {
        var w = window ?? Window(0, 30);
        return new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor-1",
            ProviderKey = "pool-key",
            Model = "sonnet",
            Spaces =
            {
                ["lessons"] = new SpaceLimits
                {
                    Windows = [new TimeWindow { From = w[0], To = w[1] }],
                    DailyMinutes = dailyMinutes,
                },
            },
            DailyBudgetUsd = dailyBudgetUsd,
            WarnMinutes = warn,
            RulesAgreedAt = rulesAgreed ? new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc) : null,
        };
    }

    private string AddChild(ChildProfile profile)
    {
        var user = _users.Add("kid-" + Guid.NewGuid().ToString("N"), "pass", "user");
        user.TimeZone = "Europe/Moscow";
        user.ChildMode = profile;
        return user.Id;
    }

    /// <summary>Детский чат, который проходит И1-бис: закреплённые поля равны профилю.</summary>
    private static Session ChildChat(string ownerId, string? childSpace = "lessons", string? onboardingKind = null) => new()
    {
        Id = "s-" + Guid.NewGuid().ToString("N"),
        OwnerId = ownerId,
        PersonaId = "tutor-1",
        Provider = "pool-key",
        Model = "sonnet",
        ProjectId = null,
        Mode = ChildModeLaunch.RequiredMode,
        ChildSpace = childSpace,
        OnboardingKind = onboardingKind,
    };

    /// <summary>
    /// Живая фабрика контекста: живой гейт над живым калькулятором (политика — UserStore, журнал —
    /// пустой, шва Spend — несущая нулевой расход: отсутствующая шва сама дала бы budget_exhausted).
    /// Возвращает и ownerId ребёнка, которого добавила.
    /// </summary>
    private (TurnLaunchContextFactory Factory, TestWebApplicationFactory Host, string OwnerId) Build(
        ChildProfile profile, IOwnerSpendReader? spend = null)
    {
        var ownerId = AddChild(profile);
        var host = new TestWebApplicationFactory();
        _ = host.CreateClient();
        var sp = host.Services;
        var calc = new ChildLimitsCalculator(_policy, _usage, spend ?? new FixedSpend(0m, 0m), _users,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChildLimitsCalculator.ConfigTimeZoneKey] = "Europe/Moscow",
            }).Build(),
            NullLogger<ChildLimitsCalculator>.Instance);
        var gate = new ChildModeGate(_policy, calc);
        // BuiltinMcpContexts строим заново — со своим гейтом; остальное берём из DI как есть
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
        return (factory, host, ownerId);
    }

    private static void LaunchNew(TurnLaunchContextFactory f, Session s) =>
        f.BuildForNewSession(s, s.OwnerId, null, Path.GetTempPath(), Path.GetTempPath(), null, null,
            new TurnAccumulator(new List<StoredMessage>()), 1, (_, _, _, _, _) => Task.CompletedTask,
            null, Path.GetTempPath(), true);

    // Путь автопродолжения после рестарта, ретраев и цикла «до готово»: EnsureProcessCoreAsync зовёт
    // BuildForReconnect (TurnProcessHost.cs:201,214) — тот же Compose и тот же гейт.
    private static void LaunchReconnect(TurnLaunchContextFactory f, Session s) =>
        f.BuildForReconnect(new SessionEntry { Info = s }, null, Path.GetTempPath(),
            new TurnAccumulator(new List<StoredMessage>()), 1, null,
            (_, _, _, _, _) => Task.CompletedTask, null, Path.GetTempPath(), true);

    private sealed class FixedSpend(decimal todayUsd, decimal monthUsd) : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => todayUsd;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => monthUsd;
    }

    // ─── Отказ по состоянию (c1) ────────────────────────────────────────────────

    public static TheoryData<string, string> RefusedStates => new()
    {
        { "time_up", TimeUpText },
        { "out_of_window", OutOfWindowText },
        { "budget_exhausted", BudgetText },
        { "rules_pending", RulesText },
    };

    [Theory]
    [MemberData(nameof(RefusedStates))]
    public void СостояниеОтказа_ХодНеСтартует_ТекстДетский(string state, string text)
    {
        var (profile, spend) = state switch
        {
            "time_up" => (Profile(dailyMinutes: 0), (IOwnerSpendReader?)null),
            "out_of_window" => (Profile(window: Window(120, 180)), null),
            "budget_exhausted" => (Profile(dailyBudgetUsd: 1m), new FixedSpend(2m, 2m)),
            _ => (Profile(rulesAgreed: false), null),
        };
        var (factory, host, ownerId) = Build(profile, spend);
        using var _ = host;

        var act = () => LaunchNew(factory, ChildChat(ownerId));
        act.Should().Throw<InvalidOperationException>().WithMessage(text, $"отказ {state} — детским текстом");
    }

    [Fact]
    public void ОкИWarn_ХодИдёт()
    {
        // 60 минут остатка — ok; 5 минут при пороге 5 — warn: предупреждение, не отказ.
        foreach (var (daily, expected) in new[] { (60, "ok"), (5, "warn") })
        {
            var (factory, host, ownerId) = Build(Profile(dailyMinutes: daily));
            using var _ = host;
            var act = () => LaunchNew(factory, ChildChat(ownerId));
            act.Should().NotThrow($"{expected}: остаток {daily} мин при пороге 5 не останавливает ход");
        }
    }

    // ─── Единственное исключение (c1) ───────────────────────────────────────────

    [Fact]
    public void ПравилаНеСогласованы_ЧатОнбордингаПравил_ХодИдёт()
    {
        var (factory, host, ownerId) = Build(Profile(rulesAgreed: false));
        using var _ = host;
        var act = () => LaunchNew(factory, ChildChat(ownerId, onboardingKind: OnboardingKinds.ChildRules));
        act.Should().NotThrow("rules_pending не блокирует чат, который эти правила и согласовывает");
    }

    public static TheoryData<string> StillRefusedForRulesChat => new() { "time_up", "out_of_window" };

    [Theory]
    [MemberData(nameof(StillRefusedForRulesChat))]
    public void ЧатОнбордингаПравил_ВнеОкнаИлиБезВремени_ХодНеСтартует(string state)
    {
        var profile = state == "time_up"
            ? Profile(dailyMinutes: 0, rulesAgreed: false)
            : Profile(window: Window(120, 180), rulesAgreed: false);
        var (factory, host, ownerId) = Build(profile);
        using var _ = host;
        var act = () => LaunchNew(factory, ChildChat(ownerId, onboardingKind: OnboardingKinds.ChildRules));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage(state == "time_up" ? TimeUpText : OutOfWindowText,
                "исключение одно: отсутствие согласия; окно и дневной лимит действуют и на него");
    }

    // ─── Другие пути к той же точке (c2) ───────────────────────────────────────

    [Fact]
    public void АвтопродолжениеПослеРестарта_ПриTimeUp_ХодНеСтартует()
    {
        var (factory, host, ownerId) = Build(Profile(dailyMinutes: 0));
        using var _ = host;
        var act = () => LaunchReconnect(factory, ChildChat(ownerId));
        act.Should().Throw<InvalidOperationException>().WithMessage(TimeUpText);
    }

    [Fact]
    public void ДетскийЧатБезПространства_ХодНеСтартует()
    {
        // «Входить некуда» — правило самого калькулятора (пустой ключ пространства = вне окна):
        // детский чат без ChildSpace — fail-closed, а не ход без лимитов.
        var (factory, host, ownerId) = Build(Profile());
        using var _ = host;
        var act = () => LaunchNew(factory, ChildChat(ownerId, childSpace: null));
        act.Should().Throw<InvalidOperationException>().WithMessage(OutOfWindowText);
    }

    [Fact]
    public void СверхДневногоБюджета_ВОткрытомОкне_ХодНеСтартует()
    {
        var (factory, host, ownerId) = Build(Profile(dailyBudgetUsd: 1m), new FixedSpend(2m, 2m));
        using var _ = host;
        var act = () => LaunchNew(factory, ChildChat(ownerId));
        act.Should().Throw<InvalidOperationException>().WithMessage(BudgetText);
    }
}
