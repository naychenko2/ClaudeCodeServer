using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Б16e сторож (И10, разрез «Маршруты взрослых для ребёнка» §13 «Нужно сторожем», ADR-041 ред. 7):
/// детский слой — свойство хода (IsChild), поля персоны его только сужают. Персона-руководитель
/// проекта-предмета со ВСЕМИ полями «на максимуме» (Access полный, DisallowedTools пуст,
/// Specialty, Tools все, гранты MCP, привязки, навыки, плагины, автоматика, MemoryEnabled) →
/// состав контекста запуска детского хода равен составу контекста хода с персоной «по умолчанию».
/// Обратное: DisallowedTools=[Write] — запрет доезжает до списка запретов контекста детского хода
/// и остаётся в нём: при сборке аргументов он больше не снимается целым списком (Х-W4-17), иначе
/// репетитор с Access = ReadOnly получал бы право писать в папку предмета. На аргументах CLI и на
/// границе can_use_tool это держит
/// ChildCliLayerTests.ХодРебёнка_ЗапретПерсоныНаЗаписи_ДоезжаетДоCLI_ИОтказываетНаСвоемФайле.
///
/// Аргументы строит та же фабрика запуска (TurnLaunchContextFactory), что в ChildCliLayerFactoryTests,
/// но фикстура — живая форма Б17b: урок — чат проекта-предмета, репетитор — руководитель проекта
/// (DefaultPersonaId), живой маршрут — модель персоны; сверка запуска ChildModeGate.LaunchMismatch
/// проходит по-настоящему.
/// Равенство — по составу контекста запуска (LlmSessionContext): сторож сравнивает его состав, сам
/// аргументы CLI не строит. --tools/--setting-sources/--strict-mcp-config сторож НЕ покрывает (мутация
/// «детский ход берёт Persona.Tools» его не краснит): это свойства детского хода, не персоны, их
/// фиксирует ChildCliLayerTests.ХодРебёнка_РазрешающийСписокВстроенных_НастройкиТолькоUser_БезПлагинов_БезCLAUDEmd
/// (ClaudeHomeServer.Llm.Tests/Services/ChildCliLayerTests.cs:294).
/// </summary>
[Trait("Category", "Host")]
public class ChildTutorPersonaMaxGuardTests : IClassFixture<ChildTutorPersonaMaxGuardTests.Hosts>
{
    // Хост на КЛАСС (храповик «хост на класс»): тесты хост не мутируют, детская политика и гейт
    // строятся локально поверх его DI, поэтому порядок тестов не важен
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _host;

    public ChildTutorPersonaMaxGuardTests(Hosts host) => _host = host;

    private const string SubjectId = "math";
    private const string PoolKey = "b16e-pool";
    private const string TutorModel = "sonnet";

    // Максимум Tools = весь каталог ключей (свойства, а не копия списка: растёт вместе с ToolCatalog)
    private static readonly string[] AllToolKeys = [.. PersonaBindingsService.ToolCatalog.Keys];

    private PersonaManager Personas => _host.Services.GetRequiredService<PersonaManager>();
    private UserStore Users => _host.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();

    private sealed class FakePolicy(ChildPolicySnapshot snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => ownerId == snapshot.OwnerId ? snapshot : null;
    }

