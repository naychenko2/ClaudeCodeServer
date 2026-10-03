using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.TestRuns;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

/// <summary>
/// Тулсет dev (build): состав постоянный — один инструмент; гейты те же, что у run_tests
/// (инструмент исполняет код репозитория): сессия владельца, подсистема, чат проекта, проект на
/// сервере, персона не ReadOnly и с Bash, цель и скрипт не опции. Отказ — текстом с IsError, без
/// исключения и без выбора среды запуска.
/// </summary>
public class DevToolsetTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;

    public DevToolsetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dev_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // Реестры «Сервисов» тестов: их стенды гасятся до удаления каталога
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private sealed record Env(DevToolset Toolset, McpToolCallContext Context, Session Session, Project Project,
        PersonaManager Personas, CountingLaunchers Launchers, DevServerService DevServer,
        ProjectServiceDiscovery Discovery) : IDisposable
    {
        public void Dispose() => DevServer.Dispose();
    }

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

    private Env Build(bool withService = true, int ceilingSeconds = TestRunsOptions.DefaultCeilingSeconds)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();
        var (sessions, projects, personas) = TestsToolsetTests.BuildSessionManager(config);
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект со сборкой", dir, TestUserId, TestUsername);
        var session = sessions.CreateAsync(project.Id, ClaudeMode.Auto).GetAwaiter().GetResult();

        var launchers = new CountingLaunchers();
        var options = new TestRunsOptions { CeilingSeconds = ceilingSeconds };
        var pipeline = new PhasePipeline();
        // Реестр «Сервисов» запускает настоящие процессы (стенд-заглушка в тестах стенда);
        // сборка — через считающий лаунчер: до неё тесты стенда доходить не должны
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxPortRangeAdapter(
            new ClaudeHomeServer.Services.Execution.SandboxManager(config,
                NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance));
        var devServer = new DevServerService(projects, new TestSessionBroadcaster(),
            NullLogger<DevServerService>.Instance, TestLauncherFactory.Instance, sandbox,
            new DevServerPortMemory(config, NullLogger<DevServerPortMemory>.Instance));
        var discovery = new ProjectServiceDiscovery(new LaunchConfigService(NullLogger<LaunchConfigService>.Instance),
            NullLogger<ProjectServiceDiscovery>.Instance);
        var toolset = withService
            ? new DevToolset(sessions, projects, personas,
                new DotnetBuildService(launchers, options, pipeline), new NpmBuildService(launchers, options, pipeline),
                devServer: devServer, discovery: discovery, options: options)
            : new DevToolset(sessions, projects, personas);
        var env = new Env(toolset, new McpToolCallContext(TestUserId, session.Id, session.Id), session, project,
            personas, launchers, devServer, discovery);
        _owned.Add(env);
        return env;
    }

    private static Task<McpToolCallResult> Call(Env env, JsonObject? args = null, McpToolCallContext? context = null) =>
        env.Toolset.CallAsync(DevToolset.ToolName, args ?? new JsonObject(), context ?? env.Context, default);

    private static Task<McpToolCallResult> Stand(Env env, JsonObject args, string tool = DevToolset.StartStandName,
        McpToolCallContext? context = null) =>
        env.Toolset.CallAsync(tool, args, context ?? env.Context, default);

    // Фронт с package.json (скрипт build) и node_modules — всё, что npm-движок проверяет до запуска
    private static void Frontend(string root)
    {
        var front = Directory.CreateDirectory(Path.Combine(root, "frontend")).FullName;
        File.WriteAllText(Path.Combine(front, "package.json"), """{"scripts":{"build":"tsc -b && vite build"}}""");
        Directory.CreateDirectory(Path.Combine(front, "node_modules"));
    }

    [Fact]
    public void СвояСессия_ОдинИнструмент_ПостоянныйСостав()
    {
        var env = Build();

        env.Toolset.ToolsFor(env.Context).Select(t => t.Name).Should().Equal("build", "start_stand", "stop_stand");
        env.Toolset.ToolsFor(env.Context).Should().BeSameAs(env.Toolset.ToolsFor(env.Context),
            "состав статичный — не зависит ни от хода, ни от вызова");
        env.Toolset.Name.Should().Be(McpEndpoints.DevName);
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
        result.Text.Should().Be(DevToolset.LocalProjectReason).And.Contain("Bash");
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

    [Fact]
    public async Task ПерсонаБезBash_Отказ()
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
    }

    [Fact]
    public async Task ПодсистемаВыключена_ЧестныйОтказ()
    {
        var env = Build(withService: false);

        var result = await Call(env);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("выключена");
    }

    // Цель едет позиционным аргументом dotnet build: строка с «-» стала бы опцией, «@файл» —
    // response-файлом. Отказ до запуска у обоих видов
    [Theory]
    [InlineData("dotnet", "-p:BaseOutputPath=/tmp/x")]
    [InlineData("dotnet", "@evil.rsp")]
    [InlineData("dotnet", "./-bl:probe.binlog")]
    [InlineData("npm", "--prefix=/tmp")]
    [InlineData("npm", "@scope")]
    public async Task ЦельОпция_Отказ(string kind, string target)
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["kind"] = kind, ["target"] = target });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("«-»", "цель-опцию надо отклонять как опцию, а не как «не найдено»");
        env.Launchers.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("dotnet", "../чужой")]
    [InlineData("npm", "a/../../чужой")]
    public async Task ЦельВнеДерева_Отказ(string kind, string target)
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["kind"] = kind, ["target"] = target });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("за пределы проекта");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task АбсолютнаяЦель_Отказ()
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["target"] = Path.Combine(env.Project.RootPath, "backend") });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("ОТНОСИТЕЛЬНО");
    }

    // Имя скрипта уходит аргументом npm: «&», пробел, «;» и ведущий «-» — отказ до запуска
    [Theory]
    [InlineData("build & calc")]
    [InlineData("build;rm")]
    [InlineData("-v")]
    [InlineData("@x")]
    [InlineData("build --watch")]
    public async Task СкриптНеИмя_Отказ(string script)
    {
        var env = Build();
        Frontend(env.Project.RootPath);

        var result = await Call(env, new JsonObject { ["kind"] = "npm", ["target"] = "frontend", ["script"] = script });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("имя скрипта");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task СкриптУDotnet_Отказ()
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["script"] = "build" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("только для kind=npm");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task НеизвестныйВид_Отказ()
    {
        var env = Build();

        var result = await Call(env, new JsonObject { ["kind"] = "make" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("dotnet или npm");
    }

    [Fact]
    public void ЦельDotnetНеЗадана_ЕдинственноеРешение_ИлиОтказСоСписком()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "tree_" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root, "backend"));
        File.WriteAllText(Path.Combine(root, "backend", "App.slnx"), "");

        DevToolset.TryResolveDotnetTarget(root, null, out var target, out _).Should().BeTrue();
        target.Should().Be("backend/App.slnx");

        File.WriteAllText(Path.Combine(root, "Other.sln"), "");
        DevToolset.TryResolveDotnetTarget(root, null, out _, out var error).Should().BeFalse();
        error.Should().Contain("несколько");
    }

    [Fact]
    public async Task Dotnet_ВсеГейтыПройдены_ДоходитДоСредыПроекта()
    {
        // Контрпример к отказам: с исправными входами вызов доходит до ForProject — значит гейты
        // выше не отказывают «на всякий случай»
        var env = Build();
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "backend"));

        var result = await Call(env, new JsonObject { ["target"] = "backend" });

        env.Launchers.Calls.Should().Be(1);
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Не удалось запустить сборку dotnet");
    }

    [Fact]
    public async Task Npm_ВсеГейтыПройдены_ДоходитДоСредыПроекта()
    {
        var env = Build();
        Frontend(env.Project.RootPath);

        var result = await Call(env, new JsonObject { ["kind"] = "npm", ["target"] = "frontend" });

        env.Launchers.Calls.Should().Be(1);
        result.Text.Should().Contain("Не удалось запустить сборку npm");
    }

    [Fact]
    public void Схема_ВидыИАргументы_Постоянны()
    {
        var schema = DevToolset.Tools.Single(t => t.Name == DevToolset.ToolName).InputSchema["properties"]!.AsObject();

        schema.Select(p => p.Key).Should().BeEquivalentTo("kind", "target", "script");
        schema["kind"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("dotnet", "npm");
    }

    // Замечание Киры (А): пример «backend/App.slnx» модель подставляла буквально
    [Fact]
    public void Схема_ОписаниеЦели_БезВыдуманныхИмён()
    {
        var target = DevToolset.Tools.Single(t => t.Name == DevToolset.ToolName)
            .InputSchema["properties"]!["target"]!["description"]!.GetValue<string>();

        target.Should().NotContain("App.slnx").And.NotContain("например").And.Contain(".slnx");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ИтогСборки_НеудачаПоследнегоЭтапа(int? exitCode, bool failed)
    {
        var result = exitCode is null ? null : new BuildRunResult(null, exitCode, false, false, TimeSpan.Zero, []);

        DevToolset.EndedBadly(result).Should().Be(failed);
        DevToolset.EndedBadly(new BuildRunResult(null, 0, Cancelled: true, false, TimeSpan.Zero, [])).Should().BeTrue();
    }

    // ── Стенд (start_stand / stop_stand) ────────────────────────────────────────────

    // Сервис-заглушка из .claude/launch.json: печатает адрес слушателя теста и живёт ~30 с —
    // как дев-сервер после старта. Настоящий порт держит TcpListener теста
    private static async Task<ProjectServiceInfo> StubService(Env env, int port)
    {
        var line = $"Local: http://localhost:{port}/";
        var (command, args) = OperatingSystem.IsWindows()
            ? ("cmd", new JsonArray("/c", $"echo {line}& ping -n 31 127.0.0.1 >nul"))
            : ("sh", new JsonArray("-c", $"echo '{line}'; sleep 30"));
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, ".claude"));
        File.WriteAllText(Path.Combine(env.Project.RootPath, ".claude", "launch.json"), new JsonObject
        {
            ["configurations"] = new JsonArray(new JsonObject
            {
                ["name"] = "Stub",
                ["runtimeExecutable"] = command,
                ["runtimeArgs"] = args,
            }),
        }.ToJsonString());
        return (await env.Discovery.DiscoverAsync(env.Project)).Single(s => s.Name == "Stub");
    }

    [Fact]
    public async Task StartStand_ПоднятыйСервис_ТотЖеПортБезСборки_StopStandГасит()
    {
        var env = Build();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var svc = await StubService(env, port);
        var started = await env.DevServer.StartAsync(env.Project.Id, TestUserId, svc.Id, svc.Name, svc.Command,
            svc.Args, readyTimeout: TimeSpan.FromSeconds(20));
        started.Success.Should().BeTrue(started.Error);

        var first = await Stand(env, new JsonObject { ["service"] = svc.Id });
        var second = await Stand(env, new JsonObject { ["service"] = svc.Id, ["port"] = 5555 });

        first.IsError.Should().BeFalse(first.Text);
        first.Text.Should().Contain($"http://127.0.0.1:{port}").And.Contain("stop_stand");
        second.Text.Should().Contain($"http://127.0.0.1:{port}", "повторный вызов возвращает тот же стенд")
            .And.Contain("Порт 5555 не применён");
        env.Launchers.Calls.Should().Be(0, "поднятый стенд не пересобирается и не перезапускается");
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().ContainSingle();

        var stop = await Stand(env, new JsonObject { ["service"] = svc.Id }, DevToolset.StopStandName);

        stop.IsError.Should().BeFalse(stop.Text);
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty();
        (await Stand(env, new JsonObject { ["service"] = svc.Id }, DevToolset.StopStandName))
            .Text.Should().Contain("не поднят продуктом");
    }

    // Стенд-заглушка под видом `npm run dev` (сборки перед запуском нет): исполняемый файл с именем
    // npm лежит в проекте и просто живёт ~30 с. Порт держит TcpListener теста, но по HTTP он не
    // отвечает никогда — проба health_path висит
    private static async Task<(ProjectServiceInfo Service, System.Net.Sockets.TcpListener Listener, int Port)>
        SilentNpmStand(Env env)
    {
        var bin = Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "fake-bin")).FullName;
        string npm;
        if (OperatingSystem.IsWindows())
        {
            npm = Path.Combine(bin, "npm.cmd");
            File.WriteAllText(npm, "@ping -n 31 127.0.0.1 >nul\r\n");
        }
        else
        {
            npm = Path.Combine(bin, "npm");
            File.WriteAllText(npm, "#!/bin/sh\nsleep 30\n");
            File.SetUnixFileMode(npm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.CreateDirectory(Path.Combine(env.Project.RootPath, ".claude"));
        File.WriteAllText(Path.Combine(env.Project.RootPath, ".claude", "launch.json"), new JsonObject
        {
            ["configurations"] = new JsonArray(new JsonObject
            {
                ["name"] = "Silent",
                ["runtimeExecutable"] = npm,
                ["runtimeArgs"] = new JsonArray("run", "dev"),
            }),
        }.ToJsonString());
        var svc = (await env.Discovery.DiscoverAsync(env.Project)).Single(s => s.Name == "Silent");
        // Порт стенда — только из 55xx–56xx: берём первый свободный
        for (var port = DevServerLaunchPolicy.AutoPortFirst; port <= DevServerLaunchPolicy.AutoPortLast; port++)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            try { listener.Start(); return (svc, listener, port); }
            catch (System.Net.Sockets.SocketException) { listener.Dispose(); }
        }
        throw new InvalidOperationException("нет свободного порта 55xx–56xx");
    }

    private static async Task WaitStarted(Env env)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!env.DevServer.GetRunning(env.Project.Id, TestUserId).Any(r => r.Status == "started"))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "стенд обязан дойти до пробы health_path");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task StartStand_СтопВоВремяПробыHealth_СтендПогашенТекстом()
    {
        var env = Build();
        var (svc, listener, port) = await SilentNpmStand(env);
        using var _ = listener;
        using var cts = new CancellationTokenSource();

        var call = env.Toolset.CallAsync(DevToolset.StartStandName,
            new JsonObject { ["service"] = svc.Id, ["port"] = port, ["health_path"] = "/health" }, env.Context, cts.Token);
        await WaitStarted(env);
        cts.Cancel();
        var result = await call;

        result.IsError.Should().BeTrue(result.Text);
        result.Text.Should().Contain("остановлен («Стоп»)").And.NotContain("потолок");
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty("стенд, не прошедший пробу, погашен");
    }

    [Fact]
    public async Task StartStand_ПотолокВоВремяПробыHealth_ТекстПроПотолокНеПроСтоп()
    {
        // Потолок 8 с: запуск занимает секунду, проба получает остаток бюджета и упирается в потолок
        var env = Build(ceilingSeconds: 8);
        var (svc, listener, port) = await SilentNpmStand(env);
        using var _ = listener;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Stand(env, new JsonObject { ["service"] = svc.Id, ["port"] = port, ["health_path"] = "/health" });

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "проба входит в бюджет потолка, а не ждёт свои 30 с");
        result.IsError.Should().BeTrue(result.Text);
        result.Text.Should().NotContain("Стоп").And.Match(t => t.Contains("потолок") || t.Contains("не ответил"));
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty();
    }

    [Fact]
    public async Task StartStand_ПерсонаБезBash_Отказ()
    {
        var env = Build();
        var persona = env.Personas.Create(TestUserId, "Аналитик", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, access: PersonaAccess.Custom,
            disallowedTools: ["Bash"]);
        env.Session.PersonaId = persona.Id;

        var start = await Stand(env, new JsonObject { ["service"] = "x" });
        var stop = await Stand(env, new JsonObject { ["service"] = "x" }, DevToolset.StopStandName);

        start.IsError.Should().BeTrue();
        start.Text.Should().Contain("запрещён Bash");
        stop.IsError.Should().BeTrue();
        stop.Text.Should().Contain("запрещён Bash");
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task StartStand_ЧужаяСессия_Отказ()
    {
        var env = Build();
        var foreign = new McpToolCallContext("someone-else", env.Session.Id, env.Session.Id);

        var result = await Stand(env, new JsonObject { ["service"] = "x" }, context: foreign);

        result.IsError.Should().BeTrue();
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task StartStand_ReadOnlyПерсона_Отказ()
    {
        var env = Build();
        var persona = env.Personas.Create(TestUserId, "Ревьюер", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, access: PersonaAccess.ReadOnly);
        env.Session.PersonaId = persona.Id;

        var start = await Stand(env, new JsonObject { ["service"] = "x" });
        var stop = await Stand(env, new JsonObject { ["service"] = "x" }, DevToolset.StopStandName);

        start.IsError.Should().BeTrue();
        start.Text.Should().Contain("Только чтение");
        stop.IsError.Should().BeTrue();
        env.Launchers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task StartStand_ЛокальныйПроект_ОтказПроПанель()
    {
        var env = Build();
        env.Project.DeviceId = "device-1";

        var result = await Stand(env, new JsonObject { ["service"] = "x" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("панелью «Сервисы»");
    }

    [Fact]
    public async Task StartStand_ВертикальВыключена_ЧестныйОтказ()
    {
        var env = Build(withService: false);

        var result = await Stand(env, new JsonObject { ["service"] = "x" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("выключены");
    }

    [Fact]
    public async Task StartStand_НеизвестныйСервис_ОтказСоСписком()
    {
        var env = Build();
        await StubService(env, 5590);

        var result = await Stand(env, new JsonObject { ["service"] = "nope" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("не найден").And.Contain("Доступные сервисы").And.Contain("Stub");
        env.Launchers.Calls.Should().Be(0);
    }

    // Порт от модели: бой, Dify и всё вне 55xx–56xx — отказ до сборки; путь пробы — только путь
    [Theory]
    [InlineData("port", 8080, "запрещён")]
    [InlineData("port", 80, "запрещён")]
    [InlineData("port", 5000, "вне диапазона")]
    [InlineData("health_path", "//evil.example/x", "health_path")]
    [InlineData("health_path", "http://evil.example/", "health_path")]
    [InlineData("health_path", "/a b", "health_path")]
    public async Task StartStand_ПлохиеАргументы_ОтказДоСборки(string name, object value, string expected)
    {
        var env = Build();
        var svc = await StubService(env, 5590);
        var args = new JsonObject { ["service"] = svc.Id };
        args[name] = value is int number ? JsonValue.Create(number) : JsonValue.Create((string)value);

        var result = await Stand(env, args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain(expected);
        env.Launchers.Calls.Should().Be(0);
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../чужой/App.csproj", "за пределы проекта")]
    [InlineData("-p:x=1.csproj", "«-»")]
    [InlineData("backend/Нет.csproj", "не найден")]
    public async Task StartStand_ПутьКПроекту_ПроверяетсяВнутриДерева(string service, string expected)
    {
        var env = Build();

        var result = await Stand(env, new JsonObject { ["service"] = service });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain(expected);
        env.Launchers.Calls.Should().Be(0);
    }

    // Не dotnet run и не скрипт npm — сборку продукт им не сделает: отказ с подсказкой про панель
    [Fact]
    public async Task StartStand_СервисБезСборки_ОтказПроПанель()
    {
        var env = Build();
        var svc = await StubService(env, 5590);

        var result = await Stand(env, new JsonObject { ["service"] = svc.Id });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("панелью «Сервисы»");
        env.Launchers.Calls.Should().Be(0);
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty();
    }

    // Контрпример к отказам: dotnet-проект из дерева доходит до сборки в среде проекта
    [Fact]
    public async Task StartStand_DotnetПроект_ДоходитДоСборки()
    {
        var env = Build();
        var app = Directory.CreateDirectory(Path.Combine(env.Project.RootPath, "backend", "App")).FullName;
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        var result = await Stand(env, new JsonObject { ["service"] = "backend/App/App.csproj" });

        env.Launchers.Calls.Should().Be(1, "сборка идёт через среду проекта");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Не удалось поднять стенд");
        env.DevServer.GetRunning(env.Project.Id, TestUserId).Should().BeEmpty("до запуска стенда не дошло");
    }

    private static ProjectServiceInfo Svc(string command, params string[] args) =>
        new("svc", "Svc", "test", command, args, null, null, false, false);

    [Fact]
    public void План_DotnetRun_СборкаЦелиИЗапускБезСборки()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "tree_" + Guid.NewGuid().ToString("N"))).FullName;
        var app = Directory.CreateDirectory(Path.Combine(root, "backend", "App")).FullName;
        File.WriteAllText(Path.Combine(app, "App.csproj"), "");

        DevStand.TryPlan(Svc("dotnet", "run", "--project", "backend/App/App.csproj", "--launch-profile", "http"),
            root, out var plan, out var error).Should().BeTrue(error);

        plan.Args.Should().Equal("run", "--no-build", "--project", "backend/App/App.csproj", "--launch-profile", "http");
        plan.Build.Should().Be(new DevStand.StandBuild(false, "backend/App/App.csproj"));
    }

    [Fact]
    public void План_Npm_ДевСерверБезСборки_PreviewСоСборкой()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "tree_" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root, "frontend"));

        DevStand.TryPlan(Svc("npm", "run", "dev") with { Cwd = "frontend" }, root, out var dev, out _).Should().BeTrue();
        DevStand.TryPlan(Svc("npm", "run", "preview") with { Cwd = "frontend" }, root, out var preview, out _).Should().BeTrue();

        dev.Build.Should().BeNull("дев-сервер собирает сам");
        dev.Args.Should().Equal("run", "dev");
        preview.Build.Should().Be(new DevStand.StandBuild(true, "frontend"));
    }

    [Theory]
    [InlineData("dotnet", "watch")]
    [InlineData("docker", "compose")]
    [InlineData("make", "dev")]
    [InlineData("npm", "run", "x y")]
    public void План_НеподдержанныйЗапуск_Отказ(string command, params string[] args)
    {
        DevStand.TryPlan(Svc(command, args), _tempDir, out _, out var error).Should().BeFalse();
        error.Should().Contain("панелью «Сервисы»");
    }

    [Fact]
    public void IdСтенда_ИзWorktree_СвояЗапись_ИзКорня_ТотЖеСервис()
    {
        var project = Path.Combine(_tempDir, "proj");
        var tree = Path.Combine(_tempDir, "wt", "branch");

        DevStand.StandId("svc", project, project).Should().Be("svc");
        DevStand.StandId("svc", project + Path.DirectorySeparatorChar, project).Should().Be("svc");
        var fromTree = DevStand.StandId("svc", tree, project);
        fromTree.Should().StartWith("svc-wt-").And.HaveLength("svc-wt-".Length + 6);
        DevStand.StandId("svc", tree, project).Should().Be(fromTree, "id стабилен между вызовами");
    }
}
