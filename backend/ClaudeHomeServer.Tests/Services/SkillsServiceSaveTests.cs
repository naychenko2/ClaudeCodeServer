using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Защита SaveGlobalSkill от path traversal: имя навыка — только имя папки, без
// разделителей и «..». Невалидные имена отклоняются ДО записи на диск.
public class SkillsServiceSaveTests
{
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveGlobalSkill_RejectsUnsafeName(string name)
    {
        var svc = new SkillsService();
        var act = () => svc.SaveGlobalSkill(name, "content");
        act.Should().Throw<ArgumentException>();
    }
}
