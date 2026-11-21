using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// И1 (KM-B3b), сторона сборки хода: детский слой запретов встроенных инструментов в
/// PersonaLayerBuilder (п.11), пустой источник плагинов в TurnLaunchContextFactory (п.13) и отказ
/// автоматике персон детского владельца (п.18). Идут через настоящие фабрики, как ChildLaunchPinTests.
/// </summary>
[Trait("Category", "Host")]
public class ChildCliLayerFactoryTests : IClassFixture<ChildCliLayerFactoryTests.Hosts>
{
    // Хост на КЛАСС (храповик «хост на класс»): тесты хост не мутируют, детская политика и гейт строятся
    // локально поверх его DI, поэтому порядок тестов не важен
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _host;

    public ChildCliLayerFactoryTests(Hosts host) => _host = host;

    // Ребёнок — уникальный id на тест: живой маршрут резолвится по первому проекту-предмету
    // совпадающего предмета (ChildModeGate.cs:234), общий id на класс привёл бы чужого руководителя.
    private static string NewKid() => "kid-cli-" + Guid.NewGuid().ToString("N");

    private sealed class FakePolicy(string kid, ChildPolicySnapshot? snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => ownerId == kid ? snapshot : null;
    }

    private sealed class FixedPlugins : IPluginDirSource
    {
        public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project? project) => ["/plugins/x"];
    }

    // Сутки целиком и согласованные правила: И3 (KM-B5d) этим тестом не проверяется, ему нужно лишь
    // пропустить ход, чтобы дойти до сборки слоя CLI (как в ChildLaunchPinTests)
    private static ChildPolicySnapshot ValidChild(string kid) => new(kid,
        new ChildProfile
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
        }, false);

    private sealed class ZeroSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
    }

    /// <summary>
    /// Живой урок (Б9b): чат проекта-предмета «math» этого ребёнка, персона — руководитель проекта,
    /// модель — живая модель руководителя (та же форма, что у ChildLaunchPinTests.Lesson и зелёного
    /// ChildChatsControllerTests.NewChild): без проекта И1-бис отказывает «старый урок только для
    /// чтения» и до сборки слоя CLI дело не доходит.
    /// </summary>
    private static Session KidSession(string kid, string? projectId, string? leaderId) => new()
    {
        Id = "s-1", OwnerId = kid, PersonaId = leaderId ?? "tutor-1", Provider = "pool-key", Model = "sonnet",
        ProjectId = projectId, Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = "math",
    };

    /// <summary>
    /// Проект-предмета ребёнка и его руководитель с явной моделью — прямая запись того, что в бою
    /// провизнит Б17a (та же форма, что в ChildLaunchPinTests.SeedSubjectProject): маркер
    /// ChildSubjectId, DefaultPersonaId = репетитор. Модель руководителя — единственный источник
    /// живого маршрута урока (решение 71), поэтому она равна модели чата в фикстуре.
    /// </summary>
    private (string ProjectId, string LeaderId) SeedSubjectProject(string childId, string subjectId, string tempDir)
    {
        var sp = _host.Services;
        var root = Path.Combine(tempDir, "subject-" + subjectId);
        // Create с createDirectory: false папку требует существующую, а не создаёт.
        Directory.CreateDirectory(root);
        var projects = sp.GetRequiredService<ProjectManager>();
        var project = projects.Create("Предмет " + subjectId, root, childId, childId,
            createDirectory: false, childSubjectId: subjectId);
        var tutor = sp.GetRequiredService<PersonaManager>().Create(childId, "Репетитор " + subjectId,
            null, null, null, "sonnet", null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        projects.SetDefaultPersona(project.Id, tutor.Id);
        return (project.Id, tutor.Id);
    }

    private (PersonaLayerBuilder Layer, TurnLaunchContextFactory Factory, IServiceProvider Sp, string? ProjectId, string? LeaderId)
        Build(ChildPolicySnapshot? snapshot, string owner)
    {
        _ = _host.CreateClient();
        var sp = _host.Services;
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs-cli-layer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var users = new UserStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataPath"] = Path.Combine(tempDir, "projects.json") }).Build(),
            new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var policy = new FakePolicy(owner, snapshot);
        var calc = new ChildLimitsCalculator(policy, new ChildUsageStore(Path.Combine(tempDir, ChildUsageStore.DirName)),
            new ZeroSpend(), users,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChildLimitsCalculator.ConfigTimeZoneKey] = "Europe/Moscow",
            }).Build(), NullLogger<ChildLimitsCalculator>.Instance);
        var (pool, llm) = ChildRouteKit.For("pool-key");
        // Детская фикстура — урок проекта-предмета: реестр проектов, стор персон и резолвер модели —
        // тот же состав, что в боевом ядре; без них И1-бис не пускает ход к сборке слоя CLI.
        var (projectId, leaderId) = snapshot is null
            ? ((string?)null, (string?)null)
            : SeedSubjectProject(owner, "math", tempDir);
        var gate = new ChildModeGate(policy, calc, null, pool, llm,
            sp.GetRequiredService<ProjectManager>(), sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ModelAssignmentResolver>());
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
        return (layer, factory, sp, projectId, leaderId);
    }

    private static ClaudeHomeServer.Services.Llm.LlmSessionContext Launch(TurnLaunchContextFactory f, Session s) =>
        f.BuildForNewSession(s, s.OwnerId, null, Path.GetTempPath(), Path.GetTempPath(), null, null,
            new TurnAccumulator(new List<StoredMessage>()), 1, (_, _, _, _, _) => Task.CompletedTask,
            null, Path.GetTempPath(), true);

    [Fact]
    public void ХодРебёнка_ВстроенныеЗакрытыЗапретами_ИИсточникПлагиновПуст()
    {
        var kid = NewKid();
        var (_, factory, _, projectId, leaderId) = Build(ValidChild(kid), kid);

        var ctx = Launch(factory, KidSession(kid, projectId, leaderId));

        using var scope = new AssertionScope();
        ctx.ExtraDisallowedTools.Should().Contain(PersonaLayerBuilder.ChildDisallowedBuiltins);
        ctx.PluginDirSource.Should().BeNull("плагины владельца ребёнку не отдаются");
    }

    [Fact]
    public void Запреты_ДетскийСлойСкладываетсяСЗапретамиПерсоны_БезПотерь()
    {
        var kid = NewKid();
        var (layer, _, _, projectId, leaderId) = Build(ValidChild(kid), kid);
        var session = KidSession(kid, projectId, leaderId);
        session.StageDisallowedTools = ["ExitPlanMode"];

        var list = layer.BuildExtraDisallowed(kid, null, session);

        list.Should().Contain("ExitPlanMode", "запрет этапа не теряется");
        list.Should().Contain(PersonaLayerBuilder.ChildDisallowedBuiltins);
        list.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ВзрослыйВладелец_ДетскихЗапретовНет_ПлагиныНаМесте()
    {
        var (layer, factory, _, _, _) = Build(null, "adult");
        // Взрослый: проекта-предмета нет, детская сверка его полей не читает
        var session = KidSession("kid-cli-adult", null, null);
        session.OwnerId = "adult";
        session.PersonaId = null;

        (layer.BuildExtraDisallowed("adult", null, session) ?? []).Should().NotContain("Bash");
        Launch(factory, session).PluginDirSource.Should().NotBeNull();
    }

    [Fact]
    public async Task АвтоматикаПерсоны_ДетскийВладелец_Отказ403_ВзрослыйНеЗатронут()
    {
        var kid = NewKid();
        var (_, _, sp, _, _) = Build(ValidChild(kid), kid);
        var gate = new ChildModeGate(new FakePolicy(kid, ValidChild(kid)));
        var crud = ActivatorUtilities.CreateInstance<PersonasCrudService>(sp, gate);
        var req = new AutomationRuleRequest(true, "правило", AutomationTriggerType.Timer, null, null, null, null,
            null, null, "сделай", null);

        var add = await crud.AddAutomationRuleAsync(kid, "any", req);
        var upd = await crud.UpdateAutomationRuleAsync(kid, "any", "r1", req);
        var adultAdd = await crud.AddAutomationRuleAsync("adult", "any", req);

        ((ObjectResult)add.Result!).StatusCode.Should().Be(403);
        ((ObjectResult)upd.Result!).StatusCode.Should().Be(403);
        adultAdd.Result.Should().BeOfType<NotFoundResult>("взрослого гейт не трогает — дальше обычная проверка персоны");
    }
}
