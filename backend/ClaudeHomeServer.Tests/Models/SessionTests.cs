using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

public class SessionTests
{
    [Fact]
    public void Origin_НиTaskIdНиAutomationRuleId_Manual()
    {
        new Session().Origin.Should().Be(ChatOrigin.Manual);
    }

    [Fact]
    public void Origin_ЕстьTaskId_Task()
    {
        new Session { TaskId = "t1" }.Origin.Should().Be(ChatOrigin.Task);
    }

    [Fact]
    public void Origin_ЕстьAutomationRuleId_Automation()
    {
        new Session { AutomationRuleId = "r1" }.Origin.Should().Be(ChatOrigin.Automation);
    }

    [Fact]
    public void Origin_ОбаЗаданы_TaskПриоритетнееAutomation()
    {
        new Session { TaskId = "t1", AutomationRuleId = "r1" }.Origin.Should().Be(ChatOrigin.Task);
    }
}
