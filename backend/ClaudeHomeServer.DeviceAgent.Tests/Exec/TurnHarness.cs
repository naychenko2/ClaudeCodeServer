using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>Стенд одного хода: каталоги устройства, исполнитель, тестовый сервер канала.</summary>
internal sealed class TurnHarness : IAsyncDisposable
{
    public const string SidecarUrl = "http://127.0.0.1:65000";
    public const string TurnToken = "turn-token-SECRET-4f1c2b";

    /// <summary>Секреты в окружении агента: ни один не должен дойти до CLI.</summary>
    public static readonly IReadOnlyDictionary<string, string> AgentSecrets = new Dictionary<string, string>
    {
        ["ANTHROPIC_API_KEY"] = "sk-ant-api-SECRET-1",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat-SECRET-2",
        ["ANTHROPIC_BASE_URL"] = "https://evil.example/SECRET-3",
        ["AWS_SECRET_ACCESS_KEY"] = "aws-SECRET-4",
        ["HTTP_PROXY"] = "http://corp-proxy.example:3128",
        ["GITHUB_TOKEN"] = "ghp_SECRET5",
    };

    public TurnHarness(string cliPath, string? leaseProblem = null, Func<TurnLaunch, TurnProcess>? launcher = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "agent-turn-" + Guid.NewGuid().ToString("N")[..10]);
        WorkDir = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
        Profile = Path.Combine(Root, "profile");
        TurnsRoot = Path.Combine(Root, "turns");
        Cli = new TestCliSource(leaseProblem is null ? cliPath : null, leaseProblem);
        Journal = new TurnJournal(Path.Combine(Root, "journal"));

        var inherited = new Dictionary<string, string>(AgentSecrets)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = Root,
            ["LANG"] = "C.UTF-8",
        };
        Executor = new TurnExecutor(new ExecOptions
        {
            TurnsRoot = TurnsRoot,
            ConfigDirectory = Profile,
            SidecarUrl = () => SidecarUrl,
            InheritedEnvironment = () => inherited,
            DrainTimeout = TimeSpan.FromSeconds(10),
            Launcher = launcher ?? TurnProcess.Start,
        }, Cli, Grants, Journal, new ListLogger<TurnExecutor>(Logs));
    }

    public string Root { get; }
    public string WorkDir { get; }
    public string Profile { get; }
    public string TurnsRoot { get; }
    public TestCliSource Cli { get; }
    public TurnGrants Grants { get; } = new();
    public TurnJournal Journal { get; }
    public TurnExecutor Executor { get; }
    public ConcurrentQueue<string> Logs { get; } = new();
    public ExecTestServer Server { get; } = new();
    public ExecLink? Link { get; private set; }
    public Task? Run { get; private set; }

    public DeviceExecSpawn Spawn(IReadOnlyList<string>? args = null, IReadOnlyList<DeviceExecFile>? files = null,
        IReadOnlyDictionary<string, string>? env = null, string fileName = "claude") =>
        new(fileName, args ?? ["-p", "--output-format", "stream-json"], WorkDir,
            env ?? new Dictionary<string, string>(), files ?? [], RedirectStdin: true);

    public async Task StartAsync(DeviceExecSpawn spawn, string turnId = "turn1", DeviceExecGateway? grant = null, TimeSpan? maxOutage = null)
    {
        Link = new ExecLink("exec-" + turnId, Server, maxOutage ?? TimeSpan.FromSeconds(20));
        await Link.StartAsync(CancellationToken.None);
        Run = Executor.RunAsync(Link, CancellationToken.None);
        await Server.SendControlAsync(new DeviceExecControl(DeviceExecControlOps.Spawn, turnId, spawn,
            grant ?? new DeviceExecGateway("gw-turn-1", TurnToken)));
    }

    public Task SendStdinAsync(string text) =>
        Server.SendAsync(DeviceExecFrameChannel.Stdin, Encoding.UTF8.GetBytes(text));

    /// <summary>Ждёт строку в stdout, копя всё прочитанное.</summary>
    public async Task<string> WaitStdoutAsync(string marker, StringBuilder into, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var frame in Server.Received.ReadAllAsync(cts.Token))
        {
            if (frame.Channel == DeviceExecFrameChannel.Stdout) into.Append(Encoding.UTF8.GetString(frame.Payload.Span));
            if (into.ToString().Contains(marker)) return into.ToString();
            if (frame.Channel == DeviceExecFrameChannel.Exit)
                throw new InvalidOperationException($"ход кончился, не дождавшись «{marker}»: {into}");
        }
        throw new TimeoutException(marker);
    }

    public static string Text(IEnumerable<DeviceExecFrame> frames, DeviceExecFrameChannel channel) =>
        string.Concat(frames.Where(f => f.Channel == channel).Select(f => Encoding.UTF8.GetString(f.Payload.Span)));

    public static DeviceExecExit ExitOf(IEnumerable<DeviceExecFrame> frames) =>
        DeviceExecJson.Deserialize<DeviceExecExit>(frames.Single(f => f.Channel == DeviceExecFrameChannel.Exit).Payload.Span)!;

    public int ReadPid(string file) => int.Parse(File.ReadAllText(Path.Combine(WorkDir, file)).Trim());

    public async ValueTask DisposeAsync()
    {
        Executor.KillAll();
        if (Run is not null) await Run.WaitAsync(TimeSpan.FromSeconds(20));
        await Server.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ListLogger<T>(ConcurrentQueue<string> sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        sink.Enqueue(formatter(state, exception) + (exception is null ? "" : " | " + exception));
}
