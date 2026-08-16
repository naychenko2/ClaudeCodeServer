using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// DiedEmpty-ретрай same-process хода (инцидент 16.08.2026): прогон умирает сразу после
// submit без единого события — RunTurnAsync молча перезапускает ход НОВЫМ процессом с ТЕМИ
// ЖЕ args. До фикса temp MCP-конфиг хода удалялся ДО ожидания конца хода, и ретрай стартовал
// CLI с --mcp-config на уже несуществующий файл: «Invalid MCP configuration: config file
// not found», мгновенный exit=1, ход гиб молча (лог прода server-20260816.log). Фикс: конфиг
// удаляется только в ветке штатного завершения same-process хода (DiedEmpty=false), а
// DiedEmpty-ветка оставляет файл новому процессу — приберёт его финализация нового прогона
// (run.TurnMcpPath); осиротевший в крайнем случае добьёт уборщик Program.cs (старше 6 часов).
//
// Сценарий на живых процессах (паттерн ClaudeSessionProcessDeathTests):
//   ход 1 → P1 (script-CLI печатает task_started + result и доживает: фоновая задача держит
//   прогон) → ход 2 уходит same-process submit'ом в stdin P1 → тест убивает P1 (пустая
//   смерть без событий) → ретрай стартует P2 → В МОМЕНТ СТАРТА P2 temp MCP-конфиг хода
//   обязан существовать → P2 убивается, ход 2 завершается видимой ошибкой, не тишиной.
//
// Платформонезависимость (CI — ubuntu, разработка — Windows): строки stream-json печатает
// echo из script-файла (cmd/sh) — содержимое чисто ASCII, экранирование аргументов не
// участвует; в backend-CI нет pwsh/node (см. ClaudeSessionBadUtf8StreamTests).
public class ClaudeSessionDiedEmptyRetryTests : IDisposable
{
    private static readonly FieldInfo RunField =
        typeof(ClaudeSession).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo LastSubmittedField =
        typeof(ClaudeSession).GetField("_lastSubmittedTurnText", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo TurnDoneField =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!
            .GetField("TurnDone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-diedempty-retry-tests");
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    // Номер запуска → (--mcp-config из args, существует ли файл на момент старта)
    private readonly ConcurrentDictionary<int, (string? Path, bool Exists)> _mcpChecks = new();
    private readonly TaskCompletionSource _p1Started = NewTcs();
    private readonly TaskCompletionSource _p2Started = NewTcs();
    private string? _firstCliScript;

    public ClaudeSessionDiedEmptyRetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // «CLI» первого хода: печатает task_started (взводит фоновую задачу — прогон доживает)
    // и result (завершает ход), затем молчит до убийства. Второй и далее — просто молчат.
    private string WriteFirstCliScript()
    {
        var lines = new[]
        {
            """{"type":"system","subtype":"task_started","task_id":"bg-1","tool_use_id":"toolu-bg-1","description":"bg","subagent_type":"general-purpose","task_type":"local_agent","prompt":"p"}""",
            """{"type":"result","subtype":"success","duration_ms":1,"num_turns":1,"result":"ok"}""",
        };
        string script;
        if (OperatingSystem.IsWindows())
        {
            var text = "@echo off\r\n"
                + string.Join("\r\n", lines.Select(l => $"echo {l}"))
                + "\r\nping -n 120 127.0.0.1 >nul\r\n";
            script = Path.Combine(_root, "fake-cli.cmd");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
        }
        else
        {
            var text = "#!/bin/sh\n"
                + string.Join("\n", lines.Select(l => $"echo '{l}'"))
                + "\nsleep 120\n";
            script = Path.Combine(_root, "fake-cli.sh");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
        }
        return script;
    }

    private sealed class RetryCliLauncher(
        string firstCliScript,
        ConcurrentDictionary<int, Process> clis,
        ConcurrentDictionary<int, (string? Path, bool Exists)> mcpChecks,
        TaskCompletionSource p1Started, TaskCompletionSource p2Started) : IProcessLauncher
    {
        private int _starts;

        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            var n = Interlocked.Increment(ref _starts);
            // Главный замер теста: путь --mcp-config обязан существовать на момент старта
            // процесса — иначе CLI падает «Invalid MCP configuration» и ход гибнет молча
            string? mcpPath = null;
            for (var i = 0; i + 1 < spec.Args.Count; i++)
                if (spec.Args[i] == "--mcp-config") { mcpPath = spec.Args[i + 1]; break; }
            mcpChecks[n] = (mcpPath, mcpPath is null || File.Exists(mcpPath));

            // Первый старт — script-CLI (печатает события хода и доживает), ретрай — молчун
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = n == 1
                    ? (OperatingSystem.IsWindows()
                        ? ["/c", firstCliScript]
                        : [firstCliScript])
                    : (OperatingSystem.IsWindows()
                        ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                        : ["-c", "sleep 120"]),
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[n] = process;
            if (n == 1) p1Started.TrySetResult(); else p2Started.TrySetResult();
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
        }
    }

