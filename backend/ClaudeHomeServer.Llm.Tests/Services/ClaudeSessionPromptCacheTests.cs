using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Шаг B-1 фичи «Индикатор prompt cache»: ResultMessage.PromptCache собирается из stream-json
// основного агента: прочитанные/записанные токены — из usage result'а хода, якорь последней
// записи — по приходу message_start вызова, писавшего кэш, причина промаха — из
// diagnostics.cache_miss_reason ПЕРВОГО вызова после эха нашего сообщения (вызовы до эха —
// ход-продолжение CLI, не наш ход). White-box через ProcessLineAsync — тот же приём, что
// ClaudeSessionReplayEchoCorrelationTests.
public class ClaudeSessionPromptCacheTests : IDisposable
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

    // Ручные часы: якорь записи обязан ставиться в момент прихода message_start, а не real-часами
    private sealed class ManualClock(long startMs) : TimeProvider
    {
        public long NowMs = startMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);
    }

    private object NewRun(string? pendingEcho = null)
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        CliRunType.GetField("PendingEchoUuid")!.SetValue(run, pendingEcho);
        return run;
    }

    private static (ClaudeSession Session, List<ServerMessage> Sent, ManualClock Clock) NewSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        var clock = new ManualClock(1_000_000);
        var session = new ClaudeSession(new Session(), context) { PromptCacheClock = clock };
        return (session, sent, clock);
    }

    private static async Task Drive(ClaudeSession session, object run, string line) =>
        await (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;

    private static string Usage(long read, long create) =>
        $$$"""{"input_tokens":3,"output_tokens":0,"cache_read_input_tokens":{{{read}}},"cache_creation_input_tokens":{{{create}}}}""";

    private static string MessageStart(string usage, string diagnostics = "null") =>
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1","role":"assistant","usage":"""
        + usage + ""","diagnostics":""" + diagnostics + "}}}";

    private static string Assistant(string usage, string diagnostics = "null") =>
        """{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"ок"}],"usage":"""
        + usage + ""","diagnostics":""" + diagnostics + "}}";

    private static string ResultLine(long read, long create) =>
        $$$"""{"type":"result","subtype":"success","duration_ms":1000,"num_turns":1,"result":"ок","usage":{"input_tokens":3,"output_tokens":10,"cache_read_input_tokens":{{{read}}},"cache_creation_input_tokens":{{{create}}}},"total_cost_usd":0.1}""";

    private static readonly string Echo =
        $$"""{"type":"user","message":{"role":"user","content":"Ну как?"},"uuid":"{{Uuid}}","isReplay":true}""";

    private static PromptCacheState? LastPromptCache(List<ServerMessage> sent)
    {
        lock (sent) return sent.OfType<ResultMessage>().Last().PromptCache;
    }

    // Прочитанные и записанные токены — из usage result'а хода
    [Fact]
    public async Task ReadWriteTokens_ИзUsageResulta()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(100_000, 5_000)));
        await Drive(session, run, ResultLine(100_000, 5_000));

        var pc = LastPromptCache(sent);
        pc.Should().NotBeNull();
        pc!.ReadTokens.Should().Be(100_000);
        pc.WrittenTokens.Should().Be(5_000);
    }

    // Якорь последней записи — приход message_start вызова, писавшего кэш; строка assistant
    // (приходит после генерации) якорь не двигает
    [Fact]
    public async Task LastWriteAtMs_ПоMessageStartПисавшегоВызова()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000)));
        var writeAt = clock.NowMs;
        clock.NowMs += 30_000; // время генерации — якорь не двигает
        await Drive(session, run, Assistant(Usage(0, 80_000)));
        await Drive(session, run, ResultLine(0, 80_000));

        var pc = LastPromptCache(sent)!;
        pc.LastWriteAtMs.Should().Be(writeAt, "якорь — приход message_start, а не конец генерации");
    }

    // Вызов, который только читал кэш, якорь записи не двигает: последней остаётся настоящая запись
    [Fact]
    public async Task ТолькоЧтение_ЯкорьЗаписиНеДвигает()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000)));
        var writeAt = clock.NowMs;
        clock.NowMs += 60_000;
        await Drive(session, run, MessageStart(Usage(50_000, 0)));
        await Drive(session, run, ResultLine(50_000, 0));

        LastPromptCache(sent)!.LastWriteAtMs.Should().Be(writeAt);
    }

    // Причина промаха — diagnostics ПЕРВОГО вызова хода; последующие вызовы её не перетирают
    [Fact]
    public async Task ПричинаПромаха_ПервогоВызоваХода()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000),
            """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":80000}}"""));
        await Drive(session, run, MessageStart(Usage(80_000, 0),
            """{"cache_miss_reason":{"type":"messages_changed","cache_missed_input_tokens":1000}}"""));
        await Drive(session, run, ResultLine(80_000, 0));

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().Be(PromptCacheMissReason.SystemChanged);
    }

    // Ход-продолжение до эха: его промах — не промах нашего хода. Первый вызов нашего хода — после эха
    [Fact]
    public async Task ПродолжениеДоЭха_НеСчитаетсяПервымВызовомХода()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun(pendingEcho: Uuid);

        // Продолжение: холодный старт после паузы — промах system_changed и запись кэша
        await Drive(session, run, MessageStart(Usage(0, 70_000),
            """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":70000}}"""));
        var writeAt = clock.NowMs;
        await Drive(session, run, Echo);
        // Наш ход: префикс уже прогрет продолжением, промаха нет
        await Drive(session, run, MessageStart(Usage(70_000, 0)));
        await Drive(session, run, ResultLine(70_000, 0));

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().Be(PromptCacheMissReason.None, "промах был у продолжения, а не у нашего хода");
        pc.LastWriteAtMs.Should().Be(writeAt, "запись кэша продолжением настоящая — якорь от неё");
        pc.ReadTokens.Should().Be(70_000);
    }

    // Незнакомый type — «причина неизвестна» (Unavailable), а не потеря; нет diagnostics — промаха нет
    [Fact]
    public async Task НезнакомыйТипПричины_Unavailable_НетDiagnostics_None()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 10_000),
            """{"cache_miss_reason":{"type":"model_changed","cache_missed_input_tokens":10000}}"""));
        await Drive(session, run, ResultLine(0, 10_000));
        LastPromptCache(sent)!.MissReason.Should().Be(PromptCacheMissReason.Unavailable);

        var (session2, sent2, _) = NewSession();
        var run2 = NewRun();
        await Drive(session2, run2, MessageStart(Usage(0, 10_000), "null"));
        await Drive(session2, run2, ResultLine(0, 10_000));
        LastPromptCache(sent2)!.MissReason.Should().Be(PromptCacheMissReason.None);
    }

    // Нет ни одного сигнала кэша — поля нет (а не пустой объект)
    [Fact]
    public async Task БезДанныхКэша_ПоляНет()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, ResultLine(0, 0));

        LastPromptCache(sent).Should().BeNull();
    }
}
