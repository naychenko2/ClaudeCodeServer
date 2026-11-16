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
// Плюс секундомеры фаз (GetStartTimings), повторный «queued» за идущим ходом (хвосты bc852f0e,
// 137df65c) и D1 смоука TS-10: replay-эхо user-строки — вход в CLI, а не событие ответа модели,
// «done» по нему не срабатывает. Приватные CliRun/ProcessLineAsync/EmitStartPhaseAsync/Note* —
// через reflection (приём соседних тестов); GetStartTimings — internal, через InternalsVisibleTo.
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

    private static readonly MethodInfo StopIncompatibleM =
        typeof(ClaudeSession).GetMethod("StopIncompatibleRunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Лаунчер-заглушка: Kill ничего не убивает (процесс в тесте — пустой), остальное тесту не нужно.
    private sealed class StubLauncher : ClaudeHomeServer.Services.Execution.IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public ClaudeHomeServer.Services.Execution.IPathMapper Paths => ClaudeHomeServer.Services.Execution.IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public Process Start(ClaudeHomeServer.Services.Execution.ProcessSpec spec) => throw new NotSupportedException();
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ClaudeHomeServer.Services.Execution.ProcessSpec spec) => 0;
    }

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

    // Порядок записей внутри NoteSpawn (сброс раньше _turnSpawnTs, хвост e33b6bd2) тестом не
    // ловится: финальные состояния «сброс→запись» и «запись→сброс» идентичны, а единственный
    // различимый сценарий — опоздавший NoteInit читателя строго внутри NoteSpawn — это окно в
    // две Volatile-записи (~нс): его попадание неустойчиво в обе стороны (в предсостоянии без
    // init старого новый порядок сам оставляет окно «сброс уже, спавн ещё старый»). Детерминирован
    // только сам факт сброса к моменту нового спавна — его держит тест выше.

    [Fact]
    public async Task ПозднийInitСтарогоПроцессаПослеСпавна_НеДаётФазуОтСтарогоИнициатора()
    {
        // Соседний с хвостом e33b6bd2 случай: строка init старого процесса доходит уже после
        // NoteSpawn нового. Сброс в NoteSpawn открыл защёлки ДО записи _turnSpawnTs — к моменту
        // нового спавна старый init не защёлкнут и не может дать новую фазу от себя: опоздавший
        // NoteInit меряет SpawnToInit от нового спавна, а init нового процесса остаётся открыт.
        // Мутация «убрать сброс в NoteSpawn»: опоздавший NoteInit — no-op (защёкнут старым init),
        // init нового процесса тоже no-op — SpawnToInit остаётся значением старой попытки.
        var (s, _) = NewSession();
        Begin(s);
        // Старая попытка полностью измерена: её init давно дошёл (фаза старой — сотни мс).
        Spawn(s, Stopwatch.GetTimestamp());
        await Task.Delay(200);
        Init(s);
        FirstEvent(s);
        s.GetStartTimings().SpawnToInitMs.Should().BeGreaterThanOrEqualTo(150, "фаза старой попытки — старая");

        // Новый спавн, затем — опоздавшая строка init старого процесса.
        Spawn(s, Stopwatch.GetTimestamp());
        Init(s);
        // Init нового процесса: меряется от нового спавна, а не от старого инициатора.
        Init(s);
        FirstEvent(s);
        var t = s.GetStartTimings();
        t.SpawnToInitMs.Should().NotBeNull();
        t.SpawnToInitMs.Should().BeLessThan(60,
            "поздний init старого процесса не дал фазу от старого инициатора");
        t.InitToFirstEventMs.Should().BeGreaterThanOrEqualTo(0);
    }

    // ——— Остановка несовместимого прогона (PrevRunStopWaitMs) ———

    private static ClaudeSession NewSessionWithStubLauncher()
    {
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(), OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, Launcher: new StubLauncher());
        return new ClaudeSession(new Session(), context);
    }

    [Fact]
    public async Task ОстановкаПрошлогоПрогона_ЖдётЧитателя_ЗамерВПаспорте()
    {
        var s = NewSessionWithStubLauncher();
        Begin(s);
        s.GetStartTimings().PrevRunStopWaitMs.Should().BeNull("останавливать было нечего — null, а не 0");

        var run = NewRun();
        CliRunType.GetProperty("ReaderTask")!.SetValue(run, Task.Delay(300));
        await (Task)StopIncompatibleM.Invoke(s, [run, TimeSpan.FromSeconds(5)])!;

        s.GetStartTimings().PrevRunStopWaitMs.Should().BeGreaterThanOrEqualTo(250,
            "ожидание финализации читателя прошлого прогона входит в замер");
        CliRunType.GetField("SuppressExited")!.GetValue(run).Should().Be(true);

        // Новый ход сбрасывает замер: он принадлежит ходу, а не сессии
        Begin(s);
        s.GetStartTimings().PrevRunStopWaitMs.Should().BeNull();
    }

    [Fact]
    public async Task ОстановкаПрошлогоПрогона_ЗависшийЧитатель_НеБлокируетСверхПотолка()
    {
        var s = NewSessionWithStubLauncher();
        Begin(s);
        var run = NewRun();
        CliRunType.GetProperty("ReaderTask")!.SetValue(run, new TaskCompletionSource().Task);
        await (Task)StopIncompatibleM.Invoke(s, [run, TimeSpan.FromMilliseconds(200)])!;

        s.GetStartTimings().PrevRunStopWaitMs.Should().BeInRange(150, 3000);
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

    // ——— D1 смоука TS-10: replay-эхо не гасит строку статуса ———
    // Гейт «done» (ProcessLineAsync) — только по первому событию ответа модели нашего хода.
    // --replay-user-messages возвращает вход CLI (isReplay): эхо нашего сообщения и эхо
    // подсаженного task-notification — не событие ответа, «Запускаю…/Жду…» они не гасят.
    // Ветка case "user" (снятие PendingEchoUuid/SkipResults/ContinuationActive) не затронута.

    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private static readonly string Echo =
        $$"""{"type":"user","message":{"role":"user","content":"ну как?"},"uuid":"{{Uuid}}","isReplay":true}""";
    private const string EchoForeign =
        """{"type":"user","message":{"role":"user","content":"<task-notification>done</task-notification>"},"uuid":"99999999-0000-4000-8000-000000000000","isReplay":true}""";
    private const string ToolResultLine =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    private static Task LineRaw(ClaudeSession s, object run, string line) => (Task)ProcessLine.Invoke(s, [run, line])!;
    private static void SetRun(object run, string field, object? v) => CliRunType.GetField(field)!.SetValue(run, v);
    private static string? GetRunStr(object run, string field) => (string?)CliRunType.GetField(field)!.GetValue(run);

    [Fact]
    public async Task ЭхоДоInit_НеГаситЗапускаю_DoneРовноОдинРазНаПервомStreamEvent()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        SetRun(run, "PendingEchoUuid", Uuid); // сообщение отправлено, эхо ожидается (ArmEchoWait)
        await Emit(s, "context");
        await Emit(s, "launching");
        await LineRaw(s, run, Echo); // CLI взял наше сообщение — эхо это вход CLI, не ответ модели
        phases.Should().Equal(new[] { "context", "launching" }, "эхо не гасит «Запускаю…»");
        await LineRaw(s, run, Event); // первое событие ответа модели — вот теперь «done»
        phases.Should().Equal(new[] { "context", "launching", "done" });
        await LineRaw(s, run, Event);
        phases.Should().Equal(new[] { "context", "launching", "done" }, "«done» ровно один раз");
    }

    [Fact]
    public async Task ЭхоПослеInit_ЖдуДержитсяДоПервогоStreamEvent()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        SetRun(run, "PendingEchoUuid", Uuid);
        await Emit(s, "context");
        await Emit(s, "launching");
        await Emit(s, "waiting"); // то же состояние, что даёт ветка init (EmitStartPhaseAsync("waiting") после init)
        await LineRaw(s, run, Echo);
        phases.Should().Equal(new[] { "context", "launching", "waiting" }, "эхо не гасит «Жду ответ модели…»");
        await LineRaw(s, run, Event);
        phases.Should().Equal(new[] { "context", "launching", "waiting", "done" });
    }

    [Fact]
    public async Task ЖивойПроцесс_ЭхоНеГаситЖду_СнимаетОжиданиеЭха_DoneНаПервомStreamEvent()
    {
        // Состояние после TrySubmitTurn + EmitStartPhaseAsync("waiting"): сообщение отдано
        // живому процессу прогона (same-process ход).
        var (s, phases) = NewSession();
        var run = NewRun();
        SetRun(run, "PendingEchoUuid", Uuid);
        await Emit(s, "context");
        await Emit(s, "waiting");
        await LineRaw(s, run, Echo);
        phases.Should().Equal(new[] { "context", "waiting" }, "эхо не гасит «Жду…» живого процесса");
        GetRunStr(run, "PendingEchoUuid").Should().BeNull("эхо опознано: ветка case \"user\" целая");
        await LineRaw(s, run, Event);
        phases.Should().Equal(new[] { "context", "waiting", "done" });
    }

    [Fact]
    public async Task ХвостПродолжения_UserСтрокиНеДаютDone_ПослеСбросаSkip_DoneОдинРаз()
    {
        var (s, phases) = NewSession();
        var run = NewRun();
        await Emit(s, "context");
        SetRun(run, "SkipResults", 1);
        await LineRaw(s, run, Event);          // стрим продолжения — не наш ход
        await LineRaw(s, run, ToolResultLine); // tool_result продолжения — не наш ход
        await LineRaw(s, run, EchoForeign);    // эхо подсаженного task-notification — вход CLI
        phases.Should().Equal(new[] { "context" }, "хвост продолжения не даёт «done»");
        SetRun(run, "SkipResults", 0);
        await LineRaw(s, run, Event);
        phases.Should().Equal(new[] { "context", "done" });
        await LineRaw(s, run, Event);
        phases.Should().Equal(new[] { "context", "done" }, "«done» ровно один раз");
    }

    [Fact]
    public async Task ОбычныйToolResultБезReplay_ПоПрежнемуДаётDone()
    {
        // Граница исключения: без isReplay user-строка — продолжение ответа модели нашего хода
        // (результат её инструмента), статусный ход она гасит корректно — исключать её нельзя.
        var (s, phases) = NewSession();
        var run = NewRun();
        await Emit(s, "context");
        await LineRaw(s, run, ToolResultLine);
        phases.Should().Equal(new[] { "context", "done" });
    }
}
