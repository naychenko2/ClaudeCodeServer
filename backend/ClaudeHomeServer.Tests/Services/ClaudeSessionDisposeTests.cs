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
// ClaudeSession с fake-CLI launcher (sleep-процесс на 10 с). Дополнительно тут launcher
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
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Launcher считает вызовы Kill: снимок ДО DisposeAsync хранится в Start, после уборки
    // разница и есть «сколько раз уборка позвала Kill». Без Kill в DisposeAsync разница 0,
    // и тест роняется — это и есть мутация, которую тест ловит.
    //
    // HasExited фиксируем СРАЗУ ПОСЛЕ Kill: после DisposeAsync процесс уже диспознут (Kill +
    // WaitForExitAsync + Dispose в одной цепочке), и Process.HasExited бросает
    // InvalidOperationException «No process is associated with this object». На момент Kill
    // объект ещё жив (Dispose приходит следом) — успеваем прочитать реальное состояние.
    private sealed class CapturingLauncher(
        ConcurrentDictionary<int, Process> clis) : IProcessLauncher
    {
        private int _killCount;
        private int _killCountAtStart;
        private bool _observedExitedAtKill;

        public int KillCallsSinceStart => _killCount - _killCountAtStart;
        public bool ObservedExitedAtKill => _observedExitedAtKill;

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
                    ? ["/c", "ping -n 10 127.0.0.1 >nul"]
                    : ["-c", "sleep 10"],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            // Фиксируем снимок счётчика Kill на момент старта хода — после уборки разница
            // покажет «сколько Kill пришло из уборки, а не откуда-то ещё»
            _killCountAtStart = _killCount;
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
            try { _observedExitedAtKill = process.HasExited; }
            catch { /* process уже освобождён — состояние не зафиксировать */ }
            _killCount++;
        }
    }

    // Инвариант: после DisposeAsync уборка убила процесс CLI (Kill + WaitForExitAsync).
    // Мутация «убрать `_launcher.Kill(_currentProcess, ...)` из DisposeAsync» — тест
    // должен упасть на ассерте KillCallsSinceStart == 0.
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
        cli!.HasExited.Should().BeFalse("сразу после Start процесс CLI ещё жив (sleep/ping на 10 с)");

        // Уборка: обязана позвать Kill на _currentProcess
        await session.DisposeAsync();

        // 1. Kill был вызван уборкой (а не где-то ещё)
        launcher.KillCallsSinceStart.Should().BeGreaterThanOrEqualTo(1,
            "DisposeAsync обязан звать _launcher.Kill — иначе процесс CLI переживёт сессию и утечёт в фоне (на Windows это node-MCP-серверы, остаются сиротами на сутки)");
        // 2. На момент Kill процесс реально был убит — без Kill он бы ещё ~10 с спал.
        // HasExited проверяем в момент Kill внутри launcher (объект ещё жив, Dispose приходит следом).
        launcher.ObservedExitedAtKill.Should().BeTrue(
            "уборка должна не просто позвать Kill, а довести процесс до выхода — на момент Kill объект жив, читаем HasExited синхронно");
    }
}
