using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Контракт уборки процесса в DisposeAsync (продолжение фикса 7792a003 — там гонку закрыли,
// но сам факт «уборка УБИВАЕТ процесс CLI» не был покрыт: ревьюер доказал мутацией, что без
// `_launcher.Kill(_currentProcess, ...)` в DisposeAsync ВСЕ тесты остаются зелёными). Тест
// фиксирует инвариант: на момент возврата из DisposeAsync процесс CLI завершён (а иначе
// живой дочерний процесс утечёт из сессии в фоне — на Windows это node-MCP-серверы,
// которые без явного Kill остаются сиротами).
//
// Паттерн — тот же CapturingLauncher, что в ClaudeSessionPromptSectionsOrderTests: настоящий
// ClaudeSession с fake-CLI launcher (sleep-процесс на 60 с). Дополнительно тут launcher
// ведёт счётчик вызовов Kill и фиксирует HasExited сразу после Kill: SendMessageAsync
// уходит в фон и не зовёт Kill (процесс живой, никаких смертей/финализаций нет),
// DisposeAsync обязан позвать Kill хотя бы раз — это и есть проверяемый контракт.
public class ClaudeSessionDisposeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-dispose-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();

    public ClaudeSessionDisposeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p); // Process.Close гонится с уборкой сессии
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Launcher считает вызовы Kill, пришедшие из DisposeAsync (по вызывающему кадру стека).
    // Kill финализации хода и отмены токена сессии не считаются. Без Kill в DisposeAsync
    // счётчик 0, и тест роняется — это и есть мутация, которую тест ловит.
    //
    // Выход процесса ловим СОБЫТИЕМ Exited, подписанным при старте, а не синхронным HasExited
    // сразу после Kill: Kill посылает сигнал, а выход фиксируется позже — на загруженном CI
    // чтение HasExited в момент Kill давало плавающий False. После DisposeAsync сам объект
    // уже диспознут, и HasExited бросает InvalidOperationException — событие этого не боится.
    private sealed class CapturingLauncher(
        ConcurrentDictionary<int, Process> clis) : IProcessLauncher
    {
        private int _disposeKillCount;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Только Kill, пришедшие НЕПОСРЕДСТВЕННО из DisposeAsync: Kill финализации хода и отмены
        // (FinalizeRunAsync и др.) в счёт не идут, иначе ассерт не отличал бы их от уборки
        public int DisposeKillCalls => Volatile.Read(ref _disposeKillCount);
        public Task Exited => _exited.Task;

        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 60 127.0.0.1 >nul"]
                    : ["-c", "sleep 60"],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _exited.TrySetResult();
            if (process.HasExited) _exited.TrySetResult();
            clis[clis.Count + 1] = process;
            return process;
        }

                public int EstimateCommandLineLength(ProcessSpec spec)
        {
            // Заглушка для фейков: тесты, которые гоняют ClaudeSession.ApplyBudget,
            // нуждаются в числовом ответе, но не в точной семантике раннера (её
            // проверяет DockerProcessRunnerCmdlineEstimationTests на реальном раннере).
            // Считаем FileName + args через TurnPromptAssembler.ArgCost — та же формула,
            // что в LocalProcessRunner.EstimateCommandLineLength, без RawArguments.
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }
        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
            // Кадр 0 — этот Kill, кадр 1 — его вызывающий; у async-метода это MoveNext
            // конечного автомата с типом «<DisposeAsync>d__N»
            var caller = new StackTrace(1, false).GetFrame(0)?.GetMethod()?.DeclaringType?.Name;
            if (caller is not null && caller.Contains("<DisposeAsync>"))
                Interlocked.Increment(ref _disposeKillCount);
        }
    }

    // Инвариант: после DisposeAsync уборка убила процесс CLI (Kill + WaitForExitAsync).
    // Мутация «убрать `_launcher.Kill(_currentProcess, ...)` из DisposeAsync» — тест
    // должен упасть на ассерте DisposeKillCalls == 1.
    [Fact]
    public async Task DisposeAsync_УбиваетПроцессCLI()
    {
        var launcher = new CapturingLauncher(_clis);

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: launcher);

        var session = new ClaudeSession(new Session(), context);

        // SendMessageAsync уходит в фон: процесс CLI жив, никакой финализации ещё не было —
        // Kill-счётчик в launcher строго нулевой до уборки
        await session.SendMessageAsync("привет");

        Process cli;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!_clis.TryGetValue(1, out cli!) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        cli.Should().NotBeNull("fake-CLI должен быть запущен в течение 15 с после SendMessageAsync");
        cli!.HasExited.Should().BeFalse("сразу после Start процесс CLI ещё жив (sleep/ping на 60 с)");

        // Уборка: обязана позвать Kill на _currentProcess
        await session.DisposeAsync();

        // 1. Kill был вызван именно уборкой: счётчик ведёт только вызовы из DisposeAsync
        launcher.DisposeKillCalls.Should().Be(1,
            "DisposeAsync обязан звать _launcher.Kill — иначе процесс CLI переживёт сессию и утечёт в фоне (на Windows это node-MCP-серверы, остаются сиротами на сутки)");
        // 2. Процесс реально завершился. Фейк живёт 60 с — заметно дольше 10-секундного
        // ожидания выхода в уборке: без Kill он переживает DisposeAsync, и событие не приходит.
        // Ожидание событийное: Delay — только потолок таймаута, а не время ожидания. На
        // загруженном раннере выход процесса приходил позже 5 с (флак 0805d3fa) — тест про то,
        // что процесс в итоге умирает, а не про скорость уборки.
        (await Task.WhenAny(launcher.Exited, Task.Delay(TimeSpan.FromSeconds(30))))
            .Should().Be(launcher.Exited,
                "уборка должна не просто позвать Kill, а довести процесс до выхода");
    }
}
