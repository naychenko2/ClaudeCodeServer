using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Инцидент 15.08.2026 (чат «Создание иконки для дизайн-системы»): процесс CLI погибал
// посреди хода, а клиент не видел ни result, ни error — чат замолкал при статусе Working.
// Сквозной тест на живом процессе: ход уходит «CLI» (фейковый лаунчер подменяет команду на
// платформенный сон), процесс убивается — клиент обязан получить ErrorMessage (видимый error
// в ленте; SessionManager переводит его в статус Error) и ExitedMessage (терминал хода,
// спиннер не залипает). Порядок обработчиков смерти (событие ОС Exited vs EOF в ридере) —
// гонка, поэтому независимо от того, кто первый, ErrorMessage приходит ровно один:
// HandleProcessExitedAsync шлёт его сам, а FinalizeRunAsync дублирует только если
// DeathDiagnosed ещё не выставлен.
//
// Платформонезависимость (CI — ubuntu, разработка — Windows): «CLI» — это cmd/sh-сон без
// вывода; событие хода (stream-json) в тесте не эмулируется, смерть БЕЗ событий — легитимный
// случай активной смерти (RetryOnEmptyExit=false у нового процесса, ретрая нет).
public class ClaudeSessionProcessDeathTests : IDisposable
{
    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
    }

    // «CLI», который стартовал и молчит: stdin не читает (запись хода колосится в буфере пайпа),
    // stdout держит открытым — активный ход «исполняется», пока тест не убьёт процесс
    private sealed class SleepingCliLauncher(List<Process> processes, TaskCompletionSource started)
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
            // Команду подменяем целиком (args настоящего claude не важны), настройки пайпов —
            // как у реального запуска: ClaudeSession пишет ход в stdin и читает stream-json
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

    [Fact]
    public async Task УбитыйПосредиХодаПроцесс_ДаетErrorВЛентеИТерминал()
    {
        var messages = new List<ServerMessage>();
        var errorSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Рабочий каталог процесса обязан существовать — иначе Start падает «неверно задано имя папки»
        var root = Path.Combine(Path.GetTempPath(), "ccs-cli-death-tests");
        Directory.CreateDirectory(root);

        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                lock (messages) messages.Add(m);
                if (m is ErrorMessage) errorSeen.TrySetResult();
                if (m is ExitedMessage) exitedSeen.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: new SleepingCliLauncher(_processes, started));
        var session = new ClaudeSession(new Session(), context);

        // Ход уходит молчащему процессу; как только он стартовал и RunTurnAsync довёл его
        // до регистрации прогона (_run), убиваем — эмуляция гибели CLI посреди хода
        await session.SendMessageAsync("тестовый ход");
        await WhenAnyAsync(started.Task, TimeSpan.FromSeconds(15), "старт процесса", messages);
        await UntilAsync(() => RunOf(session) is not null, TimeSpan.FromSeconds(5), "регистрация прогона");

        Process cli;
        lock (_processes) cli = _processes[^1];
        cli.Kill();

        // Смерть обязана доехать до клиента: error в ленте + терминал хода (не тишина Working)
        await WhenAnyAsync(Task.WhenAll(errorSeen.Task, exitedSeen.Task), TimeSpan.FromSeconds(30),
            "ErrorMessage + ExitedMessage после смерти процесса", messages);

        lock (messages)
        {
            messages.OfType<ErrorMessage>().Should().ContainSingle(
                "гибель процесса посреди хода — видимая ошибка, а не молчание");
            messages.OfType<ExitedMessage>().Should().ContainSingle("терминал хода снимает спиннер");
        }
    }

    private static object? RunOf(ClaudeSession session) =>
        typeof(ClaudeSession).GetField("_run", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(session);

    // Ожидание с дампом накопленных сообщений при провале: причина «тишины» сразу видна
    // в ассерте (например, исключение RunTurnAsync уходит ErrorMessage в этот же sink)
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
