using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож проводки правила NO_PROXY (ADR-012, находка консилиума по 3b764c58): LoopbackProxyBypass.
// ForTurn — это спецификация, а здесь проверяется, что ClaudeSession действительно зовёт её с
// флагом СРЕДЫ владельца и ставит оверрайд только в local-ветке. Лаунчер подменяет «CLI» на
// платформенный сон и захватывает spec.Env реального хода — иначе регрессия «прочитали env
// хоста до ветвления по IsSandboxed» живёт в коде незаметно.
//
// Платформонезависимость (CI — ubuntu): «CLI» — cmd/sh-сон без вывода, смерть без событий —
// легитимный случай (см. ClaudeSessionProcessDeathTests).
public class ClaudeSessionProxyBypassEnvTests : IDisposable
{
    private const string Sentinel = "ccs-no-proxy-sentinel.example";
    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
    }

    // «CLI», который стартовал и молчит: захватывает env хода, реальную команду подменяет сном
    private sealed class EnvCapturingLauncher(List<Process> processes, TaskCompletionSource started,
        bool sandboxed) : IProcessLauncher
    {
        public Dictionary<string, string>? CapturedEnv { get; private set; }
        public bool IsSandboxed => sandboxed;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedEnv = spec.Env is null ? null : new Dictionary<string, string>(spec.Env);

            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
        }
    }

    private async Task<Dictionary<string, string>?> RunTurnAsync(bool sandboxed, bool useHttp)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-proxy-env-tests");
        Directory.CreateDirectory(root);

        var launcher = new EnvCapturingLauncher(_processes, started, sandboxed);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                if (m is ExitedMessage) exited.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", "tok", UseHttp: useHttp),
            // Как в SessionManager: сводный признак http-серверов хода решается там по
            // UseHttp-контекстам (HttpMcpActive) — здесь эмулируем его тем же значением
            HttpMcpActive: useHttp,
            Launcher: launcher);
        var session = new ClaudeSession(new Session(), context);

        var prev = Environment.GetEnvironmentVariable("NO_PROXY");
        Environment.SetEnvironmentVariable("NO_PROXY", Sentinel);
        try
        {
            await session.SendMessageAsync("тестовый ход");
            var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            done.Should().Be(started.Task, "процесс хода обязан стартовать");
            launcher.CapturedEnv.Should().NotBeNull("env хода собирается до запуска процесса");

            // Убираем за собой: глушим «CLI» и дожидаемся терминала хода — читать stdout
            // убитого процесса после выхода из теста уже некому
            Process cli;
            lock (_processes) cli = _processes[^1];
            cli.Kill();
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            return launcher.CapturedEnv;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_PROXY", prev);
        }
    }

    /// <summary>
    /// БЛОКЕР консилиума №1, сквозной: ход container-владельца НЕ получает хостовой NO_PROXY —
    /// ни значения-предохранителя, ни каких-либо других ключей прокси-обхода. exec-переменная
    /// сильнее контейнерной, и любой оверрайд здесь подменял бы egress-whitelist песочницы.
    /// </summary>
    [Fact]
    public async Task ХодВПесочнице_ХостовойNO_PROXY_НеПопадаетВОкружение()
    {
        var env = await RunTurnAsync(sandboxed: true, useHttp: true);

        env!.ContainsKey("NO_PROXY").Should().BeFalse("средой exec-процесса владеет контейнер");
        env.ContainsKey("no_proxy").Should().BeFalse("нижняя форма — тоже");
        env.Values.Should().NotContain(v => v.Contains(Sentinel),
            "хостовые корпоративные исключения не доезжают в песочницу");
    }

    /// <summary>
    /// Local-владелец с http-транспортом: оверрайд стоит, унаследованное ДОПОЛНЯЕТСЯ локальными
    /// адресами и хостом эндпоинта (Merge-правило; HTTP_PROXY бывает единственным маршрутом
    /// до провайдеров, его исключения затирать нельзя).
    /// </summary>
    [Fact]
    public async Task ХодLocalВладельца_ОверрайдДополняетУнаследованное()
    {
        var env = await RunTurnAsync(sandboxed: false, useHttp: true);

        var noProxy = env!["NO_PROXY"].Split(',');
        noProxy.Should().Contain(Sentinel).And.Contain("localhost").And.Contain("127.0.0.1");
        env["no_proxy"].Should().Be(env["NO_PROXY"], "обе формы — часть http-клиентов смотрит одну из них");
    }

    /// <summary>
    /// БЛОКЕР консилиума №2, сквозной: рубильник Mcp:HttpTransport=false возвращает stdio —
    /// env-оверрайд откатывается вместе с транспортом, «откат без выкатки кода» полон.
    /// </summary>
    [Fact]
    public async Task ХодСоВыключеннымТранспортом_ОверрайдаНет()
    {
        var env = await RunTurnAsync(sandboxed: false, useHttp: false);

        env!.ContainsKey("NO_PROXY").Should().BeFalse();
        env.ContainsKey("no_proxy").Should().BeFalse();
    }
}
