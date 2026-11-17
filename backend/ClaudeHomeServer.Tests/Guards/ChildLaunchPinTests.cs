using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using ClaudeHomeServer.Services.Spend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// И1-бис (KM-B3a): на запуске хода поля детского чата сверяются с профилем; расхождение — ход не
/// стартует (исключение из Compose), без тихой подстановки. По одному кейсу на каждое из пяти
/// полей плюс «всё сошлось» и «обычный владелец не затронут». Тесты идут через настоящий
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

    public ChildLaunchPinTests(Hosts host) => _host = host;

    private const string Kid = "kid-1";

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
        Subjects =
        [
            new ChildSubject
            {
                Id = "math", Label = "Математика", Glyph = "×÷", Color = "info",
                TutorPersonaId = "tutor-math", ProviderKey = "pool-math", Model = "haiku", Enabled = true,
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

    private static Session ValidSession() => new()
    {
        Id = "s-1", OwnerId = Kid, PersonaId = "tutor-math", Provider = "pool-math", Model = "haiku",
        ProjectId = null, Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = "math",
    };

    private TurnLaunchContextFactory Build(ChildPolicySnapshot? snapshot)
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
        var gate = new ChildModeGate(policy, calc);
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

    private static ChildPolicySnapshot ValidChild() => new(Kid, Profile(), DenyAll: false);

    [Fact]
    public void ВсёСошлось_КонтекстСобираетсяБезВнешнихСерверов()
    {
        var factory = Build(ValidChild());
        var ctx = Launch(factory, ValidSession());
        ctx.ExternalMcpProvider.Should().BeNull("внешние серверы владельца ребёнку не объявляются");
        ctx.ModulesMcp.Should().BeNull();
        ctx.WebSearchMcp.Should().BeNull("веб-поиск вне детского allow-list");
        ctx.WorkspaceMcp.Should().BeNull();
        ctx.NotesMcp.Should().BeNull();
        ctx.TasksMcp.Should().BeNull();
        ctx.MemoryMcp.Should().BeNull();
        ctx.HttpMcpActive.Should().BeFalse();
    }

    public static TheoryData<string> Fields => ["persona", "provider", "model", "project", "mode"];

    [Theory]
    [MemberData(nameof(Fields))]
    public void РасхождениеОдногоПоля_ХодНеСтартует(string field)
    {
        var factory = Build(ValidChild());
        var s = ValidSession();
        switch (field)
        {
            case "persona": s.PersonaId = "other-persona"; break;
            case "provider": s.Provider = "claude"; break;
            case "model": s.Model = "opus"; break;
            case "project": s.ProjectId = "proj-1"; break;
            case "mode": s.Mode = ClaudeMode.Bypass; break;
        }
        var act = () => Launch(factory, s);
        act.Should().Throw<InvalidOperationException>().WithMessage("Детский режим*");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("null")]
    public void ПредметЗакрыт_ХодНеСтартует(string how)
    {
        var snapshot = ValidChild();
        var s = ValidSession();
        switch (how)
        {
            case "disabled": snapshot.Profile!.Subjects[0].Enabled = false; break;
            case "deleted": snapshot.Profile!.Subjects.Clear(); break;
            case "null": s.ChildSubject = null; break;
        }
        var act = () => Launch(Build(snapshot), s);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Этот предмет закрыт*");
    }

    [Fact]
    public void ЧатПравил_СверяетсяСПрофилем_БезПредмета()
    {
        var s = ValidSession();
        s.OnboardingKind = OnboardingKinds.ChildRules;
        s.ChildSubject = null; s.PersonaId = "tutor-1"; s.Provider = "pool-key"; s.Model = "sonnet";
        var factory = Build(ValidChild());
        var act = () => Launch(factory, s);
        act.Should().NotThrow();
        s.Model = "haiku"; // запись предмета чату правил не указ
        act.Should().Throw<InvalidOperationException>().WithMessage("Детский режим*");
    }

    [Fact]
    public void ПрофильDenyAll_ХодНеСтартует()
    {
        var factory = Build(new ChildPolicySnapshot(Kid, null, DenyAll: true));
        var act = () => Launch(factory, ValidSession());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ОбычныйВладелец_ПоляЧатаНеСверяются()
    {
        var factory = Build(null);
        var s = ValidSession();
        s.PersonaId = null; s.Provider = "claude"; s.Model = null; s.Mode = ClaudeMode.Bypass;
        var act = () => Launch(factory, s);
        act.Should().NotThrow();
    }
}
