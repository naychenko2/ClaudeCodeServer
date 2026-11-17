using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// И1-бис (KM-B3a): на запуске хода поля детского чата сверяются с профилем; расхождение — ход не
/// стартует (исключение из Compose), без тихой подстановки. По одному кейсу на каждое из пяти
/// полей плюс «всё сошлось» и «обычный владелец не затронут». Тесты идут через настоящий
/// TurnLaunchContextFactory.BuildForNewSession, а не через гейт напрямую: мутация «убрать вызов
/// в Compose» должна краснить их все.
/// </summary>
[Trait("Category", "Host")]
public class ChildLaunchPinTests
{
    private const string Kid = "kid-1";

    private sealed class FakePolicy(ChildPolicySnapshot? snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => snapshot;
    }

    private static ChildProfile Profile() => new()
    {
        Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
    };

    private static Session ValidSession() => new()
    {
        Id = "s-1", OwnerId = Kid, PersonaId = "tutor-1", Provider = "pool-key", Model = "sonnet",
        ProjectId = null, Mode = ChildModeLaunch.RequiredMode,
    };

    private static (TurnLaunchContextFactory Factory, TestWebApplicationFactory Host) Build(ChildPolicySnapshot? snapshot)
    {
        var host = new TestWebApplicationFactory();
        _ = host.CreateClient();
        var sp = host.Services;
        var gate = new ChildModeGate(new FakePolicy(snapshot));
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
        return (factory, host);
    }

    private static ClaudeHomeServer.Services.Llm.LlmSessionContext Launch(TurnLaunchContextFactory f, Session s) =>
        f.BuildForNewSession(s, s.OwnerId, null, Path.GetTempPath(), Path.GetTempPath(), null, null,
            new TurnAccumulator(new List<StoredMessage>()), 1, (_, _, _, _, _) => Task.CompletedTask,
            null, Path.GetTempPath(), true);

    private static ChildPolicySnapshot ValidChild() => new(Kid, Profile(), DenyAll: false);

    [Fact]
    public void ВсёСошлось_КонтекстСобираетсяБезВнешнихСерверов()
    {
        var (factory, host) = Build(ValidChild());
        using var _ = host;
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
        var (factory, host) = Build(ValidChild());
        using var _ = host;
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

    [Fact]
    public void ПрофильDenyAll_ХодНеСтартует()
    {
        var (factory, host) = Build(new ChildPolicySnapshot(Kid, null, DenyAll: true));
        using var _ = host;
        var act = () => Launch(factory, ValidSession());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ОбычныйВладелец_ПоляЧатаНеСверяются()
    {
        var (factory, host) = Build(null);
        using var _ = host;
        var s = ValidSession();
        s.PersonaId = null; s.Provider = "claude"; s.Model = null; s.Mode = ClaudeMode.Bypass;
        var act = () => Launch(factory, s);
        act.Should().NotThrow();
    }
}
