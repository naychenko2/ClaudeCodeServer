using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Фазы старта хода (TurnStartPhaseMessage): «done» шлётся один раз, по первому событию самого
// хода, и только после показанной фазы; ход в очереди не теряет «queued» от событий идущего.
// Приватные CliRun/ProcessLineAsync/EmitStartPhaseAsync — через reflection (приём соседних тестов).
public class ClaudeSessionStartPhaseTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProcessLine =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo EmitPhase =
        typeof(ClaudeSession).GetMethod("EmitStartPhaseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fake = [];
    public void Dispose() { foreach (var p in _fake) ProcessTestCleanup.SafeDispose(p); }

    private const string Event =
        """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"x"}}}""";

    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fake.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static (ClaudeSession Session, List<string> Phases) NewSession()
    {
        var phases = new List<string>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg =>
            {
                if (msg is TurnStartPhaseMessage p) lock (phases) phases.Add(p.Phase);
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null);
        return (new ClaudeSession(new Session(), context), phases);
    }

    private static Task Emit(ClaudeSession s, string phase) => (Task)EmitPhase.Invoke(s, [phase])!;
    private static Task Line(ClaudeSession s, object run) => (Task)ProcessLine.Invoke(s, [run, Event])!;

    [Fact]
    public async Task Порядок_queued_context_launching_waiting_done_ОдинDone()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        foreach (var ph in new[] { "queued", "context", "launching", "waiting" }) await Emit(s, ph);
        await Line(s, run);
        await Line(s, run);
        phases.Should().Equal("queued", "context", "launching", "waiting", "done");
    }

    [Fact]
    public async Task Done_БезПоказаннойФазы_НеШлётся()
    {
        var (s, phases) = NewSession();
        await Line(s, NewRun());
        phases.Should().BeEmpty();
    }

    [Fact]
    public async Task ОчередьAB_СобытияХодаA_НеСнимаютQueued_DoneОдинРазПослеПервогоСобытияB()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        // ход A стартовал и уже получил первое событие
        await Emit(s, "context");
        await Line(s, run);
        // ход B встал в очередь, A продолжает стримить
        await Emit(s, "queued");
        await Line(s, run);
        await Line(s, run);
        phases.Should().Equal("context", "done", "queued");
        // B взял замок и получил первое событие
        await Emit(s, "context");
        await Line(s, run);
        await Line(s, run);
        phases.Should().Equal("context", "done", "queued", "context", "done");
    }

    [Fact]
    public async Task ХвостПродолжения_SkipResults_НеШлётDone()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        await Emit(s, "context");
        CliRunType.GetField("SkipResults")!.SetValue(run, 1);
        await Line(s, run);
        phases.Should().Equal("context");
        CliRunType.GetField("SkipResults")!.SetValue(run, 0);
        await Line(s, run);
        phases.Should().Equal("context", "done");
    }
}
