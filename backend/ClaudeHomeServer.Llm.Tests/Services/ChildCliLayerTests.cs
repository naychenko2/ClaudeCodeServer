using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// И1 (ADR-041, KM-B3b → Б9c): слой CLI хода ребёнка — разрешающий список встроенных (п.11 в
// новой форме решения 10), флаги «настройки папки не действуют на ход» (И8: --setting-sources
// user, --strict-mcp-config), выключенные развёртка «/» и @-упоминаний (И11), запросы
// разрешений (п.12), плагины и CLAUDE.md (п.13). CLI 2.1.325: «--tools ""» оставляет у хода
// ноль встроенных (проверено живым запуском, init.tools = []), «--tools a,b,c» — ровно
// перечисленные; замок — разрешающий список, а не список запретов (набор встроенных меняется
// от версии к версии).
[Collection(TestCollections.ProcessGlobalState)]
public class ChildCliLayerTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];

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

    // Согласие профиля на маршрут (И2″, решение 26) стоит в фикстуре: без него ChildRoute
    // refuse'ит запуск ещё до аргументов (Б2b читает его на старте хода), и отказ был бы про
    // согласии, а не о том, что проверяет тест.
    private static readonly DateTimeOffset ConsentAt = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static ChildProfile KidProfile(string model = "sonnet") => new()
    {
        Enabled = true, ProviderKey = "kids-api", Model = model,
        RouteRiskAcknowledgedAt = ConsentAt, RouteRiskAcknowledgedBy = "parent-1",
    };

    private static ChildPolicySnapshot Child() => new("kid", KidProfile(), false);

    // --- п.12: запросы разрешений ---

    private static readonly MethodInfo Decide =
        typeof(ClaudeSession).GetMethod("DecidePermissionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewSession(
        Session info, Func<ChildPolicySnapshot?>? childPolicy)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, ChildPolicyProvider: childPolicy);
        return (new ClaudeSession(info, context), sent);
    }

    private static Task<string> DecideAsync(ClaudeSession session, string toolName)
    {
        using var doc = JsonDocument.Parse("{}");
        return (Task<string>)Decide.Invoke(session, ["req-1", toolName, doc.RootElement.Clone(), new object()])!;
    }

    // Каждая из веток, что у взрослого отвечает allow без карточки, у ребёнка обязана дать deny:
    // исполнитель задачи, «всегда разрешать», персона в AcceptEdits, свой MCP-сервер, план хода.
    [Theory]
    [InlineData("Bash", true, false)]                              // исполнитель задачи
    [InlineData("Bash", false, true)]                              // «всегда разрешать»
    [InlineData("mcp__tasks__tasks_list", false, false)]           // свой MCP, вне детского allow-list
    [InlineData("mcp__websearch__web_search", false, false)]       // веб-поиск выключен для ребёнка
    [InlineData("TaskCreate", false, false)]                       // план хода — не детский инструмент
    [InlineData("Write", false, false)]                            // обычный запрос → без карточки, deny
    public async Task ХодРебёнка_ЗапросВнеAllowList_ОтказБезКарточки(string tool, bool taskExecution, bool autoAllow)
    {
        var info = new Session { TaskExecution = taskExecution, AutoAllowTools = autoAllow ? [tool] : [], PersonaId = "p1" };
        var (session, sent) = NewSession(info, Child);
        await using var _ = session;

        var decision = await DecideAsync(session, tool).WaitAsync(TimeSpan.FromSeconds(10));

        decision.Should().Be("deny");
        lock (sent) sent.OfType<PermissionRequestMessage>().Should().BeEmpty("карточки у ребёнка нет");
    }

    [Fact]
    public async Task Контроль_ВзрослыйХод_ТеЖеВетки_Разрешают()
    {
        var info = new Session { TaskExecution = true };
        var (session, _) = NewSession(info, () => null);
        await using var _ = session;

        (await DecideAsync(session, "Bash")).Should().Be("allow");
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("mcp__")]
    [InlineData("mcp__server")]
    [InlineData("mcp____tool")]
    [InlineData("mcp__server__")]
    [InlineData("")]
    public void ChildPermissionDecision_НераспознанноеИмя_Deny(string tool) =>
        ClaudeSession.ChildPermissionDecision(tool).Should().Be("deny");

    // --- п.12 (К3, К9): пары детского allow-list — allow без карточки, остальное — deny ---

    [Theory]
    [InlineData("mcp__tutor__search_materials")]
    [InlineData("mcp__widgets__widget_show")]
    public void ChildPermissionDecision_ПарыAllowList_Allow(string tool) =>
        ClaudeSession.ChildPermissionDecision(tool).Should().Be("allow");

    // Б17c: инструменты списка и чтения описи материалов предмета сняты — для ребёнка они deny
    [Theory]
    [InlineData("mcp__tutor__list_materials")]
    [InlineData("mcp__tutor__read_material")]
    public void ChildPermissionDecision_СнятыеИнструментыОписи_Deny(string tool) =>
        ClaudeSession.ChildPermissionDecision(tool).Should().Be("deny");

    [Theory]
    [InlineData("mcp__dify__search_knowledge")]
    [InlineData("mcp__dify__list_datasets")]
    [InlineData("mcp__tutor__write_material")]
    [InlineData("mcp__tutor__search_materials__x")]
    [InlineData("mcp__wsp__files_read")]
    [InlineData("Read")]
    public void ChildPermissionDecision_ОстальноеВключаяDify_Deny(string tool) =>
        ClaudeSession.ChildPermissionDecision(tool).Should().Be("deny");

    // --- п.11 в новой форме (решение 10): разрешающий список встроенных ---

    [Fact]
    public void ForceChildBuiltinTools_СнимаетПрежнийСписокИСтавитРазрешающий()
    {
        List<string> args = ["--model", "sonnet", "--tools", "Bash Edit Read", "--print"];

        ClaudeSession.ForceChildBuiltinTools(args);

        args.Should().Equal("--model", "sonnet", "--print",
            "--tools", "Read,Glob,Grep,Write,Edit,AskUserQuestion");
    }

    // --- п.11 и п.13 целиком: собранный ProcessSpec ---

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

    private sealed class FixedPlugins : IPluginDirSource
    {
        public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project? project) =>
            [Path.Combine(Path.GetTempPath(), "ccs-plugin-x")];
    }

    private async Task<ProcessSpec> CaptureSpecAsync(Func<ChildPolicySnapshot?>? childPolicy)
    {
        var (spec, _) = await LaunchAsync(childPolicy, "sonnet", null);
        spec.Should().NotBeNull("процесс CLI должен был стартовать");
        return spec!;
    }

    private async Task<(ProcessSpec? Spec, List<string> Errors)> LaunchAsync(
        Func<ChildPolicySnapshot?>? childPolicy, string model, LlmProviderRegistry? providersOverride,
        TurnEventBus? events = null, string text = "тестовый ход",
        ICommandExpansion? commandExpansion = null, PersonasMcpContext? personasMcp = null,
        string[]? disallowedTools = null)
    {
        var errors = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-child-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        var launcher = new SpecCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m => { if (m is ErrorMessage e) { lock (errors) errors.Add(e.Text); failed.TrySetResult(); } return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, PersonasMcp: personasMcp,
            Launcher: launcher, ContentRootPath: root, OwnerId: "kid",
            PluginDirSource: new FixedPlugins(), ChildPolicyProvider: childPolicy, Events: events);
        var providers = providersOverride ?? new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{ClaudeSubscriptionPool.Section}:kids-api:ApiKey"] = "sk-ant-kids" }).Build());
        var session = new ClaudeSession(new Session { Model = model, Provider = "claude" }, context,
            commandExpansion: commandExpansion, disallowedTools: disallowedTools,
            providers: providers, subscriptionPool: pool);

        await session.SendMessageAsync(text);
        await Task.WhenAny(started.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(30));
        return (launcher.Captured, errors);
    }

    // R-KM-B6c2 п.1: звено «снимок политики → PromptSessionContext.IsChild». Контрибьюторы recall
    // закрываются по этому признаку; мутация «IsChild: false» в ClaudeSession обязана красить тест.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task СборкаПромпта_IsChildСледуетЗаСнимкомПолитики(bool child)
    {
        var bus = new TurnEventBus();
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnFilter<PromptAssembling>(100, async (e, next) =>
        {
            seen.TrySetResult(e.Session.IsChild);
            await next();
        }, "Test.IsChildProbe");

        await LaunchAsync(() => child ? Child() : null, "sonnet", null, bus);

        seen.Task.IsCompleted.Should().BeTrue("шина сборки промпта обязана отработать до старта CLI");
        (await seen.Task).Should().Be(child);
    }

    [Fact]
    public async Task ХодРебёнка_ВнешняяМодель_ОтказЗапуска_ПроцессНеСтартует()
    {
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
                ["LlmProviders:deepseek:ApiKey"] = "sk-test",
                ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            }).Build());

        // Модель хода ребёнка — модель профиля/предмета (И2′): маршрут «ключ пула» с чужой моделью — отказ.
        // Согласие профиля стоит: отказ должен быть про модели, а не про отсутствии согласия.
        var foreign = new ChildPolicySnapshot("kid", KidProfile("deepseek-chat"), false);
        var (spec, errors) = await LaunchAsync(() => foreign, "deepseek-chat", providers);

        spec.Should().BeNull("ключ пула ребёнка чужой модели не уходит");
        // Точка отказа в ветке env ClaudeSession события не публикует — текст без обещания.
        errors.Should().ContainSingle().Which.Should().Be(ChildRoute.RefusedTextNoGuardians);
    }

    [Fact]
    public async Task ХодРебёнка_МодельЧатаЧужаяПриПринятомМаршруте_ОтказВТочкеEnv()
    {
        // Маршрут принят по модели профиля (запись пула, «sonnet»), а модель чата — чужого
        // провайдера: отказывает внутренняя точка ветки env в ClaudeSession («ключ пула годится
        // только Anthropic-модели»), не ChildRoute. Она события child/route-refusal не публикует:
        // текст без обещания уведомления.
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
                ["LlmProviders:deepseek:ApiKey"] = "sk-test",
                ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            }).Build());
        var (spec, errors) = await LaunchAsync(() => Child(), "deepseek-chat", providers);

        spec.Should().BeNull("ключ пула ребёнка чужой модели чата не уходит");
        errors.Should().ContainSingle().Which.Should().Be(ChildRoute.RefusedTextNoGuardians,
            "точка ClaudeSession события не публикует — текст без обещания");
    }

    [Fact]
    public async Task ЗапускХода_ДетскаяПолитикаЧитаетсяОдинРаз()
    {
        var calls = 0;
        var (spec, _) = await LaunchAsync(() => { Interlocked.Increment(ref calls); return Child(); }, "sonnet", null);

        spec.Should().NotBeNull();
        calls.Should().Be(1, "аргументы, env, ключ и чистка env решаются по одному снимку");
    }

    [Fact]
    public async Task ХодРебёнка_РазрешающийСписокВстроенных_НастройкиТолькоUser_БезПлагинов_БезCLAUDEmd()
    {
        var spec = await CaptureSpecAsync(() => Child());

        using var scope = new AssertionScope();
        var args = spec.Args.ToList();
        args.Count(a => a == "--tools").Should().Be(1, "один разрешающий список на ход, не два");
        var i = args.IndexOf("--tools");
        i.Should().BeGreaterThanOrEqualTo(0);
        args[i + 1].Should().Be("Read,Glob,Grep,Write,Edit,AskUserQuestion",
            "ровно шесть встроенных, разрешённых ребёнку (решение 10 в новой форме)");
        // И8: «настройки папки не действуют на ход» — CLI читает user-настройки, .claude/settings*.json
        // cwd и .mcp.json cwd его не касаются (КТ0 п.3–4)
        args.Count(a => a == "--setting-sources").Should().Be(1);
        args[args.IndexOf("--setting-sources") + 1].Should().Be("user");
        args.Should().Contain("--strict-mcp-config",
            "только серверы из --mcp-config этого хода, ни user-scope, ни .mcp.json cwd");
        spec.Args.Should().NotContain("--plugin-dir", "плагины владельца ребёнку не отдаются");
        spec.Env!.GetValueOrDefault("CLAUDE_CODE_DISABLE_CLAUDE_MDS").Should().Be("1", "CLAUDE.md проекта ребёнку не грузится");
    }

    // Запрет персоны — её собственная сужающая сила, а не обход детского списка (И10, Х-W4-17):
    // репетитору с Access = ReadOnly запрещены файловые мутации, и в детском ходе они остаются
    // запрещёнными — и в аргументах CLI, и на границе can_use_tool. Прежнее снятие whole списком
    // (Except(ChildBuiltinTools)) съедало вместе с детским слоем и настоящий запрет персоны.
    // Мутации: «вернуть Except(ChildBuiltinTools)» в ClaudeSession красит первую половину
    // (Write/Edit выпадают из --disallowedTools); «убрать проверку _disallowedTools» в
    // ChildPermissionDecision — вторую (запись своего файла стала бы allow).
    [Fact]
    public async Task ХодРебёнка_ЗапретПерсоныНаЗаписи_ДоезжаетДоCLI_ИОтказываетНаСвоемФайле()
    {
        // Не выдуманный список, а то, что даёт персоне Access = ReadOnly на самом деле
        // (PersonaAccessPolicy): из встроенных ребёнка профиль «Только чтение» трогает ровно
        // запись и правку — Read, Glob, Grep и AskUserQuestion он не запрещает.
        var byPersona = ClaudeHomeServer.Services.PersonaAccessPolicy.BuildExtraDisallowed(
            new Persona { Access = PersonaAccess.ReadOnly })!;
        var onChildBuiltins = byPersona.Intersect(ClaudeSession.ChildBuiltinTools, StringComparer.Ordinal).ToList();
        onChildBuiltins.Should().Contain(new[] { "Edit", "Write" },
            "профиль «Только чтение» от встроенных ребёнка берёт запись и правку");
        onChildBuiltins.Should().NotContain("Read", "чтение профиля «Только чтение» не касается");

        var (spec, _) = await LaunchAsync(() => Child(), "sonnet", null, disallowedTools: [.. byPersona]);

        spec.Should().NotBeNull();
        var args = spec!.Args.ToList();
        var i = args.IndexOf("--disallowedTools");
        i.Should().BeGreaterThanOrEqualTo(0);
        var denied = args[i + 1].Split(',');
        using var scope = new AssertionScope();
        denied.Should().Contain("Write").And.Contain("Edit",
            "запрет персоны доходит до CLI: разрешающий --tools его не отменяет");
        denied.Should().Contain("Bash", "запрет вне детского разрешающего списка остаётся как был");

        // Та же граница на can_use_tool: свой файл в папке предмета — путь разрешён, но инструмент
        // запрещён персоной → отказ по имени, до пути.
        var root = Path.Combine(Path.GetTempPath(), "ccs-child-disallowed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        await using (var banned = ChildSessionWithDisallowed(root, [.. byPersona]))
            DecideChildWrite(banned, "Заметки/черновик.md").Behavior
                .Should().Be("deny", "Write запрещён персоной — путь своего файла его не открывает");

        // Контроль: без запрета персоны та же запись разрешена — отказывает запрет, а не путь.
        await using (var plain = ChildSessionWithDisallowed(root, []))
            DecideChildWrite(plain, "Заметки/черновик.md").Behavior.Should().Be("allow");
    }

    private ClaudeSession ChildSessionWithDisallowed(string root, string[] disallowedTools)
    {
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, ChildPolicyProvider: Child);
        return new ClaudeSession(
            new Session
            {
                OwnerId = "kid", ChildSpace = ChildSpaces.Lessons, ChildSubject = "subj-1", Model = "sonnet",
            },
            context, disallowedTools: disallowedTools);
    }

    // White-box: инстансное решение приватное (его зовёт только HandleControlRequestAsync) —
    // тот же приём, что в ChildPermissionDecisionTests.
    private static readonly MethodInfo DecideRequest =
        typeof(ClaudeSession).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ChildPermissionDecision"
                && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(JsonElement));

    private static (string Behavior, string Message) DecideChildWrite(ClaudeSession session, string filePath)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            subtype = "can_use_tool", tool_name = "Write", tool_use_id = "tool-1",
            input = new { file_path = filePath, content = "текст" },
        }));
        var result = (ValueTuple<string, string>)DecideRequest.Invoke(session, [doc.RootElement.Clone()])!;
        return (result.Item1, result.Item2);
    }

    [Fact]
    public async Task ВзрослыйХод_ВстроенныеИПлагиныНаМесте_ФлаговРебёнкаНет()
    {
        var spec = await CaptureSpecAsync(() => null);

        using var scope = new AssertionScope();
        spec.Args.Should().NotContain("--tools");
        spec.Args.Should().Contain("--plugin-dir");
        spec.Env.Should().NotContainKey("CLAUDE_CODE_DISABLE_CLAUDE_MDS");
        // Обратная сторона И8: взрослый ход по-прежнему читает настройки проекта/папки
        spec.Args.Should().NotContain("--setting-sources");
        spec.Args.Should().NotContain("--strict-mcp-config");
    }

    // --- И11: развёртка «/»-команд и @-упоминаний у ребёнка выключены ---

    private sealed class SpyExpansion : ICommandExpansion
    {
        public List<string> Calls { get; } = [];
        // Развернутый текст — маркер: по нему видно, доехала ли развёртка до хода
        public string? ExpandSkill(string message)
        {
            Calls.Add(message);
            return message.StartsWith("/review") ? "РАЗВЁРНУТЫЙ СКИЛЛ" : null;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task СлешКоманда_Ребёнку_ТекстУходитКакЕсть(bool child)
    {
        var bus = new TurnEventBus();
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnFilter<PromptAssembling>(100, async (e, next) =>
        {
            seen.TrySetResult(e.TurnText);
            await next();
        }, "Test.TurnTextProbe");
        var spy = new SpyExpansion();

        await LaunchAsync(() => child ? Child() : null, "sonnet", null, bus, "/review что поправим", spy);

        seen.Task.IsCompleted.Should().BeTrue("шина сборки промпта обязана отработать до старта CLI");
        var sent = await seen.Task;
        if (child)
        {
            spy.Calls.Should().BeEmpty("развёртка «/» ребёнку не вызывается вовсе");
            sent.Should().Be("/review что поправим", "текст уходит в CLI как есть");
        }
        else
        {
            // Обратная совместимость: взрослый ход разворачивает скилл, как раньше
            sent.Should().Be("РАЗВЁРНУТЫЙ СКИЛЛ");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task УпоминанияПерсон_Ребёнку_СекцииMentionsНет(bool child)
    {
        var bus = new TurnEventBus();
        var seen = new TaskCompletionSource<IReadOnlyList<PromptSectionDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bus.OnNotification<PromptAssembled>(async e =>
        {
            if (e.Snapshot?.Draft is { } draft && draft.Sections.Count > 0)
                seen.TrySetResult(draft.Sections);
            await Task.CompletedTask;
        });
        var personas = new PersonasMcpContext("http://localhost:5999", () => "tok", ProjectId: "proj1",
            MentionsHint: "- @gleb — Код-ревьюер (Глеб)", UseHttp: true);

        await LaunchAsync(() => child ? Child() : null, "sonnet", null, bus, "@gleb привет",
            personasMcp: personas);

        seen.Task.IsCompleted.Should().BeTrue("снимок промпта публикуется шиной до старта CLI");
        var keys = (await seen.Task).Select(s => s.Key).ToList();
        if (child)
            keys.Should().NotContain("persona-mentions",
                "ребёнку некого позвать через @: ни подсказки, ни пути позвать");
        else
            keys.Should().Contain("persona-mentions", "взрослый ход видит упоминания персон, как раньше");
    }
}
