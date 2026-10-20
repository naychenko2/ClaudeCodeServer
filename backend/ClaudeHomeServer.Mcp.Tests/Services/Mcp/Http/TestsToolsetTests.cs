using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.TestRuns;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

/// <summary>
/// Тулсет tests (run_tests): состав постоянный — один инструмент; гейты на КАЖДЫЙ вызов
/// (сессия владельца, подсистема, чат проекта, проект на сервере, не ReadOnly, цель внутри
/// дерева) отказывают текстом с IsError, без исключения и без запуска процесса.
/// </summary>
public class TestsToolsetTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;

    public TestsToolsetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tests_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private sealed record Env(TestsToolset Toolset, McpToolCallContext Context, Session Session, Project Project,
        PersonaManager Personas, CountingLaunchers Launchers, SessionManager Sessions);

    // Лаунчер, который считает обращения: отказ обязан случиться ДО выбора среды
    private sealed class CountingLaunchers : ClaudeHomeServer.Services.Execution.ILauncherFactory
    {
        public int Calls;
        public ClaudeHomeServer.Services.Execution.IProcessLauncher Local => throw new InvalidOperationException();
        public ClaudeHomeServer.Services.Execution.IProcessLauncher ForOwner(string? ownerId) => throw new InvalidOperationException();
        public ClaudeHomeServer.Services.Execution.IProcessLauncher ForProject(Project project)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("процесс в тесте гейтов не запускается");
        }
    }

    private Env Build(bool withService = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();
        var (sessions, projects, personas) = BuildSessionManager(config);
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект с тестами", dir, TestUserId, TestUsername);
        var session = sessions.CreateAsync(project.Id, ClaudeMode.Auto).GetAwaiter().GetResult();

        var launchers = new CountingLaunchers();
        var runs = withService ? new TestRunService(launchers, new TestRunsOptions()) : null;
        var toolset = new TestsToolset(sessions, projects, personas, runs);
        return new Env(toolset, new McpToolCallContext(TestUserId, session.Id, session.Id), session, project,
            personas, launchers, sessions);
    }

    private static Task<McpToolCallResult> Call(Env env, JsonObject? args = null, McpToolCallContext? context = null) =>
        env.Toolset.CallAsync(TestsToolset.ToolName, args ?? new JsonObject(), context ?? env.Context, default);

    [Fact]
    public void СвояСессия_ОдинИнструмент_ПостоянныйСостав()
    {
        var env = Build();

        env.Toolset.ToolsFor(env.Context).Select(t => t.Name).Should().Equal("run_tests");
        env.Toolset.ToolsFor(env.Context).Should().BeSameAs(env.Toolset.ToolsFor(env.Context),
            "состав статичный — не зависит ни от хода, ни от вызова");
        env.Toolset.Name.Should().Be(McpEndpoints.TestsName);
    }

    [Fact]
    public async Task ЧужаяСессия_НиСостава_НиВызова()
    {
        var env = Build();
        var foreign = new McpToolCallContext("someone-else", env.Session.Id, env.Session.Id);

        env.Toolset.ToolsFor(foreign).Should().BeEmpty();
        var result = await Call(env, context: foreign);

        result.IsError.Should().BeTrue();
        env.Launchers.Calls.Should().Be(0);
    }

    // Фактический старт — из самого tools/call: CLI выполняет выписанные подряд вызовы по
    // очереди, и карточка ждёт без «идёт», пока её вызов не начнётся. Метка ложится в историю
    // вызова (после F5 карточка не врёт); чужой вызов её не получает
    [Fact]
    public async Task ВызовСToolUseId_СтавитФактическийСтартКарточке()
    {
        var env = Build();
        var acc = env.Sessions.AccumulatorOf(env.Session.Id);
        acc.OnToolUse("toolu_run1", "mcp__tests__run_tests", new { }, startedAt: 1_000);
        acc.OnToolUse("toolu_run2", "mcp__tests__run_tests", new { }, startedAt: 1_000);

        await Call(env, context: env.Context with { ToolUseId = "toolu_run1" });

        var tools = (await env.Sessions.GetHistoryAsync(env.Session.Id)).OfType<Protocol.StoredToolUseMessage>().ToList();
        var started = tools.Single(t => t.Id == "toolu_run1");
        started.Started.Should().BeTrue();
        started.StartedAt.Should().BeGreaterThan(1_000);
        tools.Single(t => t.Id == "toolu_run2").Started.Should().BeNull("второй вызов ещё ждёт своей очереди");
    }

    // Мгновенный отказ «выключено на сервере» — не работа инструмента: карточка не мигает «идёт»
    [Fact]
    public async Task ПодсистемаВыключена_СтартНеСтавится()
    {
        var env = Build(withService: false);
        env.Sessions.AccumulatorOf(env.Session.Id).OnToolUse("toolu_off", "mcp__tests__run_tests", new { }, startedAt: 1_000);

        await Call(env, context: env.Context with { ToolUseId = "toolu_off" });

        (await env.Sessions.GetHistoryAsync(env.Session.Id)).OfType<Protocol.StoredToolUseMessage>()
            .Single().Started.Should().BeNull();
    }

    [Fact]
    public async Task ЧатВнеПроекта_Отказ()
    {
        var env = Build();
        env.Session.OwnerId = TestUserId;
        env.Session.ProjectId = null;

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("только в чате проекта");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ЛокальныйПроект_ОтказТекстомПроBash()
    {
        var env = Build();
        env.Project.DeviceId = "device-1";

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Be(TestsToolset.LocalProjectReason).And.Contain("Bash");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadOnlyПерсона_Отказ()
    {
        var env = Build();
        var persona = env.Personas.Create(TestUserId, "Ревьюер", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, access: PersonaAccess.ReadOnly);
        env.Session.PersonaId = persona.Id;

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Только чтение");
        env.Launchers.Calls.Should().Be(0);
    }

    // L1 ревью этапа 1: Custom-персона без Bash не запускает код и через run_tests — ни узла
    // в конфиге хода, ни вызова
    [Fact]
    public async Task ПерсонаБезBash_НиУзла_НиВызова()
    {
        var env = Build();
        var persona = env.Personas.Create(TestUserId, "Аналитик", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, access: PersonaAccess.Custom,
            disallowedTools: ["Bash"]);
        env.Session.PersonaId = persona.Id;

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("запрещён Bash");
        env.Launchers.Calls.Should().Be(0);
        env.Sessions.BuildTestsContext(TestUserId, env.Project.Id, persona).Should().BeNull(
            "узла tests у персоны без Bash быть не должно");
    }

    [Fact]
    public void ПерсонаСЧастичнымЗапретомBash_УзелЕсть()
    {
        // Шаблон «Bash(git:*)» — частичный запрет, а не запрет запуска кода: узел остаётся
        var env = Build();
        var persona = env.Personas.Create(TestUserId, "Разработчик", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, access: PersonaAccess.Custom,
            disallowedTools: ["Bash(git push:*)"]);

        env.Sessions.BuildTestsContext(TestUserId, env.Project.Id, persona).Should().NotBeNull();
        env.Sessions.BuildTestsContext(TestUserId, env.Project.Id, persona: null).Should().NotBeNull();
    }

    [Fact]
    public async Task ПодсистемаВыключена_ЧестныйОтказ()
    {
        var env = Build(withService: false);

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("выключен");
    }

    [Theory]
    [InlineData("../чужой")]
    [InlineData("a/../../чужой")]
    public async Task ЦельВнеДерева_Отказ(string target)
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["target"] = target });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("за пределы проекта");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task АбсолютнаяЦель_Отказ()
    {
        var env = Build();
        var absolute = Path.Combine(env.Project.RootPath, "backend");

        var result = await Call(env, new JsonObject { ["target"] = absolute });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("ОТНОСИТЕЛЬНО");
    }

    // M1 ревью этапа 1: цель едет позиционным аргументом dotnet test, строка с «-» стала бы
    // опцией — запись файлов вне дерева (--diag, --results-directory), подмена env testhost
    // (-e:DOTNET_STARTUP_HOOKS), чужой runsettings. Отказ до запуска во всех формах
    [Theory]
    [InlineData("--diag:probe.log")]
    [InlineData("--results-directory=/tmp/x")]
    [InlineData("-e:DOTNET_STARTUP_HOOKS=hook.dll")]
    [InlineData("--settings:evil.runsettings")]
    [InlineData("  --diag:probe.log")]
    [InlineData("./--diag:probe.log")]
    public async Task ЦельОпция_Отказ(string target)
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["target"] = target });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«-»", "цель-опцию надо отклонять как опцию, а не как «не найдено»");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public void ЦельОпция_ДажеЕслиТакойКаталогЕсть_Отказ()
    {
        // Существование цели не спасает: каталог «-e» в дереве всё равно ушёл бы в dotnet опцией
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-dash")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "-e"));

        TestsToolset.TryResolveTarget(root, "-e", out _, out var error).Should().BeFalse();
        TestsToolset.TryResolveTarget(root, "./-e", out _, out error).Should().BeFalse();
        error.Should().Contain("«-»");
    }

    [Theory]
    [InlineData("нет/такого")]
    [InlineData("notes.txt")]
    public void ЦельНеКаталогИНеПроект_Отказ(string target)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-missing")).FullName;
        File.WriteAllText(Path.Combine(root, "notes.txt"), "");

        TestsToolset.TryResolveTarget(root, target, out _, out var error).Should().BeFalse();
        error.Should().Contain("не найден");
    }

    [Theory]
    [InlineData("App.Tests.csproj")]
    [InlineData("App.slnx")]
    [InlineData("App.sln")]
    [InlineData("App.slnf")]
    public void ЦельФайлПроектаИлиРешения_Принимается(string file)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-files")).FullName;
        File.WriteAllText(Path.Combine(root, file), "");

        TestsToolset.TryResolveTarget(root, file, out var target, out _).Should().BeTrue();
        target.Should().Be(file);
    }

    [Fact]
    public void ЦельВнутриДерева_НормализуетсяВОтносительныйПуть()
    {
        var root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(Path.Combine(root, "backend", "App.Tests"));

        TestsToolset.TryResolveTarget(root, "backend\\App.Tests/./", out var target, out _).Should().BeTrue();

        target.Should().Be("backend/App.Tests");
    }

    // M1-bis ревью этапа 2: «@файл» dotnet раскрывает в опции из response-файла — в цели и в
    // фильтре. Отказ до запуска во всех формах (и до нормализации пути, и после)
    [Theory]
    [InlineData("@args")]
    [InlineData("  @args")]
    [InlineData("./@args")]
    [InlineData("a/../@args")]
    public async Task ЦельResponseФайл_Отказ(string target)
    {
        var env = Build();
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "@args"));

        var result = await Call(env, new JsonObject { ["target"] = target });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«@»", "даже существующий каталог «@args» ушёл бы в dotnet response-файлом");
        env.Launchers.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("@args2")]
    [InlineData(" @args2")]
    [InlineData("-e:DOTNET_STARTUP_HOOKS=x")]
    public async Task ФильтрResponseФайлИлиОпция_Отказ(string filter)
    {
        var env = Build();
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "backend"));

        var result = await Call(env, new JsonObject { ["target"] = "backend", ["filter"] = filter });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«@»");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task НеизвестныйВид_Отказ()
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["kind"] = "jest" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("dotnet, vitest или playwright");
    }

    // Кира: модель часто не передаёт target, а решения в корне нет — первый вызов падал
    [Fact]
    public void БезЦели_ЕдинственноеРешениеВДереве()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-sln")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "backend"));
        File.WriteAllText(Path.Combine(root, "backend", "App.slnx"), "");
        // Служебные и скрытые каталоги не в счёт: node_modules и worktree'ы в .claude
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "x"));
        File.WriteAllText(Path.Combine(root, "node_modules", "x", "Other.sln"), "");
        Directory.CreateDirectory(Path.Combine(root, ".claude", "worktrees", "w1"));
        File.WriteAllText(Path.Combine(root, ".claude", "worktrees", "w1", "App.slnx"), "");

        TestsToolset.TryResolveTarget(root, null, out var target, out _).Should().BeTrue();

        target.Should().Be("backend/App.slnx");
    }

    [Fact]
    public void БезЦели_НесколькоРешений_ОтказСоСписком()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-two")).FullName;
        File.WriteAllText(Path.Combine(root, "A.sln"), "");
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllText(Path.Combine(root, "b", "B.slnx"), "");

        TestsToolset.TryResolveTarget(root, "  ", out _, out var error).Should().BeFalse();

        error.Should().Contain("несколько").And.Contain("- A.sln").And.Contain("- b/B.slnx");
    }

    // L1 Светланы: кандидат «-x.sln» / «@dir/…» ушёл бы опцией или response-файлом — он не
    // цель по умолчанию и не мешает выбрать единственную настоящую
    [Fact]
    public void БезЦели_КандидатыПохожиеНаОпцию_Отсеяны()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-opt")).FullName;
        File.WriteAllText(Path.Combine(root, "-x.sln"), "");
        Directory.CreateDirectory(Path.Combine(root, "@evil"));
        File.WriteAllText(Path.Combine(root, "@evil", "E.slnx"), "");
        File.WriteAllText(Path.Combine(root, "@evil", "vitest.config.ts"), "");
        Directory.CreateDirectory(Path.Combine(root, "backend"));
        File.WriteAllText(Path.Combine(root, "backend", "App.slnx"), "");

        TestsToolset.TryResolveTarget(root, null, out var target, out var error).Should().BeTrue(error);
        target.Should().Be("backend/App.slnx");

        // Каталог с конфигом vitest только под «@evil» — цели нет, а не «@evil»
        TestsToolset.TryResolveNodeTarget(root, null, TestRunKind.Vitest, out var node, out _, out var nodeError).Should().BeFalse();
        node.Should().BeNull();
        nodeError.Should().Contain("не найден");
    }

    [Fact]
    public void БезЦели_БезРешений_ЕдинственныйТестовыйПроект()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-proj")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "src", "App"));
        File.WriteAllText(Path.Combine(root, "src", "App", "App.csproj"), "");
        Directory.CreateDirectory(Path.Combine(root, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(root, "tests", "App.Tests", "App.Tests.csproj"), "");

        TestsToolset.TryResolveTarget(root, null, out var target, out _).Should().BeTrue();
        target.Should().Be("tests/App.Tests/App.Tests.csproj");

        var empty = Directory.CreateDirectory(Path.Combine(_tempDir, "root-empty")).FullName;
        TestsToolset.TryResolveTarget(empty, null, out _, out var error).Should().BeFalse();
        error.Should().Contain("укажи target");
    }

    // --- vitest / Playwright ---

    private static string FrontendRoot(string root, bool vitest = true, bool playwright = true)
    {
        var front = Directory.CreateDirectory(Path.Combine(root, "frontend")).FullName;
        if (vitest)
        {
            File.WriteAllText(Path.Combine(front, "vitest.config.ts"), "");
            Directory.CreateDirectory(Path.Combine(front, "node_modules", "vitest"));
            File.WriteAllText(Path.Combine(front, "node_modules", "vitest", "vitest.mjs"), "");
        }
        if (playwright)
        {
            File.WriteAllText(Path.Combine(front, "playwright.config.ts"), "");
            Directory.CreateDirectory(Path.Combine(front, "node_modules", "@playwright", "test"));
            File.WriteAllText(Path.Combine(front, "node_modules", "@playwright", "test", "cli.js"), "");
        }
        Directory.CreateDirectory(Path.Combine(front, "src", "lib"));
        File.WriteAllText(Path.Combine(front, "src", "lib", "x.test.ts"), "");
        return front;
    }

    [Fact]
    public void Vitest_БезЦели_КаталогСКонфигом()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-vt")).FullName;
        FrontendRoot(root);

        TestsToolset.TryResolveNodeTarget(root, null, TestRunKind.Vitest, out var target, out _, out _).Should().BeTrue();
        target.Should().Be("frontend");
        TestsToolset.TryResolveNodeTarget(root, null, TestRunKind.Playwright, out target, out _, out _).Should().BeTrue();
        target.Should().Be("frontend");
    }

    [Fact]
    public void Vitest_НетNodeModules_Отказ()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-nomods")).FullName;
        FrontendRoot(root, vitest: false);
        File.WriteAllText(Path.Combine(root, "frontend", "vitest.config.ts"), "");

        TestsToolset.TryResolveNodeTarget(root, "frontend", TestRunKind.Vitest, out _, out _, out var error).Should().BeFalse();
        error.Should().Contain("npm ci");
    }

    // Монорепа (бой 2026-10-02, Venue Editor): пакеты подняты в node_modules корня, цель —
    // приложение глубоко в дереве; скрипт — относительно цели, cwd процесса остаётся целью
    private static string MonorepoRoot(string root)
    {
        var app = Directory.CreateDirectory(Path.Combine(root, "web", "apps", "demo")).FullName;
        File.WriteAllText(Path.Combine(app, "playwright.config.ts"), "");
        File.WriteAllText(Path.Combine(app, "vitest.config.ts"), "");
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "@playwright", "test"));
        File.WriteAllText(Path.Combine(root, "node_modules", "@playwright", "test", "cli.js"), "");
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "vitest"));
        File.WriteAllText(Path.Combine(root, "node_modules", "vitest", "vitest.mjs"), "");
        return app;
    }

    [Theory]
    [InlineData(TestRunKind.Playwright, "../../../node_modules/@playwright/test/cli.js")]
    [InlineData(TestRunKind.Vitest, "../../../node_modules/vitest/vitest.mjs")]
    public void Монорепа_ПакетВКорне_НайденВверхПоДереву(TestRunKind kind, string expected)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-mono-" + kind)).FullName;
        MonorepoRoot(root);

        TestsToolset.TryResolveNodeTarget(root, "web/apps/demo", kind, out var target, out var nodeBin, out var error)
            .Should().BeTrue(error);
        target.Should().Be("web/apps/demo");
        nodeBin.Should().Be(expected);
        TestRunService.IsNodeBinShape(nodeBin!, kind).Should().BeTrue();
    }

    [Fact]
    public void Монорепа_БлижайшийNodeModulesПобеждает()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-mono-near")).FullName;
        var app = MonorepoRoot(root);
        Directory.CreateDirectory(Path.Combine(app, "node_modules", "vitest"));
        File.WriteAllText(Path.Combine(app, "node_modules", "vitest", "vitest.mjs"), "");

        TestsToolset.TryResolveNodeTarget(root, "web/apps/demo", TestRunKind.Vitest, out _, out var nodeBin, out _)
            .Should().BeTrue();
        nodeBin.Should().Be(TestRunService.VitestBin, "node берёт ближайшую копию — её же и запускаем");
    }

    [Fact]
    public void Монорепа_ПакетВышеКорняДерева_Отказ()
    {
        // Рабочее дерево — «inner»; node_modules лежит в его родителе, то есть ВНЕ дерева
        var outer = Directory.CreateDirectory(Path.Combine(_tempDir, "root-mono-outer")).FullName;
        Directory.CreateDirectory(Path.Combine(outer, "node_modules", "@playwright", "test"));
        File.WriteAllText(Path.Combine(outer, "node_modules", "@playwright", "test", "cli.js"), "");
        var root = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "app"));

        TestsToolset.TryResolveNodeTarget(root, "app", TestRunKind.Playwright, out _, out var nodeBin, out var error)
            .Should().BeFalse("выше корня рабочего дерева поиск не идёт");
        nodeBin.Should().BeNull();
        error.Should().Contain("npm ci");
    }

    // pnpm: node_modules/@playwright/test — ссылка в node_modules/.pnpm/… того же дерева
    [SkippableFact]
    public void Pnpm_СсылкаВнутриДерева_Найдена()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-pnpm")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "web", "apps", "demo"));
        var store = Directory.CreateDirectory(Path.Combine(root, "node_modules", ".pnpm",
            "@playwright+test@1.50.0", "node_modules", "@playwright", "test")).FullName;
        File.WriteAllText(Path.Combine(store, "cli.js"), "");
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "@playwright"));
        Link(Path.Combine(root, "node_modules", "@playwright", "test"), store);

        TestsToolset.TryResolveNodeTarget(root, "web/apps/demo", TestRunKind.Playwright, out _, out var nodeBin,
            out var error).Should().BeTrue(error);
        nodeBin.Should().Be("../../../node_modules/@playwright/test/cli.js");
    }

    [SkippableFact]
    public void Pnpm_СсылкаНаружуДерева_Отказ()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "pnpm-outside", "test")).FullName;
        File.WriteAllText(Path.Combine(outside, "cli.js"), "");
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-pnpm-out")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "@playwright"));
        Link(Path.Combine(root, "node_modules", "@playwright", "test"), outside);

        TestsToolset.TryResolveNodeTarget(root, "app", TestRunKind.Playwright, out _, out var nodeBin, out var error)
            .Should().BeFalse();
        nodeBin.Should().BeNull();
        error.Should().Contain("за пределы проекта");
    }

    private static void Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, "Нет прав создавать символические ссылки: " + e.Message);
        }
    }

    [Theory]
    [InlineData("node_modules/vitest/vitest.mjs", TestRunKind.Vitest, true)]
    [InlineData("../../node_modules/@playwright/test/cli.js", TestRunKind.Playwright, true)]
    [InlineData("../node_modules/@playwright/test/cli.js", TestRunKind.Vitest, false)]
    [InlineData("/etc/evil.js", TestRunKind.Vitest, false)]
    [InlineData("../evil/node_modules/vitest/vitest.mjs", TestRunKind.Vitest, false)]
    [InlineData("-e", TestRunKind.Playwright, false)]
    public void NodeBin_ТолькоПодъёмыИКонстантаПакета(string nodeBin, TestRunKind kind, bool ok)
    {
        TestRunService.IsNodeBinShape(nodeBin, kind).Should().Be(ok);
    }

    [Theory]
    [InlineData("@front")]
    [InlineData("-c")]
    public void NodeЦель_ОпцияИлиResponseФайл_Отказ(string target)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-nodeopt")).FullName;
        Directory.CreateDirectory(Path.Combine(root, target));

        TestsToolset.TryResolveNodeTarget(root, target, TestRunKind.Vitest, out _, out _, out var error).Should().BeFalse();
        error.Should().Contain("«@»");
    }

    [Fact]
    public void Files_ОтносительноКаталогаПрогона()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-files-ok")).FullName;
        FrontendRoot(root);

        TestsToolset.TryResolveFiles(root, "frontend", new JsonArray("frontend/src/lib/x.test.ts", "frontend\\src\\lib"),
            out var files, out _).Should().BeTrue();

        files.Should().Equal("src/lib/x.test.ts", "src/lib");
    }

    [Theory]
    [InlineData("--config=/tmp/x.ts", "«@»")]
    [InlineData("@args", "«@»")]
    [InlineData("../etc/passwd", "за пределы")]
    [InlineData("frontend/нет.test.ts", "не найден")]
    [InlineData("backend", "вне каталога")]
    public void Files_Отказ(string file, string expected)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-files-bad-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        FrontendRoot(root);
        Directory.CreateDirectory(Path.Combine(root, "backend"));

        TestsToolset.TryResolveFiles(root, "frontend", new JsonArray(file), out _, out var error).Should().BeFalse();
        error.Should().Contain(expected);
    }

    [Fact]
    public void Files_ПутьОтКаталогаПрогонаНачинаетсяСМинуса_Отказ()
    {
        // Сам по себе путь от корня безопасен, но процесс получит его от каталога прогона
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "root-files-dash")).FullName;
        var front = FrontendRoot(root);
        File.WriteAllText(Path.Combine(front, "-x.test.ts"), "");

        TestsToolset.TryResolveFiles(root, "frontend", new JsonArray("frontend/-x.test.ts"), out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("«-»");
    }

    [Theory]
    [InlineData("NODE_OPTIONS", "--require x", "белого списка")]
    [InlineData("PLAYWRIGHT_BASE_URL", "http://example.com", "локальный стенд")]
    [InlineData("PLAYWRIGHT_BASE_URL", "file:///etc/passwd", "локальный стенд")]
    [InlineData("E2E_PASS", "a\nb", "управляющих")]
    public void Env_Отказ(string key, string value, string expected)
    {
        TestsToolset.TryResolveEnv(new JsonObject { [key] = value }, TestRunKind.Playwright, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain(expected);
    }

    [Fact]
    public void Env_БелыйСписок_ТолькоPlaywright()
    {
        var env = new JsonObject { ["PLAYWRIGHT_BASE_URL"] = "http://localhost:5600", ["E2E_USER"] = "admin" };

        TestsToolset.TryResolveEnv(env, TestRunKind.Playwright, out var resolved, out _).Should().BeTrue();
        resolved.Should().Contain("PLAYWRIGHT_BASE_URL", "http://localhost:5600").And.Contain("E2E_USER", "admin");
        TestsToolset.TryResolveEnv(new JsonObject { ["E2E_USER"] = "admin" }, TestRunKind.Vitest, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("только для Playwright");
    }

    [Fact]
    public async Task Vitest_ВсеГейтыПройдены_ДоходитДоСредыПроекта()
    {
        var env = Build();
        FrontendRoot(env.Project.RootPath);

        var result = await Call(env, new JsonObject
        {
            ["kind"] = "vitest",
            ["target"] = "frontend",
            ["files"] = new JsonArray("frontend/src/lib/x.test.ts"),
            ["filter"] = "проба",
        });

        env.Launchers.Calls.Should().Be(1);
        result.Text.Should().Contain("Не удалось запустить vitest");
    }

    [Fact]
    public void Схема_ВидыИАргументы_Постоянны()
    {
        var schema = TestsToolset.Tools.Single().InputSchema["properties"]!.AsObject();

        schema.Select(p => p.Key).Should().BeEquivalentTo("kind", "target", "filter", "files", "env", "no_build");
        schema["kind"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("dotnet", "vitest", "playwright");
        TestsToolset.Tools.Single().Description.Should().Contain("target");
    }

    // Замечание Киры (А): пример «backend/App.slnx» модель подставляла буквально
    [Fact]
    public void Схема_ОписаниеЦели_БезВыдуманныхИмён()
    {
        var target = TestsToolset.Tools.Single().InputSchema["properties"]!["target"]!["description"]!.GetValue<string>();

        target.Should().NotContain("App.slnx").And.NotContain("например").And.Contain(".slnx");
    }

    [Fact]
    public async Task ВсеГейтыПройдены_ДоходитДоСредыПроекта()
    {
        // Контрпример к отказам: с исправными входами вызов доходит до ForProject — значит
        // гейты выше не отказывают «на всякий случай»
        var env = Build();
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "backend", "App.Tests"));

        var result = await Call(env, new JsonObject { ["target"] = "backend/App.Tests" });

        env.Launchers.Calls.Should().Be(1);
        // Сбой среды — тоже текст с IsError, а не исключение транспорта
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Не удалось запустить");
    }

    internal static (SessionManager Sessions, ProjectManager Projects, PersonaManager Personas) BuildSessionManager(
        IConfiguration config, TestSessionBroadcaster? broadcaster = null)
    {
        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var history = new ChatHistoryService(config);
        broadcaster ??= new TestSessionBroadcaster();
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster), projectManager, personas);
    }
}
