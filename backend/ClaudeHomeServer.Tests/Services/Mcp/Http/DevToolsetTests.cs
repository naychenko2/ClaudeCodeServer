using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.TestRuns;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private sealed record Env(DevToolset Toolset, McpToolCallContext Context, Session Session, Project Project,
        PersonaManager Personas, CountingLaunchers Launchers);

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
        var (sessions, projects, personas) = TestsToolsetTests.BuildSessionManager(config);
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект со сборкой", dir, TestUserId, TestUsername);
        var session = sessions.CreateAsync(project.Id, ClaudeMode.Auto).GetAwaiter().GetResult();

        var launchers = new CountingLaunchers();
        var options = new TestRunsOptions();
        var pipeline = new PhasePipeline();
        var toolset = withService
            ? new DevToolset(sessions, projects, personas,
                new DotnetBuildService(launchers, options, pipeline), new NpmBuildService(launchers, options, pipeline))
            : new DevToolset(sessions, projects, personas);
        return new Env(toolset, new McpToolCallContext(TestUserId, session.Id, session.Id), session, project,
            personas, launchers);
    }

    private static Task<McpToolCallResult> Call(Env env, JsonObject? args = null, McpToolCallContext? context = null) =>
        env.Toolset.CallAsync(DevToolset.ToolName, args ?? new JsonObject(), context ?? env.Context, default);

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

        env.Toolset.ToolsFor(env.Context).Select(t => t.Name).Should().Equal("build");
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
        var schema = DevToolset.Tools.Single().InputSchema["properties"]!.AsObject();

        schema.Select(p => p.Key).Should().BeEquivalentTo("kind", "target", "script");
        schema["kind"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("dotnet", "npm");
    }

    // Замечание Киры (А): пример «backend/App.slnx» модель подставляла буквально
    [Fact]
    public void Схема_ОписаниеЦели_БезВыдуманныхИмён()
    {
        var target = DevToolset.Tools.Single().InputSchema["properties"]!["target"]!["description"]!.GetValue<string>();

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
}
