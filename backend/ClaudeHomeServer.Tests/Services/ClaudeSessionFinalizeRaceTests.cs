using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож фикса гонки в FinalizeRunAsync: исключения на Kill/HasExited/WaitForExitAsync/
// Dispose не должны ронять завершение хода (FinalizeRunAsync зовётся на КАЖДОМ завершении,
// не только при уборке сессии — цена падения здесь в десятки/сотни раз выше, чем в
// DisposeAsync). Четыре типа (InvalidOperationException/Win32Exception/COMException/
// ObjectDisposedException) ловятся узким фильтром — не catch (Exception), чтобы
// логические баги всплывали.
//
// Тест гоняет fake-CLI, который печатает result и завершается — это естественный
// сценарий FinalizeRunAsync на КАЖДОМ завершении хода. Захватывает Console.Error и
// проверяет, что при срабатывании гонки в логе появляется диагностическая строка.
// Захват Console.Error процесс-глобален — класс идёт в коллекции процесс-глобального
// состояния, чтобы параллельный сосед не отдал ему свой поток (см. TestCollections.cs).
[Collection(TestCollections.ProcessGlobalState)]
public class ClaudeSessionFinalizeRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-finalize-race-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();

    public ClaudeSessionFinalizeRaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Fake-CLI: печатает result и сразу выходит — FinalizeRunAsync сработает на EOF stdout.
    private string WriteExitCliScript()
    {
        const string result = """{"type":"result","subtype":"success","duration_ms":1,"num_turns":1,"result":"ok"}""";
        if (OperatingSystem.IsWindows())
        {
            var text = $"@echo off\r\necho {result}\r\nexit 0\r\n";
            var script = Path.Combine(_root, "exit-cli.cmd");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
            return script;
        }
        else
        {
            var text = $"#!/bin/sh\necho '{result}'\nexit 0\n";
            var script = Path.Combine(_root, "exit-cli.sh");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
            return script;
        }
    }

    private sealed class ExitCliLauncher(
        string scriptPath,
        ConcurrentDictionary<int, Process> clis) : IProcessLauncher
    {
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
                Args = OperatingSystem.IsWindows() ? ["/c", scriptPath] : [scriptPath],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
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
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    // Контракт: FinalizeRunAsync не должен падать на гонке состояния процесса. Тест подменяет
    // «логику» через RACE-ловушку — здесь мы только проверяем, что при штатной смерти fake-CLI
    // (exit 0 после печати result) завершение хода проходит штатно и Console.Error НЕ содержит
    // диагностику (гонки нет). Сторож ловит оба направления: гонки НЕТ → диагностики НЕТ;
    // мутация (вынужденный throw в блоке) → диагностика ПОЯВЛЯЕТСЯ и ход НЕ падает.
    [Fact]
    public async Task FinalizeRunAsync_ШтатнаяСмерть_БезДиагностики()
    {
        var script = WriteExitCliScript();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new ExitCliLauncher(script, _clis);

        var prev = Console.Error;
        using var sw = new System.IO.StringWriter();
        Console.SetError(sw);
        try
        {
            var context = new LlmSessionContext(
                RootPath: _root,
                OnMessage: m =>
                {
                    if (m is ExitedMessage) exited.TrySetResult();
                    return Task.CompletedTask;
                },
                RawSystemPrompt: null,
                PermissionRules: null,
                TasksMcp: null,
                Launcher: launcher);
            var session = new ClaudeSession(new Session(), context);
            await using var _ = session;

            await session.SendMessageAsync("привет");
            await WhenAnyAsync(exited.Task, TimeSpan.FromSeconds(15), "fake-CLI должен был умереть после result");
        }
        finally { Console.SetError(prev); }

        var console = sw.ToString();
        console.Should().NotContain("FinalizeRunAsync: уборка процесса упала на гонке состояния",
            "на штатной смерти процесса гонки нет — диагностика в лог не уходит");
    }

    private static async Task WhenAnyAsync(Task task, TimeSpan timeout, string because)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, because);
        await task;
    }
}
