using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// «Стоп» из трея рук (ADR-016 §7): агент гасит дерево хода и везёт причину в кадре Exit.
// Раньше сервер видел только внезапную смерть CLI — фолбэк классифицировал её как обрыв
// (ProcessGone → Unreachable) и запускал следующую модель цепочки: человек не мог остановить
// ИИ, управляющий его машиной. Теперь раннер устройства по причине в кадре Exit прерывает ход
// тем же путём, что веб-«Стоп», и строго раньше, чем ретранслятор узнает код выхода.
public class HandsTrayStopFallbackTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class RecordingStop(Action<string>? then = null) : IHumanTurnStop
    {
        public ConcurrentQueue<string> Sessions { get; } = new();
        public Func<bool>? RelayExitedAtCall;
        public ConcurrentQueue<bool> ExitedFlags { get; } = new();

        public void StoppedByHuman(string sessionId)
        {
            Sessions.Enqueue(sessionId);
            if (RelayExitedAtCall is { } f) ExitedFlags.Enqueue(f());
            then?.Invoke(sessionId);
        }
    }

    private static ProcessSpec Spec(string turnId) => new()
    {
        FileName = "claude",
        Args = ["--print", "--output-format", "stream-json", "--input-format", "stream-json"],
        WorkingDirectory = "/home/device-user/project",
        RedirectStdin = true,
        StdioEncoding = new UTF8Encoding(false),
        EnableRaisingEvents = true,
        TurnId = turnId,
        SessionId = "chat-1",
    };

    // Сценарий агента на одно исполнение: строка init, затем исход попытки
    private static Func<InProcessExecStream, Task> Script(Func<InProcessExecStream, ValueTask> outcome) => async s =>
    {
        await s.FromServer.ReadAsync();
        await s.DeviceSendLineAsync("""{"type":"system","subtype":"init"}""");
        await outcome(s);
    };

    private static readonly Func<InProcessExecStream, ValueTask> TrayStop =
        s => s.DeviceExitAsync(null, "SIGKILL", HandsEndReason.StoppedFromTray);

    private static readonly Func<InProcessExecStream, ValueTask> Crash = s => s.DeviceExitAsync(null, "SIGKILL");

    private static readonly Func<InProcessExecStream, ValueTask> Overloaded429 = async s =>
    {
        await s.DeviceSendLineAsync("""{"type":"result","api_error_status":"429"}""");
        await s.DeviceExitAsync(1);
    };

    private static readonly Func<InProcessExecStream, ValueTask> Ok = async s =>
    {
        await s.DeviceSendLineAsync("""{"type":"result","subtype":"success"}""");
        await s.DeviceExitAsync(0);
    };

    // --- раннер устройства ---

    [Theory]
    [InlineData(HandsEndReason.StoppedFromTray, true)]
    [InlineData(HandsEndReason.AgentStopping, false)]
    [InlineData(null, false)]
    public async Task КадрExit_СтопЧеловека_ПрерываетХодДоВыходаРетранслятора(string? stoppedBy, bool human)
    {
        var channel = new InProcessDeviceExecChannel { Agent = Script(s => s.DeviceExitAsync(null, "SIGKILL", stoppedBy)) };
        var stop = new RecordingStop();
        var runner = new RemoteProcessRunner(channel, new FakeDeviceTurnGateway(), "owner-tray", "dev-1", humanStop: stop);

        var relay = runner.Start(Spec("turn-tray-" + (stoppedBy ?? "none")));
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Exited += (_, _) => exited.TrySetResult();
        stop.RelayExitedAtCall = () => exited.Task.IsCompleted || relay.HasExited;
        await exited.Task.WaitAsync(Wait);

        if (human)
        {
            stop.Sessions.Should().Equal(["chat-1"], "чат берётся из spec сервера");
            stop.ExitedFlags.Should().Equal([false], "прерывание обязано опередить смерть ретранслятора");
        }
        else
        {
            stop.Sessions.Should().BeEmpty("остановка агентом или сам конец CLI — не «Стоп» человека");
        }
    }

    // --- раннер устройства + фолбэк ---

    // Внутренний адаптер вместо ClaudeSession: каждая попытка — настоящий запуск через раннер
    // устройства, смерть ретранслятора — ExitedMessage, Interrupt — Kill по TurnId
    private sealed class RelayInner(Session info, RemoteProcessRunner runner) : ILlmSessionAdapter
    {
        private Process? _relay;
        private string? _turnId;

        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public int Attempts;
        public int Interrupts;

        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
        public int CurrentTurnAgentDepth => 0;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool IsContinuationInFlight => false;
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
            var seq = ++SubmittedTurnSeq;
            var n = Interlocked.Increment(ref Attempts);
            _turnId = "turn-attempt-" + n;
            var relay = runner.Start(Spec(_turnId));
            _relay = relay;
            relay.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (line.Contains("\"429\"")) Emit(new ResultMessage("success", 1, 1, null, null, ApiErrorStatus: "429"));
                else if (line.Contains("\"success\"")) Emit(new ResultMessage("success", 1, 1, null, null));
            };
            relay.Exited += (_, _) =>
            {
                relay.WaitForExit(); // дочитать stdout до exited — как финализация ClaudeSession
                Emit(new ExitedMessage(seq));
            };
            relay.BeginOutputReadLine();
            return Task.CompletedTask;
        }

        private void Emit(ServerMessage msg) => Sink?.Invoke(msg).GetAwaiter().GetResult();

        public void Interrupt()
        {
            Interlocked.Increment(ref Interrupts);
            if (_relay is { } p) runner.Kill(p, _turnId);
        }

        public Task StartAsync() => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Rig(FallbackLlmSessionAdapter Sut, RelayInner Inner, List<ServerMessage> Downstream, RecordingStop Stop);

    private static Rig Build(string owner, params Func<InProcessExecStream, ValueTask>[] attempts)
    {
        var scripts = new ConcurrentQueue<Func<InProcessExecStream, ValueTask>>(attempts);
        var channel = new InProcessDeviceExecChannel
        {
            Agent = s => Script(scripts.TryDequeue(out var next) ? next : Ok)(s),
        };
        FallbackLlmSessionAdapter? sut = null;
        // Боевой HumanTurnStop ведёт в SessionManager.Interrupt → Interrupt адаптера сессии;
        // сам этот переход проверяет SessionManagerTests (Стоп_ВЧатеИВТрееРук_…)
        var stop = new RecordingStop(_ => sut!.Interrupt());
        var runner = new RemoteProcessRunner(channel, new FakeDeviceTurnGateway(), owner, "dev-1", humanStop: stop);

        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClaudeSubscriptions:acc-a:OAuthToken"] = "token-a",
            ["ClaudeSubscriptions:acc-b:OAuthToken"] = "token-b",
        }).Build());
        var session = new Session { Model = "sonnet", Provider = "acc-a" };
        var inner = new RelayInner(session, runner);
        var downstream = new List<ServerMessage>();
        sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
            msg => { lock (downstream) downstream.Add(msg); return Task.CompletedTask; },
            pool, providers: null, Path.GetTempPath(), launcher: null, initialProfileRoot: null);
        inner.Sink = sut.HandleMessageAsync;
        return new Rig(sut, inner, downstream, stop);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Wait)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Не дождался: {what}");
    }

    private static List<ServerMessage> Snapshot(Rig rig)
    {
        lock (rig.Downstream) return [.. rig.Downstream];
    }

    [Fact]
    public async Task СтопИзТрея_ХодПрерван_ФолбэкНеЗапущен()
    {
        var rig = Build("owner-fb-tray", TrayStop);

        await rig.Sut.SendMessageAsync("пошевели мышью");
        // Финал хода: прерванный — exited, а ушедший в фолбэк — result второй попытки
        await WaitForAsync(() => Snapshot(rig).Any(m => m is ExitedMessage or ResultMessage), "конец хода");
        await WaitForAsync(() => !rig.Sut.FallbackTurnActive, "конец оркестрации");

        rig.Inner.Attempts.Should().Be(1, "остановка человеком — не отказ провайдера, другая модель не запускается");
        rig.Stop.Sessions.Should().Equal(["chat-1"]);
        Snapshot(rig).OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        Snapshot(rig).OfType<ErrorMessage>().Should().BeEmpty("ход прерван, а не упал");
        Snapshot(rig).OfType<ResultMessage>().Should().BeEmpty("ход прерван, а не завершён штатно");
    }

    [Fact]
    public async Task СмертьCliБезСтопа_ФолбэкПоПрежнемуПерезапускаетХод()
    {
        var rig = Build("owner-fb-crash", Crash, Ok);

        await rig.Sut.SendMessageAsync("пошевели мышью");
        await WaitForAsync(() => Snapshot(rig).OfType<ResultMessage>().Any(), "финальный result");

        rig.Stop.Sessions.Should().BeEmpty();
        rig.Inner.Attempts.Should().Be(2, "обрыв без «Стопа» — ошибка доставки, ход перезапускается");
    }

    [Fact]
    public async Task Отказ429_ФолбэкПоПрежнемуПерезапускаетХод()
    {
        var rig = Build("owner-fb-429", Overloaded429, Ok);

        await rig.Sut.SendMessageAsync("пошевели мышью");
        await WaitForAsync(() => Snapshot(rig).OfType<ResultMessage>().Any(r => r.ApiErrorStatus is null), "успешный result");

        rig.Stop.Sessions.Should().BeEmpty();
        rig.Inner.Attempts.Should().Be(2, "429 — отказ провайдера, фолбэк обязан сработать");
    }
}
