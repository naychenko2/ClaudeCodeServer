using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// P31: порядок обработчиков смерти процесса (HandleProcessExitedAsync по событию ОС vs
// FinalizeRunAsync по EOF) не должен влиять на то, ушла ли ошибка пользователю и был ли
// ретрай. Признак ожидания control_response (permission / AskUserQuestion / план) фиксируется
// на прогоне (PendingControlAtDeath) и читается обоими обработчиками — иначе первый же
// обработчик, опустошив словари CancelPendingControlResponses, оставлял второго с ложным
// «pending нет», и поведение зависело от того, кто пришёл первым:
//   • Exited первым → FinalizeRunAsync видел пустые словари → тихий ретрай (дубль: ошибка + переигранка);
//   • EOF первым → HandleProcessExitedAsync выходит по DeathDiagnosed до CancelPending →
//     pending залипал в сессии навсегда.
// CliRun и методы приватны — доступ через reflection, тот же white-box приём, что в
// ClaudeSessionContinuationAttributionTests. Логика синхронная, без таймеров.
public class ClaudeSessionPendingControlDeathTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ResolveMethod =
        typeof(ClaudeSession).GetMethod("ResolvePendingControlAtDeath", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo CancelMethod =
        typeof(ClaudeSession).GetMethod("CancelPendingControlResponses", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo HasPendingMethod =
        typeof(ClaudeSession).GetMethod("HasPendingControlResponse", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PendingControlField =
        CliRunType.GetField("PendingControlAtDeath", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly FieldInfo PendingQuestionsField =
        typeof(ClaudeSession).GetField("_pendingQuestions", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    // CliRun — приватный вложенный класс: Process required, но ResolvePendingControlAtDeath его
    // не трогает. Ставим настоящий (незапущенный) Process, как в ClaudeSessionContinuationAttributionTests.
    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static ClaudeSession NewSession()
    {
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return new ClaudeSession(new Session(), context);
    }

    private static void AddPendingQuestion(ClaudeSession s, string toolUseId, string requestId)
    {
        var dict = (ConcurrentDictionary<string, string>)PendingQuestionsField.GetValue(s)!;
        dict[toolUseId] = requestId;
    }

    private static bool Resolve(ClaudeSession s, object run) => (bool)ResolveMethod.Invoke(s, [run])!;
    private static void Cancel(ClaudeSession s) => CancelMethod.Invoke(s, null);
    private static bool HasPending(ClaudeSession s) => (bool)HasPendingMethod.Invoke(s, null)!;
    private static bool PendingControlOf(object run) => (bool)PendingControlField.GetValue(run)!;

    // Порядок А (Exited первым, типичный): HandleProcessExitedAsync фиксирует pending на прогоне
    // и очищает словари. FinalizeRunAsync читает ФИКСАЦИЮ, а не пустые словари — иначе увидел бы
    // false и молча ретраил ход новым процессом (дубль: ошибка «ход прерван» + переигранка).
    [Fact]
    public void ПорядокExitedПервым_ФиксацияПереживаетОчистку_РетраяНет()
    {
        var session = NewSession();
        var run = NewRun();
        AddPendingQuestion(session, "tool-1", "req-1");

        // HandleProcessExitedAsync: фиксируем pending и отменяем ожидающие control_response
        Resolve(session, run).Should().BeTrue("pending был активен в момент смерти");
        PendingControlOf(run).Should().BeTrue("признак зафиксирован на прогоне");
        Cancel(session);
        HasPending(session).Should().BeFalse("словари очищены CancelPendingControlResponses");

        // FinalizeRunAsync: живых словарей нет, но фиксация хранит true → ретрая нет
        Resolve(session, run).Should().BeTrue("читаем фиксацию на прогоне, а не пустые словари");
    }

    // Порядок Б (EOF первым, AskUserQuestion/ExitPlanMode — ридер не блокируется): FinalizeRunAsync
    // первым фиксирует pending и чистит. HandleProcessExitedAsync затем выходит по DeathDiagnosed,
    // не доходя до CancelPendingControlResponses — но pending уже почищен в Finalize, и для
    // следующего хода RunTurnAsync подчистит остаток. До фикса pending залипал бы в сессии навсегда.
    [Fact]
    public void ПорядокEofПервым_ФиксацияИЧисткаВFinalize_PendingНеЗалипает()
    {
        var session = NewSession();
        var run = NewRun();
        AddPendingQuestion(session, "tool-1", "req-1");

        // FinalizeRunAsync первым: фиксируем и тут же чистим (ветка activeTurnDied, P31)
        Resolve(session, run).Should().BeTrue();
        PendingControlOf(run).Should().BeTrue();
        Cancel(session);
        HasPending(session).Should().BeFalse("pending почищен в FinalizeRunAsync — не залипает в сессии");
    }

    // Суть фикса в одном тесте: после CancelPendingControlResponses голый HasPendingControlResponse
    // возвращает false (словари пусты), а ResolvePendingControlAtDeath — true (читает фиксацию).
    // Именно это расхождение устраняет зависимость от порядка обработчиков и подавляет дубль ретрая.
    [Fact]
    public void ПослеОчистки_ResolveЧитаетФиксацию_HasPendingЛжётFalse()
    {
        var session = NewSession();
        var run = NewRun();
        AddPendingQuestion(session, "tool-1", "req-1");

        Resolve(session, run);   // первый обработчик зафиксировал
        Cancel(session);          // и очистил словари

        HasPending(session).Should().BeFalse("живое состояние словарей пусто");
        Resolve(session, run).Should().BeTrue("но фиксация на прогоне хранит true");
    }

    // Чистка на старте хода (RunTurnAsync, P31): даже если что-то зависло с прошлого прогона,
    // к моменту подачи нового хода pending-состояния быть не должно — CancelPendingControlResponses
    // в начале RunTurnAsync гарантирует, что залипание не переживает ход.
    [Fact]
    public void ЧисткаНаСтартеХода_СнимаетЗалипшийPending()
    {
        var session = NewSession();
        AddPendingQuestion(session, "tool-1", "req-1");
        HasPending(session).Should().BeTrue();

        // RunTurnAsync вызывает CancelPendingControlResponses в начале хода
        Cancel(session);

        HasPending(session).Should().BeFalse("старт нового хода — с чистого листа");
    }
}