    private sealed class FixedPlugins : IPluginDirSource
    {
        public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project? project) => ["/plugins/x"];
    }

    private sealed class ZeroSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0m;
    }

    // Валидный профиль ребёнка с согласием на риск и включённым предметом (образец ChildCliLayerFactoryTests)
    private static ChildProfile KidProfile() => new()
    {
        Enabled = true, ProviderKey = PoolKey, Model = TutorModel,
        RulesAgreedAt = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc),
        RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt, RouteRiskAcknowledgedBy = ChildTestSubjects.ConsentBy,
        Subjects =
        [
            // предмет — только плитка (Б17c): репетитор и модель — не поля предмета,
            // аккаунт живого маршрута — ChildProfile.ProviderKey (он задан выше)
            new ChildSubject { Id = SubjectId, Label = "Математика", Glyph = "×÷", Color = "info", Enabled = true },
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

    // ── фикстура: ребёнок-владелец, проект-предмет, урок; живая сверка запуска ────────

    private sealed record Prepared(string KidId, Project Project);

    private sealed record Kit(TurnLaunchContextFactory Factory, Session Session, string KidId);

    private sealed record Pair(LlmSessionContext Max, LlmSessionContext Default,
        string MaxTutorId, string KidId, Project Project);

    private Prepared Prepare(string tag)
    {
        _ = _host.CreateClient();
        var kid = Users.Add("b16e-" + tag, "b16e-pw-" + tag, "user");
        var project = Projects.Create("Предмет-" + tag, Path.Combine(_host.TempDir, "b16e_" + tag),
            kid.Id, kid.Username, createDirectory: true, childSubjectId: SubjectId);
        return new Prepared(kid.Id, project);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private sealed class TutorSpec
    {
        public string Handle = "";
        public PersonaScope Scope = PersonaScope.Project;
        public string? ProjectId;
        public PersonaAccess Access = PersonaAccess.Full;
        public List<string>? DisallowedTools;
        public PersonaSpecialty Specialty = PersonaSpecialty.None;
        public List<string>? Tools;
        public bool MemoryEnabled = true;
        public List<PersonaBinding>? Bindings;
        public List<PersonaAutomationRule>? Rules;
        public bool MaxExtras; // все «свободные» поля: контракт, голос, аватар-цвет, секции, тир-ячейки…
    }

    private Persona CreateTutor(string ownerId, TutorSpec spec)
    {
        var p = Personas.Create(ownerId, "Репетитор-" + spec.Handle, "Репетитор", "Описание репетитора",
            "legacy-промпт персоны", TutorModel, "high", spec.Scope, spec.ProjectId,
            "blue", "Привет, разберём этот урок", memoryEnabled: spec.MemoryEnabled,
            tools: spec.Tools,
            contract: spec.MaxExtras ? new PersonaContract
            {
                Character = "Спокойный терпеливый учитель", Tone = "на равных",
                MustDo = ["объяснять по шагам"], MustNot = ["не давать готовый ответ сразу"],
                OutputFormat = "коротко", SpeechExamples = ["Смотри, вот здесь…"],
                Instructions = "полный регламент роли",
            } : null,
            access: spec.Access, disallowedTools: spec.DisallowedTools, specialty: spec.Specialty,
            allProjectsAccess: false, handle: spec.Handle,
            modelTier: spec.MaxExtras ? "medium" : null,
            tierStrong: spec.MaxExtras ? "sonnet" : null, tierMedium: spec.MaxExtras ? "sonnet" : null,
            tierWeak: spec.MaxExtras ? "sonnet" : null,
            lightContext: false, gender: spec.MaxExtras ? PersonaGender.Male : null,
            inheritsAccess: false,
            promptSections: spec.MaxExtras
                ? [new PromptSectionOverride("roleRules", true, "свои правила роли"),
                   new PromptSectionOverride("processes", true, "свои процессы роли")]
                : null);
        if (spec.MaxExtras)
        {
            // поля без своей ручки в Create (стор держит экземпляр, Get отдаёт его копию)
            p.TemplateKey = "omo-test";
            p.TemplateInstructionsHash = "0123456789abcdef";
            p.SectionsSeenVersion = "v-test";
            Personas.SetVoice(p.Id, ownerId, new PersonaVoice { Voice = "Alice", Role = "friendly", Speed = 1.0 });
        }
        if (spec.Bindings is not null) Personas.UpdateBindings(p.Id, ownerId, spec.Bindings);
        if (spec.Rules is not null) Personas.UpdateRules(p.Id, ownerId, spec.Rules);
        return p;
    }

    // Максимум по каждому полю: Tools — весь каталог, гранты MCP — Tool-привязки по ключам-серверам,
    // навыки и плагины — Skill-привязки (plugin:{id}/{skill} — единственный гейт PluginDirSource),
    // автоматика — правило, MemoryEnabled — включена
    private static List<PersonaBinding> MaxBindings() =>
        [
            .. PersonaBindingsService.ToolCatalog.Keys.Select(k => new PersonaBinding
            {
                Type = PersonaBindingType.Tool, Target = k, Condition = "всегда", Mode = PersonaBindingMode.Auto,
            }),
            new() { Type = PersonaBindingType.Skill, Target = "my-skill", Condition = "когда нужен навык" },
            new() { Type = PersonaBindingType.Skill, Target = "plugin:local-a/open/sync", Condition = "когда нужен плагин" },
        ];

    private static List<PersonaAutomationRule> MaxRules() =>
        [new() { Name = "следить за уроками", Trigger = new AutomationTrigger { Type = AutomationTriggerType.Timer } }];

    // Полный список возможностей стор нормализует в null («без ограничений») — это и есть максимум поля
    private static TutorSpec MaxSpec(string handle, string projectId) => new()
    {
        Handle = handle, Scope = PersonaScope.Project, ProjectId = projectId,
        Access = PersonaAccess.Full, DisallowedTools = null,
        Specialty = PersonaSpecialty.Mentor, Tools = [.. AllToolKeys],
        MemoryEnabled = true, Bindings = MaxBindings(), Rules = MaxRules(), MaxExtras = true,
    };

    private static TutorSpec DefaultSpec(string handle) => new() { Handle = handle };

    // Сборка child-цепочки поверх DI хоста — та же проводка, что в ChildCliLayerFactoryTests,
    // плюс живой маршрут: пул/реестр для ChildRoute, резолвер модели, реальные сторы
    private Kit Build(Prepared p, Persona tutor, IPluginDirSource? pluginSource = null)
    {
        var sp = _host.Services;
        Projects.SetDefaultPersona(p.Project.Id, tutor.Id);

        var snapshot = new ChildPolicySnapshot(p.KidId, KidProfile(), false);
        var policy = new FakePolicy(snapshot);
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs-b16e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var calc = new ChildLimitsCalculator(policy,
            new ChildUsageStore(Path.Combine(tempDir, ChildUsageStore.DirName)),
            new ZeroSpend(), Users,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChildLimitsCalculator.ConfigTimeZoneKey] = "Europe/Moscow",
            }).Build(), NullLogger<ChildLimitsCalculator>.Instance);
        var (pool, providers) = ChildRouteKit.For(PoolKey);
        var gate = new ChildModeGate(policy, calc, null, pool, providers, Projects, Personas,
            sp.GetRequiredService<ModelAssignmentResolver>());

        var layer = new PersonaLayerBuilder(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<McpEndpoint>(),
            Personas, sp.GetRequiredService<PersonaBindingsService>(), sp.GetService<PersonaAgentFileSync>(),
            Projects, sp.GetService<ISphereDirectory>(), NullLogger<PersonaLayerBuilder>.Instance, gate);
        var builtin = new BuiltinMcpContexts(
            sp.GetRequiredService<McpEndpoint>(), sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaBindingsService>(), sp,
            sp.GetService<SubsystemStateStore>(), Projects,
            sp.GetService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>(),
            sp.GetRequiredService<IConfiguration>(), gate);
        var factory = new TurnLaunchContextFactory(builtin, layer,
            sp.GetRequiredService<ExternalMcpContexts>(), sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<ILauncherFactory>(), pluginSource ?? new FixedPlugins(),
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<ITurnMessageSink>>(), sp.GetRequiredService<Lazy<ITurnStarter>>());

        var session = new Session
        {
            Id = "b16e-s-" + Guid.NewGuid().ToString("N"), OwnerId = p.KidId, PersonaId = tutor.Id,
            Provider = PoolKey, Model = TutorModel, ProjectId = p.Project.Id,
            Mode = ChildModeLaunch.RequiredMode, ChildSpace = "lessons", ChildSubject = SubjectId,
        };
        return new Kit(factory, session, p.KidId);
    }

    private static LlmSessionContext Launch(Kit kit) =>
        kit.Factory.BuildForNewSession(kit.Session, kit.KidId, null, Path.GetTempPath(), Path.GetTempPath(),
            null, null, new TurnAccumulator(new List<StoredMessage>()), 1,
            (_, _, _, _, _) => Task.CompletedTask, null, Path.GetTempPath(), true);

    // Два хода одного урока: с максимальной персоной и с персоной «по умолчанию» —
    // каждый со своим руководителем проекта (живая сверка проходит по обоим)
    private Pair BuildPair(string tagPrefix)
    {
        var p = Prepare(tagPrefix);
        var maxTutor = CreateTutor(p.KidId, MaxSpec("b16e-" + tagPrefix + "mx", p.Project.Id));
        var maxCtx = Launch(Build(p, maxTutor));
        var defaultTutor = CreateTutor(p.KidId, DefaultSpec("b16e-" + tagPrefix + "df"));
        var defaultCtx = Launch(Build(p, defaultTutor));
        return new Pair(maxCtx, defaultCtx, maxTutor.Id, p.KidId, p.Project);
    }

    // ── сравнение аргументов контекста запуска ───────────────────────────────────────

    private static readonly PropertyInfo[] ContextProps =
        typeof(LlmSessionContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static readonly string[] ScalarContextProps =
    [
        nameof(LlmSessionContext.RawSystemPrompt), nameof(LlmSessionContext.BuiltInSystemPrompt),
        nameof(LlmSessionContext.ContentRootPath), nameof(LlmSessionContext.DataDir),
        nameof(LlmSessionContext.OwnerId), nameof(LlmSessionContext.RootPath), nameof(LlmSessionContext.MainRootPath),
        nameof(LlmSessionContext.CliConfigRoot), nameof(LlmSessionContext.BrowserEnabled),
        nameof(LlmSessionContext.SystemPromptViaFile), nameof(LlmSessionContext.HttpMcpActive),
        nameof(LlmSessionContext.ServerContent), nameof(LlmSessionContext.TranscriptOnServer),
        nameof(LlmSessionContext.HandsEnabled), nameof(LlmSessionContext.LlmGatewayApiUrl),
    ];

    private static void AssertContextsEqual(LlmSessionContext a, LlmSessionContext b)
    {
        using var scope = new AssertionScope();

        (a.ExtraDisallowedTools ?? []).Should().Equal(b.ExtraDisallowedTools ?? [],
            "список запретов хода (--disallowedTools)");

        foreach (var prop in ContextProps)
        {
            var name = prop.Name;
            if (name == nameof(LlmSessionContext.ExtraDisallowedTools)) continue;
            var va = prop.GetValue(a);
            var vb = prop.GetValue(b);
            if (ScalarContextProps.Contains(name))
            {
                (va?.ToString() ?? "∅").Should().Be(vb?.ToString() ?? "∅", $"{name}: скаляр аргумента хода");
                continue;
            }
            if (va is null && vb is null) continue;
            if (va is not null && vb is not null)
            {
                // непустая с обеих сторон: обвязка (Func/Action/лаунчер) — общая по построению;
                // MCP-контексты — только живые детские (tutor/widgets), их билдеры не берут персону
                va.GetType().Should().Be(vb.GetType(), $"{name}: тот же контекст, не новый");
                if (va.GetType().Name.EndsWith("McpContext"))
                    new[] { nameof(TutorMcpContext), nameof(WidgetsMcpContext) }
                        .Should().Contain(va.GetType().Name, $"{name}: поля персоны не добавляют к детскому ходу своих контекстов");
                continue;
            }
            // null с одной стороны — состав хода изменился персоной: ровно то, чего И10 не допускает
            Xunit.Assert.Fail($"{name}: контекст хода {(va is null ? "снялся" : "появился")} от полей персоны");
        }
    }

    // ── c1: поля «на максимуме» не меняют аргументы; каждое поле — отдельный кейс ─────

    [Theory]
    [InlineData(nameof(Persona.OwnerId))]
    [InlineData(nameof(Persona.Name))]
    [InlineData(nameof(Persona.Role))]
    [InlineData(nameof(Persona.Handle))]
    [InlineData(nameof(Persona.HandleCustom))]
    [InlineData(nameof(Persona.Description))]
    [InlineData(nameof(Persona.SystemPrompt))]
    [InlineData(nameof(Persona.Contract))]
    [InlineData(nameof(Persona.PromptSections))]
    [InlineData(nameof(Persona.Model))]
    [InlineData(nameof(Persona.ModelTier))]
    [InlineData(nameof(Persona.TierStrong))]
    [InlineData(nameof(Persona.TierMedium))]
    [InlineData(nameof(Persona.TierWeak))]
    [InlineData(nameof(Persona.Effort))]
    [InlineData(nameof(Persona.Specialty))]
    [InlineData(nameof(Persona.Scope))]
    [InlineData(nameof(Persona.ProjectId))]
    [InlineData(nameof(Persona.SphereId))]
    [InlineData(nameof(Persona.Avatar))]
    [InlineData(nameof(Persona.Gender))]
    [InlineData(nameof(Persona.Voice))]
    [InlineData(nameof(Persona.Tools))]
    [InlineData(nameof(Persona.Bindings))]
    [InlineData(nameof(Persona.AllProjectsAccess))]
    [InlineData(nameof(Persona.AutomationRules))]
    [InlineData(nameof(Persona.Access))]
    [InlineData(nameof(Persona.DisallowedTools))]
    [InlineData(nameof(Persona.InheritsAccess))]
    [InlineData(nameof(Persona.SectionsSeenVersion))]
    [InlineData(nameof(Persona.Greeting))]
    [InlineData(nameof(Persona.TemplateKey))]
    [InlineData(nameof(Persona.TemplateInstructionsHash))]
    [InlineData(nameof(Persona.MemoryEnabled))]
    [InlineData(nameof(Persona.LightContext))]
    public void ПолеПерсоныНаМаксимуме_АргументыДетскогоХодаКакУДефолтнойПерсоны(string field)
    {
        var pair = BuildPair(Tag());

        // поле действительно поставлено «на максимуме» — иначе кейс проверяет не его
        AssertFieldMax(field, Personas.Get(pair.MaxTutorId, pair.KidId)!, pair.Project);

        AssertContextsEqual(pair.Max, pair.Default);
    }

    private void AssertFieldMax(string field, Persona p, Project project)
    {
        switch (field)
        {
            case nameof(Persona.OwnerId): p.OwnerId.Should().Be(project.OwnerId); break;
            case nameof(Persona.Name): p.Name.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Role): p.Role.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Handle): p.Handle.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.HandleCustom): p.HandleCustom.Should().BeTrue("свой handle авто-переименование не трогает"); break;
            case nameof(Persona.Description): p.Description.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.SystemPrompt): p.SystemPrompt.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Contract): p.Contract.Should().NotBeNull(); break;
            case nameof(Persona.PromptSections): p.PromptSections.Should().NotBeNullOrEmpty(); break;
            case nameof(Persona.Model): p.Model.Should().Be(TutorModel); break;
            case nameof(Persona.ModelTier): p.ModelTier.Should().NotBeNull(); break;
            case nameof(Persona.TierStrong): p.TierStrong.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.TierMedium): p.TierMedium.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.TierWeak): p.TierWeak.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Effort): p.Effort.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Specialty): p.Specialty.Should().NotBe(PersonaSpecialty.None); break;
            case nameof(Persona.Scope): p.Scope.Should().Be(PersonaScope.Project); break;
            case nameof(Persona.ProjectId): p.ProjectId.Should().Be(project.Id); break;
            case nameof(Persona.SphereId): p.SphereId.Should().BeNull("проектный репетитор не принадлежит сфере"); break;
            case nameof(Persona.Avatar): p.Avatar.Color.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Gender): p.Gender.Should().NotBe(PersonaGender.Unspecified); break;
            case nameof(Persona.Voice): p.Voice.Should().NotBeNull(); break;
            // стор нормализует полный список возможностей в null («без ограничений») — это и есть
            // максимум поля; его смысл — все возможности каталога включены персоне
            case nameof(Persona.Tools):
                p.Tools.Should().BeNull("полный список нормализован стором в «без ограничений»");
                foreach (var key in AllToolKeys)
                    _host.Services.GetRequiredService<PersonaBindingsService>()
                        .EffectiveToolEnabled(p.OwnerId, p, key).Should().BeTrue($"{key}: максимум Tools не выключает возможность");
                break;
            case nameof(Persona.Bindings): p.Bindings.Should().NotBeNullOrEmpty(); break;
            case nameof(Persona.AllProjectsAccess): p.AllProjectsAccess.Should().BeFalse("проектный репетитор не расширяет зону до глобальной"); break;
            case nameof(Persona.AutomationRules): p.AutomationRules.Should().NotBeNullOrEmpty(); break;
            case nameof(Persona.Access): p.Access.Should().Be(PersonaAccess.Full); break;
            case nameof(Persona.DisallowedTools): p.DisallowedTools.Should().BeNull("максимум запрета — его отсутствие"); break;
            case nameof(Persona.InheritsAccess): p.InheritsAccess.Should().BeFalse("свои права явно не унаследованы"); break;
            case nameof(Persona.SectionsSeenVersion): p.SectionsSeenVersion.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.Greeting): p.Greeting.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.TemplateKey): p.TemplateKey.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.TemplateInstructionsHash): p.TemplateInstructionsHash.Should().NotBeNullOrWhiteSpace(); break;
            case nameof(Persona.MemoryEnabled): p.MemoryEnabled.Should().BeTrue(); break;
            case nameof(Persona.LightContext): p.LightContext.Should().NotBe(true); break;
            default: throw new InvalidOperationException($"Кейс «{field}» не описан в AssertFieldMax");
        }
    }

    // Разбор аргументов атрибута до скалярных значений: params-списки и typed-аргументы
    private static IEnumerable<string> FlattenAttributeValues(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is string s) { yield return s; yield break; }
        if (arg.Value is System.Collections.IEnumerable seq)
        {
            foreach (var e in seq)
            {
                if (e is CustomAttributeTypedArgument inner)
                {
                    foreach (var x in FlattenAttributeValues(inner)) yield return x;
                }
                else if (e is string es) yield return es;
                else if (e is not null) yield return e.ToString()!;
            }
            yield break;
        }
        if (arg.Value is not null) yield return arg.Value.ToString()!;
    }

    // Поля, не участвующие в аргументах хода: служебные маркеры и поля ответа, не возможности
    // персоны. Сознательное исключение с причиной; новое поле краснеет, пока не описано здесь
    // или не получило кейс — на этом и держится полнота перебора.
    private static readonly Dictionary<string, string> NotTurnFields = new()
    {
        [nameof(Persona.Id)] = "init-идентификатор записи, не возможность персоны",
        [nameof(Persona.CreatedAt)] = "init-метка создания записи",
        [nameof(Persona.InheritsAccessMigratedAt)] = "маркер разовой миграции прав, ходом не читается",
        [nameof(Persona.AccessCopiedAt)] = "маркер копирования прав при выключении наследования, ходом не читается",
        [nameof(Persona.AccessDiffCount)] = "поле ответа (ставит Hydrate на копии), в сторе не хранится",
        [nameof(Persona.SectionsVersion)] = "поле ответа, в сторе не хранится",
        [nameof(Persona.ChangedByAdminSince)] = "поле ответа, в сторе не хранится",
        [nameof(Persona.Notice)] = "поле ответа (ставит PersonasCrudService на копии)",
        [nameof(Persona.UpdatedAt)] = "служебная метка времени записи",
    };

    // Новый набор полей Persona без кейса максимума — этот тест краснеет (полнота перебора)
    [Fact]
    public void КаждоеПолеПерсоны_ПокрытоКейсомМаксимума()
    {
        var covered = typeof(ChildTutorPersonaMaxGuardTests)
            .GetMethod(nameof(ПолеПерсоныНаМаксимуме_АргументыДетскогоХодаКакУДефолтнойПерсоны),
                BindingFlags.Instance | BindingFlags.Public)!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(InlineDataAttribute))
            // InlineData(params object[]) — аргументы приходят вложенными списками/typed-аргументами
            .SelectMany(a => a.ConstructorArguments.SelectMany(FlattenAttributeValues))
            .ToHashSet(StringComparer.Ordinal);

        var fields = typeof(Persona)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        fields.Except(covered.Concat(NotTurnFields.Keys))
            .Should().BeEmpty("новое поле Persona обязано получить кейс «на максимуме» или сознательное исключение с причиной в NotTurnFields");
        NotTurnFields.Keys.Should().BeSubsetOf(fields, "исключение по удалённому полю надо снять");
    }

    // ── c2: обратное — сужение работает на уровне контекста запуска ────────────────────

    // Уровень проверки — контекст запуска (ExtraDisallowedTools): запрет персоны входит в него и
    // нигде дальше не снимается (прежний Except(ChildBuiltinTools) при сборке --disallowedTools
    // съедал его вместе с детским слоем). На аргументах CLI и на границе can_use_tool то же
    // держит другой сторож —
    // ChildCliLayerTests.ХодРебёнка_ЗапретПерсоныНаЗаписи_ДоезжаетДоCLI_ИОтказываетНаСвоемФайле.
    [Fact]
    public void ЗапретПерсоныWrite_ДоезжаетДоКонтекстаДетскогоХода_БезПотерьИБезЛишнего()
    {
        var p = Prepare("rev" + Tag());
        var tutor = CreateTutor(p.KidId, new TutorSpec
        {
            Handle = "b16e-rev" + Tag(), Scope = PersonaScope.Project, ProjectId = p.Project.Id,
            Access = PersonaAccess.Custom, DisallowedTools = ["Write"],
        });

        var ctx = Launch(Build(p, tutor));

        using var scope = new AssertionScope();
        // запрет персоны доезжает до списка запретов контекста детского хода
        ctx.ExtraDisallowedTools.Should().Contain("Write", "персона сузит права ребёнка и своим списком (на уровне контекста)");
        // и добавляет ровно себя: к детскому набору — только названный ею Write, ничего больше
        // (порядок — «свои запреты первыми», на смысл списка это не влияет)
        (ctx.ExtraDisallowedTools ?? []).OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(new[] { "Write" }.Concat(PersonaLayerBuilder.ChildDisallowedBuiltins)
                .OrderBy(x => x, StringComparer.Ordinal));

        // та же персона у взрослого: её список работает сам по себе, без детского слоя
        // (детский слой к взрослому ходу не добавляется: PersonaLayerBuilder.BuildExtraDisallowed
        // складывает его по IsChild; список контекста взрослого уходит в аргументы как есть)
        var adult = Users.Add("b16e-adult" + Tag(), "b16e-pw-adult", "user");
        var adultTutor = CreateTutor(adult.Id, new TutorSpec
        {
            Handle = "b16e-adult" + Tag(), Scope = PersonaScope.Global,
            Access = PersonaAccess.Custom, DisallowedTools = ["Write"],
        });
        var adultCtx = Launch(BuildForAdult(adult.Id, adultTutor.Id));
        adultCtx.ExtraDisallowedTools.Should().Equal(["Write"],
            "запрет персоны — её собственная сужающая сила, не побочный эффект детского слоя");
    }

    private Kit BuildForAdult(string adultId, string personaId)
    {
        var sp = _host.Services;
        // взрослый — не ребёнок: штатный гейт из DI (политика по реальным профилям)
        var gate = sp.GetRequiredService<IChildModeGate>();
        var layer = new PersonaLayerBuilder(sp.GetRequiredService<SessionStore>(), sp.GetRequiredService<McpEndpoint>(),
            Personas, sp.GetRequiredService<PersonaBindingsService>(), sp.GetService<PersonaAgentFileSync>(),
            Projects, sp.GetService<ISphereDirectory>(), NullLogger<PersonaLayerBuilder>.Instance, gate);
        var builtin = new BuiltinMcpContexts(
            sp.GetRequiredService<McpEndpoint>(), sp.GetRequiredService<FeatureFlagService>(),
            sp.GetRequiredService<PersonaBindingsService>(), sp,
            sp.GetService<SubsystemStateStore>(), Projects,
            sp.GetService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>(),
            sp.GetRequiredService<IConfiguration>(), gate);
        var factory = new TurnLaunchContextFactory(builtin, layer,
            sp.GetRequiredService<ExternalMcpContexts>(), sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<ILauncherFactory>(), new FixedPlugins(),
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<Lazy<ITurnMessageSink>>(), sp.GetRequiredService<Lazy<ITurnStarter>>());
        var session = new Session
        {
            Id = "b16e-adult-" + Guid.NewGuid().ToString("N"), OwnerId = adultId, PersonaId = personaId,
        };
        return new Kit(factory, session, adultId);
    }

    // ── c4: промпт — блок привязок ребёнку не строится; держится ровно на признаке IsChild ──

    [Fact]
    public void БлокПривязок_ДетскомуХодуНеСтроится_БезПризнакаРебёнкаСтроится()
    {
        var p = Prepare("bnd" + Tag());
        var maxTutor = CreateTutor(p.KidId, MaxSpec("b16e-bnd" + Tag(), p.Project.Id));
        var kit = Build(p, maxTutor);
        var contributor = new PersonaBindingsContributor(
            _host.Services.GetRequiredService<IPersonaBindingsSource>(),
            NullLogger<PersonaBindingsContributor>.Instance);

        // та же максимальная персона, тот же состав — разница только в признаке детского хода
        var childCtx = new PromptSessionContext(kit.Session, p.KidId, maxTutor, Path.GetTempPath(), IsChild: true);
        contributor.IsEnabled(childCtx).Should().BeFalse(
            "привязки и их выжимки в промпт детского хода не входят (KM-B6c): отсутствие привязок в промпте");
        contributor.IsEnabled(childCtx with { IsChild = false }).Should().BeTrue(
            "тот же состав без признака ребёнка блок включает: сужение держится ровно на IsChild, не на персоне");
    }

    // ── c3: плагины — сужение персоной работает у взрослого, ребёнку их нет вовсе ─────

    [Fact]
    public void Плагины_SkillБиндингПерсоныОткрываетПакет_ДетскомуХодуИсточникНеЕдет()
    {
        var p = Prepare("plg" + Tag());
        var maxTutor = CreateTutor(p.KidId, MaxSpec("b16e-plg" + Tag(), p.Project.Id));
        var plainTutor = CreateTutor(p.KidId, DefaultSpec("b16e-plp" + Tag()));

        // настоящий источник каталогов: запись пакета + его каталог + реальный резолв привязок
        var store = new PluginStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_host.TempDir, "b16e_plg_" + Tag(), "projects.json"),
        }).Build());
        store.SaveInstalled(p.KidId,
            [new PluginInstall { Id = "local-a/open", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" }]);
        Directory.CreateDirectory(store.PackageDir(p.KidId, "local-a/open", "1.0.0"));
        var dirSource = new PluginDirSource(store,
            new PersonaSkillBindingLookup(Personas, _host.Services.GetRequiredService<PersonaBindingsService>()));

        dirSource.GetPluginDirs(p.KidId, maxTutor.Id, null).Should().ContainSingle(
            "Skill-биндинг plugin:local-a/open/sync открывает пакет своей персоне");
        dirSource.GetPluginDirs(p.KidId, plainTutor.Id, null).Should().BeEmpty(
            "персона без Skill-биндинга пакет не открывает: сужение работает и в эту сторону");
        dirSource.GetPluginDirs(p.KidId, null, null).Should().ContainSingle(
            "без персоны фильтра нет — весь включённый набор");

        // сам детский ход: источник плагинов не отдаётся вовсе (И1 п.13) — ни при какой персоне
        Launch(Build(p, maxTutor)).PluginDirSource.Should().BeNull();
        Launch(Build(p, plainTutor)).PluginDirSource.Should().BeNull();
    }
}
