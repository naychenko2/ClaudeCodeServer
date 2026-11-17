using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// И1 (KM-B3b), сторона сборки хода: детский слой запретов встроенных инструментов в
/// PersonaLayerBuilder (п.11), пустой источник плагинов в TurnLaunchContextFactory (п.13) и отказ
/// автоматике персон детского владельца (п.18). Идут через настоящие фабрики, как ChildLaunchPinTests.
/// </summary>
[Trait("Category", "Host")]
public class ChildCliLayerFactoryTests
{
    private const string Kid = "kid-1";

    private sealed class FakePolicy(ChildPolicySnapshot? snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => ownerId == Kid ? snapshot : null;
    }

    private sealed class FixedPlugins : IPluginDirSource
    {
        public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project? project) => ["/plugins/x"];
    }

    private static ChildPolicySnapshot ValidChild() => new(Kid,
        new ChildProfile { Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet" }, false);

    private static Session KidSession() => new()
    {
        Id = "s-1", OwnerId = Kid, PersonaId = "tutor-1", Provider = "pool-key", Model = "sonnet",
        ProjectId = null, Mode = ChildModeLaunch.RequiredMode,
    };

    private static (PersonaLayerBuilder Layer, TurnLaunchContextFactory Factory, IServiceProvider Sp, TestWebApplicationFactory Host)
        Build(ChildPolicySnapshot? snapshot, string owner)
    {
        var host = new TestWebApplicationFactory();
        _ = host.CreateClient();
        var sp = host.Services;
        var gate = new ChildModeGate(new FakePolicy(snapshot));
        var layer = new PersonaLayerBuilder(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<PersonaManager>(), sp.GetRequiredService<PersonaBindingsService>(),
            sp.GetService<PersonaAgentFileSync>(), sp.GetRequiredService<ProjectManager>(),
            sp.GetService<ClaudeHomeServer.Services.Spheres.ISphereDirectory>(),
            NullLogger<PersonaLayerBuilder>.Instance, gate);
        var builtin = new BuiltinMcpContexts(
            sp.GetRequiredService<McpEndpoint>(), sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaBindingsService>(), sp,
            sp.GetService<ClaudeHomeServer.Services.Composition.SubsystemStateStore>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>(),
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), gate);
        var factory = new TurnLaunchContextFactory(builtin, layer,
            sp.GetRequiredService<ExternalMcpContexts>(), sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.ILauncherFactory>(), new FixedPlugins(),
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<ITurnMessageSink>>(), sp.GetRequiredService<Lazy<ITurnStarter>>());
        return (layer, factory, sp, host);
    }

    private static ClaudeHomeServer.Services.Llm.LlmSessionContext Launch(TurnLaunchContextFactory f, Session s) =>
        f.BuildForNewSession(s, s.OwnerId, null, Path.GetTempPath(), Path.GetTempPath(), null, null,
            new TurnAccumulator(new List<StoredMessage>()), 1, (_, _, _, _, _) => Task.CompletedTask,
            null, Path.GetTempPath(), true);

    [Fact]
    public void ХодРебёнка_ВстроенныеЗакрытыЗапретами_ИИсточникПлагиновПуст()
    {
        var (_, factory, _, host) = Build(ValidChild(), Kid);
        using var _h = host;

        var ctx = Launch(factory, KidSession());

        using var scope = new AssertionScope();
        ctx.ExtraDisallowedTools.Should().Contain(PersonaLayerBuilder.ChildDisallowedBuiltins);
        ctx.PluginDirSource.Should().BeNull("плагины владельца ребёнку не отдаются");
    }

    [Fact]
    public void Запреты_ДетскийСлойСкладываетсяСЗапретамиПерсоны_БезПотерь()
    {
        var (layer, _, _, host) = Build(ValidChild(), Kid);
        using var _h = host;
        var session = KidSession();
        session.StageDisallowedTools = ["ExitPlanMode"];

        var list = layer.BuildExtraDisallowed(Kid, null, session);

        list.Should().Contain("ExitPlanMode", "запрет этапа не теряется");
        list.Should().Contain(PersonaLayerBuilder.ChildDisallowedBuiltins);
        list.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ВзрослыйВладелец_ДетскихЗапретовНет_ПлагиныНаМесте()
    {
        var (layer, factory, _, host) = Build(null, "adult");
        using var _h = host;
        var session = KidSession();
        session.OwnerId = "adult";
        session.PersonaId = null;

        (layer.BuildExtraDisallowed("adult", null, session) ?? []).Should().NotContain("Bash");
        Launch(factory, session).PluginDirSource.Should().NotBeNull();
    }

    [Fact]
    public async Task АвтоматикаПерсоны_ДетскийВладелец_Отказ403_ВзрослыйНеЗатронут()
    {
        var (_, _, sp, host) = Build(ValidChild(), Kid);
        using var _h = host;
        var gate = new ChildModeGate(new FakePolicy(ValidChild()));
        var crud = ActivatorUtilities.CreateInstance<PersonasCrudService>(sp, gate);
        var req = new AutomationRuleRequest(true, "правило", AutomationTriggerType.Timer, null, null, null, null,
            null, null, "сделай", null);

        var add = await crud.AddAutomationRuleAsync(Kid, "any", req);
        var upd = await crud.UpdateAutomationRuleAsync(Kid, "any", "r1", req);
        var adultAdd = await crud.AddAutomationRuleAsync("adult", "any", req);

        ((ObjectResult)add.Result!).StatusCode.Should().Be(403);
        ((ObjectResult)upd.Result!).StatusCode.Should().Be(403);
        adultAdd.Result.Should().BeOfType<NotFoundResult>("взрослого гейт не трогает — дальше обычная проверка персоны");
    }
}
