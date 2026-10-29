using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// CLI 2.1.300: флаги в сигнатуре запуска, решаемые по СЕССИИ (не по ходу):
//   • --restricted (CLI ≥ 2.1.248) — персоны «Только чтение» (консультанты): убирает
//     встроенные инструменты запуска команд и WebFetch, держит файловые инструменты внутри
//     рабочего каталога, отказывает bypassPermissions (fail-closed на стороне CLI поверх
//     disallowed-инструментов PersonaAccessPolicy);
//   • --permission-prompts НЕ ставим никогда, в том числе ходам без человека (исполнитель
//     задачи, автоматика): права таких ходов решает сервер через --permission-prompt-tool stdio
//     (DecidePermissionAsync), а флаг CLI перекрывает этот канал и отказывает сам. Тесты ниже —
//     сторож от повторного внесения.
//
// Сквозной BuildArgs (ArgsCapturingLauncher по образцу ClaudeSessionEffortArgsTests):
// реальный набор флагов, уходящих в процесс хода. Версия CLI — через шов
// ClaudeSession.CliVersionProbeForTests (по умолчанию реальный ClaudeCliVersion), чтобы
// гейт по версии был детерминирован.
public class ClaudeSessionRestrictedArgsTests : IDisposable
{
    private readonly List<Process> _processes = [];