    [Fact]
    public async Task DiedEmptyРетрай_СтартуетНовыйПроцессСЖивымMcpКонфигом()
    {
        var messages = new List<ServerMessage>();
        var turn2ErrorSeen = NewTcs();
        var turn2ExitedSeen = NewTcs();

        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m =>
            {
                lock (messages) messages.Add(m);
                if (m is ErrorMessage) turn2ErrorSeen.TrySetResult();
                if (m is ExitedMessage) turn2ExitedSeen.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            // Один http-сервер реестра: BuildTurnMcpConfig обязан собрать temp-конфиг хода
            ExternalMcpProvider: () => new ExternalMcpContext(
            [
                new ExternalMcpServer(
                    Key: "test-http", Transport: "http", Command: null, Args: [],
                    Env: new Dictionary<string, string>(), Url: "https://example.invalid/mcp",
                    Headers: new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 1),
            ]),
            Launcher: new RetryCliLauncher(_firstCliScript = WriteFirstCliScript(),
                _clis, _mcpChecks, _p1Started, _p2Started));
        var session = new ClaudeSession(new Session(), context);

        // Ход 1: P1 печатает result и доживает с фоновой задачей — ход завершён, прогон жив
        // (await на CompletedTask — ход исполняется в фоне QueueTurnAsync)
        await session.SendMessageAsync("первый ход");
        await WhenAnyAsync(_p1Started.Task, TimeSpan.FromSeconds(15), "старт первого процесса", messages);
        await UntilAsync(() => RunOf(session) is not null, TimeSpan.FromSeconds(5), "регистрация прогона");
        var run1 = RunOf(session)!;
        // Ждём именно флаг прогона (а не ResultMessage в sink): событие клиенту уходит ДО
        // выставления TurnDone — чтение по приходу сообщения было бы гонкой
        await UntilAsync(() => TurnDoneOf(run1), TimeSpan.FromSeconds(30), "завершение первого хода result'ом");
        TurnDoneOf(run1).Should().BeTrue("ход 1 завершён result'ом при живом прогоне (окно same-process)");

        // Ход 2 уходит same-process submit'ом в stdin живого P1 (тот же прогон, та же сигнатура)
        await session.SendMessageAsync("второй ход");
        await UntilAsync(
            () => LastSubmittedOf(session) == "второй ход" && !TurnDoneOf(run1),
            TimeSpan.FromSeconds(10), "same-process submit второго хода");

        // Пустая смерть P1 сразу после submit (TOCTOU): ни одного события хода не пришло —
        // это ровно сценарий DiedEmpty-ретрая
        _clis[1].Kill(entireProcessTree: true);

        // Ретрай: новый процесс стартует с ТЕМИ ЖЕ args — его --mcp-config обязан существовать
        await WhenAnyAsync(_p2Started.Task, TimeSpan.FromSeconds(30), "старт ретрай-процесса", messages);
        _mcpChecks.TryGetValue(2, out var check).Should().BeTrue("ретрай обязан стартовать второй процесс");
        check.Path.Should().NotBeNullOrEmpty(
            "контекст теста задаёт внешний MCP-сервер — temp MCP-конфиг хода обязан собираться");
        check.Exists.Should().BeTrue(
            "temp MCP-конфиг обязан существовать на момент старта ретрай-процесса: до фикса его " +
            "удаляли до ожидания конца same-process хода, CLI умирал с «Invalid MCP " +
            "configuration: config file not found», и ход погибал молча");

        // Ход 2 обязан завершиться видимой ошибкой, а не тишиной: убиваем P2 (ретрай
        // единственный, пустая смерть нового прогона уходит наружу как Unreachable)
        _clis[2].Kill(entireProcessTree: true);
        await WhenAnyAsync(Task.WhenAll(turn2ErrorSeen.Task, turn2ExitedSeen.Task),
            TimeSpan.FromSeconds(30), "ErrorMessage + ExitedMessage второго хода", messages);
    }

    private static object? RunOf(ClaudeSession session) => RunField.GetValue(session);

    private static bool TurnDoneOf(object run) => (bool)TurnDoneField.GetValue(run)!;

    private static string? LastSubmittedOf(ClaudeSession session) =>
        (string?)LastSubmittedField.GetValue(session);

    // Ожидание с дампом накопленных сообщений при провале: причина «тишины» сразу видна
    // в ассерте (тот же приём, что ClaudeSessionProcessDeathTests)
    private static async Task WhenAnyAsync(Task task, TimeSpan timeout, string what, List<ServerMessage> messages)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        if (done == task) return;
        string dump;
        lock (messages)
        {
            dump = string.Join(" | ", messages.Select(m =>
                m switch
                {
                    ErrorMessage e => $"Error: {e.Text}",
                    ResultMessage => "Result",
                    ExitedMessage => "Exited",
                    _ => m.GetType().Name,
                }));
        }
        done.Should().Be(task, $"не дождались: {what}; сообщения: [{dump}]");
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        condition().Should().BeTrue($"не дождались: {what}");
    }
}
