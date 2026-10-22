using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Гейт текстового учёта фоновых запусков: маркер «Async agent launched … agentId:» ищется только
// в результате инструмента, умеющего запускать фон. Бой 15.10.2026: PowerShell вывел дамп
// чужого транскрипта с этой строкой, прогон записал себе несуществующего агента — значок
// «Работают агенты» горел, процесс доживал до потолка BgLingerTimeout.
public class ClaudeSessionBgLaunchGateTests
{
    private const string LaunchDump =
        "RES: Async agent launched successfully. agentId: a08065a0410476228 (internal ID)";

    [Theory]
    [InlineData("Agent")]
    [InlineData("Task")]
    [InlineData("SendMessage")]
    [InlineData("Workflow")]
    public void ЗапускающиеИнструменты_ПроверяютМаркер(string tool) =>
        ClaudeSession.IsBgCapableTool(tool).Should().BeTrue();

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("Bash")]
    [InlineData("Read")]
    [InlineData("mcp__wsp__chats_history")]
    public void ПрочиеИнструменты_МаркерНеИщется(string tool) =>
        ClaudeSession.IsBgCapableTool(tool).Should().BeFalse();

    // Сам маркер в дампе распознаётся — поэтому и нужен гейт по инструменту
    [Fact]
    public void МаркерВДампе_Распознаётся() =>
        ClaudeSession.HasBgLaunchMarker(LaunchDump).Should().BeTrue();
}
