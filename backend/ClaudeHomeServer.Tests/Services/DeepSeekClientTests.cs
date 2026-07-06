using ClaudeHomeServer.Services.Llm.DeepSeek;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты парсера SSE-чанков DeepSeek chat completions на записанных примерах.
/// </summary>
public class DeepSeekClientTests
{
    [Fact]
    public void ParseChunk_ТекстоваяДельта_ДаётContentDelta()
    {
        var json = """{"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Привет"},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().ContainSingle().Which.Should().Be(new DsContentDelta("Привет"));
    }

    [Fact]
    public void ParseChunk_ReasoningДельта_ДаётReasoningDelta()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning_content":"думаю..."},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().ContainSingle().Which.Should().Be(new DsReasoningDelta("думаю..."));
    }

    [Fact]
    public void ParseChunk_НачалоToolCall_ДаётStartИПервыйФрагментАргументов()
    {
        var json = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"pa"}}]},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().HaveCount(2);
        events[0].Should().Be(new DsToolCallStart(0, "call_1", "read_file"));
        events[1].Should().Be(new DsToolCallArgsDelta(0, "{\"pa"));
    }

    [Fact]
    public void ParseChunk_ФрагментАргументовБезId_ДаётТолькоArgsDelta()
    {
        var json = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.txt\"}"}}]},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().ContainSingle().Which.Should().Be(new DsToolCallArgsDelta(0, "th\":\"a.txt\"}"));
    }

    [Fact]
    public void ParseChunk_ПараллельныеToolCalls_РазличаютсяПоIndex()
    {
        var json = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file"}},{"index":1,"id":"call_2","function":{"name":"list_dir"}}]},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().Equal(
            new DsToolCallStart(0, "call_1", "read_file"),
            new DsToolCallStart(1, "call_2", "list_dir"));
    }

    [Fact]
    public void ParseChunk_FinishReason_ДаётFinish()
    {
        var json = """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().ContainSingle().Which.Should().Be(new DsFinish("tool_calls"));
    }

    [Fact]
    public void ParseChunk_UsageЧанк_ДаётUsage()
    {
        var json = """{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":45,"prompt_cache_hit_tokens":100,"prompt_cache_miss_tokens":20,"total_tokens":165}}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().ContainSingle().Which.Should().Be(new DsUsage(120, 45, 100));
    }

    [Fact]
    public void ParseChunk_ПовреждённыйJson_НеБросаетИДаётПусто()
    {
        var events = DeepSeekClient.ParseChunk("{оборванный чанк");

        events.Should().BeEmpty();
    }

    [Fact]
    public void ParseChunk_ПустаяДельта_ДаётПусто()
    {
        var json = """{"choices":[{"index":0,"delta":{"content":""},"finish_reason":null}]}""";

        var events = DeepSeekClient.ParseChunk(json);

        events.Should().BeEmpty();
    }
}
