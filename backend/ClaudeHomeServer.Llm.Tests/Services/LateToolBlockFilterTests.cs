using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Llm.Gateway;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистка late tool blocks из тела запроса (дефект Kimi K3.1, d22fc75c): CLI инжектит в
// messages role:system-сообщение с блоками tool_addition/tool_removal (beta
// mid-conversation-tool-changes), Kimi отвечает generic 400, фолбэк CLI не матчится.
// Фильтр шлюза вычищает ровно эти два block-типа; system-сообщение без прочих блоков
// удаляется, не-system — получает text-заглушку; остальные block-типы не трогаются.
public sealed class LateToolBlockFilterTests
{
    private static byte[] Body(JsonArray messages) => Encoding.UTF8.GetBytes(
        new JsonObject
        {
            ["model"] = "k3.1-pro",
            ["messages"] = messages,
        }.ToJsonString());

    private static JsonObject Msg(string role, params JsonNode[] blocks) => new()
    {
        ["role"] = role,
        ["content"] = new JsonArray(blocks),
    };

    private static JsonObject Text(string text) => new()
    {
        ["type"] = "text",
        ["text"] = text,
    };

    private static JsonObject Addition(string name) => new()
    {
        ["type"] = "tool_addition",
        ["tool"] = new JsonObject
        {
            ["type"] = "tool_definition",
            ["definition"] = new JsonObject
            {
                ["name"] = name,
            },
        },
    };

    private static JsonObject Removal(string name) => new()
    {
        ["type"] = "tool_removal",
        ["tool"] = new JsonObject
        {
            ["type"] = "tool_reference",
            ["name"] = name,
        },
    };

    private static JsonArray FirstBlocks(byte[] body) => (JsonArray)((JsonArray)JsonNode.Parse(body)!.AsObject()["messages"]!)[0]!["content"]!;

    [Fact]
    public void System_message_with_text_keeps_text_and_drops_late_blocks()
    {
        var body = Body([Msg("system", Text("env"), Addition("a"), Addition("b"))]);
        var scrubbed = LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(2);
        stats.RemovedMessages.Should().Be(0);
        var block = (JsonObject)FirstBlocks(scrubbed)[0]!;
        block["type"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void System_message_with_only_late_blocks_is_removed_whole()
    {
        var body = Body([Msg("user", Text("вопрос")), Msg("system", Addition("x"))]);
        var scrubbed = LateToolBlockFilter.Scrub(body, out var stats);
        var messages = (JsonArray)JsonNode.Parse(scrubbed)!.AsObject()["messages"]!;

        stats.RemovedBlocks.Should().Be(1);
        stats.RemovedMessages.Should().Be(1);
        messages.Count.Should().Be(1);
    }

    [Fact]
    public void Non_system_message_left_empty_gets_text_stub()
    {
        var body = Body([Msg("user", Addition("x"))]);
        var scrubbed = LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(1);
        stats.RemovedMessages.Should().Be(0);
        var block = (JsonObject)FirstBlocks(scrubbed)[0]!;
        block["type"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void Tool_removal_is_also_scrubbed()
    {
        var body = Body([Msg("system", Text("env"), Removal("x"))]);
        var scrubbed = LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(1);
        var block = (JsonObject)FirstBlocks(scrubbed)[0]!;
        block["type"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void Other_block_types_are_preserved()
    {
        var body = Body([
            Msg("assistant", new JsonObject { ["type"] = "thinking", ["thinking"] = "шаг" },
                new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "f", ["input"] = new JsonObject() }),
            Msg("user", new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = "t1",
                ["content"] = new JsonArray(Text("результат")),
            }),
        ]);
        LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(0);
        stats.RemovedMessages.Should().Be(0);
    }

    [Fact]
    public void Clean_body_is_returned_byte_identical()
    {
        var body = Body([Msg("user", Text("привет"))]);
        var scrubbed = LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(0);
        stats.RemovedMessages.Should().Be(0);
        scrubbed.Should().Equal(body);
    }

    [Fact]
    public void Non_json_body_is_returned_as_is()
    {
        var body = Encoding.UTF8.GetBytes("not json");
        LateToolBlockFilter.Scrub(body, out var stats);

        stats.RemovedBlocks.Should().Be(0);
        stats.RemovedMessages.Should().Be(0);
    }
}
