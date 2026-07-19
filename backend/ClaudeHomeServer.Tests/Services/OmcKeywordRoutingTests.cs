using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Детект «магических слов» oh-my-claudecode: важны и позитив, и НЕГАТИВ —
// ложный запуск workflow из-за слова в обычной речи ломает ход пользователю.
public class OmcKeywordRoutingTests
{
    [Theory]
    [InlineData("ralph go", "ralph")]
    [InlineData("запусти ultrawork please", "ultrawork")]
    [InlineData("ulw", "ultrawork")]
    [InlineData("autopilot", "autopilot")]
    [InlineData("full auto", "autopilot")]
    [InlineData("wiki this decision", "wiki")]
    [InlineData("wiki add note", "wiki")]
    public void DetectSkills_МагсловоРаспознаётся(string text, string expectedSkill)
    {
        OmcKeywordRouting.DetectSkills(text).Should().Contain(expectedSkill);
    }

    [Theory]
    [InlineData("посмотри на wiki-страницу проекта")]
    [InlineData("открой wiki и почитай")]
    [InlineData("the wiki page is outdated")]
    [InlineData("wiki")]
    public void DetectSkills_ГолоеWiki_НеТриггерит(string text)
    {
        OmcKeywordRouting.DetectSkills(text).Should().NotContain("wiki");
    }

    [Theory]
    [InlineData("надо отрефакторить код")]           // «раф» внутри — не ralph
    [InlineData("результаты ultrawide монитора")]     // не ultrawork/ulw
    [InlineData("bulwark защита")]                     // ulw внутри слова
    [InlineData("обычное сообщение без магии")]
    [InlineData("")]
    [InlineData(null)]
    public void DetectSkills_ОбычнаяРечь_Пусто(string? text)
    {
        OmcKeywordRouting.DetectSkills(text).Should().BeEmpty();
    }

    [Fact]
    public void DetectSkills_НесколькоМагслов_ВПорядкеПриоритета()
    {
        // ralph раньше ultrawork в таблице приоритетов
        var skills = OmcKeywordRouting.DetectSkills("сначала ralph потом ultrawork");
        skills.Should().Equal("ralph", "ultrawork");
    }

    [Fact]
    public void BuildKeywordHint_ПустойТекст_Null()
    {
        OmcKeywordRouting.BuildKeywordHint("просто текст").Should().BeNull();
    }

    [Fact]
    public void BuildKeywordHint_Магслово_СодержитИнструкциюЗапуска()
    {
        var hint = OmcKeywordRouting.BuildKeywordHint("ralph");
        hint.Should().NotBeNull();
        hint.Should().Contain("/oh-my-claudecode:ralph");
    }
}
