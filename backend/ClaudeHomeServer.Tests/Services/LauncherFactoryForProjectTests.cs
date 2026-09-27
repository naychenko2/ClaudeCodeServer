using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// ADR-016, задача 3.2: среда исполнения проекта. Серверный проект исполняется как раньше —
// в среде владельца (local/песочница), проект на устройстве — через RemoteProcessRunner.
// Офлайн-устройство — честная ошибка хода с текстом отказа и без фолбэка по цепочке.
public class LauncherFactoryForProjectTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lf_project_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tmp);

    private (LauncherFactory Factory, UserStore Users) Create(IDeviceExecChannel? channel, IDeviceTurnGateway? gateway = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(_tmp, "ClaudeSandbox"),
        }).Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        return (new LauncherFactory(users, sandbox, channel is null ? null : () => channel, () => gateway ?? new FakeDeviceTurnGateway()), users);
    }

    private static string NewOwner(UserStore users, string environment) =>
        users.Add("owner_" + Guid.NewGuid().ToString("N")[..8], "pwd", "user", environment).Id;

    // Канал с устройством в офлайне: отказ ровно формой DeviceExecChannel.OpenAsync
    private sealed class OfflineChannel : IDeviceExecChannel
    {
        public int Opens;

        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) =>
            new(deviceId, "Ноутбук", Online: false, Platform: "linux", AgentVersion: "1", CliVersion: "2",
                RequiredCliVersion: "2", Capabilities: [DeviceCapabilities.Exec], HarnessReady: true, HarnessProblem: null);

        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opens);
            throw new DeviceExecRefusedException(DeviceExecRefusal.Offline, "Устройство «Ноутбук» не в сети — сообщение не взято в работу.");
        }
    }

    [Theory]
    [InlineData(ExecutionEnvironments.Local)]
    [InlineData(ExecutionEnvironments.Container)]
    public void СерверныйПроект_СредаВладельцаКакРаньше(string environment)
    {
        var (factory, users) = Create(new OfflineChannel());
        var owner = NewOwner(users, environment);
        var project = new Project { OwnerId = owner, RootPath = _tmp };

        factory.ForProject(project).Should().BeSameAs(factory.ForOwner(owner),
            "серверный проект исполняется там же, где и до локальных проектов");
    }

    [Fact]
    public void ПроектНаУстройстве_RemoteProcessRunnerЭтогоУстройства()
    {
        var (factory, users) = Create(new OfflineChannel());
        var owner = NewOwner(users, ExecutionEnvironments.Container);
        var project = new Project { OwnerId = owner, DeviceId = "dev-1", RootPath = "/home/me/app" };

        var launcher = factory.ForProject(project);

        launcher.Should().BeOfType<RemoteProcessRunner>().Which.DeviceId.Should().Be("dev-1");
        factory.ForProject(project).Should().BeSameAs(launcher, "раннер устройства кэшируется на пару владелец+устройство");
    }

    [Fact]
    public void ПроектНаУстройстве_БезКаналаУстройств_ОтказПриСтарте_НеСервер()
    {
        var (factory, users) = Create(channel: null);
        var project = new Project { OwnerId = NewOwner(users, ExecutionEnvironments.Local), DeviceId = "dev-1", RootPath = "/app" };

        var launcher = factory.ForProject(project);
        launcher.Should().NotBeSameAs(LocalProcessRunner.Instance, "проект устройства не исполняется на сервере никогда");
        var start = () => launcher.Start(new ProcessSpec { FileName = "claude", WorkingDirectory = "/app", SessionId = "chat-1" });
        start.Should().Throw<DeviceExecRefusedException>().WithMessage("*не найдено*");
    }

    // Главный сценарий офлайна: ход через настоящий ClaudeSession и раннер устройства.
    // Ошибка хода — текст отказа устройства (а не общий «что-то пошло не так»), в Details —
    // маркер для классификатора, result нет (ход не «завершился штатно»), фолбэк не зовётся.
    [Fact]
    public async Task УстройствоОфлайн_ХодЗавершаетсяОшибкойСТекстомОтказа_БезФолбэка()
    {
        var channel = new OfflineChannel();
        var (factory, users) = Create(channel);
        var project = new Project { OwnerId = NewOwner(users, ExecutionEnvironments.Local), DeviceId = "dev-1", RootPath = _tmp };
        Directory.CreateDirectory(_tmp);

        var sent = new List<ServerMessage>();
        var error = new TaskCompletionSource<ErrorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new LlmSessionContext(
            RootPath: _tmp,
            OnMessage: m =>
            {
                lock (sent) sent.Add(m);
                if (m is ErrorMessage e) error.TrySetResult(e);
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: factory.ForProject(project));
        var session = new ClaudeSession(new Session(), context);
        await using var _ = session;

        await session.SendMessageAsync("привет");
        var done = await Task.WhenAny(error.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        done.Should().Be(error.Task, "отказ устройства обязан закончить ход ошибкой");

        var e = await error.Task;
        e.Text.Should().Contain("не в сети");
        e.Details.Should().StartWith(TurnErrorClassifier.DeviceRefusedMarker);
        channel.Opens.Should().Be(1);
        lock (sent) sent.OfType<ResultMessage>().Should().BeEmpty("ход не дошёл до CLI — штатного результата нет");

        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = e.Details })
            .Should().Be(FallbackErrorClass.None, "соседняя пара пошла бы на то же офлайн-устройство");
    }

    [Fact]
    public void ОбрывБезResultБезМаркера_По_прежнемуUnreachable() =>
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = "Устройство «X» офлайн" })
            .Should().Be(FallbackErrorClass.Unreachable, "решает маркер в начале Details, а не русская формулировка");
}
