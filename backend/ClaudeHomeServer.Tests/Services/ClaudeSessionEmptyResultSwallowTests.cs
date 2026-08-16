using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Инцидент 16.08.2026: ходы «Проверь»/«Ну как» завершались «успехом» за ~280 мс без единого
// токена. Причина — ПУСТОЙ result CLI (numTurns=0, success, нулевой usage): так CLI
// завершает свои служебные микро-ходи (task-notification на --resume; запуск без submit при
// ре-аттемпте фолбэком), а бэкенд засчитывал его ответом пользовательскому ходу — ход
// «завершался», настоящий result потом скипался фильтром корреляции. Фикс: пустой result
// проглатывается (лог + ожидание настоящего result). White-box через ProcessLineAsync —
// тот же приём, что ClaudeSessionContinuationAttributionTests.
public class ClaudeSessionEmptyResultSwallowTests : IDisposable
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

    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    private static bool TurnDoneOf(object run) =>
        (bool)CliRunType.GetField("TurnDone")!.GetValue(run)!;

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

    // Ровно result из инцидента (запись 489 истории чата): success, 277-283 мс, numTurns=0,
    // все счётчики usage нулевые.
    private const string EmptyResultLine =
        """{"type":"result","subtype":"success","duration_ms":277,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0},"total_cost_usd":0}""";

    private const string RealResultLine =
        """{"type":"result","subtype":"success","duration_ms":35069,"num_turns":1,"usage":{"input_tokens":2,"output_tokens":772,"cache_read_input_tokens":52037,"cache_creation_input_tokens":77156},"total_cost_usd":0.81}""";

    // Пустой result активного хода: НЕ резолвит ход (TurnDone остаётся false), НЕ шлёт
    // ResultMessage клиенту — ход ждёт настоящего result.
    [Fact]
    public async Task ПустойResult_НеЗавершаетХод()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await DriveLine(session, run, EmptyResultLine);

        TurnDoneOf(run).Should().BeFalse("пустой result — служебный маркер CLI, ход не завершён");
        lock (sent) sent.OfType<ResultMessage>().Should().BeEmpty(
            "пользователь не должен видеть ход отработавшим без ответа");
    }

    // Настоящий result (есть ходы модели и токены) — резолвит ход как раньше.
    [Fact]
    public async Task НастоящийResult_ЗавершаетХод()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await DriveLine(session, run, RealResultLine);

        TurnDoneOf(run).Should().BeTrue("настоящий result завершает ход");
        lock (sent) sent.OfType<ResultMessage>().Should().HaveCount(1);
    }

    // Последовательность инцидента: пустой result (микро-ход notification) → настоящий result
    // хода. До фикса первый завершал ход, второй скипался фильтром корреляции (TurnDone=true).
    [Fact]
    public async Task ПустойЗатемНастоящий_РезолвитТолькоНастоящий()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await DriveLine(session, run, EmptyResultLine);
        await DriveLine(session, run, RealResultLine);

        TurnDoneOf(run).Should().BeTrue("настоящий result завершил ход после пустого");
        lock (sent) sent.OfType<ResultMessage>()
            .Should().ContainSingle(r => r.NumTurns == 1, "в ленту ушёл только настоящий result");
    }
}
