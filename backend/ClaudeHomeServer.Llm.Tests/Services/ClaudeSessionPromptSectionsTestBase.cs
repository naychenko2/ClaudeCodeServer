using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Общая фикстура четырёх классов-сторожей секций промпта ClaudeSession (разрез 0d1f4bde):
// исходный класс был самой длинной серийной цепочкой Fast-шарда (39 настоящих ClaudeSession
// с sh sleep подряд) и под CPU-голодом становился хвостом шарда. Классы разложены по темам;
// фикстура общая: fake-CLI лаунчер с захватом args, изолированный профиль, уборка процессов
// через ProcessTestCleanup (без неё возвращается NRE-гонка двойного Dispose, b96e1f270).
public abstract class ClaudeSessionPromptSectionsTestBase : IDisposable
{
    protected readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-prompt-order-" + Guid.NewGuid().ToString("N"));
    protected readonly ConcurrentDictionary<int, Process> _clis = new();
    protected readonly TaskCompletionSource<IReadOnlyList<string>> _argsCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected ClaudeSessionPromptSectionsTestBase() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p); // Process.Close гонится с уборкой сессии
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Захватывает args первого старта процесса, дальше держит фейковый CLI живым (молчуном) —
    // ход сам по себе тесту не нужен, только сигнатура запуска
    protected sealed class CapturingLauncher(
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

    // Провайдер урезает набор MCP до tasks, как local-qwen по умолчанию
    protected static readonly Dictionary<string, string?> TrimToTasks = new()
    {
        ["LlmProviders:test-local:TrimMcpServers"] = "true",
        ["LlmProviders:test-local:KeepMcpServers:0"] = "tasks",
    };

    protected static Session ProjectChat() => new() { Model = "qwen-test-27b", OwnerId = "u1", ProjectId = "p1" };

    protected async Task<(string SystemPrompt, IReadOnlyList<PromptSectionDto> Sections)> RunTailTurnAsync(
        bool recallInTurnText, string stateText, Action<TurnEventBus>? extra = null, Session? info = null,
        Func<LlmSessionContext, LlmSessionContext>? tweak = null, Dictionary<string, string?>? providerConfig = null,
        int agentDepth = 0)
    {
        var clis = new ConcurrentDictionary<int, Process>();
        var argsCaptured = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new TaskCompletionSource<IReadOnlyList<PromptSectionDto>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var bus = new TurnEventBus();
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection("dossier-trailer", "МАРКЕР_DOSSIER_TRAILER"));
            e.Sections.Add(new ClaudeHomeServer.Services.Turn.PromptSection(
                "image-editor-state", stateText, "Состояние редактора", InTurnTail: true));
            return next();
        }, "Test.ImageEditorState");
        extra?.Invoke(bus);
        bus.OnNotification<PromptAssembled>(e =>
        {
            if (e.Snapshot?.Draft is { } draft) snapshot.TrySetResult(draft.Sections);
            return Task.CompletedTask;
        }, "Test.Snapshot");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:test-local:DisplayName"] = "Тестовый локальный",
            ["LlmProviders:test-local:AnthropicBaseUrl"] = "http://127.0.0.1:65535",
            ["LlmProviders:test-local:IsLocal"] = "true",
            ["LlmProviders:test-local:RecallInTurnText"] = recallInTurnText ? "true" : "false",
            ["LlmProviders:test-local:Models:0:Id"] = "qwen-test-27b",
            ["LlmProviders:test-local:Models:0:DisplayName"] = "Qwen Test",
        }).AddInMemoryCollection(IsolatedProfiles()).AddInMemoryCollection(providerConfig ?? []).Build();

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: new CapturingLauncher(clis, argsCaptured),
            SystemPromptViaFile: false,
            Events: bus);
        if (tweak is not null) context = tweak(context);

        try
        {
            var session = new ClaudeSession(info ?? new Session { Model = "qwen-test-27b" }, context,
                providers: new LlmProviderRegistry(config));
            await using var _ = session;
            await session.SendMessageAsync("привет", agentDepth: agentDepth);

            // Потолок — лишь страховка от зависания: старт задерживал синк настоящего ~/.claude,
            // с изолированным профилем (IsolatedProfiles) он укладывается в доли секунды
            var args = await WhenAnyAsync(argsCaptured.Task, TimeSpan.FromSeconds(15));
            var idx = args.ToList().IndexOf("--append-system-prompt");
            idx.Should().BeGreaterThanOrEqualTo(0, "стабильная секция непустая — аргумент обязан присутствовать");
            var done = await Task.WhenAny(snapshot.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            done.Should().Be(snapshot.Task, "снимок промпта хода обязан быть опубликован");
            return (args[idx + 1], await snapshot.Task);
        }
        finally
        {
            foreach (var p in clis.Values)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                // Process.Dispose гонится с сессией, которая убирает тот же процесс сама (NRE в Close)
                ProcessTestCleanup.SafeDispose(p);
            }
        }
    }

    // Профили CLI — в папке теста, пользовательский ~/.claude — несуществующий каталог.
    // Без этого каждый новый LlmProviderRegistry на первом ходе синхронно синкал НАСТОЯЩИЙ
    // ~/.claude (десятки тысяч файлов plugins/) в общий профиль bin/data/claude-profiles и
    // переписывал его манифест в несколько МБ — секунды до старта fake-CLI на каждый тест,
    // а под параллельными классами (тот же профиль) старт не укладывался в потолок ожидания
    protected Dictionary<string, string?> IsolatedProfiles() => new()
    {
        ["DataPath"] = Path.Combine(_root, "data", "projects.json"),
        ["ClaudeUserProfileDir"] = Path.Combine(_root, "no-user-claude"),
    };

    protected static async Task<IReadOnlyList<string>> WhenAnyAsync(
        Task<IReadOnlyList<string>> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, "не дождались старта fake-CLI процесса с захватом args хода");
        return await task;
    }
}
