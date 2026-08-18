using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Технические тексты сбоя не должны попадать в ленту чата: наружу идёт формулировка из
// TurnFailureText, сырой текст (ответ CLI, ex.Message) — под «Подробностями» и в логе.
// Здесь — две точки ClaudeSession: ветка is_error из result CLI (white-box через
// ProcessLineAsync, как в ClaudeSessionEmptyResultSwallowTests) и текст catch'а хода.
public class ClaudeSessionTurnErrorTextTests : IDisposable
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

    // Ровно текст перегрузки из прода: CLI отдаёт subtype=success + is_error=true.
    private const string OverloadedRaw =
        "API Error: 529 Overloaded. This is a server-side issue, usually temporary — try again in a moment.";

    private static string ResultLine(string raw) =>
        """{"type":"result","subtype":"success","duration_ms":1200,"num_turns":1,"is_error":true,"result":"""
        + JsonSerializer.Serialize(raw)
        + ""","usage":{"input_tokens":2,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""";

    // Перегрузка провайдера: человеку — русский текст, английский ответ CLI — в Details.
    [Fact]
    public async Task Перегрузка_ЧеловеческийТекстСыройВDetails()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await DriveLine(session, run, ResultLine(OverloadedRaw));

        ErrorMessage error;
        lock (sent) error = sent.OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.Overloaded);
        error.Details.Should().Be(OverloadedRaw);
        error.ExpectResultFollows.Should().BeTrue("следом идёт ResultMessage того же хода");
    }

    // Нераспознанный текст ошибки виден как есть — иначе диагностика ослепнет.
    [Fact]
    public async Task НераспознаннаяОшибка_СыройТекстБезDetails()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        const string raw = "Credit balance is too low";

        await DriveLine(session, run, ResultLine(raw));

        ErrorMessage error;
        lock (sent) error = sent.OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(raw);
        error.Details.Should().BeNull("дублировать тот же текст в подробностях незачем");
    }

    // Успешный result без is_error ошибку в ленту не порождает.
    [Fact]
    public async Task УспешныйResult_БезОшибкиВЛенте()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        await DriveLine(session, run,
            """{"type":"result","subtype":"success","duration_ms":1200,"num_turns":1,"usage":{"input_tokens":2,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""");

        lock (sent) sent.OfType<ErrorMessage>().Should().BeEmpty();
    }

    // Текст catch'а хода (QueueTurnAsync): запись в stdin закрывающегося CLI — частная
    // формулировка, любое другое исключение — общая. Сам ex.Message уходит в Details.
    [Fact]
    public void ИсключениеХода_ЗакрытиеКанала_ЧастнаяФормулировка()
        => TurnFailureText.ForException(new IOException("Идет закрытие канала."))
            .Should().Be(TurnFailureText.PipeClosing);

    [Fact]
    public void ИсключениеХода_Произвольное_ОбщаяФормулировка()
        => TurnFailureText.ForException(new InvalidOperationException("Collection was modified"))
            .Should().Be(TurnFailureText.Generic);
}
