using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Остановка хода пользователем («Стоп» в UI или прерывание ради очереди — SessionManager.Interrupt /
// PreemptTurnForQueue) убивает процесс CLI. До фикса HandleProcessExitedAsync не знал, что смерть
// ожидаемая, и слал ErrorMessage «Процесс модели завершился во время хода — ответ не был получен»:
// в ленте появлялись ДВА элемента — серый маркер «Ход остановлен пользователем» (правильный) и
// красная плашка рядом (ложная). Признак _interruptedByUser взводится в Interrupt() и гасится
// ровно в начале следующего хода (RunTurnAsync), чтобы настоящая смерть процесса по-прежнему
// доезжала до клиента ошибкой (P27: иначе чат висит в «ожидании» до watchdog).
//
// CliRun и HandleProcessExitedAsync приватны — доступ через reflection, тот же white-box приём,
// что в ClaudeSessionPendingControlDeathTests. Логика синхронная, без таймеров и путей ФС.
public class ClaudeSessionUserInterruptDeathTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo HandleExitedMethod =
        typeof(ClaudeSession).GetMethod("HandleProcessExitedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo RunField =
        typeof(ClaudeSession).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo InterruptedField =
        typeof(ClaudeSession).GetField("_interruptedByUser", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    // Активный ход: TurnDone=false (по умолчанию), RetryOnEmptyExit=false → ретрая не будет,
    // это ровно та ветка, что раньше безусловно слала ErrorMessage.
    private object NewActiveRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();     // незапущенный: Kill/ExitCode внутри под try/catch
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
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

    private static ClaudeSession WithActiveRun(List<ServerMessage> sink, object run)
    {
        var session = NewSession(sink);
        RunField.SetValue(session, run);   // прогон должен быть «текущим», иначе обработчик выходит сразу
        return session;
    }

    private static Task HandleExited(ClaudeSession s, object run) => (Task)HandleExitedMethod.Invoke(s, [run])!;

    // Пользователь нажал «Стоп»: процесс убит по нашей же воле — красной плашки быть не должно,
    // маркер остановки в ленту ставит фронт. Терминал хода наружу отдаёт FinalizeRunAsync
    // (ExitedMessage) — этот обработчик не шлёт ничего.
    [Fact]
    public async Task ХодОстановленПользователем_ОшибкаОСмертиПроцессаНеУходит()
    {
        var messages = new List<ServerMessage>();
        var run = NewActiveRun();
        var session = WithActiveRun(messages, run);

        session.Interrupt();
        await HandleExited(session, run);

        messages.OfType<ErrorMessage>().Should().BeEmpty("смерть процесса после Interrupt ожидаема");
    }

    // Обратный случай (страховка от залипания флага): процесс умер сам посреди хода — клиент
    // обязан увидеть ошибку, иначе гибель выглядит как штатное ожидание ввода (P27).
    [Fact]
    public async Task СмертьПроцессаБезInterrupt_ОшибкаУходитКакРаньше()
    {
        var messages = new List<ServerMessage>();
        var run = NewActiveRun();
        var session = WithActiveRun(messages, run);

        await HandleExited(session, run);

        messages.OfType<ErrorMessage>().Should().ContainSingle()
            .Which.Text.Should().Contain("во время хода");
    }

    // Флаг живёт ровно до начала следующего хода: RunTurnAsync гасит его рядом с
    // CancelPendingControlResponses. Имитируем этот единственный сброс — после него смерть
    // процесса снова рапортует ошибкой (иначе залипший флаг навсегда заглушил бы P27).
    [Fact]
    public async Task СбросВНачалеСледующегоХода_ВозвращаетОтчётОСмерти()
    {
        var messages = new List<ServerMessage>();
        var session = NewSession(messages);

        session.Interrupt();
        InterruptedField.GetValue(session).Should().Be(true, "Interrupt пометил ход прерванным");

        InterruptedField.SetValue(session, false);   // то, что делает RunTurnAsync на старте хода

        var run = NewActiveRun();
        RunField.SetValue(session, run);
        await HandleExited(session, run);

        messages.OfType<ErrorMessage>().Should().ContainSingle("новый ход — смерть процесса снова реальна");
    }
}
