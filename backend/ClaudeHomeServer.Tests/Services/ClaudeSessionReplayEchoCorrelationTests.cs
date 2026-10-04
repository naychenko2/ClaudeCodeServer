using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
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
}
