using System.Text.Json;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор транскрипта сабагента (agent-*.jsonl) в паспорт прогона. Ключевой признак — ОБРЫВ:
// последнее сообщение модели закончилось вызовом инструмента и продолжения не последовало.
// Отчёт агента всегда приходит с end_turn (разбор двух прод-прогонов: tool_use 70 и 130 раз,
// end_turn 1 и 2 — ровно у финальных отчётов), поэтому признак однозначен.
public class SubagentRunTallyTests
{
    private static SubagentRunTally Feed(string agentId, params string[] lines)
    {
        var tally = new SubagentRunTally(agentId);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            tally.Feed(doc.RootElement);
        }
        return tally;
    }

    // Строки транскрипта пишем с одинарными кавычками — так JSON читается без экранирования
    private static string Json(string withSingleQuotes) => withSingleQuotes.Replace('\'', '"');

    private static string Prompt(string ts, string text) =>
        Json($"{{'type':'user','timestamp':'{ts}','message':{{'role':'user','content':'{text}'}}}}");

    private static string ToolResult(string ts) =>
        Json($"{{'type':'user','timestamp':'{ts}','message':{{'role':'user'," +
            "'content':[{'type':'tool_result','content':'ok'}]}}");

    private static string ToolCall(string ts, string tool, int output = 100, int input = 10, int cacheRead = 1000) =>
        Json($"{{'type':'assistant','timestamp':'{ts}','message':{{'role':'assistant'," +
            "'model':'claude-opus-5','stop_reason':'tool_use'," +
            $"'usage':{{'input_tokens':{input},'cache_read_input_tokens':{cacheRead}," +
            $"'cache_creation_input_tokens':0,'output_tokens':{output}}}," +
            $"'content':[{{'type':'tool_use','name':'{tool}','input':{{}}}}]}}}}");

    private static string Report(string ts, int output = 500) =>
        Json($"{{'type':'assistant','timestamp':'{ts}','message':{{'role':'assistant'," +
            "'model':'claude-opus-5','stop_reason':'end_turn'," +
            $"'usage':{{'input_tokens':2,'cache_read_input_tokens':2000," +
            $"'cache_creation_input_tokens':50,'output_tokens':{output}}}," +
            "'content':[{'type':'text','text':'Готово'}]}}");

    [Fact]
    public void ПоследнееСообщение_ВызовИнструмента_СчитаетсяОбрывом()
    {
        var tally = Feed("a1",
            Prompt("2026-08-18T09:16:00.000Z", "Задача"),
            ToolCall("2026-08-18T09:16:10.000Z", "Read"),
            ToolResult("2026-08-18T09:16:11.000Z"),
            ToolCall("2026-08-18T09:25:00.000Z", "Bash"),
            // Результат инструмента получен, а продолжения от модели нет — тот самый обрыв
            ToolResult("2026-08-18T09:25:30.000Z"));

        tally.Truncated.Should().BeTrue();
        tally.LastStopReason.Should().Be("tool_use");
        tally.LastTool.Should().Be("Bash");
        tally.ToolUses.Should().Be(2);
        tally.AssistantMessages.Should().Be(2);
        tally.Prompts.Should().Be(1);
    }

    [Fact]
    public void ПоследнееСообщение_Отчёт_ОбрывомНеСчитается()
    {
        var tally = Feed("a2",
            Prompt("2026-08-18T09:16:00.000Z", "Задача"),
            ToolCall("2026-08-18T09:16:10.000Z", "Read"),
            ToolResult("2026-08-18T09:16:11.000Z"),
            Report("2026-08-18T09:17:00.000Z"));

        tally.Truncated.Should().BeFalse();
        tally.LastStopReason.Should().Be("end_turn");
    }

    [Fact]
    public void ДобитыйАгент_ПродолжаетТотЖеТранскрипт_СчитаетсяВтороеСообщение()
    {
        var tally = Feed("a3",
            Prompt("2026-08-18T09:16:00.000Z", "Задача"),
            ToolCall("2026-08-18T09:20:00.000Z", "Bash"),
            ToolResult("2026-08-18T09:20:05.000Z"),
            // Добивание приезжает агенту обычным текстовым сообщением
            Prompt("2026-08-18T09:34:00.000Z", "The coordinator sent a message while you were working"),
            Report("2026-08-18T09:39:00.000Z"));

        tally.Prompts.Should().Be(2);
        tally.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Токены_ОкноБерётсяОтПоследнегоСообщения_ВыходСуммируется()
    {
        var tally = Feed("a4",
            ToolCall("2026-08-18T09:16:10.000Z", "Read", output: 100, input: 10, cacheRead: 1000),
            ToolCall("2026-08-18T09:16:20.000Z", "Read", output: 200, input: 20, cacheRead: 5000));

        // Окно последнего запроса: input + cache_read + cache_creation (почти весь контекст
        // сабагента идёт кэшем — по одному input_tokens обрыв никогда не сойдётся)
        tally.ContextTokens.Should().Be(5020);
        tally.OutputTokens.Should().Be(300);
    }

    [Fact]
    public void Паспорт_НесётДлительностьИсточникИПризнакОбрыва()
    {
        var tally = Feed("a5",
            Prompt("2026-08-18T09:16:00.000Z", "Задача"),
            ToolCall("2026-08-18T09:25:00.000Z", "Bash"),
            ToolResult("2026-08-18T09:25:30.000Z"));
        tally.AgentType = "mark";
        tally.Description = "Волна 1";
        tally.ToolUseId = "toolu_1";

        var passport = tally.Build("sess-1", "bg_done", transcriptBytes: 4242);

        passport.AgentId.Should().Be("a5");
        passport.AgentType.Should().Be("mark");
        passport.SessionId.Should().Be("sess-1");
        passport.ToolUseId.Should().Be("toolu_1");
        passport.DurationSeconds.Should().Be(570);
        passport.Truncated.Should().BeTrue();
        passport.LastTool.Should().Be("Bash");
        passport.Model.Should().Be("claude-opus-5");
        passport.TranscriptBytes.Should().Be(4242);
        passport.FinishedBy.Should().Be("bg_done");
        passport.NudgeAttempts.Should().Be(0);
    }

    [Fact]
    public void СтрокиБезСообщения_НеЛомаютСчёт()
    {
        var tally = Feed("a6",
            Json("{'type':'summary','summary':'свёртка'}"),
            Json("{'type':'assistant','timestamp':'2026-08-18T09:16:10.000Z'}"),
            Report("2026-08-18T09:16:20.000Z"));

        tally.AssistantMessages.Should().Be(1);
        tally.Truncated.Should().BeFalse();
    }
}
