using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Шаг B-1/B-1r фичи «Индикатор prompt cache»: ResultMessage.PromptCache собирается из stream-json
// основного агента: прочитанные/записанные токены — из usage result'а хода, якорь последней
// записи — по приходу message_start вызова, писавшего кэш, TTL — из usage.cache_creation.ephemeral_*,
// причина промаха и MissedTokens — из diagnostics.cache_miss_reason ПЕРВОГО вызова после эха
// нашего сообщения (вызовы до эха — ход-продолжение CLI, не наш ход), страховка причины — из
// строки assistant того же вызова. Поле не появляется у сторонних провайдеров (свой кэш без
// TTL, ephemeral_* — эхо CLI). White-box через ProcessLineAsync — тот же приём, что
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
            { PromptCacheClock = clock };
        return (session, sent, clock);
    }

    private static async Task Drive(ClaudeSession session, object run, string line) =>
        await (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;

    private static string Usage(long read, long create, long eph1h = 0, long eph5m = 0) =>
        $$$"""{"input_tokens":3,"output_tokens":0,"cache_read_input_tokens":{{{read}}},"cache_creation_input_tokens":{{{create}}},"cache_creation":{"ephemeral_1h_input_tokens":{{{eph1h}}},"ephemeral_5m_input_tokens":{{{eph5m}}}}}""";

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
    public async Task AnchorAtMs_ПоMessageStartПисавшегоВызова()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000)));
        var writeAt = clock.NowMs;
        clock.NowMs += 30_000; // время генерации — якорь не двигает
        await Drive(session, run, Assistant(Usage(0, 80_000)));
        await Drive(session, run, ResultLine(0, 80_000));

        var pc = LastPromptCache(sent)!;
        pc.AnchorAtMs.Should().Be(writeAt, "якорь — приход message_start, а не конец генерации");
    }

    // API продлевает TTL и при попадании в чтение: вызов, который только читал кэш,
    // двигает якорь на момент своего message_start
    [Fact]
    public async Task ТолькоЧтение_ЯкорьДвигаетсяПоКасанию()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000)));
        clock.NowMs += 60_000;
        var readAt = clock.NowMs;
        await Drive(session, run, MessageStart(Usage(50_000, 0)));
        await Drive(session, run, ResultLine(50_000, 0));

        LastPromptCache(sent)!.AnchorAtMs.Should().Be(readAt);
    }

    // Вызов без касания кэша (ни чтения, ни записи) якорь не двигает
    [Fact]
    public async Task БезКасанияКэша_ЯкорьНеДвигается()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000)));
        var writeAt = clock.NowMs;
        clock.NowMs += 60_000;
        await Drive(session, run, MessageStart(Usage(0, 0)));
        await Drive(session, run, ResultLine(0, 0));

        LastPromptCache(sent)!.AnchorAtMs.Should().Be(writeAt);
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
        pc.AnchorAtMs.Should().Be(writeAt, "запись кэша продолжением настоящая — якорь от неё");
        pc.ReadTokens.Should().Be(70_000);
    }

    // Все различаемые причины origin (bdf2c9bbd) мапятся в свои значения; незнакомый type —
    // «причина неизвестна» (Unavailable), а не потеря; нет diagnostics — промаха нет
    [Fact]
    public async Task ПричиныПромаха_Мапятся_НезнакомыйType_Unavailable_БезDiagnostics_None()
    {
        foreach (var (type, expected) in new[]
        {
            ("system_changed", PromptCacheMissReason.SystemChanged),
            ("messages_changed", PromptCacheMissReason.MessagesChanged),
            ("previous_message_not_found", PromptCacheMissReason.PreviousMessageNotFound),
            ("model_changed", PromptCacheMissReason.ModelChanged),
            ("tools_changed", PromptCacheMissReason.ToolsChanged),
            ("unavailable", PromptCacheMissReason.Unavailable),
            ("window_expired", PromptCacheMissReason.Unavailable), // новый тип API — не в известные bucket'ы
        })
        {
            var (session, sent, _) = NewSession();
            var run = NewRun();
            await Drive(session, run, MessageStart(Usage(0, 10_000),
                "{\"cache_miss_reason\":{\"type\":\"" + type + "\",\"cache_missed_input_tokens\":10000}}"));
            await Drive(session, run, ResultLine(0, 10_000));
            LastPromptCache(sent)!.MissReason.Should().Be(expected, $"type {type}");
        }

        var (session2, sent2, _) = NewSession();
        var run2 = NewRun();
        await Drive(session2, run2, MessageStart(Usage(0, 10_000), "null"));
        await Drive(session2, run2, ResultLine(0, 10_000));
        LastPromptCache(sent2)!.MissReason.Should().Be(PromptCacheMissReason.None);
    }

    // MissedTokens — cache_missed_input_tokens того же diagnostics; без промаха — null
    [Fact]
    public async Task MissedTokens_ИзDiagnostics_БезПромаха_Null()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000),
            """{"cache_miss_reason":{"type":"previous_message_not_found","cache_missed_input_tokens":80000}}"""));
        await Drive(session, run, ResultLine(0, 80_000));

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().Be(PromptCacheMissReason.PreviousMessageNotFound);
        pc.MissedTokens.Should().Be(80_000);

        var (session2, sent2, _) = NewSession();
        var run2 = NewRun();
        await Drive(session2, run2, MessageStart(Usage(50_000, 0), "null"));
        await Drive(session2, run2, ResultLine(50_000, 0));
        LastPromptCache(sent2)!.MissedTokens.Should().BeNull();
    }

    // TTL — из usage.cache_creation.ephemeral_* вызова, писавшего кэш; без ephemeral — null
    // (записи за жизнь процесса сервер не видел)
    [Fact]
    public async Task Ttl_ИзEphemeralUsage_БезЗаписи_Null()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 120_000, eph1h: 120_000)));
        await Drive(session, run, ResultLine(0, 120_000));
        LastPromptCache(sent)!.Ttl.Should().Be("1h");

        var (session2, sent2, _) = NewSession();
        var run2 = NewRun();
        await Drive(session2, run2, MessageStart(Usage(0, 9_000, eph5m: 9_000)));
        await Drive(session2, run2, ResultLine(0, 9_000));
        LastPromptCache(sent2)!.Ttl.Should().Be("5m");

        var (session3, sent3, _) = NewSession();
        var run3 = NewRun();
        await Drive(session3, run3, MessageStart(Usage(0, 10_000)));
        await Drive(session3, run3, ResultLine(0, 10_000));
        LastPromptCache(sent3)!.Ttl.Should().BeNull("ephemeral_* не пришёл — срок записи неизвестен");
    }

    // Реальная форма потока: разбивка ephemeral_* приходит только в usage строки assistant
    // (в message_start её нет) — TTL берётся оттуда, якорь строкой assistant не двигается
    [Fact]
    public async Task Ttl_ИзUsageСтрокиAssistant_ЯкорьНеДвигается()
    {
        var (session, sent, clock) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart("""{"input_tokens":3,"cache_read_input_tokens":0,"cache_creation_input_tokens":80000}"""));
        var writeAt = clock.NowMs;
        clock.NowMs += 30_000;
        await Drive(session, run, Assistant(Usage(0, 80_000, eph1h: 80_000)));
        await Drive(session, run, ResultLine(0, 80_000));

        var pc = LastPromptCache(sent)!;
        pc.Ttl.Should().Be("1h");
        pc.AnchorAtMs.Should().Be(writeAt);
    }

    // Вызов, который только читал кэш, TTL не стирает: наследует срок последней записи
    [Fact]
    public async Task ТолькоЧтение_TtlНаследуется()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000)));
        await Drive(session, run, MessageStart(Usage(50_000, 0)));
        await Drive(session, run, ResultLine(50_000, 0));

        LastPromptCache(sent)!.Ttl.Should().Be("1h");
    }

    // Сторонний провайдер: свой кэш без TTL, ephemeral_* в его потоке — эхо CLI; поля нет вовсе
    [Fact]
    public async Task СтороннийПровайдер_PromptCacheNull()
    {
        const string thirdPartyModel = "deepseek-v9-test";
        var providers = new LlmProviderRegistry(TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = thirdPartyModel,
        }));
        var (session, sent, _) = NewSession(thirdPartyModel, providers);
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000),
            """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":50000}}"""));
        await Drive(session, run, ResultLine(0, 50_000));

        LastPromptCache(sent).Should().BeNull();
    }

    // Страховка: если message_start не принёс diagnostics, причина берётся из строки assistant
    // того же вызова (по id сообщения); промах последующего вызова хода — не промах между ходами
    [Fact]
    public async Task Страховка_ПричинаИзСтрокиAssistant_ТогоЖеВызова()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();

        await Drive(session, run, MessageStart(Usage(0, 80_000))); // diagnostics нет
        await Drive(session, run, Assistant(Usage(0, 80_000),
            """{"cache_miss_reason":{"type":"messages_changed","cache_missed_input_tokens":80000}}"""));
        await Drive(session, run, ResultLine(0, 80_000));

        var pc = LastPromptCache(sent)!;
        pc.MissReason.Should().Be(PromptCacheMissReason.MessagesChanged);
        pc.MissedTokens.Should().Be(80_000);

        // Второй вызов того же хода (другой id) не переносит причину на промах между ходами
        var (session2, sent2, _) = NewSession();
        var run2 = NewRun();
        await Drive(session2, run2, MessageStart(Usage(70_000, 0)));
        await Drive(session2, run2, """{"type":"assistant","message":{"id":"msg_2","role":"assistant","content":[{"type":"text","text":"ок"}],"diagnostics":{"cache_miss_reason":{"type":"tools_changed","cache_missed_input_tokens":1000}}}}""");
        await Drive(session2, run2, ResultLine(70_000, 0));
        LastPromptCache(sent2)!.MissReason.Should().Be(PromptCacheMissReason.None,
            "промах второго вызова — греет префикс самого хода, а не пауза между ходами");
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

    // ── Сторож регресса (шаг B-2): метрика ccs.prompt_cache.turns ────────────────────────────

    // Метка «измерение из этого теста»: колбэк MeterListener исполняется синхронно в потоке
    // вызова Add, поэтому AsyncLocal отсекает измерения параллельных тестов других классов
    private static readonly AsyncLocal<bool> Mine = new();

    private static async Task<List<string>> CapturePromptCacheTurns(Func<Task> action)
    {
        var reasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName && instrument.Name == "ccs.prompt_cache.turns")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!Mine.Value) return;
            foreach (var t in tags)
                if (t.Key == "reason" && t.Value is string reason) lock (reasons) reasons.Add(reason);
        });
        listener.Start();
        Mine.Value = true;
        await action();
        return reasons;
    }

    [Fact]
    public async Task Метрика_ПричинаПромахаПервогоВызова()
    {
        var (session, _, _) = NewSession();
        var run = NewRun();

        var reasons = await CapturePromptCacheTurns(async () =>
        {
            await Drive(session, run, MessageStart(Usage(0, 80_000, eph1h: 80_000),
                """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":80000}}"""));
            await Drive(session, run, MessageStart(Usage(80_000, 0)));
            await Drive(session, run, ResultLine(80_000, 0));
        });

        reasons.Should().Equal(["systemChanged"], "один ход — одно измерение, по первому вызову");
    }

    [Fact]
    public async Task Метрика_БезПромаха_None()
    {
        var (session, _, _) = NewSession();
        var run = NewRun();

        var reasons = await CapturePromptCacheTurns(async () =>
        {
            await Drive(session, run, MessageStart(Usage(40_000, 0)));
            await Drive(session, run, ResultLine(40_000, 0));
        });

        reasons.Should().Equal(["none"], "ход без промаха — знаменатель доли холодных стартов");
    }

    // Сторонний провайдер: PromptCache null — в сторож ничего не идёт
    [Fact]
    public async Task Метрика_СтороннийПровайдер_НеПишется()
    {
        const string thirdPartyModel = "deepseek-v9-test";
        var providers = new LlmProviderRegistry(TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = thirdPartyModel,
        }));
        var (session, _, _) = NewSession(thirdPartyModel, providers);
        var run = NewRun();

        var reasons = await CapturePromptCacheTurns(async () =>
        {
            await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000),
                """{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":50000}}"""));
            await Drive(session, run, ResultLine(0, 50_000));
        });

        reasons.Should().BeEmpty("у стороннего провайдера своя семантика кэша — в сторож не идёт");
    }

    // TTL и якорь живут через ходы, и PromptCache у result есть и без вызовов модели в этом
    // ходу; такой ход в метрику не пишется, иначе он разбавил бы долю промахов
    [Fact]
    public async Task Метрика_ХодБезВызоваМодели_НеПишется()
    {
        var (session, sent, _) = NewSession();
        var run = NewRun();
        await Drive(session, run, MessageStart(Usage(0, 50_000, eph1h: 50_000)));
        await Drive(session, run, ResultLine(0, 50_000));
        typeof(ClaudeSession).GetMethod("ResetTurnPromptCacheMiss", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, null);

        // Новый прогон: result на завершённом прогоне — ход без вызова модели
        var nextRun = NewRun();
        int resultsBefore;
        lock (sent) resultsBefore = sent.OfType<ResultMessage>().Count();
        var reasons = await CapturePromptCacheTurns(() => Drive(session, nextRun, ResultLine(50_000, 0)));

        lock (sent) sent.OfType<ResultMessage>().Count().Should().Be(resultsBefore + 1, "result второго хода обработан");
        LastPromptCache(sent).Should().NotBeNull("TTL прошлого хода остался");
        reasons.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PromptCacheMissReason.None, "none")]
    [InlineData(PromptCacheMissReason.SystemChanged, "systemChanged")]
    [InlineData(PromptCacheMissReason.MessagesChanged, "messagesChanged")]
    [InlineData(PromptCacheMissReason.Unavailable, "unavailable")]
    [InlineData(PromptCacheMissReason.PreviousMessageNotFound, "previousMessageNotFound")]
    [InlineData(PromptCacheMissReason.ModelChanged, "modelChanged")]
    [InlineData(PromptCacheMissReason.ToolsChanged, "toolsChanged")]
    public void Метрика_ЗначениеТегаЗамкнуто(PromptCacheMissReason reason, string expected) =>
        TurnTelemetry.CacheMissReasonTag(reason).Should().Be(expected);
}
