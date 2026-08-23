using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сценарное правило выбора codegraph / LSP / Grep в системном промпте хода (ADR-011 шаг 3):
// секция "code-navigation" добавляется при том же гейте, что slice графа (CodeGraphProvider
// не null — проектный чат с включённым codegraph), но, в отличие от slice, НЕ зависит от
// построения графа. Ключевой кейс: свежий проект без graph.json — slice нет, правило есть;
// иначе модель не узнала бы про LSP ровно так же, как до ADR-011 (8 вызовов за 14 дней).
// Проверяем фактический аргумент --append-system-prompt, ушедший бы модели: гоняем
// настоящую ClaudeSession с fake-CLI launcher (паттерн ClaudeSessionPromptSectionsOrderTests).
public class ClaudeSessionCodeNavigationPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-code-nav-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _argsCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClaudeSessionCodeNavigationPromptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Захватывает args первого старта процесса, дальше держит фейковый CLI живым (молчуном) —
    // ход сам по себе тесту не нужен, только аргументы запуска
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
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
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

    private async Task<string> RunTurnCapturePromptAsync(Func<string?, Task<string?>>? codeGraphProvider)
    {
        var messages = new List<ServerMessage>();
        var info = new Session();
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            CodeGraphProvider: codeGraphProvider,
            Launcher: new CapturingLauncher(_clis, _argsCaptured));

        var session = new ClaudeSession(info, context);
        await using var _ = session;

        await session.SendMessageAsync("привет");

        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0, "с провайдером навигации аргумент обязан присутствовать");
        return args[idx + 1];
    }

    [Fact]
    public async Task ПравилоНавигации_ЕстьПриНепостроенномГрафе_ВФактическомАргументеХода()
    {
        // Провайдер задан, но граф не построен — slice (null) в промпт не попадает,
        // а правило обязано доехать: оно статично и не зависит от graph.json
        var prompt = await RunTurnCapturePromptAsync(_ => Task.FromResult<string?>(null));

        prompt.Should().Contain("три уровня, не взаимозаменяемы");
        prompt.Should().Contain("codegraph_hubs");
        prompt.Should().Contain("codegraph_neighbors");
        prompt.Should().Contain("codegraph_find");
        prompt.Should().Contain("goToDefinition");
        prompt.Should().Contain("findReferences");
        // Момент выбора, а не перечень возможностей — формулировка ADR-011
        prompt.Should().Contain("сначала LSP findReferences, а не Grep");
        // Slice нет — данные графа не приехали, правило живёт само по себе
        prompt.Should().NotContain("Структура кода проекта", "граф не построен — slice быть не должно");
    }

    [Fact]
    public async Task ПравилоНавигации_ЕстьИПриПостроенномГрафе_РядомСSlice()
    {
        var prompt = await RunTurnCapturePromptAsync(
            _ => Task.FromResult<string?>("## Структура кода проекта (Code Graph)\nМАРКЕР_SLICE"));

        var sliceIdx = prompt.IndexOf("МАРКЕР_SLICE", StringComparison.Ordinal);
        var ruleIdx = prompt.IndexOf("три уровня, не взаимозаменяемы", StringComparison.Ordinal);
        sliceIdx.Should().BeGreaterThanOrEqualTo(0);
        ruleIdx.Should().BeGreaterThanOrEqualTo(0);
        sliceIdx.Should().BeLessThan(ruleIdx, "правило встаёт следом за данными графа");
    }

    [Fact]
    public async Task БезПровайдераГрафа_ПравилаНавигацииНет()
    {
        // Чат вне проекта / codegraph выключен off-привязкой: правило советовало бы
        // инструменты, которых в этом ходе нет
        var prompt = await RunTurnCapturePromptAsync(null);

        prompt.Should().NotContain("три уровня, не взаимозаменяемы",
            "без CodeGraphProvider правила быть не должно");
    }

    private static async Task<IReadOnlyList<string>> WhenAnyAsync(
        Task<IReadOnlyList<string>> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, "не дождались старта fake-CLI процесса с захватом args хода");
        return await task;
    }
}
