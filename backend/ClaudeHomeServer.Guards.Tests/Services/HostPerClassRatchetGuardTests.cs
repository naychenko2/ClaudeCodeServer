using System.Reflection.Metadata;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Храповик «хост на класс» (фича «Ускорение тестов», G-H0). Хостовые классы переведены на хост на КЛАСС
/// (вложенная фикстура <c>Hosts : TestWebApplicationFactory</c> через <c>IClassFixture</c>); без сторожа
/// новый класс снова поднимет хост на каждый тест. Правило: <c>new</c> фабрики (<c>TestWebApplicationFactory</c>
/// и её наследников) допустим ТОЛЬКО во вложенной фикстуре: в самой фабрике-наследнике, её вложенных типах и в
/// держателе хоста (<c>Класс+Hosts</c> из <c>IClassFixture</c>), который xunit создаёт сам. В теле теста, конструкторе,
/// инициализаторе поля, async-машине и замыкании тест-класса, в любом другом его вложенном типе (в том числе в обычном
/// внутреннем helper) и в базовом классе из той же сборки — красное, если класса нет ни в <see cref="Remainder"/>,
/// ни в <see cref="UndoneH0"/> (код проверяет оба списка). Скан — по метаданным и IL собранных тестовых сборок:
/// <c>HostScan.Solution</c> нужен храповику только ради счётчика хостовых классов (≥ 100), создателей фабрик ищет
/// собственный проход по IL над сборками из <c>HostCategoryGuardTests.TestAssemblies()</c>.
/// Список остатка только УМЕНЬШАЕТСЯ: запись, у которой в коде создания фабрики уже нет, — красное, а рост списков
/// режет потолок (<see cref="RemainderCeiling"/>, <see cref="UndoneH0Ceiling"/>).
/// Охват скана: тело тест-класса, его async-машины и замыкания, любые вложенные типы тест-класса (кроме
/// вложенной фикстуры-наследника фабрики и держателя хоста из `IClassFixture<...>`) и базовые классы тест-класса из той же тестовой сборки. Ограничение: внешний
/// helper-класс, на который лишь ссылается тест и который сам создаёт фабрику, сторож не видит — для него нужен
/// межтиповой граф вызовов, дешёвого способа без ложных срабатываний на пулы хостов нет.
/// </summary>
public class HostPerClassRatchetGuardTests
{
    /// <summary>
    /// Остаток: тест-класс (полное имя) → причина, по которой хост на конфигурацию/тест задуман. Новые строки
    /// без правки сторожа и обоснования запрещены.
    /// </summary>
    internal static readonly Dictionary<string, string> Remainder = new(StringComparer.Ordinal)
    {
        ["ClaudeHomeServer.Tests.Composition.BackgroundsSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.CodeGraphSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.CompositionNamespaceResolutionTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.DeploySubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.DossiersSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.DuplicateSingletonRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.GitSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.InspectionModeHostedServicesTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.KnowledgeSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.LlmSubsystemDiCycleTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.MemorySubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.ProjectIconsSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.ProjectManagerAdapterTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.SingletonForwarderIdentityTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.SkillsSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.SpendSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.TtsSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.UserStoreAdapterTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.VideoSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Composition.YandexSubsystemRegistrationTests"] = "Composition: хост на конфигурацию (регистрация подсистем, DI) задуман",
        ["ClaudeHomeServer.Tests.Controllers.AgentDownloadsEndpointTests"] = "хелпер HostWith: свой каталог релизов и лимит на тест",
        ["ClaudeHomeServer.Tests.Controllers.LlmProxyEventsControllerTests"] = "тест Секрет_НеНастроен_РучкиНет: хост без настроенного секрета, общий хост класса его настроил бы",
        ["ClaudeHomeServer.Tests.Controllers.TasksControllerTests"] = "класс на пуле общих хостов CORE (SharedHostPool); фабрика в отдельных тестах со своей конфигурацией",
        ["ClaudeHomeServer.Tests.Services.EngineCallLedgerDiTests"] = "проверка DI-композиции: хост на конфигурацию",
        ["ClaudeHomeServer.Tests.Services.LocalEngineGateDiTests"] = "проверка DI-композиции: хост на конфигурацию",
        ["ClaudeHomeServer.Tests.Services.LocalStats.LocalModelTurnMeterDiTests"] = "проверка DI-композиции: хост на конфигурацию провайдеров",
        ["ClaudeHomeServer.Tests.Services.Mcp.Http.HiggsfieldRegistrationTests"] = "проверка регистрации: хост на конфигурацию",
        ["ClaudeHomeServer.Tests.Services.Reader.ReaderSubsystemRegistrationTests"] = "проверка регистрации подсистемы: хост на конфигурацию",
        ["ClaudeHomeServer.Tests.Subsystems.SpendDisabledTests"] = "тест СнапшотПриSubsystemsOffDynamicModulesOn: хост с выключенными подсистемами и включёнными динамическими модулями",
    };

