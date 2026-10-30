using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Храповик «хост на класс» (фича «Ускорение тестов», G-H0). Хостовые классы переведены на хост на КЛАСС
/// (вложенная фикстура <c>Hosts : TestWebApplicationFactory</c> через <c>IClassFixture</c>); без сторожа
/// новый класс снова поднимет хост на каждый тест. Правило: <c>new</c> фабрики (<c>TestWebApplicationFactory</c>
/// и её наследников) допустим ТОЛЬКО вне тест-класса — во вложенной фикстуре или помощнике; в теле теста,
/// конструкторе, инициализаторе поля, async-машине и замыкании тест-класса — красное, если класса нет
/// в <see cref="Remainder"/>. Скан — по метаданным и IL собранных тестовых сборок (общий с HostScan).
/// Список остатка только УМЕНЬШАЕТСЯ: запись, у которой в коде создания фабрики уже нет, — красное.
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
        ["ClaudeHomeServer.Tests.Controllers.ChatDefaultModeTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
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
        ["ClaudeHomeServer.Tests.Controllers.WorkspaceHttpChatsContinueTests"] = "недоделанный H0: хост на каждый тест или на конфигурацию без разбора — перевести на хост на класс",
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
    };

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

            var tests = mainTypes.Where(t => t.IsTestClass).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in mainTypes)
            {
                var owner = OwnerTestClass(t.Name, tests);
                if (owner is null || result.Contains(owner)) continue;
                if (t.MemberNames().Any(ctors.Contains)) result.Add(owner);
            }
            return result;
        }
        finally
        {
            foreach (var o in others) o.Dispose();
        }
    }

    // Сам тест-класс или его сгенерированный вложенный тип (async-машина, замыкание: сегмент начинается с «<»);
    // вложенная фикстура-помощник тест-классу не принадлежит
    private static string? OwnerTestClass(string typeName, HashSet<string> tests)
    {
        if (tests.Contains(typeName)) return typeName;
        var i = typeName.IndexOf("+<", StringComparison.Ordinal);
        if (i < 0) return null;
        return tests.Contains(typeName[..i]) ? typeName[..i] : null;
    }
}
