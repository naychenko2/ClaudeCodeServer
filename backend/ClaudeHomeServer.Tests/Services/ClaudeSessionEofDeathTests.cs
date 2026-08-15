using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Обрыв хода по EOF (stdout прогона закрылся раньше события ОС Exited): FinalizeRunAsync —
// единственный обработчик, который успел выставить DeathDiagnosed. До фикса он слал клиенту
// ТОЛЬКО ExitedMessage: HandleProcessExitedAsync после DeathDiagnosed молча выходил, и ход
// заканчивался «никак» — ни result, ни error в ленте при статусе Working (инцидент 15.08.2026,
// чат «Создание иконки для дизайн-системы»: два хода подряд оборвались в тишине).
//
// Фикс: ветка активной смерти в FinalizeRunAsync сама шлёт ErrorMessage тем же текстом и с теми
// же гейтами (кроме остановки пользователем), что и HandleProcessExitedAsync. Здесь проверяются
// все ветки: EOF-обрыв / дубль после обработчика смерти / interrupt / same-process ретрай.
//
// CliRun и FinalizeRunAsync приватны — reflection, тот же white-box приём, что в
// ClaudeSessionPendingControlDeathTests. Процесс — реальный, уже завершённый (мгновенный exit):
// финализация читает HasExited/ExitCode и зовёт Kill, на несвязанном Process это кидает.
public class ClaudeSessionEofDeathTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo FinalizeMethod =
        typeof(ClaudeSession).GetMethod("FinalizeRunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo HandleExitedMethod =
        typeof(ClaudeSession).GetMethod("HandleProcessExitedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo RunField =
        typeof(ClaudeSession).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo InterruptedField =
        typeof(ClaudeSession).GetField("_interruptedByUser", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var p in _processes) p.Dispose();
    }

    // Реальный процесс, который уже завершился: HasExited=true безопасен, ExitCode валиден
    private Process ExitedProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        var p = Process.Start(psi)!;
        _processes.Add(p);
        p.WaitForExit(5000).Should().BeTrue("фикстура: процесс обязан завершиться мгновенно");
        return p;
    }

    // Активный ход посреди выдачи: TurnDone=false (result не пришёл), TurnGotEvent=true
    // (модель уже отвечала — это НЕ гонка старта), RetryOnEmptyExit=false (новый процесс)
    private object NewMidTurnRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        CliRunType.GetProperty("Process")!.SetValue(run, ExitedProcess());
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        CliRunType.GetField("TurnGotEvent")!.SetValue(run, true);
        return run;
    }

    private static ClaudeSession NewSession(List<ServerMessage> sink)
    {
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: m => { lock (sink) sink.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return new ClaudeSession(new Session(), context);
    }

    private static ClaudeSession WithRun(List<ServerMessage> sink, object run)
    {
        var session = NewSession(sink);
        RunField.SetValue(session, run);
        return session;
    }

    private static Task Finalize(ClaudeSession s, object run) => (Task)FinalizeMethod.Invoke(s, [run])!;
    private static Task HandleExited(ClaudeSession s, object run) => (Task)HandleExitedMethod.Invoke(s, [run])!;

    // Главный кейс инцидента: EOF при активном ходе — клиент обязан видеть error, а не тишину.
    // До фикса здесь уходил только ExitedMessage (статус Active, «ход закончился никак»).
    [Fact]
    public async Task EofПриАктивномХоде_ОшибкаИТерминалКлиенту()
    {
        var messages = new List<ServerMessage>();
        var run = NewMidTurnRun();
        var session = WithRun(messages, run);

        await Finalize(session, run);

        messages.OfType<ErrorMessage>().Should().ContainSingle("обрыв хода обязан стать видимой ошибкой")
            .Which.Text.Should().Contain("во время хода");
        messages.OfType<ExitedMessage>().Should().ContainSingle("терминал хода — ExitedMessage");
    }

    // Порядок «Exited первым» (типичный): HandleProcessExitedAsync уже поставил DeathDiagnosed
    // и отправил ошибку — финализация НЕ дублирует ErrorMessage, но терминал всё равно шлёт.
    [Fact]
    public async Task ПослеОбработчикаСмерти_ФинализацияНеДублируетОшибку()
    {
        var messages = new List<ServerMessage>();
        var run = NewMidTurnRun();
        var session = WithRun(messages, run);

        await HandleExited(session, run);
        await Finalize(session, run);

        messages.OfType<ErrorMessage>().Should().ContainSingle("ошибка смерти хода — ровно одна");
        messages.OfType<ExitedMessage>().Should().ContainSingle();
    }

    // Ход убит пользователем («Стоп»): фронт уже поставил маркер остановки, красная плашка
    // рядом лжёт — тот же гейт, что в HandleProcessExitedAsync.
    [Fact]
    public async Task EofПослеОстановкиПользователем_ОшибкиНет()
    {
        var messages = new List<ServerMessage>();
        var run = NewMidTurnRun();
        var session = WithRun(messages, run);
        InterruptedField.SetValue(session, true);

        await Finalize(session, run);

        messages.OfType<ErrorMessage>().Should().BeEmpty("смерть после Interrupt ожидаема");
        messages.OfType<ExitedMessage>().Should().ContainSingle();
    }

    // Гонка same-process старта (DiedEmpty-ретрай): смерть до первого события поглощается
    // перезапуском на той же паре — ни ошибки, ни ExitedMessage наружу (её подавит SuppressExited).
    [Fact]
    public async Task EofПустойСмертиSameProcess_ТихийРетрайБезОшибки()
    {
        var messages = new List<ServerMessage>();
        var run = NewMidTurnRun();
        CliRunType.GetField("TurnGotEvent")!.SetValue(run, false);
        CliRunType.GetField("RetryOnEmptyExit")!.SetValue(run, true);
        var session = WithRun(messages, run);

        await Finalize(session, run);

        messages.Should().BeEmpty("ретрай новой парой — смерть наружу не идёт");
        CliRunType.GetField("DiedEmpty")!.GetValue(run).Should().Be(true);
        CliRunType.GetField("SuppressExited")!.GetValue(run).Should().Be(true);
    }
}