    // «CLI», который стартовал и молчит: захватывает args реального хода, реальную команду
    // подменяет сном.
    private sealed class ArgsCapturingLauncher(List<Process> processes, TaskCompletionSource started) : IProcessLauncher
    {
        public IReadOnlyList<string>? CapturedArgs { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedArgs = spec.Args?.ToArray();

            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "exec sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

        public int EstimateCommandLineLength(ProcessSpec spec)
        {
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
        }
    }

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            try { p.Dispose(); } catch (NullReferenceException) { } // Process.Close гонится с уборкой сессии
        }
        // Шов версии процесс-глобален: сбрасываем, чтобы параллельные классы не видели нашу
        // фиксированную версию (всё равно никто не ассертит на версию-зависимые аргументы).
        ClaudeSession.CliVersionProbeForTests = null;
    }

    // Запустить ход и вернуть собранные аргументы. personaAccess — профиль доступа персоны
    // (ReadOnly → --restricted), origin — происхождение чата (Manual/Task/Automation),
    // cliVersion — версия CLI хоста (гейт по порогу).
    private async Task<IReadOnlyList<string>> RunTurnAsync(
        PersonaAccess personaAccess, ChatOrigin origin, string? cliVersion)
    {
        ClaudeSession.CliVersionProbeForTests = () => cliVersion;

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-restricted-args-tests");
        Directory.CreateDirectory(root);

        var launcher = new ArgsCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                if (m is ExitedMessage) exited.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null,
            BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            PersonaProvider: () => new Persona { Access = personaAccess },
            Launcher: launcher);

        var session = new Session
        {
            PersonaId = "test-persona",
            TaskId = origin == ChatOrigin.Task ? "task-1" : null,
            AutomationRuleId = origin == ChatOrigin.Automation ? "rule-1" : null,
        };
        var cs = new ClaudeSession(session, context);

        try
        {
            await cs.SendMessageAsync("тестовый ход");
            var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            done.Should().Be(started.Task, "процесс хода обязан стартовать");
            launcher.CapturedArgs.Should().NotBeNull("args хода собираются до запуска процесса");

            Process cli;
            lock (_processes) cli = _processes[^1];
            cli.Kill();
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            return launcher.CapturedArgs!;
        }
        finally
        {
            ClaudeSession.CliVersionProbeForTests = null;
        }
    }

    // --- консультант: --restricted ---

    [Fact]
    public async Task Консультант_СвежийCli_СтавитRestricted()
    {
        var args = await RunTurnAsync(PersonaAccess.ReadOnly, ChatOrigin.Manual, "2.1.300");

        args.Should().Contain("--restricted",
            "ReadOnly-персона на CLI ≥ 2.1.248 получает --restricted");
        args.Should().NotContain("--permission-prompts",
            "--permission-prompts не ставится никому");
    }

    // --- ходы без человека: права решает сервер по stdio, --permission-prompts нет ---

    private const string NoPermissionPromptsReason =
        "права ходов без человека решает сервер через stdio-канал (DecidePermissionAsync), " +
        "а --permission-prompts перекрывает его: CLI сам отказывает в подтверждаемом " +
        "(tasks_complete, chats_report_up и пр.)";

    [Fact]
    public async Task ИсполнительЗадачи_СвежийCli_БезPermissionPrompts_СоStdioКаналом()
    {
        var args = await RunTurnAsync(PersonaAccess.Full, ChatOrigin.Task, "2.1.300");

        args.Should().NotContain("--permission-prompts", NoPermissionPromptsReason);
        AssertStdioPermissionTool(args);
        args.Should().NotContain("--restricted",
            "Full-персона (не ReadOnly) --restricted не получает");
    }

    [Fact]
    public async Task Автоматика_СвежийCli_БезPermissionPrompts_СоStdioКаналом()
    {
        var args = await RunTurnAsync(PersonaAccess.Full, ChatOrigin.Automation, "2.1.300");

        args.Should().NotContain("--permission-prompts", NoPermissionPromptsReason);
        AssertStdioPermissionTool(args);
    }

    private static void AssertStdioPermissionTool(IReadOnlyList<string> args)
    {
        var arr = args.ToArray();
        var idx = Array.IndexOf(arr, "--permission-prompt-tool");
        idx.Should().BeGreaterOrEqualTo(0, "запросы прав обязаны идти на сервер по stdio-каналу");
        idx.Should().BeLessThan(arr.Length - 1, "за флагом должно следовать значение");
        arr[idx + 1].Should().Be("stdio");
    }

    // --- обычный чат: ни одного флага ---

    [Fact]
    public async Task ОбычныйЧат_СвежийCli_ОбаФлагаОтсутствуют()
    {
        var args = await RunTurnAsync(PersonaAccess.Full, ChatOrigin.Manual, "2.1.300");

        args.Should().NotContain("--restricted", "Full-персона (не ReadOnly) --restricted не получает");
        args.Should().NotContain("--permission-prompts", "--permission-prompts не ставится никому");
    }

    // --- гейт по версии: старый CLI флага не получает ---

    [Fact]
    public async Task Консультант_СтарыйCli_НиRestricted()
    {
        // 2.1.247 < 2.1.248: --restricted ещё не существует, ставить его — ронять запуск
        var args = await RunTurnAsync(PersonaAccess.ReadOnly, ChatOrigin.Manual, "2.1.247");

        args.Should().NotContain("--restricted", "CLI 2.1.247 не знает --restricted");
    }

    [Fact]
    public async Task НеизвестнаяВерсия_ОбаФлагаОтсутствуют()
    {
        // null — опроса CLI ещё не было: fail-safe, не ставим флаг, который CLI может не понять
        var args = await RunTurnAsync(PersonaAccess.ReadOnly, ChatOrigin.Task, null);

        args.Should().NotContain("--restricted", "неизвестная версия — не ставим --restricted");
        args.Should().NotContain("--permission-prompts", "--permission-prompts не ставится никому");
    }

    // --- чистая функция сравнения версий ---

    [Theory]
    [InlineData("2.1.248", 2, 1, 248, true)]
    [InlineData("2.1.247", 2, 1, 248, false)]
    [InlineData("2.1.259", 2, 1, 259, true)]
    [InlineData("2.1.258", 2, 1, 259, false)]
    [InlineData("2.1.300", 2, 1, 248, true)]
    [InlineData("3.0.0", 2, 1, 248, true)]
    [InlineData(null, 2, 1, 248, false)]
    [InlineData("garbage", 2, 1, 248, false)]
    public void CliAtLeast_ГраничныеСлучаи(string? version, int major, int minor, int patch, bool expected)
    {
        ClaudeSession.CliAtLeast(version, major, minor, patch).Should().Be(expected);
    }
}
