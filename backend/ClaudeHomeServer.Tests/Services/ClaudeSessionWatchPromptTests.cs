using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Обе ветки флага chat-watchdogs для промпта хода (шаг 4 плана): флаг выключен —
// контекста нет, секции «долгое ожидание → watch_start» в --append-system-prompt нет;
// включён (контекст приезжает) — секция клеится. Паттерн — как у
// ClaudeSessionPromptSectionsOrderTests: настоящий ClaudeSession с fake-CLI launcher,
// читаем фактический аргумент, ушедший бы модели.
public class ClaudeSessionWatchPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-watch-prompt-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _argsCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClaudeSessionWatchPromptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Захватывает args старта процесса и держит фейковый CLI живым (молчуном)
    private sealed class CapturingLauncher(
        ConcurrentDictionary<int, Process> clis,
        TaskCompletionSource<IReadOnlyList<string>> argsCaptured) : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            argsCaptured.TrySetResult(spec.Args);
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    private async Task<string> PromptOfTurnAsync(WatchMcpContext? watch, Func<bool>? httpEnabled = null)
    {
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, PermissionRules: null,
            TasksMcp: null,
            WatchMcp: watch,
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            HttpMcpEnabledProvider: httpEnabled);
        var session = new ClaudeSession(new Session(), context);
        await using var _ = session;

        await session.SendMessageAsync("привет");
        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        // Базовые секции (проектные правила) клеятся всегда — аргумент обязан присутствовать
        idx.Should().BeGreaterThanOrEqualTo(0);
        return args[idx + 1];
    }

    [Fact]
    public async Task ФлагВключен_СекцияСторожейКлеитсяВПромпт()
    {
        var prompt = await PromptOfTurnAsync(
            new WatchMcpContext("http://localhost:5000", () => "tok-W", UseHttp: true));
        prompt.Should().Contain(ClaudeHomeServer.Services.Prompts.WatchPrompts.SectionText,
            "включённый флаг доставляет контекст — секция обязана доехать до модели");
    }

    [Fact]
    public async Task ФлагВыключен_СекцииСторожейНет()
    {
        // Флаг выключен = SessionManager не строит контекст вовсе (BuildWatchContext → null)
        var prompt = await PromptOfTurnAsync(watch: null);
        prompt.Should().NotContainAny(["watch_start", "Серверные сторожа"],
            "флаг выключен — секции и намёков на watch-инструменты быть не должно");
    }

    [Fact]
    public async Task UseHttpОтключён_СекцииСторожейНет()
    {
        // Схема адреса не допускает http (UseHttp=false): узла servers["watch"] в ходу
        // не будет — секция обязана молчать (блокер ревью: рассинхрон давал бы модели
        // «No such tool available»)
        var prompt = await PromptOfTurnAsync(
            new WatchMcpContext("http://localhost:5000", () => "tok-W", UseHttp: false));
        prompt.Should().NotContainAny(["watch_start", "Серверные сторожа"],
            "тулсета нет в ходу — обучающая секция вводила бы модель в ошибку");
    }

    [Fact]
    public async Task РубильникHttpВыключен_СекцииСторожейНет()
    {
        // Живой рубильник Mcp:HttpTransport выключен: условие подключения одно на узел и
        // секцию (WatchHttpOn) — тулсета нет, секции тоже нет
        var prompt = await PromptOfTurnAsync(
            new WatchMcpContext("http://localhost:5000", () => "tok-W", UseHttp: true),
            httpEnabled: () => false);
        prompt.Should().NotContainAny(["watch_start", "Серверные сторожа"],
            "тулсета нет в ходу — обучающая секция вводила бы модель в ошибку");
    }

    [Fact]
    public void СекцияДиктуетФорматPollКоманды_БезВложенныхОбёрток()
    {
        // Дефект 01.09: модель обернула poll-команду в powershell — вложенные кавычки
        // в cmd /c разваливались в строковый литерал (эхо тела, exit 0 = ложный fired).
        // Секция обязана диктовать синтаксис ОБОЛОЧКИ владельца и запрещать обёртки
        ClaudeHomeServer.Services.Prompts.WatchPrompts.SectionText.Should()
            .Contain("cmd").And.Contain("bash")
            .And.Contain("powershell", "запрет обёрток должен быть назван прямо")
            .And.Contain("if exist");
    }

    private static async Task<IReadOnlyList<string>> WhenAnyAsync(
        Task<IReadOnlyList<string>> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, "не дождались старта fake-CLI процесса с захватом args хода");
        return await task;
    }
}