    /// <summary>
    /// Недоделанный H0: классы без обоснованной причины (хост на каждый тест через IDisposable + поле/конструктор
    /// либо фабрика в теле теста). Это долг, а не решение: список только сокращается, по мере перевода классов
    /// на `Hosts : TestWebApplicationFactory` строки удаляются (устаревшая строка — красное).
    /// </summary>
    internal static readonly Dictionary<string, string> UndoneH0 = new(StringComparer.Ordinal)
    {
        ["ClaudeHomeServer.Tests.Contract.ProjectRelayContractTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.AnonymousEndpointsGuardTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.AssistantDeletionTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ChatsControllerHandoffTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.DifyHttpOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.GitControllerLogTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.McpCatalogControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.McpHttpOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.MediaSelfScopeFailClosedTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ModelsControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.NotesHttpOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.NotificationUrlGuardTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.OnboardingApplyTranscriptTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.PersonaCreateOnboardingGuardTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.PersonaGenderApiTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.PersonasHttpOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.PlansControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProjectCapabilityGuardHttpTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProjectsControllerLegacyIconTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProjectsControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProjectsControllerTrainingPhotosCascadeTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProvidersControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProvisionEntryPointsTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProxyControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.RemoteCommandsControllerEnabledTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.RunDelegationGateTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.SessionManagerProjectLauncherTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.SkillsProjectOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.SpecialtyPromptSectionsControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.WatchHttpToolsetTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.WorkspaceDeployDelegationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.WorkspaceHttpChatsSendPreemptTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.WorkspaceHttpContextListTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.WorkspaceHttpOwnerIsolationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.DynamicModules.RemoteCacheBustingTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.DynamicModules.StubModuleEndpointTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Http.RateLimiterLifetimeTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Hubs.SessionHubDeniedLogTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.ImageEditor.FaceTargetLaunchHttpTwoFacesTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.ImageEditor.ModuleEventSerializationTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.ImageEditor.Spending.ImageEditSpendVisibilityTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.ImageEditor.Threads.ImageEditorMcpContextTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.ImageEditor.Threads.ThreadChangedDeliveryTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Plugins.PluginBindingPurgeBattleTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Plugins.PluginDraftsControllerTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ChatContext.TurnContextMcpLiveTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.Composition.DelegatedTurnGateAdapterTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.Composition.DelegatedTurnGateDepthTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.Mcp.Http.McpToolDescriptionLengthTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ModuleRecordFreshChatTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.PersonaAskServiceTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.PersonaLightContextWhitelistTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ProcessRegistryHostOwnershipTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ProjectSessionPersonaLayerGuardTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Subsystems.ChatContextDisabledVerticalTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.EditorTurnInSharedChatTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.GitCommitAuthorRestTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.McpNarrowFramePersonalSpaceTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.McpNarrowFrameTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.MemoryPrivateChatTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.ProjectMembersControllerTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.TaskAutoLaunchActorTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Controllers.TasksRunExecutorActorTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Hubs.HubDenialCodeTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Integration.SharedChatTurnControlTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ActorRightMcpWiringTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.ActorRightToolCatalogTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
        ["ClaudeHomeServer.Tests.Services.McpSharedWithMembersTurnTests"] = "недоделанный H0: класс фичи «Общие проекты», влит мержем master 03.11 раньше храповика — перевести на хост на класс",
    };

    // Потолки списков: запись в Remainder/UndoneH0 не лазейка, а долг — размер только уменьшается
    internal const int UndoneH0Ceiling = 68;
    internal const int RemainderCeiling = 29;

    // Скан решения (все тестовые сборки по метаданным и IL) — Slow по «Правилу новой метки» testing.md
    [Fact]
    [Trait("Category", "Slow")]
    public void НовыйХостТестКласса_ТолькоВоВложеннойФикстуреИлиВОстатке()
    {
        var scans = HostScan.Solution.ToList();
        var hosted = scans.Sum(s => s.Hosted.Count);
        var creators = FactoryCreators().ToList();

        hosted.Should().BeGreaterThanOrEqualTo(100, "хостовых классов в решении сотни — иначе сторож прошёл вакуумно");

        var violations = creators.Where(c => !Remainder.ContainsKey(c) && !UndoneH0.ContainsKey(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(violations.Count == 0, $"Тест-классы создают TestWebApplicationFactory мимо вложенной фикстуры ({violations.Count}) — "
            + "это хост на каждый тест. Вынеси хост в `Hosts : TestWebApplicationFactory` + IClassFixture "
            + "(образец — AudioEditorControllerTests); осознанное исключение — только в Remainder с причиной:\n"
            + string.Join("\n", violations));

        var stale = Remainder.Keys.Concat(UndoneH0.Keys).Where(k => !creators.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Запись остатка устарела — в классе уже нет создания фабрики, удали строку из Remainder "
            + "(храповик закручивается):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void ОстатокИДолгНеВышеПотолка()
    {
        (UndoneH0.Count <= UndoneH0Ceiling).Should().BeTrue(
            $"в UndoneH0 {UndoneH0.Count} записей при потолке {UndoneH0Ceiling}: список можно только уменьшать; при удалении записи опусти потолок");
        (Remainder.Count <= RemainderCeiling).Should().BeTrue(
            $"в Remainder {Remainder.Count} записей при потолке {RemainderCeiling}: список можно только уменьшать; при удалении записи опусти потолок");
    }

    [Fact]
    public void ОстатокИДолгНеПересекаются()
    {
        Remainder.Keys.Intersect(UndoneH0.Keys).Should().BeEmpty();
    }

    [Fact]
    public void ОстатокКаждаяЗаписьСПричиной()
    {
        Remainder.Concat(UndoneH0).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).Should().BeEmpty("безпричинных исключений быть не должно");
    }

    // Тест-классы, в телах которых (вместе с async-машинами и замыканиями) создаётся фабрика
    private static IEnumerable<string> FactoryCreators() =>
        HostCategoryGuardTests.TestAssemblies().ToList().AsParallel().SelectMany(CreatorsIn).ToList();

    private static IEnumerable<string> CreatorsIn(string dll)
    {
        var dir = Path.GetDirectoryName(dll)!;
        var others = Directory.EnumerateFiles(dir, "ClaudeHomeServer.TestKit*.dll").Select(f => new HostScan.Asm(f)).ToList();
        try
        {
            using var main = new HostScan.Asm(dll);
            var mainTypes = main.Types().ToList();
            var all = new List<HostScan.TypeInfoMd>(mainTypes);
            foreach (var o in others) all.AddRange(o.Types());
            var byName = all.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Фабрика и наследники по цепочке баз
            var factories = new HashSet<string>(StringComparer.Ordinal) { HostScan.FactoryName };
            foreach (var t in all)
                for (var b = t.BaseName; b is not null; b = byName.TryGetValue(b, out var bt) ? bt.BaseName : null)
                    if (b == HostScan.FactoryName) { factories.Add(t.Name); break; }
            var ctors = factories.Select(f => f + "::.ctor").ToHashSet(StringComparer.Ordinal);

            // Типы, создающие фабрику (сама фабрика, её наследники и их вложенные типы не в счёт);
            // сгенерированные вложенные (async-машины, замыкания) сворачиваются в объявивший их тип
            var creators = new HashSet<string>(StringComparer.Ordinal);
            // Члены каждого типа — один разбор IL на все проверки ниже
            var members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var t in mainTypes) members.TryAdd(t.Name, t.MemberNames().ToHashSet(StringComparer.Ordinal));

            // Вложенный держатель хоста класса: тип из `IClassFixture<T+Hosts>` тест-класса. xunit передаёт его в
            // конструктор сам, поэтому ему положено создавать фабрику. Если тест-класс (с async-машинами и замыканиями)
            // создаёт его сам, `new Hosts()` на тест — это уже не фикстура, красное.
            var testNames = mainTypes.Where(t => t.IsTestClass).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var holders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in main.Md.TypeDefinitions)
            {
                var owner = HostScan.Names.Of(main.Md, h);
                if (!testNames.Contains(owner)) continue;
                foreach (var ih in main.Md.GetTypeDefinition(h).GetInterfaceImplementations())
                {
                    var named = HostScan.Names.All(main.Md, main.Md.GetInterfaceImplementation(ih).Interface).ToList();
                    if (!named.Any(n => n.Contains("IClassFixture", StringComparison.Ordinal))) continue;
                    foreach (var x in named.Where(n => n.StartsWith(owner + "+", StringComparison.Ordinal)))
                    {
                        var createdBySelf = mainTypes.Any(u => (u.Name == owner || u.Name.StartsWith(owner + "+<", StringComparison.Ordinal))
                            && members[u.Name].Contains(x + "::.ctor"));
                        if (!createdBySelf) holders.Add(x);
                    }
                }
            }
            foreach (var t in mainTypes)
            {
                if (factories.Contains(t.Name) || factories.Any(f => t.Name.StartsWith(f + "+", StringComparison.Ordinal))) continue;
                if (holders.Any(h => t.Name == h || t.Name.StartsWith(h + "+", StringComparison.Ordinal))) continue;
                if (!members[t.Name].Any(ctors.Contains)) continue;
                var i = t.Name.IndexOf("+<", StringComparison.Ordinal);
                creators.Add(i < 0 ? t.Name : t.Name[..i]);
            }

            // Все «+»-префиксы создателей: тест-класс владеет создателем, если он сам, его вложенный тип или
            // один из его баз (по цепочке BaseType) стоит в этом наборе
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in creators)
            {
                prefixes.Add(c);
                for (var i = c.LastIndexOf('+'); i > 0; i = c.LastIndexOf('+', i - 1)) prefixes.Add(c[..i]);
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in mainTypes.Where(t => t.IsTestClass))
            {
                for (var n = t.Name; n is not null; n = byName.TryGetValue(n, out var nt) ? nt.BaseName : null)
                    if (prefixes.Contains(n)) { result.Add(t.Name); break; }
            }
            return result;
        }
        finally
        {
            foreach (var o in others) o.Dispose();
        }
    }
}
