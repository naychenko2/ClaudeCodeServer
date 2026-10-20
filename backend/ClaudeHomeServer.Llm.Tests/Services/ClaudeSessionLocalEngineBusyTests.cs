using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Второе звено правила «фон не лезет в занятый локальный движок»: признак занятости обязан
// ставить САМ ход. Решение (LocalActionRouter.LocalBlockedByTurn) и исполнение
// (CheapTextRunner) проверяет LocalEngineBusyGateTests — здесь сторож на то, что признак
// вообще появляется: без него правило зелёное в тестах и мёртвое в бою.
//
// Трекер у каждого прогона свой (через LlmSessionContext.LocalEngineBusy): процесс-глобальный
// Instance делили бы параллельные тестовые классы, которые тоже гоняют ходы на локальном
// провайдере.
//
// Платформонезависимость (CI — ubuntu): «CLI» — cmd/sh-сон без вывода, как в
// ClaudeSessionProxyBypassEnvTests.
public class ClaudeSessionLocalEngineBusyTests : IDisposable
{
    private const string LocalModelId = "qwen3.8-27b";
    private const string CloudModelId = "deepseek-v4-pro";
    private readonly List<Process> _processes = [];

    private static LlmProviderRegistry Providers() => new(Helpers.TestConfig.Build(
        new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18021",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:Models:0:Id"] = LocalModelId,
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = CloudModelId,
        }));

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
    }

    // «CLI», который стартовал и молчит: ход живёт, пока процесс не убьют.
    private sealed class SleepingLauncher(List<Process> processes, TaskCompletionSource started)
        : IProcessLauncher
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
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
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

    // Гоняет один ход указанной моделью и возвращает снимок признака ВО ВРЕМЯ хода
    // и после его конца.
    private async Task<(bool DuringTurn, bool AfterTurn)> RunTurnAsync(string model)
    {
        var busy = new LocalEngineBusyTracker();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-local-busy-tests");
        Directory.CreateDirectory(root);

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
            Launcher: new SleepingLauncher(_processes, started),
            LocalEngineBusy: busy);
        var session = new ClaudeSession(new Session { Model = model }, context, providers: Providers());

        await session.SendMessageAsync("тестовый ход");
        var startedFirst = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        startedFirst.Should().Be(started.Task, "процесс хода обязан стартовать");
        var during = busy.Busy;

        // Останавливаем сессию целиком, а не убиваем «CLI»: смерть процесса без единого события
        // — это штатный DiedEmpty-перезапуск хода новым процессом (ход продолжается, и движок
        // законно остаётся занятым). Отмена сессии завершает тело хода — ровно тот выход, на
        // котором обязан сняться признак.
        await session.DisposeAsync();
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (busy.Busy && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        return (during, busy.Busy);
    }

    /// <summary>
    /// Ход на ЛОКАЛЬНОМ провайдере (IsLocal) помечает движок занятым на всё своё время —
    /// именно по этому признаку фоновые one-shot действия уходят мимо локали (живой прогон
    /// 2026-09-23: 14 фоновых запросов на 213k токенов параллельно с ходом исполнителя).
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальномПровайдере_ПомечаетДвигательЗанятым()
    {
        var (during, after) = await RunTurnAsync(LocalModelId);

        during.Should().BeTrue("пока идёт ход на локали, фону туда нельзя");
        after.Should().BeFalse("по выходу из хода признак обязан сняться, иначе фон уедет в облако навсегда");
    }

    /// <summary>
    /// Ход на облачном провайдере локальный движок не занимает: фоновые действия владельцев,
    /// работающих не на локали, правило не касается вовсе.
    /// </summary>
    [Fact]
    public async Task ХодНаОблачномПровайдере_ДвигательСвободен()
    {
        var (during, after) = await RunTurnAsync(CloudModelId);

        during.Should().BeFalse("чужой эндпоинт KV-бюджет локального движка не занимает");
        after.Should().BeFalse();
    }
}
