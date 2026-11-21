using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// И2′ (ADR-041 ред. 2), шаг Т5b: ход ребёнка по маршруту «провайдер» — без пул-блока, самолечения пары,
// фолбэка и BareMode. Процесс CLI подменён сном — проверяем собранный ProcessSpec и отказы запуска.
[Collection(TestCollections.ProcessGlobalState)]
public class ChildProviderRouteTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];

    private const string CloudModel = "deepseek-chat";
    private const string LocalModel = "qwen3.8-27b";
    // Облачная модель без цены: шаг цепочки, который фильтр ребёнка обязан выбросить (решение 27).
    private const string FreeModel = "free-model";

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p);
        }
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* уже удалён */ }
    }

    private sealed class SpecCapturingLauncher(List<Process> processes, TaskCompletionSource started) : IProcessLauncher
    {
        public ProcessSpec? Captured { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            Captured = spec;
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", "ping -n 120 127.0.0.1 >nul"] : ["-c", "exec sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            });
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

        public int EstimateCommandLineLength(ProcessSpec spec) =>
            (spec.FileName ?? "").Length + spec.Args.Sum(TurnPromptAssembler.ArgCost);

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Настоящий сборщик env песочницы (DockerProcessRunner.BuildTurnEnv) для зеркала профиля:
    // тот же образец, что Runner() в ChildRouteTests.
    private static DockerProcessRunner SandboxRunner()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "ccs-child-sandbox-" + Guid.NewGuid().ToString("N"), "projects.json"),
            // WorkingDirectory захваченного spec лежит в temp — маппинг песочницы обязан его знать,
            // иначе BuildDockerExecArgs падает на -w ещё до env-части.
            ["Sandbox:ProjectsRoot"] = Path.GetTempPath(),
        }).Build();
        return new DockerProcessRunner(new SandboxManager(config, NullLogger<SandboxManager>.Instance), "kid");
    }

    private IConfiguration Config(string dir) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds-secret",
            ["LlmProviders:deepseek:Models:0:Id"] = CloudModel,
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:BareMode"] = "true",
            ["LlmProviders:local-qwen:SystemPromptFile"] = "SystemPrompts/CLAUDE-local.md",
            ["LlmProviders:local-qwen:Models:0:Id"] = LocalModel,
            [$"{ClaudeSubscriptionPool.Section}:kids-api:ApiKey"] = "sk-ant-kids",
            [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "oauth-sub-2",
            // Запись пула только с setup-tokenом: форма маршрута из решения 25.
            [$"{ClaudeSubscriptionPool.Section}:kids-oauth:OAuthToken"] = "oauth-kids",
            // Облачный провайдер без цены: его модели годны как маршрут, но не как шаг цепочки.
            ["LlmProviders:freemodel:ApiKey"] = "sk-free",
            ["LlmProviders:freemodel:AnthropicBaseUrl"] = "https://free.example.com",
            ["LlmProviders:freemodel:Models:0:Id"] = FreeModel,
        }).Build();

    // Согласие профиля на маршрут (И2″) стоит в фикстуре: без него ChildRoute отказывает на старте
    // хода ещё до аргументов, и тесты проверяли бы отсутствие согласия, а не маршрут.
    private static ChildPolicySnapshot Snapshot(string providerKey, string model) =>
        new("kid", new ChildProfile
        {
            Enabled = true, ProviderKey = providerKey, Model = model,
            RouteRiskAcknowledgedAt = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
            RouteRiskAcknowledgedBy = "parent-1",
            Subjects =
            [
                new ChildSubject
                {
                    Id = "math", Label = "Математика", Enabled = true,
                },
            ],
        }, false);

    private static Session KidSession(string? provider = null, string? model = null) => new()
    {
        OwnerId = "kid", PersonaId = "tutor-1", ChildSpace = "lessons", ChildSubject = "math",
        Provider = provider ?? "claude", Model = model,
    };

    private sealed record Launched(ProcessSpec? Spec, List<string> Errors, ClaudeSession Session);

    private async Task<Launched> LaunchAsync(ChildPolicySnapshot? snapshot, Session info,
        ModelAssignmentResolver? assignments = null)
    {
        var dir = NewTempDir("ccs-child-provider-");
        var serverRoot = NewTempDir("ccs-child-server-");
        Directory.CreateDirectory(Path.Combine(serverRoot, "SystemPrompts"));
        File.WriteAllText(Path.Combine(serverRoot, "SystemPrompts", "CLAUDE-local.md"), "# карта AI Home");
        var config = Config(dir);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        var launcher = new SpecCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: dir,
            OnMessage: m =>
            {
                if (m is ErrorMessage e) { lock (errors) errors.Add(e.Text); failed.TrySetResult(); }
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null,
            Launcher: launcher, ContentRootPath: serverRoot, OwnerId: "kid",
            ChildPolicyProvider: () => snapshot);
        var session = new ClaudeSession(info, context, providers: new LlmProviderRegistry(config),
            subscriptionPool: new ClaudeSubscriptionPool(config), assignments: assignments);

        await session.SendMessageAsync("тестовый ход");
        await Task.WhenAny(started.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(30));
        return new Launched(launcher.Captured, errors, session);
    }

    // Облачный провайдер: ключ провайдера по env как у взрослого, токена подписки нет, метка ребёнка стоит.
    [Fact]
    public async Task ХодРебёнка_Облако_ЕнвПровайдера_БезТокенаПодписки()
    {
        var r = await LaunchAsync(Snapshot("deepseek", CloudModel), KidSession("deepseek", CloudModel));

        r.Spec.Should().NotBeNull("маршрут «провайдер» принят — процесс стартует");
        using var scope = new AssertionScope();
        r.Spec!.Env!["ANTHROPIC_BASE_URL"].Should().Be("https://api.deepseek.com/anthropic");
        r.Spec.Env.Values.Should().Contain("sk-ds-secret");
        r.Spec.Env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
        r.Spec.Env[ChildTurnMarker.EnvKey].Should().Be("1");
        r.Spec.ClearEnv.Should().Contain("CLAUDE_CODE_OAUTH_TOKEN", "унаследованный токен подписки вычищается");
    }

    // Локальный провайдер: тот же запуск без токена в env.
    [Fact]
    public async Task ХодРебёнка_Локаль_ТокенаПодпискиВEnvНет()
    {
        var r = await LaunchAsync(Snapshot("local-qwen", LocalModel), KidSession("local-qwen", LocalModel));

        r.Spec.Should().NotBeNull();
        r.Spec!.Env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
        r.Spec.ClearEnv.Should().Contain("CLAUDE_CODE_OAUTH_TOKEN");
    }

    // Пул-блок: Info.Provider указывает на подписочную запись пула — ребёнку её учётка не накладывается.
    // Чат правил: маршрут — поля профиля (kids-api), а не Info.Provider («claude-2»).
    [Fact]
    public async Task ХодРебёнка_ПулБлокНеИсполняется_УчёткаПодпискиПулаНеДоезжает()
    {
        var rulesChat = KidSession("claude-2", "sonnet");
        rulesChat.OnboardingKind = OnboardingKinds.ChildRules;
        var r = await LaunchAsync(Snapshot("kids-api", "sonnet"), rulesChat);

        r.Spec.Should().NotBeNull();
        using var scope = new AssertionScope();
        r.Spec!.Env!["ANTHROPIC_API_KEY"].Should().Be("sk-ant-kids");
        r.Spec.Env.Values.Should().NotContain("oauth-sub-2");
        r.Spec.Env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    // Запись пула только с setup-tokenом (решение 25): env детского хода несёт токен самой записи,
    // хостовый API-ключ и чужие токены в него не доезжают. Профиль записи изолированный «sub-{key}»,
    // а у песочницы-ребёнка RewriteProfileEnv переписывает его на профиль ребёнка ({childId}/…).
    [Fact]
    public async Task ХодРебёнка_ЗаписьПулаСетапТокен_ТокенЗаписиИПрофильПесочницыРебёнка()
    {
        var r = await LaunchAsync(Snapshot("kids-oauth", "sonnet"), KidSession("kids-oauth", "sonnet"));

        r.Spec.Should().NotBeNull("запись пула с setup-tokenом — допустимый маршрут ребёнка");
        using var scope = new AssertionScope();
        r.Spec!.Env!["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("oauth-kids");
        r.Spec.Env.Should().NotContainKey("ANTHROPIC_API_KEY", "хостовый API-ключ детского хода не касается");
        r.Spec.Env.Values.Should().NotContain("sk-ant-kids").And.NotContain("oauth-sub-2");
        // Зеркало песочницы: реальные аргументы контейнерного запуска (BuildDockerExecArgs →
        // BuildTurnEnv) несут профиль записи уже песочным профилем ребёнка.
        var execArgs = SandboxRunner().BuildDockerExecArgs(r.Spec);
        var envArgs = Enumerable.Range(0, execArgs.Count - 1)
            .Where(i => execArgs[i] == "-e")
            .Select(i => execArgs[i + 1])
            .ToList();
        envArgs.Should().Contain($"CLAUDE_CONFIG_DIR={SandboxManager.ProfilesMount}/kid/sub-kids-oauth");
    }

    // BareMode: ни одного аргумента BuildBareModeArgs и нет --system-prompt-file на локальном провайдере с BareMode.
    [Fact]
    public async Task ХодРебёнка_ЛокальБезBareMode_БезКартыСистемнымПромптом()
    {
        var r = await LaunchAsync(Snapshot("local-qwen", LocalModel), KidSession("local-qwen", LocalModel));

        r.Spec.Should().NotBeNull();
        r.Spec!.Args.Should().NotContain("--system-prompt-file", "карта AI Home ребёнку не отдаётся");
        r.Session.LastBareModeApplied.Should().BeFalse();
    }

    [Fact]
    public async Task Контроль_ВзрослыйНаЛокалиСBareMode_КартаЕсть()
    {
        var r = await LaunchAsync(null, new Session { Provider = "local-qwen", Model = LocalModel });

        r.Spec!.Args.Should().Contain("--system-prompt-file");
        r.Session.LastBareModeApplied.Should().BeTrue();
    }

    // Модель чата ребёнка (её закрепил запуск по персоне проекта-предмета, Б17c: явной модели
    // предмета нет) — явная модель для резолва места: назначение ChatPersona её не переопределяет.
    [Fact]
    public async Task ХодРебёнка_НазначениеМестаНеПереопределяетМодельЧата()
    {
        var dir = NewTempDir("ccs-child-assign-");
        var config = Config(dir);
        var providers = new LlmProviderRegistry(config);
        var store = new LocalActionOverridesStore(config);
        store.Set(LocalActionCatalog.ChatPersona, LocalModel).Should().BeTrue();
        var resolver = new ModelAssignmentResolver(new NoTiers(), store, providers: providers);

        var r = await LaunchAsync(Snapshot("deepseek", CloudModel), KidSession("deepseek", CloudModel), resolver);

        using var scope = new AssertionScope();
        r.Session.EffectiveTurnModel.Should().Be(CloudModel);
        r.Spec.Should().NotBeNull("модель чата осталась облачной, маршрут совпал");
        r.Spec!.Env!["ANTHROPIC_BASE_URL"].Should().Be("https://api.deepseek.com/anthropic");
    }

    // Цепочка ребёнка (решение 27): первый шаг — явная модель маршрута у каждого его вида;
    // дальше — хвост слотов (после него решит фильтр), стартовое закрепление не сдвигается.
    [Theory]
    [InlineData("kids-api", "sonnet")]
    [InlineData("kids-oauth", "sonnet")]
    [InlineData("deepseek", CloudModel)]
    [InlineData("local-qwen", LocalModel)]
    public async Task ХодРебёнка_ЦепочкаНачинаетсяСМоделиМаршрута_УКаждогоВидаМаршрута(string providerKey, string model)
    {
        var dir = NewTempDir("ccs-child-chain-");
        var config = Config(dir);
        var providers = new LlmProviderRegistry(config);
        var store = new LocalActionOverridesStore(config);
        var resolver = new ModelAssignmentResolver(new NoTiers(), store, providers: providers);

        var r = await LaunchAsync(Snapshot(providerKey, model), KidSession(providerKey, model), resolver);

        r.Session.EffectiveTurnChain.Should().StartWith(model);
    }

    // Фильтр цепочки ребёнка (решение 27): облачные шаги без цены из неё выпадают — на них бюджет
    // И3 молча перестал бы ограничивать. Первый шаг (модель маршрута) остаётся: цепочка не пустеет.
    [Fact]
    public async Task ХодРебёнка_Цепочка_ОблачныйШагБезЦеныВыпадает()
    {
        var dir = NewTempDir("ccs-child-chain-free-");
        var config = Config(dir);
        var providers = new LlmProviderRegistry(config);
        var store = new LocalActionOverridesStore(config);
        // Инстанс-слот — облако без цены: ResolveChain допишет его после модели маршрута,
        // фильтр ребёнка обязан его выбросить.
        var resolver = new ModelAssignmentResolver(new AllTiers(), store, providers: providers);

        var r = await LaunchAsync(Snapshot("kids-api", "sonnet"), KidSession("kids-api", "sonnet"), resolver);

        r.Session.EffectiveTurnChain.Should().Equal("sonnet");
    }

    private sealed class NoTiers : ITierModelResolver
    {
        public string? TierModel(ModelTier tier) => tier == ModelTier.Strong ? "claude-fable-5-2" : "claude-opus-5-5";
    }

    private sealed class AllTiers : ITierModelResolver
    {
        public string? TierModel(ModelTier tier) => FreeModel;
    }

    // Фабрика: рассогласованная пара ребёнка не самолечится и пул не спрашивается.
    private sealed class CountingPool(IConfiguration config) : ClaudeSubscriptionPool(config)
    {
        public int PickCalls;
        public override string Pick(string? model = null)
        {
            Interlocked.Increment(ref PickCalls);
            return base.Pick(model);
        }
    }

    private (LlmSessionAdapterFactory Factory, CountingPool Pool, LlmSessionContext Context) Factory(
        ChildPolicySnapshot? snapshot)
    {
        var dir = NewTempDir("ccs-child-factory-");
        var config = Config(dir);
        var providers = new LlmProviderRegistry(config);
        var pool = new CountingPool(config);
        var factory = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), providers, pool);
        var context = new LlmSessionContext(dir, _ => Task.CompletedTask, RawSystemPrompt: null,
            BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt, PermissionRules: null, TasksMcp: null,
            OwnerId: "kid", ChildPolicyProvider: () => snapshot);
        return (factory, pool, context);
    }

    [Fact]
    public void Фабрика_Ребёнок_РассогласованнаяПара_ОтказБезСамолеченияИБезPick()
    {
        // Модель предмета — Anthropic (пул), а в сессии ключ стороннего провайдера: у взрослого пара лечится через Pick
        var (factory, pool, context) = Factory(Snapshot("kids-api", "sonnet"));
        var session = KidSession("deepseek", "sonnet");

        var act = () => factory.Create(session, context);

        // Фабрика — внутренняя точка: события отказа она не публикует, текст без обещания.
        act.Should().Throw<ChildRouteRefusedException>().WithMessage(ChildRoute.RefusedTextNoGuardians);
        pool.PickCalls.Should().Be(0, "пул подписок ребёнку не спрашивается");
        session.Provider.Should().Be("deepseek", "пара не самолечится");
    }

    [Fact]
    public void Фабрика_Взрослый_ТаЖеПара_ЛечитсяЧерезPick()
    {
        var (factory, pool, context) = Factory(null);
        var session = KidSession("deepseek", "sonnet");

        factory.Create(session, context);

        pool.PickCalls.Should().BeGreaterThan(0);
        session.Provider.Should().NotBe("deepseek");
    }

    [Fact]
    public void Фабрика_Ребёнок_СогласованныйПровайдер_Создаёт()
    {
        var (factory, pool, context) = Factory(Snapshot("deepseek", CloudModel));

        factory.Create(KidSession("deepseek", CloudModel), context).Should().NotBeNull();
        pool.PickCalls.Should().Be(0);
    }
}
