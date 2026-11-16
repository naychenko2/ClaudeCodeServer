using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Фазы старта хода (TurnStartPhaseMessage): «done» шлётся один раз, по первому событию самого
// хода, и только после показанной фазы; ход в очереди не теряет «queued» от событий идущего.
// Плюс секундомеры фаз (GetStartTimings) и повторный «queued» за идущим ходом (хвосты bc852f0e,
// 137df65c). Приватные CliRun/ProcessLineAsync/EmitStartPhaseAsync/Note* — через reflection
// (приём соседних тестов); GetStartTimings — internal, через InternalsVisibleTo.
public class ClaudeSessionStartPhaseTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;
    private static readonly MethodInfo ProcessLine =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo EmitPhase =
        typeof(ClaudeSession).GetMethod("EmitStartPhaseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo BeginTimings =
        typeof(ClaudeSession).GetMethod("BeginStartTimings", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo NoteSpawnM =
        typeof(ClaudeSession).GetMethod("NoteSpawn", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo NoteInitM =
        typeof(ClaudeSession).GetMethod("NoteInit", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo NoteFirstEventM =
        typeof(ClaudeSession).GetMethod("NoteFirstEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo QueuedTurnsField =
        typeof(ClaudeSession).GetField("_queuedTurns", BindingFlags.NonPublic | BindingFlags.Instance)!;

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
    private static void Begin(ClaudeSession s) => _ = BeginTimings.Invoke(s, [Stopwatch.GetTimestamp()]);
    private static void Spawn(ClaudeSession s, long ts) => _ = NoteSpawnM.Invoke(s, [ts]);
    private static void Init(ClaudeSession s) => _ = NoteInitM.Invoke(s, []);
    private static void FirstEvent(ClaudeSession s) => _ = NoteFirstEventM.Invoke(s, [JsonDocument.Parse(Event).RootElement]);
    private static void SetQueued(ClaudeSession s, int n) => QueuedTurnsField.SetValue(s, n);

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

    // ——— Секундомеры фаз старта (GetStartTimings) ———

    [Fact]
    public void Секундомеры_БезСпавна_Null_ПолныйПоток_Неотрицательные()
    {
        var (s, _) = NewSession();
        Begin(s);
        var t = s.GetStartTimings();
        // Процесс не спавнился, init не дошёл, промпт не опубликован: фазы — null, а не 0
        // (0 — настоящее значение, -1 — «не наблюдалось», см. поле _turnSpawnToInitMs).
        t.QueueWaitMs.Should().BeGreaterThanOrEqualTo(0);
        t.PromptAssemblyMs.Should().BeNull();
        t.SpawnToInitMs.Should().BeNull();
        t.InitToFirstEventMs.Should().BeNull();

        // Полный поток: спавн → init → первое событие — значения реальные, неотрицательные.
        Spawn(s, Stopwatch.GetTimestamp());
        Init(s);
        FirstEvent(s);
        var full = s.GetStartTimings();
        full.SpawnToInitMs.Should().BeGreaterThanOrEqualTo(0);
        full.InitToFirstEventMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ПовторныйСпавнВРетрае_ФазыОтражаютПоследнююПопытку()
    {
        var (s, _) = NewSession();
        Begin(s);
        // Первая попытка: процесс спавнен, init дошёл через 120+ мс, затем умер, не отдав
        // ни одного события своего хода (путь DiedEmpty-ретрая).
        Spawn(s, Stopwatch.GetTimestamp());
        await Task.Delay(120);
        Init(s);
        // Ретрай стартует новый процесс: свой спавн, свой init, своё первое событие.
        Spawn(s, Stopwatch.GetTimestamp());
        Init(s);
        FirstEvent(s);
        var t = s.GetStartTimings();
        // Фазы относятся к последней попытке: первая (её init старше на 120+ мс) осталась бы
        // без сброса защёлок в NoteSpawn — мутация «убрать сброс» краснит обе строки.
        t.SpawnToInitMs.Should().BeLessThan(60);
        t.InitToFirstEventMs.Should().BeLessThan(60);
    }

    // ——— Повторный «queued» за идущим ходом (первое событие, хвост 137df65c) ———

    [Fact]
    public async Task ОчередьЗаИдущимХодом_QueuedОтправленСноваПослеПервогоСобытия()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        // Ход A идёт, за ним запаркованы B и C: _queuedTurns считает и сам A — 3.
        await Emit(s, "context");
        SetQueued(s, 3);
        await Emit(s, "queued"); // B/C стоят — строка «В очереди» показана
        await Line(s, run); // первое событие A: «done» снял бы её, вернём для очереди
        phases.Should().Equal("context", "queued", "done", "queued");
    }

    [Fact]
    public async Task БезОчереди_ПовторногоQueuedНет()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        // За идущим ходом никого нет (_queuedTurns == 1 — только сам A): вернуть строку некому.
        await Emit(s, "context");
        SetQueued(s, 1);
        await Line(s, run);
        phases.Should().Equal("context", "done");
    }
}
