using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Д1 (инцидент 2026-08-10): same-process ход отдан в прогон, у которого ещё доживает
// ход-продолжение CLI (ответ на task-notification). TrySubmitTurn при ContinuationActive
// делает SkipResults++, чтобы result продолжения не засчитался нашему ходу. Но хвост
// продолжения (stream_event/assistant ДО его result) шёл через ProcessLineAsync и — до фикса —
// взводил TurnGotEvent на любой строке. Когда продолжение заканчивалось, а процесс умирал, не
// тронув нашу очередь в stdin, смерть уходила наружу как легитимный Unreachable (TurnGotEvent=true
// → ShouldRetryEmptyExit=false) вместо тихого ретрая той же парой → ложная подмена провайдера.
//
// Фикс: TurnGotEvent взводится только событиями самого хода (SkipResults==0), а не хвостом
// продолжения. CliRun и ProcessLineAsync приватны (состояние прогона не течёт наружу) — доступ
// через reflection, тот же white-box приём, что в StructuredBgEventsTests. Логика синхронная,
// таймеров нет — TaskCompletionSource/Task.Delay не требуется.
public class ClaudeSessionContinuationAttributionTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    // CliRun — приватный вложенный класс: Process required, но путь stream_event/result-skip его
    // не трогает. Ставим настоящий (незапущенный) Process — дёшево, не оставляет required-поле
    // в непредсказуемом дефолте (как в StructuredBgEventsTests).
    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static bool TurnGotEventOf(object run) =>
        (bool)CliRunType.GetField("TurnGotEvent")!.GetValue(run)!;
    private static bool TurnDoneOf(object run) =>
        (bool)CliRunType.GetField("TurnDone")!.GetValue(run)!;
    private static int SkipResultsOf(object run) =>
        (int)CliRunType.GetField("SkipResults")!.GetValue(run)!;
    private static void SetSkipResults(object run, int value) =>
        CliRunType.GetField("SkipResults")!.SetValue(run, value);
    private static void SetContinuationActive(object run, bool value) =>
        CliRunType.GetField("ContinuationActive")!.SetValue(run, value);

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(new Session(), context), sent);
    }

    private static async Task DriveLine(ClaudeSession session, object run, string line)
    {
        var task = (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;
        await task;
    }

    // stream_event хода-продолжения CLI (text_delta) — ровно тот класс строк, что до фикса
    // ложно взводил TurnGotEvent.
    private const string ContinuationStreamEvent =
        """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"продолжение"}}}""";

    // --- 1. Событие продолжения (SkipResults>0) НЕ взводит TurnGotEvent ---

    [Fact]
    public async Task ProcessLine_СобытиеПродолжения_НеВзводитTurnGotEvent()
    {
        // Состояние после TrySubmitTurn при активном продолжении: ход активен (TurnDone=false),
        // result продолжения ещё не пришёл (SkipResults=1), ContinuationActive уже снят.
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        SetSkipResults(run, 1);
        SetContinuationActive(run, false);

        await DriveLine(session, run, ContinuationStreamEvent);

        TurnGotEventOf(run).Should().BeFalse(
            "событие ход-продолжения (SkipResults>0) принадлежит продолжению, а не нашему ходу");
    }

    // --- 2. Контроль: событие самого хода (SkipResults=0) взводит TurnGotEvent как раньше ---

    [Fact]
    public async Task ProcessLine_СобытиеСамогоХода_ВзводитTurnGotEvent()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        SetSkipResults(run, 0); // продолжений в полёте нет — событие наше
        SetContinuationActive(run, false);

        await DriveLine(session, run, ContinuationStreamEvent);

        TurnGotEventOf(run).Should().BeTrue(
            "событие самого хода (SkipResults=0) доказывает, что процесс взялся за наше сообщение");
    }

    // --- 3. Регрессия инцидента: submit во время continuation → смерть процесса → тихий ретрай
    //         той же парой, НЕ ExitedMessage наружу ---

    [Fact]
    public async Task SubmitВоВремяПродолжения_СмертьПроцесса_ТихийРетрайТойЖеПарой()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        // Пост-TrySubmitTurn состояние: same-process ход активен, RetryOnEmptyExit=true (только
        // его ставит TrySubmitTurn), продолжение ещё доживает.
        SetSkipResults(run, 1);
        SetContinuationActive(run, false);

        // (а) хвост продолжения стримится — TurnGotEvent НЕ взводится (фикс)
        await DriveLine(session, run, ContinuationStreamEvent);
        TurnGotEventOf(run).Should().BeFalse();

        // (б) result продолжения приходит первым (stdout последователен) — пропускается,
        //     наш ход всё ещё активен и без единого своего события
        await DriveLine(session, run,
            """{"type":"result","subtype":"success","duration_ms":1,"num_turns":1,"result":"ок"}""");
        SkipResultsOf(run).Should().Be(0, "result продолжения снял один пропуск");
        TurnDoneOf(run).Should().BeFalse("result продолжения не завершает наш ход");
        TurnGotEventOf(run).Should().BeFalse("своих событий у хода так и не было");

        // (в) процесс умирает, не выдав ни одного события нашего хода. FinalizeRunAsync считает
        //     activeTurnDied=!TurnDone=true, и решает ретрай через ту же чистую функцию:
        ClaudeSession.ShouldRetryEmptyExit(
            activeTurnDied: true, retryOnEmptyExit: true, turnGotEvent: TurnGotEventOf(run),
            reuseSubmit: false)
            .Should().BeTrue(
                "пустая смерть same-process хода — гонка TOCTOU, перезапуск той же парой; " +
                "SuppressExited гасит ExitedMessage — фолбэк не считает смерть Unreachable");
    }
}
