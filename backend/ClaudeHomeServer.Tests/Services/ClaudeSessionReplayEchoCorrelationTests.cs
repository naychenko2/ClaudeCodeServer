using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Инцидент 03.10.2026: на --resume CLI сначала доигрывает «осиротевший» task-notification прошлой
// сессии и выдаёт на него result (numTurns=0) РАНЬШЕ, чем возьмётся за наше сообщение. Бэкенд
// засчитывал его ходу → TurnDone → CloseStdin, и настоящий ход жил с закрытым stdin («Stream closed»).
// Корень: result принимался без подтверждения, что CLI принял именно наше сообщение. Теперь
// сообщение уходит с uuid, а result'ы до эха (--replay-user-messages, isReplay + тот же uuid)
// считаются чужими. Доступ к приватному состоянию прогона — reflection, как в соседних наборах.
public class ClaudeSessionReplayEchoCorrelationTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private const string Uuid = "11111111-2222-4333-8444-555555555555";

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    private object NewRun(string? pendingEcho = Uuid)
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        CliRunType.GetField("PendingEchoUuid")!.SetValue(run, pendingEcho);
        return run;
    }

    private static T Field<T>(object run, string name) => (T)CliRunType.GetField(name)!.GetValue(run)!;
    private static void SetField(object run, string name, object? v) => CliRunType.GetField(name)!.SetValue(run, v);

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(new Session(), context), sent);
    }

    private static Task Drive(ClaudeSession session, object run, string line) =>
        (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;

    // numTurns=0, но с непустым usage: эвристика IsEmptyNoopResult такой result НЕ отсеивает —
    // ровно тот промах, что дал инцидент.
    private const string OrphanResult =
        """{"type":"result","subtype":"success","duration_ms":3,"num_turns":0,"usage":{"input_tokens":5,"output_tokens":2},"session_id":"s"}""";
    private const string RealResult =
        """{"type":"result","subtype":"success","duration_ms":9,"num_turns":1,"result":"ок","usage":{"input_tokens":5,"output_tokens":2}}""";
    private static readonly string Echo =
        $$"""{"type":"user","message":{"role":"user","content":"Ну как?"},"uuid":"{{Uuid}}","isReplay":true}""";
    private const string MainStreamEvent =
        """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"т"}}}""";
    private const string ToolUseAssistant =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""";

    [Fact]
    public async Task ResultДоЭха_НеЗавершаетХод_НеЗакрываетStdin()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await Drive(session, run, OrphanResult);

        Field<bool>(run, "TurnDone").Should().BeFalse("result до эха принадлежит осиротевшему notification");
        Field<bool>(run, "StdinClosed").Should().BeFalse();
        sent.OfType<ResultMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ПоследовательностьСиротаЭхоХодResult_ХодЗавершаетсяТолькоСвоимResult()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await Drive(session, run, OrphanResult);
        await Drive(session, run, Echo);
        Field<string?>(run, "PendingEchoUuid").Should().BeNull("эхо подтвердило приём нашего сообщения");

        await Drive(session, run, MainStreamEvent);
        await Drive(session, run, ToolUseAssistant);
        Field<bool>(run, "TurnDone").Should().BeFalse();
        Field<bool>(run, "StdinClosed").Should().BeFalse("инструмент хода не должен упасть в «Stream closed»");

        await Drive(session, run, RealResult);
        Field<bool>(run, "TurnDone").Should().BeTrue();
        sent.OfType<ResultMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task ЧужоеЭхо_ПендингНеСнимается()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        var other = Echo.Replace(Uuid, "99999999-0000-4000-8000-000000000000");

        await Drive(session, run, other);

        Field<string?>(run, "PendingEchoUuid").Should().Be(Uuid);
    }

    [Fact]
    public async Task ResultДоЭха_ГасимОдинПропускНеДваЖды()
    {
        // submit во время continuation: SkipResults=1 и эхо ещё не пришло. Result продолжения
        // приходит до эха — это ОДНО событие, пропуск не должен съесть ещё и result нашего хода.
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        SetField(run, "SkipResults", 1);

        await Drive(session, run, OrphanResult);
        Field<int>(run, "SkipResults").Should().Be(0);

        await Drive(session, run, Echo);
        await Drive(session, run, RealResult);
        Field<bool>(run, "TurnDone").Should().BeTrue("result хода не съеден лишним пропуском");
    }

    [Fact]
    public async Task ПослеЭха_ОстаточныйSkipResultsСбрасывается()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        SetField(run, "SkipResults", 1);

        await Drive(session, run, Echo);

        Field<int>(run, "SkipResults").Should().Be(0);
    }

    [Fact]
    public async Task БезОжиданияЭха_ResultЗасчитываетсяКакРаньше()
    {
        // Ре-аттемпт без submit и прогоны без эха: PendingEchoUuid=null — прежнее поведение
        var (session, _) = NewClaudeSession();
        var run = NewRun(pendingEcho: null);

        await Drive(session, run, RealResult);

        Field<bool>(run, "TurnDone").Should().BeTrue();
    }

    [Fact]
    public async Task ToolUseПриЗакрытомStdinПослеХода_ЭтоАномалия_ГасимПроцессОшибкой()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun(pendingEcho: null);
        SetField(run, "TurnDone", true);
        SetField(run, "StdinClosed", true);

        await Drive(session, run, ToolUseAssistant);
        await Drive(session, run, ToolUseAssistant); // повтор не плодит вторую ошибку

        sent.OfType<ErrorMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task ToolUseПриОткрытомStdin_НеАномалия()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun(pendingEcho: null);
        SetField(run, "TurnDone", true); // доживающий прогон с фоновыми задачами, stdin открыт

        await Drive(session, run, ToolUseAssistant);

        sent.OfType<ErrorMessage>().Should().BeEmpty();
    }

    // ── Фолбэк: CLI не присылает эхо совсем (обновление убрало флаг молча) ──

    private sealed class ErrorCountLogger : ILogger
    {
        public int Errors;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Interlocked.Increment(ref Errors);
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // Живой «CLI», который печатает строки и молчит; никакого эха. Платформонезависимо: echo из
    // script-файла (cmd/sh), содержимое — ASCII.
    private Process StartSilentEchoCli(string[] lines, int delayBeforeLastSec = 0)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-noecho-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var text = "@echo off\r\n"
                + string.Join("\r\n", lines.Select((l, i) =>
                    (i == lines.Length - 1 && delayBeforeLastSec > 0 ? $"ping -n {delayBeforeLastSec + 1} 127.0.0.1 >nul\r\n" : "")
                    + $"echo {l}"))
                + "\r\nping -n 60 127.0.0.1 >nul\r\n";
            var script = Path.Combine(dir, "cli.cmd");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"");
        }
        else
        {
            var text = "#!/bin/sh\n"
                + string.Join("\n", lines.Select((l, i) =>
                    (i == lines.Length - 1 && delayBeforeLastSec > 0 ? $"sleep {delayBeforeLastSec}\n" : "")
                    + $"echo '{l}'"))
                + "\nsleep 60\n";
            var script = Path.Combine(dir, "cli.sh");
            File.WriteAllText(script, text, System.Text.Encoding.ASCII);
            psi = new ProcessStartInfo("/bin/sh", script);
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardInput = true;
        var process = Process.Start(psi)!;
        _fakeProcesses.Add(process);
        return process;
    }

    private async Task<(int Results, int Errors, bool TurnDone)> RunNoEchoScenarioAsync(string[] lines, int delayBeforeLastSec)
    {
        var sent = new List<ServerMessage>();
        var logger = new ErrorCountLogger();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        var session = new ClaudeSession(new Session(), context, sessionLogger: new LoggerAdapter(logger))
        {
            EchoCutoff = TimeSpan.FromMilliseconds(400),
        };
        var process = StartSilentEchoCli(lines, delayBeforeLastSec);
        var run = NewRun();
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetField("EchoDeadlineTick")!.SetValue(run, Environment.TickCount64 + 400);

        using var cts = new CancellationTokenSource();
        var loop = (Task)ReadLoopMethod.Invoke(session, [run, cts.Token])!;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!Field<bool>(run, "TurnDone") && DateTime.UtcNow < deadline) await Task.Delay(50);
        var turnDone = Field<bool>(run, "TurnDone");
        int results;
        lock (sent) results = sent.OfType<ResultMessage>().Count();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));
        return (results, logger.Errors, turnDone);
    }

    private sealed class LoggerAdapter(ILogger inner) : ILogger<ClaudeSession>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static readonly MethodInfo ReadLoopMethod =
        typeof(ClaudeSession).GetMethod("ReadLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task ЭхаНетСовсем_БыстрыйResult_ХодЗавершаетсяСвоимResult_LogErrorОдин()
    {
        // Result приходит до отсечки и глотается как «до эха»; по отсечке он доигрывается как наш
        var (results, errors, turnDone) = await RunNoEchoScenarioAsync(
            [MainStreamEvent, RealResult], delayBeforeLastSec: 0);

        turnDone.Should().BeTrue("без эха ход не должен висеть до IdleTimeout");
        results.Should().Be(1);
        errors.Should().Be(1);
    }

    [Fact]
    public async Task ЭхаНетСовсем_ResultПослеОтсечки_ЗасчитываетсяБезПропуска()
    {
        var (results, errors, turnDone) = await RunNoEchoScenarioAsync(
            [MainStreamEvent, RealResult], delayBeforeLastSec: 2);

        turnDone.Should().BeTrue();
        results.Should().Be(1);
        errors.Should().Be(1);
    }

    [Fact]
    public async Task ЭхоПришлоДоОтсечки_ФолбэкНеВключается()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        await Drive(session, run, Echo);
        await Drive(session, run, RealResult);

        Field<bool>(run, "TurnDone").Should().BeTrue();
        Field<int>(run, "EchoUnsupported").Should().Be(0);
    }
}
