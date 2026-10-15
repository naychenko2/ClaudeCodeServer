using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Индикатор TTL prompt cache (шаг 1 плана): ResultMessage.PromptCache собирается из
// stream-json основного агента — TTL из usage.cache_creation.ephemeral_*, якорь по приходу
// stream_event/message_start, причина промаха из diagnostics.cache_miss_reason первого вызова
// хода. White-box через ProcessLineAsync — тот же приём, что ClaudeSessionEmptyResultSwallowTests.
public class ClaudeSessionPromptCacheTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private const string ThirdPartyModel = "deepseek-v9-test";

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    // Ручные часы: якорь обязан браться в момент прихода message_start
    private sealed class ManualClock(long startMs) : TimeProvider
    {
        public long NowMs = startMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);
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

    private static (ClaudeSession Session, List<ServerMessage> Sent, ManualClock Clock) NewSession(
        string? model = null, LlmProviderRegistry? providers = null)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        var clock = new ManualClock(1_000_000);
        var session = new ClaudeSession(new Session { Model = model }, context, providers: providers)
        {
            PromptCacheClock = clock,
        };
        return (session, sent, clock);
    }

    private static async Task Drive(ClaudeSession session, object run, string line) =>
        await (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;

    private static string Usage(long read, long create, long eph1h = 0, long eph5m = 0) =>
        $$$"""{"input_tokens":3,"output_tokens":0,"cache_read_input_tokens":{{{read}}},"cache_creation_input_tokens":{{{create}}},"cache_creation":{"ephemeral_1h_input_tokens":{{{eph1h}}},"ephemeral_5m_input_tokens":{{{eph5m}}}}}""";

    private static string ParentJson(string? parent) => parent is null ? "null" : $"\"{parent}\"";

    private static string MessageStart(string usage, string diagnostics = "null", string? parent = null) =>
        """{"type":"stream_event","parent_tool_use_id":""" + ParentJson(parent)
        + ""","event":{"type":"message_start","message":{"id":"msg_1","role":"assistant","usage":""" + usage
        + ""","diagnostics":""" + diagnostics + "}}}";

    private static string Assistant(string usage, string? parent = null) =>
        $$$"""{"type":"assistant","parent_tool_use_id":{{{ParentJson(parent)}}},"message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"ок"}],"usage":{{{usage}}}}}""";

    private const string ResultLine =
        """{"type":"result","subtype":"success","duration_ms":1000,"num_turns":1,"usage":{"input_tokens":3,"output_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":50},"total_cost_usd":0.1}""";

    private static PromptCacheInfo? LastPromptCache(List<ServerMessage> sent)
    {
        lock (sent) return sent.OfType<ResultMessage>().Last().PromptCache;
    }

    [Fact]
    public async Task Запись1h_TtlИЯкорьПоMessageStart()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 120_000, eph1h: 120_000)));
        var anchor = clock.NowMs;
        clock.NowMs += 30_000; // генерация ответа — якорь не двигает
        await Drive(session, run, Assistant(Usage(0, 120_000, eph1h: 120_000)));
        await Drive(session, run, ResultLine);

        var pc = LastPromptCache(sent);
        pc.Should().NotBeNull();
        pc!.Ttl.Should().Be("1h");
        pc.AnchorAtMs.Should().Be(anchor, "якорь — приход message_start, а не assistant");
        pc.MissReason.Should().BeNull();
        pc.PrefixTokens.Should().Be(120_003, "PrefixTokens = ContextTokens последнего запроса");
    }

    [Fact]
    public async Task Запись5m_Ttl5m()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 9_000, eph5m: 9_000)));
        await Drive(session, run, ResultLine);

        LastPromptCache(sent)!.Ttl.Should().Be("5m");
    }

    // Вызов, который только читал кэш, наследует TTL последней записи и двигает якорь
    [Fact]
    public async Task ТолькоЧтение_TtlНаследуется_ЯкорьДвигается()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000)));
        clock.NowMs += 60_000;
        await Drive(session, run, MessageStart(Usage(50_000, 0)));
        var readAnchor = clock.NowMs;
        await Drive(session, run, ResultLine);

        var pc = LastPromptCache(sent)!;
        pc.Ttl.Should().Be("1h", "чтение не пишет кэш — TTL от последней записи");
        pc.AnchorAtMs.Should().Be(readAnchor, "чтение продлевает жизнь кэша — якорь сдвигается");
    }

    // Сабагент со своим кэшем (5m) не двигает ни якорь, ни TTL основного агента
    [Fact]
    public async Task Сабагент_НеДвигаетЯкорьИTtl()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000)));
        var mainAnchor = clock.NowMs;
        clock.NowMs += 120_000;
        await Drive(session, run, MessageStart(Usage(0, 8_000, eph5m: 8_000), parent: "toolu_sub"));
        await Drive(session, run, Assistant(Usage(0, 8_000, eph5m: 8_000), parent: "toolu_sub"));
        await Drive(session, run, ResultLine);

        var pc = LastPromptCache(sent)!;
        pc.AnchorAtMs.Should().Be(mainAnchor);
        pc.Ttl.Should().Be("1h");
    }

    // Причина промаха — из diagnostics ПЕРВОГО вызова хода; последующие вызовы её не трут
    [Fact]
    public async Task ПричинаПромаха_ПервогоВызоваХода()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000, eph1h: 80_000),
            """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":80000}}"""));
        await Drive(session, run, MessageStart(Usage(80_000, 0), "null"));
        await Drive(session, run, ResultLine);

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().Be("system_changed");
        pc.MissedTokens.Should().Be(80_000);
    }

    [Fact]
    public async Task DiagnosticsNull_ПричиныНет()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(40_000, 0), "null"));
        await Drive(session, run, ResultLine);

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().BeNull();
        pc.MissedTokens.Should().BeNull();
        pc.Ttl.Should().BeNull("записи кэша процесс ещё не видел");
        pc.AnchorAtMs.Should().NotBeNull();
    }

    // Нет ни одного сигнала кэша — поля нет (а не пустой объект)
    [Fact]
    public async Task БезДанныхКэша_ПоляНет()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, ResultLine);

        LastPromptCache(sent).Should().BeNull();
    }

    // Сторонний провайдер: ephemeral_* в его потоке — эхо CLI, индикатора нет
    [Fact]
    public async Task СтороннийПровайдер_PromptCacheNull()
    {
        var providers = new LlmProviderRegistry(Helpers.TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = ThirdPartyModel,
        }));
        var (session, sent, _) = NewSession(ThirdPartyModel, providers);
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000)));
        await Drive(session, run, ResultLine);

        LastPromptCache(sent).Should().BeNull();
    }
}
